using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.User;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-11c review finding #10 — <c>IdentityEraseService.EraseAsync</c> stages every mutation (end
/// memberships, hard-delete secrets, scrub invites, tombstone the row) and flushes them all with ONE
/// <c>SaveChangesAsync()</c> call inside <c>ExecuteInTransactionAsync</c> (see UserService/
/// IdentityEraseService review finding #5 comments). This is a regression guard for that atomicity:
/// it forces that single save to fail (a <see cref="SaveChangesInterceptor"/> that throws once it
/// sees the staged <see cref="ApiKey"/> deletion — i.e. "after the secrets are deleted" in the
/// in-memory change tracker, before anything is actually persisted) and asserts NOTHING landed —
/// same Sqlite shared-cache fixture as <see cref="WorkspaceBeforeIdentityOrderingTests"/> (enforces
/// real constraints, unlike the InMemory provider the rest of the erase tests use).
/// </summary>
public class EraseAtomicityTests
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

    private sealed class IdentityHasher : IPasswordHasher
    {
        public string Hash(string password) => "h:" + password;
        public bool Verify(string password, string hash) => hash == "h:" + password;
    }

    private sealed class FakeReset : IResetTokenService
    {
        public string Create(Guid id, Guid stamp) => "r";

        public bool TryValidate(string token, out Guid id, out Guid stamp)
        {
            id = Guid.Empty;
            stamp = Guid.Empty;
            return false;
        }

        public string CreateScoped(Guid id, Guid stamp, string purpose, string? payload = null) => "r";

        public bool TryValidateScoped(string token, string purpose, out Guid id, out Guid stamp, out string? payload)
        {
            id = Guid.Empty;
            stamp = Guid.Empty;
            payload = null;
            return false;
        }
    }

    private sealed class NoopEmail : IEmailService
    {
        public Task<bool> SendAsync(string to, string subject, string html, CancellationToken ct = default) =>
            Task.FromResult(true);
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
                Pointer.Application.Response.Result<Pointer.Application.DTOs.Branding.BrandingResponse>.Success(Response())
            );

        public Task<Pointer.Application.Response.Result<Pointer.Application.DTOs.Branding.BrandingResponse>> UpdateAsync(
            Pointer.Application.DTOs.Branding.BrandingWriteDto dto,
            string publicBase,
            IReadOnlySet<string> existingKinds
        ) =>
            Task.FromResult(
                Pointer.Application.Response.Result<Pointer.Application.DTOs.Branding.BrandingResponse>.Success(Response())
            );

        public Task<int> BumpVersionAsync() => Task.FromResult(0);

        public Task<Pointer.Application.DTOs.Branding.BrandingResponse> BuildResponseAsync(
            string publicBase,
            IReadOnlySet<string> existingKinds
        ) => Task.FromResult(Response());
    }

    /// <summary>
    /// Throws as soon as the change tracker shows a staged <see cref="ApiKey"/> deletion — i.e.
    /// right when <c>IdentityEraseService.EraseAsync</c>'s single <c>SaveChangesAsync()</c> call
    /// tries to flush everything it staged (memberships ended, secrets marked deleted, invites
    /// scrubbed, identity tombstoned) — simulating a failure that lands after the app code has
    /// "deleted" the secrets in memory but before any of it reaches the database.
    /// </summary>
    private sealed class ThrowOnApiKeyDeletionInterceptor : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            ThrowIfApiKeyDeletionStaged(eventData.Context);
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
        {
            ThrowIfApiKeyDeletionStaged(eventData.Context);
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private static void ThrowIfApiKeyDeletionStaged(Microsoft.EntityFrameworkCore.DbContext? context)
        {
            if (context is null)
                return;

            var hasStagedApiKeyDeletion = context
                .ChangeTracker.Entries<ApiKey>()
                .Any(e => e.State == EntityState.Deleted);

            if (hasStagedApiKeyDeletion)
                throw new InvalidOperationException("Simulated failure mid-erase (DB-11c review finding #10).");
        }
    }

    /// <summary>Sqlite shared-cache fixture (enforces FKs and unique indexes, unlike InMemory).</summary>
    private sealed class TestDb : IDisposable
    {
        private readonly SqliteConnection _keepAlive;
        private readonly string _connectionString;

        public TestDb()
        {
            _connectionString = $"DataSource=file:{Guid.NewGuid():N}?mode=memory&cache=shared";
            _keepAlive = new SqliteConnection(_connectionString);
            _keepAlive.Open();

            using var bootstrap = MakeContext(new FakeCurrentUser { IsSuperAdmin = true }, failing: false);
            bootstrap.Database.EnsureCreated();
        }

        public AppDbContext MakeContext(ICurrentUser user, bool failing) =>
            new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite(_connectionString)
                    .AddInterceptors(
                        failing
                            ? new IInterceptor[] { new SqliteBtrimFunctionInterceptor(), new ThrowOnApiKeyDeletionInterceptor() }
                            : new IInterceptor[] { new SqliteBtrimFunctionInterceptor() }
                    )
                    .Options,
                user,
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
            );

        public void Dispose() => _keepAlive.Dispose();
    }

    private static IdentityEraseService BuildEraseService(ICurrentUser user, AppDbContext ctx)
    {
        var uow = new UnitOfWork(ctx);
        return new IdentityEraseService(
            uow,
            new MembershipService(uow),
            user,
            new IdentityHasher(),
            new FakeReset(),
            new NoopEmail(),
            new NoopBrandingService()
        );
    }

    [Fact]
    public async Task Erase_FailureMidTransaction_LeavesIdentityAndSecretsUnchanged()
    {
        using var db = new TestDb();
        var workspaceId = Guid.NewGuid();
        Guid memberPublicId;
        int memberRowId;
        string originalPasswordHash;

        using (var seed = db.MakeContext(new FakeCurrentUser { IsSuperAdmin = true }, failing: false))
        {
            var role = new Role { Name = "Engineer", GrantsAdmin = false, IsSystem = false, IsActive = true };
            seed.Roles.Add(role);
            seed.SaveChanges();

            seed.Set<Workspace>()
                .Add(new Workspace { Id = workspaceId, Name = "Atomicity Co", CreatedAt = DateTime.UtcNow, CreatedBy = workspaceId });

            originalPasswordHash = "h:MemberPass1!";
            var member = new User
            {
                PublicId = Guid.NewGuid(),
                Email = "member@atomicity.test",
                PasswordHash = originalPasswordHash,
                DisplayName = "Member",
                RoleId = role.Id,
                IsActive = true,
            };
            seed.Users.Add(member);
            seed.SaveChanges();
            memberPublicId = member.PublicId;
            memberRowId = member.Id;

            seed.Set<WorkspaceMembership>()
                .Add(
                    new WorkspaceMembership
                    {
                        UserId = member.Id,
                        OwnerId = workspaceId,
                        RoleId = role.Id,
                        IsActive = true,
                        ApprovalStatus = ApprovalStatus.Approved,
                        SecurityStamp = Guid.NewGuid(),
                        JoinedAt = DateTime.UtcNow,
                    }
                );

            seed.ApiKeys.Add(
                new ApiKey
                {
                    UserId = member.Id,
                    OwnerId = workspaceId,
                    Hash = "atomicity-hash",
                    Encrypted = "e",
                    Prefix = "ptr_atomic",
                }
            );

            seed.SaveChanges();
        }

        var caller = new FakeCurrentUser { Id = memberPublicId, TenantId = workspaceId };
        using (var failingCtx = db.MakeContext(caller, failing: true))
        {
            var svc = BuildEraseService(caller, failingCtx);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.EraseSelfAsync(new DeleteMyAccountRequest { Password = "MemberPass1!" })
            );
        }

        // A fresh, non-failing context proves the transaction rolled back completely: identity,
        // membership and secret all exactly as seeded.
        using var check = db.MakeContext(new FakeCurrentUser { IsSuperAdmin = true }, failing: false);

        var userRow = check.Users.IgnoreQueryFilters().Single(u => u.Id == memberRowId);
        Assert.Equal("member@atomicity.test", userRow.Email);
        Assert.Equal("Member", userRow.DisplayName);
        Assert.Equal(originalPasswordHash, userRow.PasswordHash);
        Assert.Null(userRow.ErasedAt);
        Assert.Null(userRow.DeletedAt);

        var membershipRow = check.WorkspaceMemberships.IgnoreQueryFilters().Single(m => m.UserId == memberRowId && m.OwnerId == workspaceId);
        Assert.Null(membershipRow.LeftAt);
        Assert.True(membershipRow.IsActive);

        var apiKeyRow = check.ApiKeys.IgnoreQueryFilters().Single(k => k.UserId == memberRowId);
        Assert.Null(apiKeyRow.RevokedAt);
        Assert.Null(apiKeyRow.DeletedAt);
    }
}
