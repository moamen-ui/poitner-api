using System;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Domain.Entity;
using Pointer.Infrastructure;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-05: soft-delete-aware unique indexes. A soft-deleted row must no longer block re-creating the
/// same unique value (email/name/owner), while two LIVE rows sharing that value are still rejected.
/// </summary>
public class SoftDeleteUniqueIndexTests
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

    /// <summary>
    /// Same shape as <c>UsageEventFirstCommentTests.TestDb</c> (Tests/UsageEventFirstCommentTests.cs:43-60):
    /// an in-memory SQLite database shared across independently-opened connections/contexts, kept
    /// alive by one dedicated connection for the lifetime of the fixture.
    /// </summary>
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
            // ux_users_email_live is deliberately NOT modelled in EF (lower(email) can't be
            // expressed via HasIndex — UserMapping.cs), so EnsureCreated() never creates it. It is
            // the ONLY thing enforcing global e-mail uniqueness since DB-11f dropped the modelled
            // ux_users_email_owner_live index (per-owner uniqueness). Recreate it here (raw SQL
            // migration 20260922205015_AddUsersEmailLiveUniqueIndex), same as production, so this
            // Sqlite-only test harness still proves the real invariant.
            bootstrap.Database.ExecuteSqlRaw(
                "CREATE UNIQUE INDEX ux_users_email_live ON users (lower(email)) WHERE deleted_at IS NULL;"
            );
        }

        public AppDbContext MakeContext(ICurrentUser user) =>
            new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite(_connectionString)
                    .AddInterceptors(new SqliteBtrimFunctionInterceptor())
                    .Options,
                user,
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
            );

        public void Dispose() => _keepAlive.Dispose();
    }

    private static readonly FakeCurrentUser SuperAdmin = new() { IsSuperAdmin = true };

    // DB-03: every owner_id now FKs to workspaces(id) — Sqlite enforces this FK (unlike InMemory),
    // so every ownerId these tests mint needs a workspace row first.
    private static async Task SeedWorkspaceAsync(TestDb db, Guid ownerId)
    {
        using var ctx = db.MakeContext(SuperAdmin);
        ctx.Workspaces.Add(
            new Workspace
            {
                Id = ownerId,
                Name = "Workspace",
                CreatedAt = DateTime.UtcNow,
                CreatedBy = ownerId,
            }
        );
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task User_SameEmail_AfterSoftDelete_IsAllowed()
    {
        using var db = new TestDb();
        var ownerId = Guid.NewGuid();
        await SeedWorkspaceAsync(db, ownerId);

        Role role;
        using (var ctx = db.MakeContext(SuperAdmin))
        {
            role = new Role { Name = "Member", OwnerId = ownerId };
            ctx.Roles.Add(role);
            await ctx.SaveChangesAsync();
        }

        User userA;
        using (var ctx = db.MakeContext(SuperAdmin))
        {
            userA = new User
            {
                Email = "dup@example.com",
                PasswordHash = "hash",
                DisplayName = "A",
            };
            ctx.Users.Add(userA);
            await ctx.SaveChangesAsync();
        }

        using (var ctx = db.MakeContext(SuperAdmin))
        {
            var tracked = await ctx.Users.FirstAsync(u => u.Id == userA.Id);
            tracked.DeletedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }

        using (var ctx = db.MakeContext(SuperAdmin))
        {
            ctx.Users.Add(
                new User
                {
                    Email = "dup@example.com",
                    PasswordHash = "hash",
                    DisplayName = "B",
                }
            );
            // Must NOT throw: the soft-deleted row no longer participates in the unique index.
            await ctx.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task User_SameEmail_LiveDuplicate_IsRejected()
    {
        using var db = new TestDb();
        var ownerId = Guid.NewGuid();
        await SeedWorkspaceAsync(db, ownerId);

        Role role;
        using (var ctx = db.MakeContext(SuperAdmin))
        {
            role = new Role { Name = "Member", OwnerId = ownerId };
            ctx.Roles.Add(role);
            await ctx.SaveChangesAsync();
        }

        using (var ctx = db.MakeContext(SuperAdmin))
        {
            ctx.Users.Add(
                new User
                {
                    Email = "dup2@example.com",
                    PasswordHash = "hash",
                    DisplayName = "A",
                }
            );
            await ctx.SaveChangesAsync();
        }

        using (var ctx = db.MakeContext(SuperAdmin))
        {
            ctx.Users.Add(
                new User
                {
                    Email = "dup2@example.com",
                    PasswordHash = "hash",
                    DisplayName = "B",
                }
            );
            await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task Role_SameName_AfterSoftDelete_IsAllowed()
    {
        using var db = new TestDb();
        var ownerId = Guid.NewGuid();
        await SeedWorkspaceAsync(db, ownerId);

        Role roleA;
        using (var ctx = db.MakeContext(SuperAdmin))
        {
            roleA = new Role { Name = "Client", OwnerId = ownerId };
            ctx.Roles.Add(roleA);
            await ctx.SaveChangesAsync();
        }

        using (var ctx = db.MakeContext(SuperAdmin))
        {
            var tracked = await ctx.Roles.FirstAsync(r => r.Id == roleA.Id);
            tracked.DeletedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }

        using (var ctx = db.MakeContext(SuperAdmin))
        {
            ctx.Roles.Add(new Role { Name = "Client", OwnerId = ownerId });
            // Must NOT throw: the soft-deleted role no longer participates in the unique index.
            await ctx.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Subscription_SecondLive_IsRejected_AfterSoftDelete_IsAllowed()
    {
        using var db = new TestDb();
        var ownerId = Guid.NewGuid();
        await SeedWorkspaceAsync(db, ownerId);

        Plan plan;
        using (var ctx = db.MakeContext(SuperAdmin))
        {
            plan = new Plan { Name = "Pro", Slug = "pro" };
            ctx.Plans.Add(plan);
            await ctx.SaveChangesAsync();
        }

        Subscription subA;
        using (var ctx = db.MakeContext(SuperAdmin))
        {
            subA = new Subscription { OwnerId = ownerId, PlanId = plan.Id };
            ctx.Subscriptions.Add(subA);
            await ctx.SaveChangesAsync();
        }

        // A second LIVE subscription for the same tenant is rejected.
        using (var ctx = db.MakeContext(SuperAdmin))
        {
            ctx.Subscriptions.Add(new Subscription { OwnerId = ownerId, PlanId = plan.Id });
            await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
        }

        // Soft-delete the first subscription.
        using (var ctx = db.MakeContext(SuperAdmin))
        {
            var tracked = await ctx.Subscriptions.FirstAsync(s => s.Id == subA.Id);
            tracked.DeletedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }

        // Now a fresh live subscription for the same tenant is allowed.
        using (var ctx = db.MakeContext(SuperAdmin))
        {
            ctx.Subscriptions.Add(new Subscription { OwnerId = ownerId, PlanId = plan.Id });
            await ctx.SaveChangesAsync();
        }
    }
}
