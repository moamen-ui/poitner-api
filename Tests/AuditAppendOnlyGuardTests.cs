using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Pointer.Application.Abstractions;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Billing;
using Pointer.Infrastructure.Repository;

namespace Pointer.Tests;

/// <summary>
/// DB-12 §6 test 4 — append-only at the code level (the provider-agnostic
/// <c>AppDbContext.SaveChangesAsync</c> guard) and at the FK level (a workspace hard-delete
/// DETACHES audit rows via ON DELETE SET NULL and keeps them — R8.8; Sqlite honours the FK
/// action in the database, so the guard is not hit).
/// </summary>
public class AuditAppendOnlyGuardTests
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
    }

    private sealed class FakePasswordHasher : IPasswordHasher
    {
        public string Hash(string password) => "hashed:" + password;

        public bool Verify(string password, string hash) => hash == "hashed:" + password;
    }

    private sealed class NoopFileStorage : IFileStorage
    {
        public Task<string> SaveAsync(
            string ownerSegment,
            string project,
            Stream content,
            string extension
        ) => Task.FromResult("");

        public Task DeleteAsync(string relativePathOrUrl) => Task.CompletedTask;

        public Task DeleteOwnerFilesAsync(string ownerSegment) => Task.CompletedTask;
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

    private static AppDbContext InMemory(string dbName) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options,
            new FakeCurrentUser { IsSuperAdmin = true },
            new ConfigurationBuilder().Build()
        );

    private static AuditEvent Row(Guid? ownerId = null) =>
        new()
        {
            OccurredAt = DateTime.UtcNow,
            OwnerId = ownerId,
            ActorKind = AuditActorKind.User,
            Action = Pointer.Application.Common.AuditActions.WorkspaceRenamed,
            TargetType = Pointer.Application.Common.AuditTargets.Workspace,
        };

    [Fact]
    public async Task ModifiedAuditEvent_SaveThrows()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = InMemory(dbName);
        var ev = Row();
        db.AuditEvents.Add(ev);
        await db.SaveChangesAsync();

        db.Entry(ev).State = EntityState.Modified;

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task RemovedAuditEvent_SaveThrows()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = InMemory(dbName);
        var ev = Row();
        db.AuditEvents.Add(ev);
        await db.SaveChangesAsync();

        db.AuditEvents.Remove(ev);

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    /// <summary>
    /// Review finding #12 (NIT): the append-only guard lived only on the async
    /// <c>SaveChangesAsync(CancellationToken)</c> override — DbContext's SYNCHRONOUS
    /// <c>SaveChanges()</c> does not route through it, so it silently bypassed the guard entirely.
    /// All four overloads now share one guarded+stamped path.
    /// </summary>
    [Fact]
    public void ModifiedAuditEvent_SyncSaveChanges_Throws()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = InMemory(dbName);
        var ev = Row();
        db.AuditEvents.Add(ev);
        db.SaveChanges();

        db.Entry(ev).State = EntityState.Modified;

        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
    }

    [Fact]
    public async Task AddedAuditEvent_SavesFine()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = InMemory(dbName);
        db.AuditEvents.Add(Row());
        await db.SaveChangesAsync();

        Assert.Single(db.AuditEvents.IgnoreQueryFilters());
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

        public AppDbContext MakeContext() =>
            new(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite(_connectionString)
                    .AddInterceptors(new SqliteBtrimFunctionInterceptor())
                    .Options,
                new FakeCurrentUser { IsSuperAdmin = true },
                new ConfigurationBuilder().Build()
            );

        public void Dispose() => _keepAlive.Dispose();
    }

    [Fact]
    public async Task HardDelete_Workspace_DetachesAuditRows_KeepsThem()
    {
        using var db = new TestDb();
        var workspaceId = Guid.NewGuid();

        using (var seed = db.MakeContext())
        {
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = workspaceId,
                    Name = "Workspace",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = Guid.NewGuid(),
                }
            );
            seed.AuditEvents.Add(
                new AuditEvent
                {
                    OccurredAt = DateTime.UtcNow,
                    OwnerId = workspaceId,
                    ActorKind = AuditActorKind.User,
                    Action = Pointer.Application.Common.AuditActions.WorkspaceRenamed,
                    TargetType = Pointer.Application.Common.AuditTargets.Workspace,
                    TargetId = workspaceId.ToString(),
                }
            );
            await seed.SaveChangesAsync();
        }

        using (var ctx = db.MakeContext())
        {
            var svc = new TenantService(
                new UnitOfWork(ctx),
                new FakePasswordHasher(),
                new NoopFileStorage(),
                new FakeSettings(),
                new NoopBillingProvider(),
                new MembershipService(new UnitOfWork(ctx))
            );

            var result = await svc.HardDeleteAsync(workspaceId);
            Assert.True(result.IsSuccess, result.Message ?? "hard delete failed");
        }

        using var verify = db.MakeContext();
        var row = verify.AuditEvents.IgnoreQueryFilters().Single();
        Assert.Null(row.OwnerId); // detached, kept — the operator record survives (R8.8)
    }
}
