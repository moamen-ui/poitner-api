using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Audit;

namespace Pointer.Tests;

/// <summary>
/// DB-12 §6 test 2 — the writer mechanism itself (PART 1: no service calls the writer yet; the
/// call sites are PART 2). Proves the row written from a fake HTTP context carries request id,
/// the keyed IP hash, a truncated user agent, sanitised before/after, the resolved actor
/// membership, and that an unknown action is a bug, not an audit row.
/// </summary>
public class AuditWriterTests
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

    private sealed class FakeHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class TestDb : IDisposable
    {
        private readonly SqliteConnection _keepAlive;
        private readonly string _connectionString;

        public TestDb()
        {
            _connectionString = $"DataSource=file:{Guid.NewGuid():N}?mode=memory&cache=shared";
            _keepAlive = new SqliteConnection(_connectionString);
            _keepAlive.Open();

            using var bootstrap = MakeContext();
            bootstrap.Database.EnsureCreated();
        }

        public AppDbContext MakeContext(
            ICurrentUser? user = null,
            IInterceptor? extraInterceptor = null
        )
        {
            var builder = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_connectionString)
                .AddInterceptors(new SqliteBtrimFunctionInterceptor());
            if (extraInterceptor is not null)
                builder.AddInterceptors(extraInterceptor);
            return new(
                builder.Options,
                user ?? new FakeCurrentUser { IsSuperAdmin = true },
                new ConfigurationBuilder().Build()
            );
        }

        public void Dispose() => _keepAlive.Dispose();
    }

    /// <summary>
    /// Deterministically fails any <c>SaveChanges(Async)</c> that would insert an <see
    /// cref="AuditEvent"/> — simulating finding #1's "audit insert fails" without depending on a
    /// provider actually enforcing a column max length (SQLite does not).
    /// </summary>
    private sealed class FailOnAuditEventInsertInterceptor : SaveChangesInterceptor
    {
        private static void ThrowIfAuditEventPending(DbContext? context)
        {
            if (
                context?.ChangeTracker.Entries<AuditEvent>().Any(e => e.State == EntityState.Added)
                == true
            )
                throw new InvalidOperationException("simulated audit write failure (test)");
        }

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData,
            InterceptionResult<int> result
        )
        {
            ThrowIfAuditEventPending(eventData.Context);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
        {
            ThrowIfAuditEventPending(eventData.Context);
            return ValueTask.FromResult(result);
        }
    }

    private static IConfiguration Config(params (string Key, string Value)[] pairs)
    {
        var builder = new ConfigurationBuilder();
        foreach (var (key, value) in pairs)
            builder.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value });
        return builder.Build();
    }

    private static DefaultHttpContext HttpContextWith(
        string requestId = "req-1234-abcd-5678",
        string userAgent = "agent"
    )
    {
        var http = new DefaultHttpContext();
        http.Items[AuditWriter.RequestIdItemKey] = requestId; // == RequestIdMiddleware.ItemKey (Tests/RequestIdMiddlewareTests.cs)
        http.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        http.Request.Headers.UserAgent = userAgent;
        return http;
    }

    /// <summary>Seed (workspace, identity, live membership) so the writer can resolve actor_membership_id from (sub, tenant).</summary>
    private static async Task<(
        Guid Workspace,
        Guid ActorPublicId,
        int MembershipId
    )> SeedMembershipAsync(AppDbContext seed)
    {
        var workspace = Guid.NewGuid();
        var actorPublicId = Guid.NewGuid();

        // Sqlite enforces FKs — the identity's role_id and the membership's role_id need a Role
        // row, SAVED FIRST so the identity-generated role.Id exists before it is referenced.
        var role = new Role
        {
            Name = "Workspace Admin",
            GrantsAdmin = true,
            IsActive = true,
        };
        seed.Roles.Add(role);
        await seed.SaveChangesAsync();

        seed.Workspaces.Add(
            new Workspace
            {
                Id = workspace,
                Name = "Workspace",
                CreatedAt = DateTime.UtcNow,
                CreatedBy = Guid.NewGuid(),
            }
        );
        var user = new User
        {
            PublicId = actorPublicId,
            Email = "actor@example.com",
            PasswordHash = "h",
            DisplayName = "Actor",
            RoleId = role.Id,
            OwnerId = workspace,
        };
        seed.Users.Add(user);
        await seed.SaveChangesAsync();

        var membership = new WorkspaceMembership
        {
            UserId = user.Id,
            OwnerId = workspace,
            RoleId = role.Id,
            SecurityStamp = Guid.NewGuid(),
            JoinedAt = DateTime.UtcNow.AddDays(-1),
        };
        seed.WorkspaceMemberships.Add(membership);
        await seed.SaveChangesAsync();

        return (workspace, actorPublicId, membership.Id);
    }

    [Fact]
    public async Task WriteAsync_PersistsFullRow_FromHttpContext()
    {
        using var db = new TestDb();

        Guid workspace,
            actorPublicId;
        int membershipId;
        using (var seed = db.MakeContext())
        {
            (workspace, actorPublicId, membershipId) = await SeedMembershipAsync(seed);
        }

        var http = HttpContextWith(
            requestId: "req-1234-abcd-5678",
            userAgent: new string('U', 400)
        );
        var accessor = new FakeHttpContextAccessor { HttpContext = http };

        var actor = new FakeCurrentUser
        {
            Id = actorPublicId,
            TenantId = workspace,
            IsSuperAdmin = false,
        };
        using (var ctx = db.MakeContext(actor))
        {
            var writer = new AuditWriter(
                ctx,
                actor,
                accessor,
                Config(("JWT:SigningKey", "0123456789abcdef0123456789abcdef")),
                NullLogger<AuditWriter>.Instance
            );

            await writer.WriteAsync(
                new AuditEntry(
                    Action: AuditActions.WorkspaceRenamed,
                    TargetType: AuditTargets.Workspace,
                    TargetId: workspace.ToString(),
                    OwnerId: workspace,
                    Before: new Dictionary<string, string>
                    {
                        ["name"] = "Old name",
                        ["email"] = "someone@example.com", // not whitelisted — must be dropped
                    },
                    After: new Dictionary<string, string> { ["name"] = new string('n', 250) } // truncated to 200
                )
            );

            Assert.True((bool)http.Items[AuditWriter.WrittenItemKey]);
        }

        using var verify = db.MakeContext();
        var row = verify.AuditEvents.IgnoreQueryFilters().Single();

        Assert.Equal(AuditActions.WorkspaceRenamed, row.Action);
        Assert.Equal(AuditTargets.Workspace, row.TargetType);
        Assert.Equal(workspace, row.OwnerId);
        Assert.Equal(actorPublicId, row.ActorUserId);
        Assert.Equal(membershipId, row.ActorMembershipId);
        Assert.Equal(AuditActorKind.User, row.ActorKind);
        Assert.Equal("req-1234-abcd-5678", row.RequestId);
        Assert.Matches(new Regex("^[0-9a-f]{64}$"), row.IpHash ?? "");
        Assert.Equal(256, row.UserAgent?.Length);
        // Sanitised: unknown key dropped, value truncated to 200.
        Assert.Equal(new Dictionary<string, string> { ["name"] = "Old name" }, row.Before);
        Assert.Equal(200, row.After["name"].Length);
    }

    [Fact]
    public async Task WriteAsync_SuperAdminActor_NoMembershipResolution()
    {
        using var db = new TestDb();

        Guid workspace,
            actorPublicId;
        int membershipId;
        using (var seed = db.MakeContext())
        {
            (workspace, actorPublicId, membershipId) = await SeedMembershipAsync(seed);
        }

        using (var ctx = db.MakeContext())
        {
            var superAdmin = new FakeCurrentUser { Id = actorPublicId, IsSuperAdmin = true };
            var writer = new AuditWriter(
                ctx,
                superAdmin,
                new FakeHttpContextAccessor { HttpContext = HttpContextWith() },
                Config(),
                NullLogger<AuditWriter>.Instance
            );

            await writer.WriteAsync(
                new AuditEntry(
                    AuditActions.TenantStatusChanged,
                    AuditTargets.Membership,
                    membershipId.ToString(),
                    workspace
                )
            );
        }

        using var verify = db.MakeContext();
        var row = verify.AuditEvents.IgnoreQueryFilters().Single();
        Assert.Equal(AuditActorKind.SuperAdmin, row.ActorKind);
        Assert.Equal(actorPublicId, row.ActorUserId);
        // §3.4: a super admin acting on operator surfaces carries no membership.
        Assert.Null(row.ActorMembershipId);
    }

    [Fact]
    public async Task WriteAsync_UnknownAction_ThrowsArgumentException_AndWritesNothing()
    {
        using var db = new TestDb();
        var accessor = new FakeHttpContextAccessor { HttpContext = HttpContextWith() };

        using var ctx = db.MakeContext();
        var writer = new AuditWriter(
            ctx,
            new FakeCurrentUser { IsSuperAdmin = true },
            accessor,
            Config(),
            NullLogger<AuditWriter>.Instance
        );

        await Assert.ThrowsAsync<ArgumentException>(() =>
            writer.WriteAsync(new AuditEntry("typo.action", AuditTargets.Workspace, null, null))
        );

        using var verify = db.MakeContext();
        Assert.Empty(verify.AuditEvents.IgnoreQueryFilters());
    }

    [Fact]
    public async Task WriteAsync_SaveFails_SwallowsByDefault()
    {
        using var db = new TestDb();
        var accessor = new FakeHttpContextAccessor { HttpContext = HttpContextWith() };

        var ctx = db.MakeContext();
        await ctx.DisposeAsync(); // the save must fail

        var writer = new AuditWriter(
            ctx,
            new FakeCurrentUser { IsSuperAdmin = true },
            accessor,
            Config(),
            NullLogger<AuditWriter>.Instance
        );

        // D12.1: best-effort — no throw, the (already committed) mutation stands.
        await writer.WriteAsync(
            new AuditEntry(AuditActions.SettingsUpdated, AuditTargets.Settings, "global", null)
        );
    }

    [Fact]
    public async Task WriteAsync_SaveFails_ThrowsWhenFailClosed()
    {
        using var db = new TestDb();
        var accessor = new FakeHttpContextAccessor { HttpContext = HttpContextWith() };

        var ctx = db.MakeContext();
        await ctx.DisposeAsync();

        var writer = new AuditWriter(
            ctx,
            new FakeCurrentUser { IsSuperAdmin = true },
            accessor,
            Config(("Audit:FailClosed", "true")),
            NullLogger<AuditWriter>.Instance
        );

        await Assert.ThrowsAnyAsync<Exception>(() =>
            writer.WriteAsync(
                new AuditEntry(AuditActions.SettingsUpdated, AuditTargets.Settings, "global", null)
            )
        );
    }

    /// <summary>
    /// Review finding #1 (HIGH): a failed audit insert must not poison the rest of the request's
    /// DbContext. Uses a SHARED, NOT DISPOSED context with an interceptor that deterministically
    /// fails any save carrying a pending <see cref="AuditEvent"/> — before the fix, the still-Added
    /// row would be resubmitted (and fail again) on the very next, otherwise-unrelated save.
    /// </summary>
    [Fact]
    public async Task WriteAsync_SaveFails_DetachesFailedRow_DoesNotPoisonSharedContext()
    {
        using var db = new TestDb();
        var accessor = new FakeHttpContextAccessor { HttpContext = HttpContextWith() };

        using var ctx = db.MakeContext(extraInterceptor: new FailOnAuditEventInsertInterceptor());
        var writer = new AuditWriter(
            ctx,
            new FakeCurrentUser { IsSuperAdmin = true },
            accessor,
            Config(),
            NullLogger<AuditWriter>.Instance
        );

        // D12.1 best-effort: the write fails (interceptor throws), is logged, and swallowed.
        await writer.WriteAsync(
            new AuditEntry(AuditActions.SettingsUpdated, AuditTargets.Settings, "global", null)
        );

        // The failed row must be detached, never left tracked as Added.
        Assert.Empty(ctx.ChangeTracker.Entries<AuditEvent>());

        // A later, UNRELATED SaveChangesAsync on the SAME context must succeed. Before the fix,
        // the still-Added AuditEvent would be resubmitted alongside this change and the
        // interceptor (which fails any save carrying a pending AuditEvent) would fail it again.
        ctx.Roles.Add(new Role { Name = "Unrelated", IsActive = true });
        await ctx.SaveChangesAsync();
    }

    /// <summary>Review finding #7 (LOW): blank/whitespace counts as "not configured" for both
    /// Audit:HashKey and its JWT:SigningKey fallback — with neither set, ip_hash is stored NULL
    /// rather than HMAC'd with an empty key.</summary>
    [Fact]
    public async Task WriteAsync_BlankHashKeyAndSigningKey_StoresNullIpHash()
    {
        using var db = new TestDb();
        var accessor = new FakeHttpContextAccessor { HttpContext = HttpContextWith() };

        using (var ctx = db.MakeContext())
        {
            var writer = new AuditWriter(
                ctx,
                new FakeCurrentUser { IsSuperAdmin = true },
                accessor,
                Config(("Audit:HashKey", "   "), ("JWT:SigningKey", "")),
                NullLogger<AuditWriter>.Instance
            );

            await writer.WriteAsync(
                new AuditEntry(AuditActions.SettingsUpdated, AuditTargets.Settings, "global", null)
            );
        }

        using var verify = db.MakeContext();
        var row = verify.AuditEvents.IgnoreQueryFilters().Single();
        Assert.Null(row.IpHash);
    }

    /// <summary>Review finding #8 (LOW): TargetId/TargetType are clamped to the mapped column
    /// lengths (128/64) by the writer itself — the same Truncate helper used for UserAgent — so an
    /// oversized value never depends on the provider enforcing HasMaxLength.</summary>
    [Fact]
    public async Task WriteAsync_ClampsTargetTypeAndTargetIdToMappedLengths()
    {
        using var db = new TestDb();
        var accessor = new FakeHttpContextAccessor { HttpContext = HttpContextWith() };

        using (var ctx = db.MakeContext())
        {
            var writer = new AuditWriter(
                ctx,
                new FakeCurrentUser { IsSuperAdmin = true },
                accessor,
                Config(),
                NullLogger<AuditWriter>.Instance
            );

            await writer.WriteAsync(
                new AuditEntry(
                    Action: AuditActions.SettingsUpdated,
                    TargetType: new string('t', 100), // mapped max is 64
                    TargetId: new string('i', 200), // mapped max is 128
                    OwnerId: null
                )
            );
        }

        using var verify = db.MakeContext();
        var row = verify.AuditEvents.IgnoreQueryFilters().Single();
        Assert.Equal(64, row.TargetType.Length);
        Assert.Equal(128, row.TargetId?.Length);
    }

    [Fact]
    public async Task WriteAsync_SystemOverride_NoHttpContext_NullActorAndRequestFacts()
    {
        using var db = new TestDb();
        var accessor = new FakeHttpContextAccessor { HttpContext = null }; // hosted job

        using (var ctx = db.MakeContext())
        {
            var writer = new AuditWriter(
                ctx,
                new FakeCurrentUser(), // Id null — hosted jobs have no authenticated user
                accessor,
                Config(),
                NullLogger<AuditWriter>.Instance
            );

            await writer.WriteAsync(
                new AuditEntry(
                    Action: AuditActions.TenantHardDeleted,
                    TargetType: AuditTargets.Workspace,
                    TargetId: Guid.NewGuid().ToString(),
                    OwnerId: null,
                    After: new Dictionary<string, string> { ["reason"] = "demo_expired" },
                    ActorKindOverride: AuditActorKind.System
                )
            );
        }

        using var verify = db.MakeContext();
        var row = verify.AuditEvents.IgnoreQueryFilters().Single();

        Assert.Equal(AuditActorKind.System, row.ActorKind);
        Assert.Null(row.ActorUserId);
        Assert.Null(row.ActorMembershipId);
        Assert.Null(row.RequestId);
        Assert.Null(row.IpHash);
        Assert.Null(row.UserAgent);
        Assert.Equal(new Dictionary<string, string> { ["reason"] = "demo_expired" }, row.After);
    }
}
