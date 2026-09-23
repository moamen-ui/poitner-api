using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pointer.API.Auth;
using Pointer.API.Extensions;
using Pointer.API.Hosted;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Impersonation;
using Pointer.Application.Resources;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Audit;
using Pointer.Infrastructure.Auth;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-13 (F2) — metadata-only operator + audited, time-boxed impersonation. §6 test coverage:
/// the six content query filters lose their unconditional super-admin branch (tests 1-3), the
/// impersonation service start/end/list lifecycle (tests 4-6, 13), the token fence + per-request
/// liveness check (tests 7-8), the request counter (test 11), and the read-scope services that
/// needed a matching fix (SuggestionService, ProfileService, AuditQueryService — test 13).
/// </summary>
public class ImpersonationTests
{
    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; }
        public bool IsQuickAccess { get; set; }
        public Guid? TenantId { get; set; }
        public int? RoleId { get; set; }
        public string? KeyScopes { get; set; }
        public string? Scope { get; set; }
        public long? ImpersonationSessionId { get; set; }
        public bool IsImpersonating => ImpersonationSessionId != null;
    }

    private sealed class CapturingEmail : IEmailService
    {
        public List<(string To, string Subject, string Html)> Sent { get; } = new();

        public Task<bool> SendAsync(
            string to,
            string subject,
            string htmlBody,
            CancellationToken ct = default
        )
        {
            Sent.Add((to, subject, htmlBody));
            return Task.FromResult(true);
        }
    }

    private sealed class NoopBrandingService : IBrandingService
    {
        private static Pointer.Application.DTOs.Branding.BrandingResponse Response() =>
            new() { ProductName = "Pointer" };

        public Task<Pointer.Application.Response.Result<Pointer.Application.DTOs.Branding.BrandingResponse>> GetAsync(
            string publicBase,
            IReadOnlySet<string> existingKinds
        ) =>
            Task.FromResult(
                Pointer.Application.Response.Result<Pointer.Application.DTOs.Branding.BrandingResponse>.Success(
                    Response()
                )
            );

        public Task<Pointer.Application.Response.Result<Pointer.Application.DTOs.Branding.BrandingResponse>> UpdateAsync(
            Pointer.Application.DTOs.Branding.BrandingWriteDto dto,
            string publicBase,
            IReadOnlySet<string> existingKinds
        ) =>
            Task.FromResult(
                Pointer.Application.Response.Result<Pointer.Application.DTOs.Branding.BrandingResponse>.Success(
                    Response()
                )
            );

        public Task<int> BumpVersionAsync() => Task.FromResult(0);

        public Task<Pointer.Application.DTOs.Branding.BrandingResponse> BuildResponseAsync(
            string publicBase,
            IReadOnlySet<string> existingKinds
        ) => Task.FromResult(Response());
    }

    private sealed class FakeHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    // ---------------------------------------------------------------------------
    // InMemory fixture (filter tests) — TenantQueryFilterTests.cs:20-46 shape.
    // ---------------------------------------------------------------------------

    private static AppDbContext InMemoryContext(ICurrentUser user, string dbName) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options,
            user,
            new ConfigurationBuilder().Build()
        );

    // ---------------------------------------------------------------------------
    // Sqlite shared-cache fixture (service/fence/counter tests) — RetentionServiceTests.cs shape.
    // ---------------------------------------------------------------------------

    private sealed class TestDb : IDisposable
    {
        private readonly SqliteConnection _keepAlive;
        private readonly string _connectionString;

        public TestDb()
        {
            _connectionString = $"DataSource=file:{Guid.NewGuid():N}?mode=memory&cache=shared";
            _keepAlive = new SqliteConnection(_connectionString);
            _keepAlive.Open();

            using var bootstrap = MakeContext(new FakeCurrentUser { IsSuperAdmin = true });
            bootstrap.Database.EnsureCreated();
        }

        public AppDbContext MakeContext(ICurrentUser user) =>
            new(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite(_connectionString)
                    .AddInterceptors(new SqliteBtrimFunctionInterceptor())
                    .Options,
                user,
                new ConfigurationBuilder().Build()
            );

        public void Dispose() => _keepAlive.Dispose();
    }

    private static ITokenService BuildTokenService() =>
        new JwtTokenService(
            Options.Create(
                new JwtOptions
                {
                    SigningKey = new string('k', 40),
                    Issuer = "pointer-api",
                    LifetimeHours = 12,
                }
            )
        );

    private static ImpersonationService BuildService(
        AppDbContext db,
        ICurrentUser user,
        IAuditWriter? audit = null,
        IEmailService? email = null,
        IBrandingService? branding = null
    ) =>
        new(
            new UnitOfWork(db),
            user,
            BuildTokenService(),
            audit ?? new FakeAuditWriter(),
            new MembershipService(new UnitOfWork(db)),
            email ?? new CapturingEmail(),
            branding ?? new NoopBrandingService(),
            NullLogger<ImpersonationService>.Instance
        );

    /// <summary>Seeds a workspace and returns its id; optionally an admin membership too.</summary>
    private static (Guid WorkspaceId, Guid AdminPublicId) SeedWorkspaceWithAdmin(
        AppDbContext db,
        bool grantsAdmin = true,
        bool live = true
    )
    {
        var workspaceId = Guid.NewGuid();
        db.Workspaces.Add(
            new Workspace
            {
                Id = workspaceId,
                Name = "Acme",
                CreatedAt = DateTime.UtcNow,
                CreatedBy = workspaceId,
            }
        );
        var role = new Role
        {
            Name = "Workspace Admin",
            GrantsAdmin = grantsAdmin,
            IsActive = true,
            IsSystem = true,
            OwnerId = workspaceId,
        };
        db.Roles.Add(role);
        db.SaveChanges();

        var adminPublicId = Guid.NewGuid();
        var admin = new User
        {
            PublicId = adminPublicId,
            Email = "admin@acme.com",
            DisplayName = "Acme Admin",
            PasswordHash = "x",
            RoleId = role.Id,
            OwnerId = workspaceId,
            IsActive = true,
            ApprovalStatus = ApprovalStatus.Approved,
        };
        db.Users.Add(admin);
        db.SaveChanges();
        TestSeed.Join(
            db,
            admin,
            workspaceId,
            role,
            isActive: live,
            status: ApprovalStatus.Approved
        );

        return (workspaceId, adminPublicId);
    }

    private static User SeedOperator(AppDbContext db, Guid operatorPublicId)
    {
        var role = new Role
        {
            Name = "Super Admin",
            GrantsAdmin = true,
            IsSuperAdmin = true,
            IsActive = true,
            IsSystem = true,
            OwnerId = null,
        };
        db.Roles.Add(role);
        db.SaveChanges();
        var op = new User
        {
            PublicId = operatorPublicId,
            Email = "operator@pointer.internal",
            DisplayName = "Operator",
            PasswordHash = "x",
            RoleId = role.Id,
            OwnerId = null,
            IsActive = true,
            ApprovalStatus = ApprovalStatus.Approved,
        };
        db.Users.Add(op);
        db.SaveChanges();
        return op;
    }

    // ---------------------------------------------------------------------------
    // §6 test 1 — plain super admin sees no content, but metadata is unaffected.
    // ---------------------------------------------------------------------------

    [Fact]
    public void SuperAdmin_WithoutTenantClaim_SeesNoContent()
    {
        var db = Guid.NewGuid().ToString();
        var a = Guid.NewGuid();

        using (var seed = InMemoryContext(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = a,
                    Name = "A",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = a,
                }
            );
            var project = new Project
            {
                Key = "a-proj",
                Name = "A Project",
                OwnerId = a,
            };
            seed.Projects.Add(project);
            seed.SaveChanges();

            var comment = new Comment
            {
                ProjectId = project.Id,
                Environment = EnvironmentTag.Staging,
                Status = CommentStatus.Open,
                AuthorId = Guid.NewGuid(),
                Body = "hi",
                OwnerId = a,
            };
            seed.Comments.Add(comment);
            seed.SaveChanges();
            seed.Replies.Add(
                new Reply
                {
                    CommentId = comment.Id,
                    AuthorId = Guid.NewGuid(),
                    Body = "reply",
                    OwnerId = a,
                }
            );
            seed.PageContextSnapshots.Add(
                new PageContextSnapshot
                {
                    ProjectId = project.Id,
                    Environment = EnvironmentTag.Staging,
                    Route = "/x",
                    SessionId = "s1",
                    OwnerId = a,
                    LastEventAt = DateTime.UtcNow,
                }
            );
            seed.PredefinedActionSuggestions.Add(
                new PredefinedActionSuggestion
                {
                    ProjectId = project.Id,
                    Text = "Sug",
                    Prompt = "P",
                    OwnerId = a,
                }
            );
            seed.AiRules.Add(
                new AiRule
                {
                    OwnerId = a,
                    Title = "Rule",
                    Prompt = "Do X",
                    IsActive = true,
                }
            );
            seed.PredefinedActions.Add(
                new PredefinedAction
                {
                    OwnerId = a,
                    Text = "Action",
                    Prompt = "P",
                }
            );
            seed.SaveChanges();
        }

        using var ctx = InMemoryContext(new FakeCurrentUser { IsSuperAdmin = true }, db);

        // Content: all six sets are empty for the plain operator. (PredefinedAction's null-owner
        // "global" bucket is DB-enforced NOT NULL and covered separately in TenantQueryFilterTests —
        // here only the tenant row's invisibility is asserted.)
        Assert.Empty(ctx.Comments.ToList());
        Assert.Empty(ctx.Replies.ToList());
        Assert.Empty(ctx.PageContextSnapshots.ToList());
        Assert.Empty(ctx.PredefinedActionSuggestions.ToList());
        Assert.Empty(ctx.AiRules.ToList());
        Assert.Empty(ctx.PredefinedActions.ToList());

        // Metadata: unaffected.
        Assert.Single(ctx.Projects.ToList());
        Assert.Single(ctx.Workspaces.ToList());
    }

    // ---------------------------------------------------------------------------
    // §6 test 2 — impersonating operator sees exactly the target's content, nothing of B.
    // ---------------------------------------------------------------------------

    [Fact]
    public void SuperAdmin_Impersonating_SeesOnlyTargetContent()
    {
        var db = Guid.NewGuid().ToString();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        using (var seed = InMemoryContext(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = a,
                    Name = "A",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = a,
                }
            );
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = b,
                    Name = "B",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = b,
                }
            );
            var projectA = new Project
            {
                Key = "a-proj",
                Name = "A Project",
                OwnerId = a,
            };
            var projectB = new Project
            {
                Key = "b-proj",
                Name = "B Project",
                OwnerId = b,
            };
            seed.Projects.AddRange(projectA, projectB);
            seed.SaveChanges();

            var commentA = new Comment
            {
                ProjectId = projectA.Id,
                Environment = EnvironmentTag.Staging,
                Status = CommentStatus.Open,
                AuthorId = Guid.NewGuid(),
                Body = "a-comment",
                OwnerId = a,
            };
            var commentB = new Comment
            {
                ProjectId = projectB.Id,
                Environment = EnvironmentTag.Staging,
                Status = CommentStatus.Open,
                AuthorId = Guid.NewGuid(),
                Body = "b-comment",
                OwnerId = b,
            };
            seed.Comments.AddRange(commentA, commentB);
            seed.SaveChanges();
            seed.Replies.Add(
                new Reply
                {
                    CommentId = commentA.Id,
                    AuthorId = Guid.NewGuid(),
                    Body = "a-reply",
                    OwnerId = a,
                }
            );
            seed.Replies.Add(
                new Reply
                {
                    CommentId = commentB.Id,
                    AuthorId = Guid.NewGuid(),
                    Body = "b-reply",
                    OwnerId = b,
                }
            );
            seed.PageContextSnapshots.Add(
                new PageContextSnapshot
                {
                    ProjectId = projectA.Id,
                    Environment = EnvironmentTag.Staging,
                    Route = "/a",
                    SessionId = "sa",
                    OwnerId = a,
                    LastEventAt = DateTime.UtcNow,
                }
            );
            seed.PageContextSnapshots.Add(
                new PageContextSnapshot
                {
                    ProjectId = projectB.Id,
                    Environment = EnvironmentTag.Staging,
                    Route = "/b",
                    SessionId = "sb",
                    OwnerId = b,
                    LastEventAt = DateTime.UtcNow,
                }
            );
            seed.PredefinedActionSuggestions.Add(
                new PredefinedActionSuggestion
                {
                    ProjectId = projectA.Id,
                    Text = "SugA",
                    Prompt = "P",
                    OwnerId = a,
                }
            );
            seed.PredefinedActionSuggestions.Add(
                new PredefinedActionSuggestion
                {
                    ProjectId = projectB.Id,
                    Text = "SugB",
                    Prompt = "P",
                    OwnerId = b,
                }
            );
            seed.AiRules.Add(
                new AiRule
                {
                    OwnerId = a,
                    Title = "RuleA",
                    Prompt = "Do X",
                    IsActive = true,
                }
            );
            seed.AiRules.Add(
                new AiRule
                {
                    OwnerId = b,
                    Title = "RuleB",
                    Prompt = "Do Y",
                    IsActive = true,
                }
            );
            seed.SaveChanges();
        }

        using var ctx = InMemoryContext(
            new FakeCurrentUser
            {
                IsSuperAdmin = true,
                TenantId = a,
                ImpersonationSessionId = 1,
            },
            db
        );

        Assert.Single(ctx.Comments.ToList());
        Assert.Equal("a-comment", ctx.Comments.Single().Body);
        Assert.Single(ctx.Replies.ToList());
        Assert.Equal("a-reply", ctx.Replies.Single().Body);
        Assert.Single(ctx.PageContextSnapshots.ToList());
        Assert.Equal("/a", ctx.PageContextSnapshots.Single().Route);
        Assert.Single(ctx.PredefinedActionSuggestions.ToList());
        Assert.Equal("SugA", ctx.PredefinedActionSuggestions.Single().Text);
        Assert.Single(ctx.AiRules.ToList());
        Assert.Equal("RuleA", ctx.AiRules.Single().Title);
    }

    // ---------------------------------------------------------------------------
    // §6 test 3 (R8) — workspace B never sees a session that targeted A.
    // ---------------------------------------------------------------------------

    [Fact]
    public void TenantB_SeesNoSessionOfA()
    {
        var db = Guid.NewGuid().ToString();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        using (var seed = InMemoryContext(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = a,
                    Name = "A",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = a,
                }
            );
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = b,
                    Name = "B",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = b,
                }
            );
            seed.SaveChanges();
            seed.ImpersonationSessions.Add(
                new ImpersonationSession
                {
                    OwnerId = a,
                    OperatorUserId = Guid.NewGuid(),
                    Reason = "checking something out for support",
                    StartedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(30),
                }
            );
            seed.SaveChanges();
        }

        using var ctxB = InMemoryContext(new FakeCurrentUser { TenantId = b }, db);
        Assert.Empty(ctxB.ImpersonationSessions.ToList());

        using var ctxA = InMemoryContext(new FakeCurrentUser { TenantId = a }, db);
        Assert.Single(ctxA.ImpersonationSessions.ToList());
    }

    // ---------------------------------------------------------------------------
    // §6 test 4 — Start inserts the session, issues a correctly-shaped token, audits, e-mails.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Start_InsertsSession_IssuesToken_Audits_Emails()
    {
        using var testDb = new TestDb();
        var operatorPublicId = Guid.NewGuid();
        Guid workspaceId;
        using (var seed = testDb.MakeContext(new FakeCurrentUser { IsSuperAdmin = true }))
        {
            (workspaceId, _) = SeedWorkspaceWithAdmin(seed);
            var adminRole = seed.Roles.IgnoreQueryFilters().Single(r => r.OwnerId == workspaceId);

            // A second, disabled admin membership — must NOT receive a mail.
            var disabledAdmin = new User
            {
                PublicId = Guid.NewGuid(),
                Email = "disabled@acme.com",
                DisplayName = "Disabled",
                PasswordHash = "x",
                RoleId = adminRole.Id,
                OwnerId = workspaceId,
                IsActive = true,
                ApprovalStatus = ApprovalStatus.Approved,
            };
            seed.Users.Add(disabledAdmin);
            seed.SaveChanges();
            TestSeed.Join(seed, disabledAdmin, workspaceId, adminRole, isActive: false);

            // A second live admin — SHOULD receive a mail (2 total).
            var secondAdmin = new User
            {
                PublicId = Guid.NewGuid(),
                Email = "second@acme.com",
                DisplayName = "Second Admin",
                PasswordHash = "x",
                RoleId = adminRole.Id,
                OwnerId = workspaceId,
                IsActive = true,
                ApprovalStatus = ApprovalStatus.Approved,
            };
            seed.Users.Add(secondAdmin);
            seed.SaveChanges();
            TestSeed.Join(seed, secondAdmin, workspaceId, adminRole, isActive: true);

            SeedOperator(seed, operatorPublicId);
        }

        var opUser = new FakeCurrentUser
        {
            Id = operatorPublicId,
            IsAdmin = true,
            IsSuperAdmin = true,
        };
        var audit = new FakeAuditWriter();
        var email = new CapturingEmail();
        using var ctx = testDb.MakeContext(opUser);
        var svc = BuildService(ctx, opUser, audit, email);

        var result = await svc.StartAsync(
            workspaceId,
            new StartImpersonationRequest
            {
                Reason = "investigating a support ticket",
                Minutes = 15,
            }
        );

        Assert.True(result.IsSuccess);
        var data = result.Data!;
        Assert.Equal(workspaceId, data.WorkspaceId);
        Assert.True(data.SessionId > 0);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(data.Token);
        Assert.Equal("impersonate", jwt.Claims.First(c => c.Type == "scope").Value);
        Assert.Equal(workspaceId.ToString(), jwt.Claims.First(c => c.Type == "tenant").Value);
        Assert.Equal(data.SessionId.ToString(), jwt.Claims.First(c => c.Type == "imp").Value);
        Assert.Equal("true", jwt.Claims.First(c => c.Type == "is_super_admin").Value);
        Assert.DoesNotContain(jwt.Claims, c => c.Type == "mstamp");
        Assert.True(jwt.ValidTo <= DateTime.UtcNow.AddMinutes(16));

        var started = Assert.Single(
            audit.Entries,
            e => e.Action == AuditActions.ImpersonationStarted
        );
        Assert.Equal(workspaceId, started.OwnerId);

        Assert.Equal(2, email.Sent.Count);
        Assert.All(email.Sent, m => Assert.DoesNotContain("operator@pointer.internal", m.Html));
        Assert.All(email.Sent, m => Assert.Contains("investigating a support ticket", m.Html));
    }

    // ---------------------------------------------------------------------------
    // §6 test 5 — Start guards.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Start_SecondLiveSession_Conflict()
    {
        using var testDb = new TestDb();
        var operatorPublicId = Guid.NewGuid();
        Guid workspaceId;
        using (var seed = testDb.MakeContext(new FakeCurrentUser { IsSuperAdmin = true }))
        {
            (workspaceId, _) = SeedWorkspaceWithAdmin(seed);
            SeedOperator(seed, operatorPublicId);
        }

        var opUser = new FakeCurrentUser
        {
            Id = operatorPublicId,
            IsAdmin = true,
            IsSuperAdmin = true,
        };
        using var ctx1 = testDb.MakeContext(opUser);
        var svc1 = BuildService(ctx1, opUser);
        var first = await svc1.StartAsync(
            workspaceId,
            new StartImpersonationRequest { Reason = "first session, ten chars", Minutes = 10 }
        );
        Assert.True(first.IsSuccess);

        using var ctx2 = testDb.MakeContext(opUser);
        var svc2 = BuildService(ctx2, opUser);
        var second = await svc2.StartAsync(
            workspaceId,
            new StartImpersonationRequest { Reason = "second session attempt", Minutes = 10 }
        );
        Assert.True(second.IsConflict);
        Assert.Equal(MessageKeys.Impersonation.AlreadyActive, second.Message);
    }

    [Fact]
    public async Task Start_NonSuperAdmin_Forbidden()
    {
        using var testDb = new TestDb();
        Guid workspaceId;
        using (var seed = testDb.MakeContext(new FakeCurrentUser { IsSuperAdmin = true }))
            (workspaceId, _) = SeedWorkspaceWithAdmin(seed);

        var plainAdmin = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            IsAdmin = true,
            TenantId = workspaceId,
        };
        using var ctx = testDb.MakeContext(plainAdmin);
        var svc = BuildService(ctx, plainAdmin);

        var result = await svc.StartAsync(
            workspaceId,
            new StartImpersonationRequest { Reason = "not a super admin at all", Minutes = 10 }
        );
        Assert.True(result.IsForbidden);
    }

    [Fact]
    public async Task Start_UnknownWorkspace_NotFound()
    {
        using var testDb = new TestDb();
        var operatorPublicId = Guid.NewGuid();
        using (var seed = testDb.MakeContext(new FakeCurrentUser { IsSuperAdmin = true }))
            SeedOperator(seed, operatorPublicId);

        var opUser = new FakeCurrentUser
        {
            Id = operatorPublicId,
            IsAdmin = true,
            IsSuperAdmin = true,
        };
        using var ctx = testDb.MakeContext(opUser);
        var svc = BuildService(ctx, opUser);

        var result = await svc.StartAsync(
            Guid.NewGuid(),
            new StartImpersonationRequest { Reason = "workspace does not exist", Minutes = 10 }
        );
        Assert.True(result.IsNotFound);
    }

    [Fact]
    public void Start_Validator_RejectsShortReason_And61Minutes()
    {
        var validator = new Pointer.Application.Validators.StartImpersonationValidator();

        var tooShort = validator.Validate(
            new StartImpersonationRequest { Reason = "short", Minutes = 30 }
        );
        Assert.False(tooShort.IsValid);

        var tooLong = validator.Validate(
            new StartImpersonationRequest
            {
                Reason = "a reason with plenty of characters in it",
                Minutes = 61,
            }
        );
        Assert.False(tooLong.IsValid);

        var ok = validator.Validate(
            new StartImpersonationRequest { Reason = "a perfectly fine reason", Minutes = 60 }
        );
        Assert.True(ok.IsValid);
    }

    // ---------------------------------------------------------------------------
    // §6 test 13 — the unique partial index is the real race guard, not just the pre-check.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Start_ConcurrentSecondSession_Conflict_ViaUniqueIndex()
    {
        using var testDb = new TestDb();
        var operatorPublicId = Guid.NewGuid();
        Guid workspaceId;
        using (var seed = testDb.MakeContext(new FakeCurrentUser { IsSuperAdmin = true }))
        {
            (workspaceId, _) = SeedWorkspaceWithAdmin(seed);
            SeedOperator(seed, operatorPublicId);
            // Insert a live session row directly — bypasses the service's own pre-check, so only
            // the database's unique partial index can catch it.
            seed.ImpersonationSessions.Add(
                new ImpersonationSession
                {
                    OwnerId = workspaceId,
                    OperatorUserId = operatorPublicId,
                    Reason = "already running, inserted directly",
                    StartedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(30),
                }
            );
            seed.SaveChanges();
        }

        var opUser = new FakeCurrentUser
        {
            Id = operatorPublicId,
            IsAdmin = true,
            IsSuperAdmin = true,
        };
        using var ctx = testDb.MakeContext(opUser);
        var svc = BuildService(ctx, opUser);

        var result = await svc.StartAsync(
            workspaceId,
            new StartImpersonationRequest { Reason = "race condition attempt", Minutes = 10 }
        );

        Assert.True(result.IsConflict);
        using var verify = testDb.MakeContext(new FakeCurrentUser { IsSuperAdmin = true });
        Assert.Single(
            verify.ImpersonationSessions.Where(s =>
                s.OperatorUserId == operatorPublicId && s.EndedAt == null
            )
        );
    }

    [Fact]
    public async Task Start_WorkspaceWithoutAdmins_SucceedsWithoutMail()
    {
        using var testDb = new TestDb();
        var operatorPublicId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        using (var seed = testDb.MakeContext(new FakeCurrentUser { IsSuperAdmin = true }))
        {
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = workspaceId,
                    Name = "No Admins",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = workspaceId,
                }
            );
            seed.SaveChanges();
            SeedOperator(seed, operatorPublicId);
        }

        var opUser = new FakeCurrentUser
        {
            Id = operatorPublicId,
            IsAdmin = true,
            IsSuperAdmin = true,
        };
        var audit = new FakeAuditWriter();
        var email = new CapturingEmail();
        using var ctx = testDb.MakeContext(opUser);
        var svc = BuildService(ctx, opUser, audit, email);

        var result = await svc.StartAsync(
            workspaceId,
            new StartImpersonationRequest { Reason = "post-deploy verification run", Minutes = 10 }
        );

        Assert.True(result.IsSuccess);
        Assert.Empty(email.Sent);
        Assert.Single(audit.Entries, e => e.Action == AuditActions.ImpersonationStarted);
    }

    // ---------------------------------------------------------------------------
    // §6 test 6 — End sets EndedAt, audits with request count; wrong operator is NotFound; the
    // sweep closes expired sessions with reason "expired".
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task End_SetsEndedAt_Audits_WithRequestCount()
    {
        using var testDb = new TestDb();
        var operatorPublicId = Guid.NewGuid();
        Guid workspaceId;
        using (var seed = testDb.MakeContext(new FakeCurrentUser { IsSuperAdmin = true }))
        {
            (workspaceId, _) = SeedWorkspaceWithAdmin(seed);
            SeedOperator(seed, operatorPublicId);
        }

        var opUser = new FakeCurrentUser
        {
            Id = operatorPublicId,
            IsAdmin = true,
            IsSuperAdmin = true,
        };
        using var ctxStart = testDb.MakeContext(opUser);
        var startSvc = BuildService(ctxStart, opUser);
        var started = await startSvc.StartAsync(
            workspaceId,
            new StartImpersonationRequest { Reason = "end-to-end lifecycle test", Minutes = 30 }
        );
        Assert.True(started.IsSuccess);

        // Bump the request count directly (simulates ImpersonationRequestCounter having run).
        using (var bump = testDb.MakeContext(new FakeCurrentUser { IsSuperAdmin = true }))
        {
            var row = bump
                .ImpersonationSessions.IgnoreQueryFilters()
                .Single(s => s.Id == started.Data!.SessionId);
            row.RequestCount = 3;
            bump.SaveChanges();
        }

        var impUser = new FakeCurrentUser
        {
            Id = operatorPublicId,
            IsAdmin = true,
            IsSuperAdmin = true,
            TenantId = workspaceId,
            ImpersonationSessionId = started.Data!.SessionId,
        };
        var audit = new FakeAuditWriter();
        using var ctxEnd = testDb.MakeContext(impUser);
        var endSvc = BuildService(ctxEnd, impUser, audit);

        var ended = await endSvc.EndAsync(null);
        Assert.True(ended.IsSuccess);

        using var verify = testDb.MakeContext(new FakeCurrentUser { IsSuperAdmin = true });
        var session = verify
            .ImpersonationSessions.IgnoreQueryFilters()
            .Single(s => s.Id == started.Data!.SessionId);
        Assert.NotNull(session.EndedAt);
        Assert.Equal(ImpersonationEndReason.Manual, session.EndReason);

        var endedEntry = Assert.Single(
            audit.Entries,
            e => e.Action == AuditActions.ImpersonationEnded
        );
        Assert.Equal("3", endedEntry.After!["request_count"]);
        Assert.Equal("manual", endedEntry.After["reason"]);
    }

    [Fact]
    public async Task End_ByOtherOperator_NotFound()
    {
        using var testDb = new TestDb();
        var operatorPublicId = Guid.NewGuid();
        var otherOperatorId = Guid.NewGuid();
        Guid workspaceId;
        using (var seed = testDb.MakeContext(new FakeCurrentUser { IsSuperAdmin = true }))
        {
            (workspaceId, _) = SeedWorkspaceWithAdmin(seed);
            SeedOperator(seed, operatorPublicId);
            SeedOperator(seed, otherOperatorId);
        }

        var opUser = new FakeCurrentUser
        {
            Id = operatorPublicId,
            IsAdmin = true,
            IsSuperAdmin = true,
        };
        using var ctxStart = testDb.MakeContext(opUser);
        var startSvc = BuildService(ctxStart, opUser);
        var started = await startSvc.StartAsync(
            workspaceId,
            new StartImpersonationRequest { Reason = "session started by operator A", Minutes = 30 }
        );
        Assert.True(started.IsSuccess);

        var otherOp = new FakeCurrentUser
        {
            Id = otherOperatorId,
            IsAdmin = true,
            IsSuperAdmin = true,
        };
        using var ctxEnd = testDb.MakeContext(otherOp);
        var endSvc = BuildService(ctxEnd, otherOp);

        var result = await endSvc.EndAsync(started.Data!.SessionId);
        Assert.True(result.IsNotFound);
    }

    [Fact]
    public async Task Sweep_ClosesExpired_WritesEndedRow_ReasonExpired()
    {
        using var testDb = new TestDb();
        var operatorPublicId = Guid.NewGuid();
        Guid workspaceId;
        long sessionId;
        using (var seed = testDb.MakeContext(new FakeCurrentUser { IsSuperAdmin = true }))
        {
            (workspaceId, _) = SeedWorkspaceWithAdmin(seed);
            var session = new ImpersonationSession
            {
                OwnerId = workspaceId,
                OperatorUserId = operatorPublicId,
                Reason = "will expire without being ended manually",
                StartedAt = DateTime.UtcNow.AddMinutes(-10),
                ExpiresAt = DateTime.UtcNow.AddMinutes(-1),
            };
            seed.ImpersonationSessions.Add(session);
            seed.SaveChanges();
            sessionId = session.Id;
        }

        var audit = new FakeAuditWriter();
        using var ctx = testDb.MakeContext(new FakeCurrentUser { IsSuperAdmin = true });
        var closed = await ImpersonationSweepService.SweepOnceAsync(
            ctx,
            audit,
            NullLogger.Instance,
            CancellationToken.None
        );

        Assert.Equal(1, closed);
        using var verify = testDb.MakeContext(new FakeCurrentUser { IsSuperAdmin = true });
        var row = verify.ImpersonationSessions.IgnoreQueryFilters().Single(s => s.Id == sessionId);
        Assert.NotNull(row.EndedAt);
        Assert.Equal(ImpersonationEndReason.Expired, row.EndReason);

        var endedEntry = Assert.Single(
            audit.Entries,
            e => e.Action == AuditActions.ImpersonationEnded
        );
        Assert.Equal("expired", endedEntry.After!["reason"]);
        Assert.Equal(Domain.Enums.AuditActorKind.System, endedEntry.ActorKindOverride);
    }

    // ---------------------------------------------------------------------------
    // §6 test 7 — the fence: reads (+ its own end) only.
    // ---------------------------------------------------------------------------

    [Fact]
    public void Fence_AllowsReadsAndEndOnly()
    {
        Assert.True(ImpersonationScopeFence.Allows("GET", "/api/comments/1"));
        Assert.True(ImpersonationScopeFence.Allows("POST", "/api/admin/impersonation/end"));
        Assert.False(ImpersonationScopeFence.Allows("POST", "/api/admin/impersonation/end/"));
        Assert.False(ImpersonationScopeFence.Allows("POST", "/api/admin/impersonation/endx"));
        Assert.False(ImpersonationScopeFence.Allows("PUT", "/api/admin/workspace/name"));
        Assert.False(ImpersonationScopeFence.Allows("DELETE", "/api/admin/users/1"));
        Assert.False(ImpersonationScopeFence.Allows("POST", "/api/events"));
    }

    // ---------------------------------------------------------------------------
    // §6 test 8 — the liveness check itself.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Validator_RejectsEndedOrExpiredSession()
    {
        using var testDb = new TestDb();
        var operatorId = Guid.NewGuid();
        var expiredOperatorId = Guid.NewGuid();
        var wrongOperatorId = Guid.NewGuid();
        Guid workspaceId;
        var wrongWorkspace = Guid.NewGuid();
        long liveId,
            endedId,
            expiredId;

        using (var seed = testDb.MakeContext(new FakeCurrentUser { IsSuperAdmin = true }))
        {
            (workspaceId, _) = SeedWorkspaceWithAdmin(seed);
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = wrongWorkspace,
                    Name = "Wrong",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = wrongWorkspace,
                }
            );

            // "expired" gets its own operator id — the unique partial index (ended_at IS NULL)
            // allows at most one such row per operator, and an expired-but-not-yet-swept session
            // still counts as live for that purpose (by design: the operator can't start a second
            // session until the sweep or a manual end closes the first).
            var live = new ImpersonationSession
            {
                OwnerId = workspaceId,
                OperatorUserId = operatorId,
                Reason = "still live right now",
                StartedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(30),
            };
            var ended = new ImpersonationSession
            {
                OwnerId = workspaceId,
                OperatorUserId = operatorId,
                Reason = "already manually ended",
                StartedAt = DateTime.UtcNow.AddMinutes(-20),
                ExpiresAt = DateTime.UtcNow.AddMinutes(10),
                EndedAt = DateTime.UtcNow,
                EndReason = ImpersonationEndReason.Manual,
            };
            var expired = new ImpersonationSession
            {
                OwnerId = workspaceId,
                OperatorUserId = expiredOperatorId,
                Reason = "time box elapsed already",
                StartedAt = DateTime.UtcNow.AddMinutes(-40),
                ExpiresAt = DateTime.UtcNow.AddMinutes(-10),
            };
            seed.ImpersonationSessions.AddRange(live, ended, expired);
            seed.SaveChanges();
            liveId = live.Id;
            endedId = ended.Id;
            expiredId = expired.Id;
        }

        using var ctx = testDb.MakeContext(new FakeCurrentUser { IsSuperAdmin = true });

        Assert.True(await ImpersonationLiveness.IsLiveAsync(ctx, liveId, workspaceId, operatorId));
        Assert.False(
            await ImpersonationLiveness.IsLiveAsync(ctx, endedId, workspaceId, operatorId)
        );
        Assert.False(
            await ImpersonationLiveness.IsLiveAsync(ctx, expiredId, workspaceId, expiredOperatorId)
        );
        Assert.False(
            await ImpersonationLiveness.IsLiveAsync(ctx, liveId, wrongWorkspace, operatorId)
        );
        Assert.False(
            await ImpersonationLiveness.IsLiveAsync(ctx, liveId, workspaceId, wrongOperatorId)
        );
    }

    // ---------------------------------------------------------------------------
    // §6 test 10 — ProjectService.EnsureAsync / WorkspaceService.GetAsync read scope.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ProjectEnsure_Impersonating_ResolvesTargetProject_PlainOperator_NotFound()
    {
        var db = Guid.NewGuid().ToString();
        var workspaceId = Guid.NewGuid();
        using (var seed = InMemoryContext(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = workspaceId,
                    Name = "W",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = workspaceId,
                }
            );
            seed.Projects.Add(
                new Project
                {
                    Key = "target",
                    Name = "Target",
                    OwnerId = workspaceId,
                    IsActiveProduction = true,
                }
            );
            seed.SaveChanges();
        }

        var plainOperator = new FakeCurrentUser { IsSuperAdmin = true };
        using var plainCtx = InMemoryContext(plainOperator, db);
        var plainSvc = new ProjectService(
            new UnitOfWork(plainCtx),
            plainOperator,
            new PassThroughEntitlements(),
            TestProjectServiceDeps.Settings(),
            TestProjectServiceDeps.Configuration(),
            new FakeAuditWriter()
        );
        var plainResult = await plainSvc.EnsureAsync("target");
        Assert.True(plainResult.IsNotFound);

        var impersonating = new FakeCurrentUser
        {
            IsSuperAdmin = true,
            TenantId = workspaceId,
            ImpersonationSessionId = 1,
        };
        using var impCtx = InMemoryContext(impersonating, db);
        var impSvc = new ProjectService(
            new UnitOfWork(impCtx),
            impersonating,
            new PassThroughEntitlements(),
            TestProjectServiceDeps.Settings(),
            TestProjectServiceDeps.Configuration(),
            new FakeAuditWriter()
        );
        var impResult = await impSvc.EnsureAsync("target");
        Assert.True(impResult.IsSuccess);
    }

    [Fact]
    public async Task WorkspaceGet_Impersonating_ReturnsTarget()
    {
        var db = Guid.NewGuid().ToString();
        var workspaceId = Guid.NewGuid();
        using (var seed = InMemoryContext(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = workspaceId,
                    Name = "Target Workspace",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = workspaceId,
                }
            );
            seed.SaveChanges();
        }

        var plainOperator = new FakeCurrentUser { IsSuperAdmin = true };
        using var plainCtx = InMemoryContext(plainOperator, db);
        var plainResult = await new WorkspaceService(
            new UnitOfWork(plainCtx),
            plainOperator
        ).GetAsync();
        Assert.True(plainResult.IsForbidden);

        var impersonating = new FakeCurrentUser
        {
            IsSuperAdmin = true,
            TenantId = workspaceId,
            ImpersonationSessionId = 1,
        };
        using var impCtx = InMemoryContext(impersonating, db);
        var impResult = await new WorkspaceService(
            new UnitOfWork(impCtx),
            impersonating
        ).GetAsync();
        Assert.True(impResult.IsSuccess);
        Assert.Equal("Target Workspace", impResult.Data!.Name);
    }

    // ---------------------------------------------------------------------------
    // §6 test 11 — the request counter increments on a relational (Sqlite) provider.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task RequestCounter_IncrementsOnSqlite()
    {
        using var testDb = new TestDb();
        var operatorPublicId = Guid.NewGuid();
        Guid workspaceId;
        long sessionId;
        using (var seed = testDb.MakeContext(new FakeCurrentUser { IsSuperAdmin = true }))
        {
            (workspaceId, _) = SeedWorkspaceWithAdmin(seed);
            var session = new ImpersonationSession
            {
                OwnerId = workspaceId,
                OperatorUserId = operatorPublicId,
                Reason = "counter increment test run",
                StartedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(30),
            };
            seed.ImpersonationSessions.Add(session);
            seed.SaveChanges();
            sessionId = session.Id;
        }

        var impersonating = new FakeCurrentUser
        {
            Id = operatorPublicId,
            IsSuperAdmin = true,
            TenantId = workspaceId,
            ImpersonationSessionId = sessionId,
        };
        using var ctx = testDb.MakeContext(impersonating);
        var counter = new ImpersonationRequestCounter(
            impersonating,
            ctx,
            NullLogger<ImpersonationRequestCounter>.Instance
        );

        var httpContext = new DefaultHttpContext();
        var actionContext = new Microsoft.AspNetCore.Mvc.ActionContext(
            httpContext,
            new Microsoft.AspNetCore.Routing.RouteData(),
            new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()
        );
        var executingContext = new Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext(
            actionContext,
            new List<Microsoft.AspNetCore.Mvc.Filters.IFilterMetadata>(),
            new Dictionary<string, object?>(),
            controller: new object()
        );

        await counter.OnActionExecutionAsync(
            executingContext,
            () =>
                Task.FromResult(
                    new Microsoft.AspNetCore.Mvc.Filters.ActionExecutedContext(
                        actionContext,
                        new List<Microsoft.AspNetCore.Mvc.Filters.IFilterMetadata>(),
                        controller: new object()
                    )
                )
        );

        using var verify = testDb.MakeContext(new FakeCurrentUser { IsSuperAdmin = true });
        var row = verify.ImpersonationSessions.IgnoreQueryFilters().Single(s => s.Id == sessionId);
        Assert.Equal(1, row.RequestCount);
        Assert.NotNull(row.LastRequestAt);
    }

    // ---------------------------------------------------------------------------
    // §6 test 13 — SuggestionService.LoadOwnAsync: the operator never reviews content, even
    // impersonating (the fence blocks the write anyway); the workspace's own admin still can.
    // ---------------------------------------------------------------------------

    private static (
        Guid tenant,
        int projectId,
        Guid submitterId,
        int suggestionId
    ) SeedPendingSuggestion(AppDbContext db)
    {
        var tenant = Guid.NewGuid();
        db.Workspaces.Add(
            new Workspace
            {
                Id = tenant,
                Name = "SuggestWorkspace",
                CreatedAt = DateTime.UtcNow,
                CreatedBy = tenant,
            }
        );
        var project = new Project
        {
            Key = "sugproj",
            Name = "SugProj",
            OwnerId = tenant,
        };
        db.Projects.Add(project);
        db.SaveChanges();

        var submitterId = Guid.NewGuid();
        var suggestion = new PredefinedActionSuggestion
        {
            ProjectId = project.Id,
            Text = "Add a refund action",
            Prompt = "Issue the refund via the billing API",
            OwnerId = tenant,
            Status = SuggestionStatus.Pending,
            CreatedBy = submitterId,
        };
        db.PredefinedActionSuggestions.Add(suggestion);
        db.SaveChanges();

        return (tenant, project.Id, submitterId, suggestion.Id);
    }

    [Fact]
    public async Task Suggestions_PlainOperator_ApproveRejectRequestChanges_NotFound()
    {
        var db = Guid.NewGuid().ToString();
        Guid tenant;
        int suggestionId;
        using (var seed = InMemoryContext(new FakeCurrentUser { IsSuperAdmin = true }, db))
            (tenant, _, _, suggestionId) = SeedPendingSuggestion(seed);

        // Even impersonating: this is a WRITE, and the fence blocks it — the service itself refuses
        // any super admin, impersonating or not (a plain content read, unlike this review action, is
        // what the impersonation session is for).
        foreach (
            var operatorUser in new[]
            {
                new FakeCurrentUser { Id = Guid.NewGuid(), IsSuperAdmin = true },
                new FakeCurrentUser
                {
                    Id = Guid.NewGuid(),
                    IsSuperAdmin = true,
                    TenantId = tenant,
                    ImpersonationSessionId = 1,
                },
            }
        )
        {
            using var ctx = InMemoryContext(operatorUser, db);
            var svc = new SuggestionService(
                new UnitOfWork(ctx),
                operatorUser,
                new CapturingEmail()
            );

            var approve = await svc.ApproveAsync(suggestionId);
            Assert.True(approve.IsNotFound);
            var reject = await svc.RejectAsync(suggestionId);
            Assert.True(reject.IsNotFound);
            var changes = await svc.RequestChangesAsync(
                suggestionId,
                new Pointer.Application.DTOs.Suggestion.RequestChangesRequest
                {
                    Feedback = "please clarify",
                }
            );
            Assert.True(changes.IsNotFound);
        }

        using var verifyCtx = InMemoryContext(new FakeCurrentUser { IsSuperAdmin = true }, db);
        var row = verifyCtx
            .PredefinedActionSuggestions.IgnoreQueryFilters()
            .Single(s => s.Id == suggestionId);
        Assert.Equal(SuggestionStatus.Pending, row.Status);
    }

    [Fact]
    public async Task Suggestions_WorkspaceAdmin_StillApproves()
    {
        var dbA = Guid.NewGuid().ToString();
        Guid tenantA;
        int suggestionIdA;
        using (var seed = InMemoryContext(new FakeCurrentUser { IsSuperAdmin = true }, dbA))
            (tenantA, _, _, suggestionIdA) = SeedPendingSuggestion(seed);

        var adminA = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            IsAdmin = true,
            TenantId = tenantA,
        };
        using var ctxA = InMemoryContext(adminA, dbA);
        var svcA = new SuggestionService(new UnitOfWork(ctxA), adminA, new CapturingEmail());
        var approve = await svcA.ApproveAsync(suggestionIdA);
        Assert.True(approve.IsSuccess);

        // B's admin can never reach A's suggestion.
        var dbB = Guid.NewGuid().ToString();
        Guid tenantB;
        int suggestionIdB;
        using (var seed = InMemoryContext(new FakeCurrentUser { IsSuperAdmin = true }, dbB))
            (tenantB, _, _, suggestionIdB) = SeedPendingSuggestion(seed);

        var adminB = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            IsAdmin = true,
            TenantId = tenantB,
        };
        using var ctxCrossTenant = InMemoryContext(adminA, dbB);
        var crossSvc = new SuggestionService(
            new UnitOfWork(ctxCrossTenant),
            adminA,
            new CapturingEmail()
        );
        var crossApprove = await crossSvc.ApproveAsync(suggestionIdB);
        Assert.True(crossApprove.IsNotFound);
    }

    // ---------------------------------------------------------------------------
    // §6 test 13 — ProfileService: counts across workspaces for a plain operator (metadata), pinned
    // to the target workspace under impersonation.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Profile_PlainOperator_CountsAcrossWorkspaces()
    {
        var db = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var authorPublicId = Guid.NewGuid();
        int authorRowId;

        using (var seed = InMemoryContext(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = tenantA,
                    Name = "A",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = tenantA,
                }
            );
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = tenantB,
                    Name = "B",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = tenantB,
                }
            );
            var projectA = new Project
            {
                Key = "pa",
                Name = "PA",
                OwnerId = tenantA,
            };
            var projectB = new Project
            {
                Key = "pb",
                Name = "PB",
                OwnerId = tenantB,
            };
            seed.Projects.AddRange(projectA, projectB);
            var role = new Role
            {
                Name = "Member",
                GrantsAdmin = false,
                OwnerId = tenantA,
            };
            seed.Roles.Add(role);
            seed.SaveChanges();
            var author = new User
            {
                PublicId = authorPublicId,
                Email = "author@x.com",
                DisplayName = "Author",
                PasswordHash = "x",
                RoleId = role.Id,
                OwnerId = tenantA,
            };
            seed.Users.Add(author);
            seed.SaveChanges();
            authorRowId = author.Id;
            TestSeed.Join(seed, author, tenantA, role);

            seed.Comments.Add(
                new Comment
                {
                    ProjectId = projectA.Id,
                    Environment = EnvironmentTag.Staging,
                    Status = CommentStatus.Open,
                    AuthorId = authorPublicId,
                    Body = "in A",
                    OwnerId = tenantA,
                }
            );
            seed.Comments.Add(
                new Comment
                {
                    ProjectId = projectB.Id,
                    Environment = EnvironmentTag.Staging,
                    Status = CommentStatus.Open,
                    AuthorId = authorPublicId,
                    Body = "in B",
                    OwnerId = tenantB,
                }
            );
            seed.SaveChanges();
        }

        var plainOperator = new FakeCurrentUser { IsSuperAdmin = true };
        using var plainCtx = InMemoryContext(plainOperator, db);
        var plainProfile = new ProfileService(
            new UnitOfWork(plainCtx),
            new ApiKeyService(new UnitOfWork(plainCtx), new TestApiKeyProtector()),
            plainOperator
        );
        var plainResult = await plainProfile.GetByIdAsync(authorRowId);
        Assert.True(plainResult.IsSuccess);
        Assert.Equal(2, plainResult.Data!.Totals.Comments);

        var adminA = new FakeCurrentUser { IsAdmin = true, TenantId = tenantA };
        using var adminCtx = InMemoryContext(adminA, db);
        var adminProfile = new ProfileService(
            new UnitOfWork(adminCtx),
            new ApiKeyService(new UnitOfWork(adminCtx), new TestApiKeyProtector()),
            adminA
        );
        var adminResult = await adminProfile.GetByIdAsync(authorRowId);
        Assert.True(adminResult.IsSuccess);
        Assert.Equal(1, adminResult.Data!.Totals.Comments);

        var impersonatingA = new FakeCurrentUser
        {
            IsSuperAdmin = true,
            TenantId = tenantA,
            ImpersonationSessionId = 1,
        };
        using var impCtx = InMemoryContext(impersonatingA, db);
        var impProfile = new ProfileService(
            new UnitOfWork(impCtx),
            new ApiKeyService(new UnitOfWork(impCtx), new TestApiKeyProtector()),
            impersonatingA
        );
        var impResult = await impProfile.GetByIdAsync(authorRowId);
        Assert.True(impResult.IsSuccess);
        Assert.Equal(1, impResult.Data!.Totals.Comments);
    }

    // ---------------------------------------------------------------------------
    // §6 test 13 — the audit workspace view: impersonating operator reads the target's Security
    // log with full identity; the target's own admin never learns the operator's identity.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task AuditWorkspaceView_Impersonating_ReturnsTargetRows()
    {
        var db = Guid.NewGuid().ToString();
        var workspaceId = Guid.NewGuid();
        using (var seed = InMemoryContext(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = workspaceId,
                    Name = "W",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = workspaceId,
                }
            );
            seed.AuditEvents.Add(
                new AuditEvent
                {
                    OccurredAt = DateTime.UtcNow,
                    OwnerId = workspaceId,
                    ActorKind = Domain.Enums.AuditActorKind.Impersonation,
                    Action = AuditActions.ImpersonationStarted,
                    TargetType = AuditTargets.ImpersonationSession,
                    TargetId = "1",
                }
            );
            seed.SaveChanges();
        }

        var impersonating = new FakeCurrentUser
        {
            IsSuperAdmin = true,
            TenantId = workspaceId,
            ImpersonationSessionId = 1,
        };
        using var ctx = InMemoryContext(impersonating, db);
        var svc = new AuditQueryService(new UnitOfWork(ctx), impersonating);

        var result = await svc.ListForWorkspaceAsync(
            new Pointer.Application.DTOs.Audit.AuditQuery()
        );
        Assert.True(result.IsSuccess);
        Assert.Single(result.Data!.Items);
        // The caller IS the super admin/operator — actor identity is not redacted for them.
        Assert.NotEqual(
            Pointer.Application.DTOs.Audit.AuditEventDto.OperatorLabel,
            result.Data.Items[0].ActorName
        );
    }

    [Fact]
    public async Task AuditWorkspaceView_WorkspaceAdmin_SeesNoOperatorIdentity()
    {
        using var testDb = new TestDb();
        var operatorPublicId = Guid.NewGuid();
        Guid workspaceId;
        Guid adminId;
        using (var seed = testDb.MakeContext(new FakeCurrentUser { IsSuperAdmin = true }))
        {
            (workspaceId, adminId) = SeedWorkspaceWithAdmin(seed);
            SeedOperator(seed, operatorPublicId);
        }

        // Real end-to-end: StartAsync through a real AuditWriter so the row it writes carries the
        // correct ActorKind/ActorUserId resolved from ICurrentUser, exactly as production does.
        var opUser = new FakeCurrentUser
        {
            Id = operatorPublicId,
            IsAdmin = true,
            IsSuperAdmin = true,
        };
        using var ctxStart = testDb.MakeContext(opUser);
        var realAudit = new AuditWriter(
            ctxStart,
            opUser,
            new FakeHttpContextAccessor(),
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["JWT:SigningKey"] = "0123456789abcdef0123456789abcdef",
                    }
                )
                .Build(),
            NullLogger<AuditWriter>.Instance
        );
        var startSvc = BuildService(ctxStart, opUser, realAudit);
        var started = await startSvc.StartAsync(
            workspaceId,
            new StartImpersonationRequest { Reason = "verifying the D12.5 redaction", Minutes = 30 }
        );
        Assert.True(started.IsSuccess);

        var adminUser = new FakeCurrentUser
        {
            Id = adminId,
            IsAdmin = true,
            TenantId = workspaceId,
        };
        using var ctxAdmin = testDb.MakeContext(adminUser);
        var auditQuery = new AuditQueryService(new UnitOfWork(ctxAdmin), adminUser);
        var result = await auditQuery.ListForWorkspaceAsync(
            new Pointer.Application.DTOs.Audit.AuditQuery { Action = "impersonation." }
        );

        Assert.True(result.IsSuccess);
        Assert.NotEmpty(result.Data!.Items);
        Assert.All(
            result.Data.Items,
            i =>
            {
                Assert.Equal(
                    Pointer.Application.DTOs.Audit.AuditEventDto.OperatorLabel,
                    i.ActorName
                );
                Assert.Null(i.ActorUserId);
            }
        );
    }
}
