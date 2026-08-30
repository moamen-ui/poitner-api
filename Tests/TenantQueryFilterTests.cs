using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
        public bool IsQuickAccess { get; set; }
        public Guid? TenantId { get; set; }
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static AppDbContext BuildContext(FakeCurrentUser user, string dbName, bool strictNullTenant = false)
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tenancy:StrictNullTenantIsolation"] = strictNullTenant ? "true" : "false"
            })
            .Build();
        return new AppDbContext(opts, user, config);
    }

    private static AppDbContext SuperAdminContext(string dbName) =>
        BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName);

    // ---------------------------------------------------------------------------
    // Project — strict-own filter (super OR own)
    // ---------------------------------------------------------------------------

    [Fact]
    public void Project_TenantA_SeesOnlyOwnRows()
    {
        // Project.OwnerId is DB-enforced NOT NULL (super admins can no longer own/create one), so
        // this only needs to pin tenant-vs-tenant isolation — the null-owner-bucket case is covered
        // separately below via User, which can still legitimately be null-owner (a super admin's own
        // account row).
        var db = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        // Seed via super-admin context so filters don't block the inserts.
        using (var seed = SuperAdminContext(db))
        {
            seed.Projects.AddRange(
                new Project { Key = "A1", Name = "A1", OwnerId = tenantA },
                new Project { Key = "A2", Name = "A2", OwnerId = tenantA },
                new Project { Key = "B1", Name = "B1", OwnerId = tenantB }
            );
            seed.SaveChanges();
        }

        // Tenant A scoped context.
        using var ctx = BuildContext(new FakeCurrentUser { TenantId = tenantA, IsSuperAdmin = false }, db);
        var results = ctx.Set<Project>().ToList();

        Assert.Equal(2, results.Count);
        Assert.All(results, p => Assert.Equal(tenantA, p.OwnerId));
        Assert.DoesNotContain(results, p => p.OwnerId == tenantB);
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
                new Project { Key = "B1", Name = "B1", OwnerId = tenantB }
            );
            seed.SaveChanges();
        }

        using var ctx = SuperAdminContext(db);
        var results = ctx.Set<Project>().ToList();

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void User_TenantA_DoesNotSeeNull_OwnerRows()
    {
        // Strict-own: null OwnerId rows (a super admin's own account — the one entity in this group
        // that can still legitimately be null-owner) are NOT visible to tenants. Project can no
        // longer hold OwnerId == null at all (DB-enforced), so User is the vehicle here instead.
        var db = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();

        using (var seed = SuperAdminContext(db))
        {
            seed.Users.Add(new User { Email = "a@x", PasswordHash = "h", DisplayName = "a", PublicId = Guid.NewGuid(), OwnerId = tenantA, RoleId = 1 });
            seed.Users.Add(new User { Email = "super@x", PasswordHash = "h", DisplayName = "super", PublicId = Guid.NewGuid(), OwnerId = null, RoleId = 1 });
            seed.SaveChanges();
        }

        using var ctx = BuildContext(new FakeCurrentUser { TenantId = tenantA, IsSuperAdmin = false }, db);
        var results = ctx.Set<User>().ToList();

        Assert.Single(results);
        Assert.Equal(tenantA, results[0].OwnerId);
    }

    // ---------------------------------------------------------------------------
    // C1 — null-tenant collapse on strict-own entities.
    // A non-super principal with TenantId == null (no `tenant` claim → null owner_id) has its
    // strict-own filter collapse to `owner_id IS NULL`, exposing the whole null-owner bucket.
    // These tests pin BOTH modes of the Tenancy:StrictNullTenantIsolation lever.
    // ---------------------------------------------------------------------------

    [Fact]
    public void NullTenant_NonSuper_DefaultFlag_SeesNullOwnerBucket()
    {
        // DEFAULT (flag off) — documents the current, pre-back-fill behavior: a null-tenant
        // non-super principal sees every null-owner row (and NOT another tenant's rows). Project can
        // no longer hold OwnerId == null (DB-enforced), so User is the vehicle — still a real,
        // reachable shape (a super admin's own account row has OwnerId == null).
        var db = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();

        using (var seed = SuperAdminContext(db))
        {
            seed.Users.Add(new User { Email = "a@x", PasswordHash = "h", DisplayName = "a", PublicId = Guid.NewGuid(), OwnerId = tenantA, RoleId = 1 });
            seed.Users.Add(new User { Email = "n1@x", PasswordHash = "h", DisplayName = "n1", PublicId = Guid.NewGuid(), OwnerId = null, RoleId = 1 });
            seed.Users.Add(new User { Email = "n2@x", PasswordHash = "h", DisplayName = "n2", PublicId = Guid.NewGuid(), OwnerId = null, RoleId = 1 });
            seed.SaveChanges();
        }

        using var ctx = BuildContext(
            new FakeCurrentUser { TenantId = null, IsSuperAdmin = false }, db, strictNullTenant: false);
        var results = ctx.Set<User>().ToList();

        Assert.Equal(2, results.Count);                              // both null-owner rows
        Assert.All(results, u => Assert.Null(u.OwnerId));
        Assert.DoesNotContain(results, u => u.OwnerId == tenantA);   // never another tenant's row
    }

    [Fact]
    public void NullTenant_NonSuper_StrictFlag_SeesNothing()
    {
        // STRICT (flag on) — the C1 fix: a null-tenant non-super principal sees NO strict-own rows,
        // even null-owner ones. Enable only after back-filling owner_id (fix-plan T4). Project/Comment
        // can no longer hold OwnerId == null at all (DB-enforced) — User remains the one entity in
        // this group where it's still a real, reachable shape.
        var db = Guid.NewGuid().ToString();

        using (var seed = SuperAdminContext(db))
        {
            seed.Users.Add(new User { Email = "n@x", PasswordHash = "h", DisplayName = "n", PublicId = Guid.NewGuid(), OwnerId = null, RoleId = 1 });
            seed.SaveChanges();
        }

        using var ctx = BuildContext(
            new FakeCurrentUser { TenantId = null, IsSuperAdmin = false }, db, strictNullTenant: true);

        Assert.Empty(ctx.Set<User>().ToList());
    }

    [Fact]
    public void NullTenant_NonSuper_StrictFlag_SeesNoGlobalRoles()
    {
        // Regression for the own-plus-global asymmetry fix: before, Role/StatusPresentation/
        // PredefinedAction ignored `strict` entirely, so a null-tenant caller always swept up the
        // whole global bucket regardless of the flag. Confirms the flag now closes this gap too,
        // matching the strict-own group's C1 behavior above — while a REAL tenant (tested elsewhere:
        // Role_TenantA_SeesOwnAndGlobalRoles_NotTenantBRoles) still sees globals unconditionally.
        var db = Guid.NewGuid().ToString();

        using (var seed = SuperAdminContext(db))
        {
            seed.Roles.Add(new Role { Name = "Global-Viewer", OwnerId = null });
            seed.SaveChanges();
        }

        using var ctx = BuildContext(
            new FakeCurrentUser { TenantId = null, IsSuperAdmin = false }, db, strictNullTenant: true);

        Assert.Empty(ctx.Set<Role>().ToList());
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
