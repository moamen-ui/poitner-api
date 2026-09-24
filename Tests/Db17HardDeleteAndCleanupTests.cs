using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pointer.API.Hosted;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Billing;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-17 §3.3/§3.5 (Gemini Pro #2): <c>TenantService.HardDeleteAsync</c>'s demo_expired pre-check
/// (refuses a converted/extended workspace before the folder delete) and
/// <c>DemoCleanupService.SweepOnceAsync</c>'s own expiry query + per-item re-check. InMemory
/// provider throughout — <see cref="IUnitOfWork.ExecuteSqlRawAsync"/>'s `FOR UPDATE` no-ops there
/// (it is Postgres-only syntax; the real re-check under lock is proven in the R11 rehearsal, §9).
///
/// DB-17 review (post-`cb2180a`) findings #1 (HIGH) and #2 (MEDIUM) added: <c>SweepOnceAsync</c>
/// now takes an <see cref="IServiceScopeFactory"/> and resolves a fresh <see cref="IUnitOfWork"/>/
/// <see cref="ITenantService"/> PER ITEM (never one shared across the whole sweep), and
/// <c>HardDeleteAsync</c>'s <c>demo_expired</c> path writes its audit row / deletes files only
/// after the locked re-check passes.
/// </summary>
public class Db17HardDeleteAndCleanupTests
{
    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; } = true;
        public bool IsQuickAccess { get; set; }
        public Guid? TenantId { get; set; }
        public int? RoleId { get; set; }
        public string? KeyScopes { get; set; }
        public string? Scope { get; set; }
        public long? ImpersonationSessionId { get; set; }
        public bool IsImpersonating => ImpersonationSessionId != null;
    }

    private sealed class FakePasswordHasher : IPasswordHasher
    {
        public string Hash(string password) => "hashed:" + password;

        public bool Verify(string password, string hash) => hash == "hashed:" + password;
    }

    private sealed class FakeSettings : ISettingsService
    {
        public Task<bool> GetBoolAsync(string key, bool fallback = false) =>
            Task.FromResult(fallback);

        public Task SetBoolAsync(string key, bool value) => Task.CompletedTask;

        public Task<string> GetStringAsync(string key, string fallback = "") =>
            Task.FromResult(fallback);

        public Task SetStringAsync(string key, string value) => Task.CompletedTask;

        public Task<int> GetIntAsync(string key, int fallback = 0) => Task.FromResult(fallback);

        public Task SetIntAsync(string key, int value) => Task.CompletedTask;
    }

    /// <summary>Records every workspace segment passed to DeleteOwnerFilesAsync — proves a refused
    /// demo_expired delete never touches the filesystem.</summary>
    private sealed class RecordingFileStorage : IFileStorage
    {
        public List<string> DeletedOwners { get; } = [];

        public Task<string> SaveAsync(
            string ownerSegment,
            string project,
            Stream content,
            string extension
        ) => Task.FromResult("");

        public Task DeleteAsync(string relativePathOrUrl) => Task.CompletedTask;

        public Task DeleteOwnerFilesAsync(string ownerSegment)
        {
            DeletedOwners.Add(ownerSegment);
            return Task.CompletedTask;
        }
    }

    private static AppDbContext Ctx(string db) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(db)
                .ConfigureWarnings(w =>
                    w.Ignore(
                        Microsoft
                            .EntityFrameworkCore
                            .Diagnostics
                            .InMemoryEventId
                            .TransactionIgnoredWarning
                    )
                )
                .Options,
            new FakeCurrentUser(),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
        );

    private static TenantService BuildTenantService(
        IUnitOfWork uow,
        RecordingFileStorage files,
        FakeAuditWriter audit
    ) =>
        new(
            uow,
            new FakePasswordHasher(),
            files,
            new FakeSettings(),
            new NoopBillingProvider(),
            new MembershipService(uow),
            audit
        );

    private static Guid SeedWorkspace(
        AppDbContext db,
        DateTime? demoExpiresAt,
        DateTime? demoConvertedAt = null
    )
    {
        var workspaceId = Guid.NewGuid();
        db.Workspaces.Add(
            new Workspace
            {
                Id = workspaceId,
                Name = "Demo Workspace",
                CreatedAt = DateTime.UtcNow,
                CreatedBy = workspaceId,
                DemoExpiresAt = demoExpiresAt,
                DemoConvertedAt = demoConvertedAt,
            }
        );
        db.SaveChanges();
        return workspaceId;
    }

    // ── HardDeleteAsync's demo_expired pre-check ────────────────────────────────────────────

    [Fact]
    public async Task HardDelete_DemoExpiredReason_RefusesConvertedWorkspace_NoFolderDelete()
    {
        var db = Ctx(nameof(HardDelete_DemoExpiredReason_RefusesConvertedWorkspace_NoFolderDelete));
        // Converted (or extended) — DemoExpiresAt is null, so it is no longer "an expired demo".
        var workspaceId = SeedWorkspace(db, demoExpiresAt: null, demoConvertedAt: DateTime.UtcNow);
        var files = new RecordingFileStorage();
        var audit = new FakeAuditWriter();
        var svc = BuildTenantService(new UnitOfWork(db), files, audit);

        var result = await svc.HardDeleteAsync(workspaceId, reason: "demo_expired");

        Assert.False(result.IsSuccess);
        Assert.Empty(files.DeletedOwners);
        Assert.DoesNotContain(audit.Entries, e => e.Action == AuditActions.TenantHardDeleted);
        Assert.NotNull(
            db.Workspaces.IgnoreQueryFilters().SingleOrDefault(w => w.Id == workspaceId)
        );
    }

    [Fact]
    public async Task HardDelete_AdminReason_UnaffectedByDemoState()
    {
        // A converted/live workspace is fair game for an ordinary admin-initiated delete.
        var db = Ctx(nameof(HardDelete_AdminReason_UnaffectedByDemoState));
        var workspaceId = SeedWorkspace(db, demoExpiresAt: null, demoConvertedAt: DateTime.UtcNow);
        var files = new RecordingFileStorage();
        var audit = new FakeAuditWriter();
        var svc = BuildTenantService(new UnitOfWork(db), files, audit);

        var result = await svc.HardDeleteAsync(workspaceId);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Single(files.DeletedOwners);
        Assert.Contains(audit.Entries, e => e.Action == AuditActions.TenantHardDeleted);
    }

    [Fact]
    public async Task HardDelete_DemoExpiredReason_DeletesActuallyExpiredDemo()
    {
        var db = Ctx(nameof(HardDelete_DemoExpiredReason_DeletesActuallyExpiredDemo));
        var workspaceId = SeedWorkspace(db, demoExpiresAt: DateTime.UtcNow.AddHours(-1));
        var files = new RecordingFileStorage();
        var audit = new FakeAuditWriter();
        var svc = BuildTenantService(new UnitOfWork(db), files, audit);

        var result = await svc.HardDeleteAsync(workspaceId, reason: "demo_expired");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Single(files.DeletedOwners);
        var entry = Assert.Single(audit.Entries, e => e.Action == AuditActions.TenantHardDeleted);
        Assert.Null(entry.OwnerId);
    }

    /// <summary>
    /// DB-17 review finding #2 (MEDIUM): an extension committing between the pre-check (before the
    /// transaction) and the `FOR UPDATE` lock (the transaction's first statement) must abort the
    /// delete — and, since the review, must leave no audit row and no filesystem delete either
    /// (both moved to fire only after the locked re-check passes). Models the race with a decorator
    /// around a real <see cref="UnitOfWork"/> that mutates the workspace — via its OWN separate
    /// <see cref="AppDbContext"/>, exactly as a concurrent request would — the instant
    /// <see cref="IUnitOfWork.ExecuteSqlRawAsync"/> (the `FOR UPDATE` call) fires.
    /// </summary>
    [Fact]
    public async Task HardDelete_DemoExpiredReason_ExtensionCommitsBetweenPrecheckAndLock_NoFilesNoAudit()
    {
        var dbName = nameof(
            HardDelete_DemoExpiredReason_ExtensionCommitsBetweenPrecheckAndLock_NoFilesNoAudit
        );
        var db = Ctx(dbName);
        var workspaceId = SeedWorkspace(db, demoExpiresAt: DateTime.UtcNow.AddHours(-1));
        var files = new RecordingFileStorage();
        var audit = new FakeAuditWriter();
        var racyUow = new ExtendingOnLockUnitOfWork(new UnitOfWork(db), dbName, workspaceId);
        var svc = BuildTenantService(racyUow, files, audit);

        // HardDeleteAsync's locked re-check throwing propagates OUT of ExecuteInTransactionAsync
        // uncaught (by design — the hosted sweep loop's own try/catch is what's meant to catch it,
        // §3.5) — never converted into a Result.Failure here. DB-18 code review (Opus MEDIUM): now
        // a dedicated DeletionPreconditionChangedException (still an InvalidOperationException, so
        // WorkspaceDeletionService's own generic-Exception handler stays a safety net either way).
        await Assert.ThrowsAsync<DeletionPreconditionChangedException>(() =>
            svc.HardDeleteAsync(workspaceId, reason: "demo_expired")
        );

        Assert.Empty(files.DeletedOwners);
        Assert.DoesNotContain(audit.Entries, e => e.Action == AuditActions.TenantHardDeleted);
        Assert.NotNull(
            db.Workspaces.IgnoreQueryFilters().SingleOrDefault(w => w.Id == workspaceId)
        );
    }

    /// <summary>Decorates a real <see cref="UnitOfWork"/>, mutating the workspace (via a brand-new
    /// <see cref="AppDbContext"/> against the SAME named InMemory database — modelling a genuinely
    /// separate concurrent connection) the instant <see cref="ExecuteSqlRawAsync"/> — the `FOR
    /// UPDATE` lock statement — is called, i.e. between the pre-check and the locked re-check.</summary>
    private sealed class ExtendingOnLockUnitOfWork(
        UnitOfWork inner,
        string dbName,
        Guid workspaceId
    ) : IUnitOfWork
    {
        public IRepository<T> Repository<T>()
            where T : BaseEntity => inner.Repository<T>();

        public DbSet<UsageEvent> UsageEvents => inner.UsageEvents;
        public DbSet<UsageDaily> UsageDaily => inner.UsageDaily;
        public DbSet<Workspace> Workspaces => inner.Workspaces;
        public DbSet<UserAlias> UserAliases => inner.UserAliases;
        public DbSet<AuditEvent> AuditEvents => inner.AuditEvents;
        public DbSet<ImpersonationSession> ImpersonationSessions => inner.ImpersonationSessions;

        public Task<int> SaveChangesAsync() => inner.SaveChangesAsync();

        public Task ExecuteInTransactionAsync(Func<Task> action) =>
            inner.ExecuteInTransactionAsync(action);

        public void PreserveCreatedAtOnInsert(BaseEntity entity) =>
            inner.PreserveCreatedAtOnInsert(entity);

        public void ClearChangeTracker() => inner.ClearChangeTracker();

        public Task<int> AtomicClaimInviteSlotAsync(int inviteId, DateTime now) =>
            inner.AtomicClaimInviteSlotAsync(inviteId, now);

        public async Task ExecuteSqlRawAsync(string sql, params object[] parameters)
        {
            using (var concurrent = Ctx(dbName))
            {
                var ws = concurrent
                    .Workspaces.IgnoreQueryFilters()
                    .Single(w => w.Id == workspaceId);
                ws.DemoExpiresAt = null;
                ws.DemoExtendedAt = DateTime.UtcNow;
                await concurrent.SaveChangesAsync();
            }
            await inner.ExecuteSqlRawAsync(sql, parameters);
        }
    }

    // ── DemoCleanupService.SweepOnceAsync (§3.5) ────────────────────────────────────────────

    /// <summary>DB-17 review finding #1 (HIGH): builds a real DI container so <c>SweepOnceAsync</c>
    /// can take a fresh <see cref="IUnitOfWork"/>/<see cref="ITenantService"/> scope PER ITEM,
    /// exactly as it does against the real API host — every scope's <see cref="AppDbContext"/>
    /// targets the SAME named InMemory database, so they all see each other's committed writes.
    /// </summary>
    private static ServiceProvider BuildSweepContainer(
        string dbName,
        RecordingFileStorage files,
        FakeAuditWriter audit
    )
    {
        var services = new ServiceCollection();
        services.AddSingleton<IFileStorage>(files);
        services.AddSingleton<IAuditWriter>(audit);
        services.AddSingleton<ISettingsService>(new FakeSettings());
        services.AddSingleton<IPasswordHasher>(new FakePasswordHasher());
        services.AddSingleton<IBillingProvider, NoopBillingProvider>();
        services.AddScoped(_ => Ctx(dbName));
        services.AddScoped<IUnitOfWork>(sp => new UnitOfWork(
            sp.GetRequiredService<AppDbContext>()
        ));
        services.AddScoped<IMembershipService>(sp => new MembershipService(
            sp.GetRequiredService<IUnitOfWork>()
        ));
        services.AddScoped<ITenantService>(sp => new TenantService(
            sp.GetRequiredService<IUnitOfWork>(),
            sp.GetRequiredService<IPasswordHasher>(),
            sp.GetRequiredService<IFileStorage>(),
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<IBillingProvider>(),
            sp.GetRequiredService<IMembershipService>(),
            sp.GetRequiredService<IAuditWriter>()
        ));
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task DemoCleanup_DeletesExpiredWorkspace_ByWorkspaceColumn_NotUsers()
    {
        var dbName = nameof(DemoCleanup_DeletesExpiredWorkspace_ByWorkspaceColumn_NotUsers);
        var db = Ctx(dbName);
        var workspaceId = SeedWorkspace(db, demoExpiresAt: DateTime.UtcNow.AddHours(-1));
        // The workspace column alone selects and deletes it (there is no users column any more — DB-11e).
        var role = new Role { Name = "Workspace Admin", OwnerId = null };
        db.Roles.Add(role);
        var admin = new User
        {
            PublicId = Guid.NewGuid(),
            Email = $"demo-{Guid.NewGuid():N}@demo.pointer",
            PasswordHash = "x",
            DisplayName = "Demo",
            RoleId = role.Id,
            Role = role,
            OwnerId = workspaceId,
            IsActive = true,
            IsDemo = true,
            ApprovalStatus = ApprovalStatus.Approved,
        };
        db.Users.Add(admin);
        db.SaveChanges();
        db.WorkspaceMemberships.Add(
            new WorkspaceMembership
            {
                UserId = admin.Id,
                OwnerId = workspaceId,
                RoleId = role.Id,
                Role = role,
                IsActive = true,
                ApprovalStatus = ApprovalStatus.Approved,
                JoinedAt = DateTime.UtcNow,
            }
        );
        db.SaveChanges();

        var files = new RecordingFileStorage();
        var audit = new FakeAuditWriter();
        await using var container = BuildSweepContainer(dbName, files, audit);

        var deleted = await DemoCleanupService.SweepOnceAsync(
            container.GetRequiredService<IServiceScopeFactory>(),
            NullLogger.Instance,
            CancellationToken.None
        );

        Assert.Equal(1, deleted);
        Assert.Null(db.Workspaces.IgnoreQueryFilters().SingleOrDefault(w => w.Id == workspaceId));
    }

    [Fact]
    public async Task DemoCleanup_LeavesLiveDemo()
    {
        var dbName = nameof(DemoCleanup_LeavesLiveDemo);
        var db = Ctx(dbName);
        var workspaceId = SeedWorkspace(db, demoExpiresAt: DateTime.UtcNow.AddHours(1));

        var files = new RecordingFileStorage();
        var audit = new FakeAuditWriter();
        await using var container = BuildSweepContainer(dbName, files, audit);

        var deleted = await DemoCleanupService.SweepOnceAsync(
            container.GetRequiredService<IServiceScopeFactory>(),
            NullLogger.Instance,
            CancellationToken.None
        );

        Assert.Equal(0, deleted);
        Assert.NotNull(
            db.Workspaces.IgnoreQueryFilters().SingleOrDefault(w => w.Id == workspaceId)
        );
    }

    /// <summary>
    /// DB-17 review finding #5 (test debt): rewritten to exercise the REAL <see cref="TenantService"/>
    /// (the original version modelled the race with a fake <see cref="ITenantService"/> that never
    /// called it). The conversion now lands between the sweep's initial id query and the per-item
    /// loop's own re-check by hooking the SECOND <see cref="IServiceScopeFactory.CreateScope"/> call
    /// (the first is the id query's own scope; the second is the one expired item's scope, about to
    /// run the per-item re-check) — mutating the workspace through a brand-new
    /// <see cref="AppDbContext"/>, exactly as a concurrent <c>UpgradeAsync</c> request would.
    /// </summary>
    [Fact]
    public async Task DemoCleanup_SkipsWorkspaceConvertedBetweenQueryAndDelete()
    {
        var dbName = nameof(DemoCleanup_SkipsWorkspaceConvertedBetweenQueryAndDelete);
        var db = Ctx(dbName);
        var workspaceId = SeedWorkspace(db, demoExpiresAt: DateTime.UtcNow.AddHours(-1));

        var files = new RecordingFileStorage();
        var audit = new FakeAuditWriter();
        await using var container = BuildSweepContainer(dbName, files, audit);
        var racingFactory = new ConvertOnSecondScopeFactory(
            container.GetRequiredService<IServiceScopeFactory>(),
            dbName,
            workspaceId
        );

        var deleted = await DemoCleanupService.SweepOnceAsync(
            racingFactory,
            NullLogger.Instance,
            CancellationToken.None
        );

        Assert.Equal(0, deleted);
        // A fresh context for verification — `db` above still has the ORIGINAL workspace tracked
        // from seeding it, and a tracking query on the SAME context instance returns that tracked
        // reference (not the store's current values) rather than re-fetching what the mutation
        // (through its own, separate context) actually wrote.
        using var verify = Ctx(dbName);
        Assert.NotNull(
            verify.Workspaces.IgnoreQueryFilters().SingleOrDefault(w => w.Id == workspaceId)
        );
        Assert.Null(
            verify.Workspaces.IgnoreQueryFilters().Single(w => w.Id == workspaceId).DemoExpiresAt
        );
        // Never reached HardDeleteAsync at all — the per-item re-check alone skipped it.
        Assert.Empty(files.DeletedOwners);
        Assert.DoesNotContain(audit.Entries, e => e.Action == AuditActions.TenantHardDeleted);
    }

    private sealed class ConvertOnSecondScopeFactory(
        IServiceScopeFactory inner,
        string dbName,
        Guid workspaceId
    ) : IServiceScopeFactory
    {
        private int _scopeCount;

        public IServiceScope CreateScope()
        {
            _scopeCount++;
            if (_scopeCount == 2)
            {
                // Scope #1 was the sweep's own initial id query (already returned/disposed); scope
                // #2 is the one expired item's own fresh scope (DB-17 review finding #1), about to
                // run its per-item re-check — convert right before handing it over, modelling a
                // concurrent UpgradeAsync that committed in exactly that gap.
                using var concurrent = Ctx(dbName);
                var ws = concurrent
                    .Workspaces.IgnoreQueryFilters()
                    .Single(w => w.Id == workspaceId);
                ws.DemoExpiresAt = null;
                ws.DemoConvertedAt = DateTime.UtcNow;
                concurrent.SaveChanges();
            }
            return inner.CreateScope();
        }
    }
}
