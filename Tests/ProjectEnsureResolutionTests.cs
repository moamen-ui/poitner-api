using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Services.Implementation;
using Pointer.Domain.Entity;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;

namespace Pointer.Tests;

/// <summary>
/// EnsureAsync is the widget-side strict project resolver (comments / predefined-actions by key).
/// Super admins can no longer own or create projects (ProjectService.CreateAsync forbids it, and
/// production data confirms no null-owner project can exist anymore either) — so EnsureAsync no
/// longer special-cases them at all: TenantStamp.OwnerFor(_currentUser) is null for a super admin,
/// which correctly never matches any real project's OwnerId. These tests pin that down, including
/// against a hypothetical legacy row that WOULD have resolved under the old (removed) logic.
/// </summary>
public class ProjectEnsureResolutionTests
{
    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; }
        public bool IsQuickAccess { get; set; }
        public Guid? TenantId { get; set; }
    }

    private static AppDbContext BuildContext(ICurrentUser user, string dbName) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options, user, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    private static ProjectService Wire(ICurrentUser user, string dbName) =>
        new(new UnitOfWork(BuildContext(user, dbName)), user, new PassThroughEntitlements());

    private static void Seed(string dbName, params Project[] projects)
    {
        using var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        seed.Projects.AddRange(projects);
        seed.SaveChanges();
    }

    [Fact]
    public async Task SuperAdmin_DoesNotResolve_ProjectStampedWithOwnId()
    {
        // Under the old (removed) logic this WOULD have resolved. Now that super admins can never
        // own or create a project, this scenario can't occur going forward — pinned as a hypothetical
        // legacy row to prove the removed self-id branch is really gone, not just untriggered.
        var db = Guid.NewGuid().ToString();
        var superId = Guid.NewGuid();
        Seed(db, new Project { Key = "clubs", Name = "clubs", OwnerId = superId });

        var svc = Wire(new FakeCurrentUser { Id = superId, IsAdmin = true, IsSuperAdmin = true }, db);
        var result = await svc.EnsureAsync("clubs");

        Assert.True(result.IsNotFound);
    }

    [Fact]
    public async Task SuperAdmin_DoesNotResolve_OtherTenantsProjectByKey()
    {
        var db = Guid.NewGuid().ToString();
        Seed(db, new Project { Key = "clubs", Name = "clubs", OwnerId = Guid.NewGuid() });

        var svc = Wire(new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, IsSuperAdmin = true }, db);
        var result = await svc.EnsureAsync("clubs");

        Assert.True(result.IsNotFound);
    }

    [Fact]
    public async Task TenantStakeholder_Resolves_OwnTenantProject()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        Seed(db, new Project { Key = "clubs", Name = "clubs", OwnerId = tenant });

        var svc = Wire(new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant }, db);
        var result = await svc.EnsureAsync("clubs");

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task TenantStakeholder_DoesNotResolve_OtherTenantsProject()
    {
        var db = Guid.NewGuid().ToString();
        Seed(db, new Project { Key = "clubs", Name = "clubs", OwnerId = Guid.NewGuid() });

        var svc = Wire(new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = Guid.NewGuid() }, db);
        var result = await svc.EnsureAsync("clubs");

        Assert.True(result.IsNotFound);
    }
}
