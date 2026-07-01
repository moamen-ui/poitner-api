using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Domain.Entity;
using Pointer.Infrastructure;

namespace Pointer.Tests;

/// <summary>
/// Verifies that EF global query filters enforce tenant isolation.
/// These tests are SECURITY-CRITICAL: a failing assertion means a cross-tenant data leak.
/// </summary>
public class TenantQueryFilterTests
{
    // ---------------------------------------------------------------------------
    // Test double
    // ---------------------------------------------------------------------------

    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; }
        public Guid? TenantId { get; set; }
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static AppDbContext BuildContext(FakeCurrentUser user, string dbName)
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new AppDbContext(opts, user);
    }

    private static AppDbContext SuperAdminContext(string dbName) =>
        BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName);

    // ---------------------------------------------------------------------------
    // Project — strict-own filter (super OR own)
    // ---------------------------------------------------------------------------

    [Fact]
    public void Project_TenantA_SeesOnlyOwnRows()
    {
        var db = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        // Seed via super-admin context so filters don't block the inserts.
        using (var seed = SuperAdminContext(db))
        {
            seed.Projects.AddRange(
                new Project { Key = "A1", Name = "A1", OwnerId = tenantA },
                new Project { Key = "A2", Name = "A2", OwnerId = tenantA },
                new Project { Key = "B1", Name = "B1", OwnerId = tenantB },
                new Project { Key = "NULL", Name = "NULL", OwnerId = null }
            );
            seed.SaveChanges();
        }

        // Tenant A scoped context.
        using var ctx = BuildContext(new FakeCurrentUser { TenantId = tenantA, IsSuperAdmin = false }, db);
        var results = ctx.Set<Project>().ToList();

        Assert.Equal(2, results.Count);
        Assert.All(results, p => Assert.Equal(tenantA, p.OwnerId));
        Assert.DoesNotContain(results, p => p.OwnerId == tenantB);
        Assert.DoesNotContain(results, p => p.OwnerId == null);
    }

    [Fact]
    public void Project_SuperAdmin_SeesAllRows()
    {
        var db = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        using (var seed = SuperAdminContext(db))
        {
            seed.Projects.AddRange(
                new Project { Key = "A1", Name = "A1", OwnerId = tenantA },
                new Project { Key = "B1", Name = "B1", OwnerId = tenantB },
                new Project { Key = "NULL", Name = "NULL", OwnerId = null }
            );
            seed.SaveChanges();
        }

        using var ctx = SuperAdminContext(db);
        var results = ctx.Set<Project>().ToList();

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void Project_TenantA_DoesNotSeeNull_OwnerRows()
    {
        // Strict-own: null OwnerId rows (global/super-admin) are NOT visible to tenants.
        var db = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();

        using (var seed = SuperAdminContext(db))
        {
            seed.Projects.AddRange(
                new Project { Key = "A1", Name = "A1", OwnerId = tenantA },
                new Project { Key = "GLOBAL", Name = "GLOBAL", OwnerId = null }
            );
            seed.SaveChanges();
        }

        using var ctx = BuildContext(new FakeCurrentUser { TenantId = tenantA, IsSuperAdmin = false }, db);
        var results = ctx.Set<Project>().ToList();

        Assert.Single(results);
        Assert.Equal(tenantA, results[0].OwnerId);
    }

    // ---------------------------------------------------------------------------
    // Role — own-plus-global filter (super OR own OR OwnerId == null)
    // ---------------------------------------------------------------------------

    [Fact]
    public void Role_TenantA_SeesOwnAndGlobalRoles_NotTenantBRoles()
    {
        var db = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        using (var seed = SuperAdminContext(db))
        {
            seed.Roles.AddRange(
                new Role { Name = "A-Admin", OwnerId = tenantA },
                new Role { Name = "A-Dev", OwnerId = tenantA },
                new Role { Name = "B-PM", OwnerId = tenantB },
                new Role { Name = "Global-Viewer", OwnerId = null }
            );
            seed.SaveChanges();
        }

        using var ctx = BuildContext(new FakeCurrentUser { TenantId = tenantA, IsSuperAdmin = false }, db);
        var results = ctx.Set<Role>().ToList();

        // Should see tenantA rows + global (null) row — 3 total.
        Assert.Equal(3, results.Count);
        Assert.DoesNotContain(results, r => r.OwnerId == tenantB);
        Assert.Contains(results, r => r.OwnerId == null);
        Assert.Equal(2, results.Count(r => r.OwnerId == tenantA));
    }

    [Fact]
    public void Role_SuperAdmin_SeesAllRoles()
    {
        var db = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        using (var seed = SuperAdminContext(db))
        {
            seed.Roles.AddRange(
                new Role { Name = "A-Admin", OwnerId = tenantA },
                new Role { Name = "B-PM", OwnerId = tenantB },
                new Role { Name = "Global", OwnerId = null }
            );
            seed.SaveChanges();
        }

        using var ctx = SuperAdminContext(db);
        var results = ctx.Set<Role>().ToList();

        Assert.Equal(3, results.Count);
    }

    // ---------------------------------------------------------------------------
    // StatusPresentation — own-plus-global filter
    // ---------------------------------------------------------------------------

    [Fact]
    public void StatusPresentation_TenantA_SeesOwnAndGlobal_NotTenantB()
    {
        var db = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        using (var seed = SuperAdminContext(db))
        {
            seed.StatusPresentations.AddRange(
                new StatusPresentation { StatusValue = 1, Label = "A-Open", OwnerId = tenantA },
                new StatusPresentation { StatusValue = 2, Label = "B-Open", OwnerId = tenantB },
                new StatusPresentation { StatusValue = 3, Label = "Global-Closed", OwnerId = null }
            );
            seed.SaveChanges();
        }

        using var ctx = BuildContext(new FakeCurrentUser { TenantId = tenantA, IsSuperAdmin = false }, db);
        var results = ctx.Set<StatusPresentation>().ToList();

        Assert.Equal(2, results.Count);
        Assert.DoesNotContain(results, s => s.OwnerId == tenantB);
        Assert.Contains(results, s => s.OwnerId == null);
    }

    // ---------------------------------------------------------------------------
    // PredefinedAction — own-plus-global filter (super OR own OR null-owner);
    // OwnerId is nullable (null = global/null-owner project).
    // ---------------------------------------------------------------------------

    [Fact]
    public void PredefinedAction_TenantA_CannotSeeTenantB()
    {
        var db = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        using (var seed = SuperAdminContext(db))
        {
            seed.PredefinedActions.AddRange(
                new PredefinedAction { OwnerId = tenantA, Text = "A-tenant", Prompt = "pa" },
                new PredefinedAction { OwnerId = tenantA, Text = "A-project", Prompt = "pa", ProjectId = 1 },
                new PredefinedAction { OwnerId = tenantB, Text = "B-tenant", Prompt = "pb" }
            );
            seed.SaveChanges();
        }

        using var ctx = BuildContext(new FakeCurrentUser { TenantId = tenantA, IsSuperAdmin = false }, db);
        var results = ctx.Set<PredefinedAction>().ToList();

        Assert.Equal(2, results.Count);
        Assert.All(results, a => Assert.Equal(tenantA, a.OwnerId));
        Assert.DoesNotContain(results, a => a.OwnerId == tenantB);
    }

    [Fact]
    public void PredefinedAction_SuperAdmin_SeesAllRows()
    {
        var db = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        using (var seed = SuperAdminContext(db))
        {
            seed.PredefinedActions.AddRange(
                new PredefinedAction { OwnerId = tenantA, Text = "A", Prompt = "pa" },
                new PredefinedAction { OwnerId = tenantB, Text = "B", Prompt = "pb" }
            );
            seed.SaveChanges();
        }

        using var ctx = SuperAdminContext(db);
        Assert.Equal(2, ctx.Set<PredefinedAction>().Count());
    }

    // ---------------------------------------------------------------------------
    // IgnoreQueryFilters — super-admin/system paths can bypass filters explicitly
    // ---------------------------------------------------------------------------

    [Fact]
    public void Project_IgnoreQueryFilters_BypassesTenantFilter()
    {
        var db = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        using (var seed = SuperAdminContext(db))
        {
            seed.Projects.AddRange(
                new Project { Key = "A1", Name = "A1", OwnerId = tenantA },
                new Project { Key = "B1", Name = "B1", OwnerId = tenantB }
            );
            seed.SaveChanges();
        }

        using var ctx = BuildContext(new FakeCurrentUser { TenantId = tenantA, IsSuperAdmin = false }, db);

        // Without IgnoreQueryFilters: only tenant A's rows.
        var filtered = ctx.Set<Project>().ToList();
        Assert.Single(filtered);

        // With IgnoreQueryFilters: all rows visible (for cascade delete / background jobs).
        var unfiltered = ctx.Set<Project>().IgnoreQueryFilters().ToList();
        Assert.Equal(2, unfiltered.Count);
    }
}
