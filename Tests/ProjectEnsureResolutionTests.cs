using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Services.Implementation;
using Pointer.Domain.Entity;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;

namespace Pointer.Tests;

/// <summary>
/// EnsureAsync is the widget-side strict project resolver (comments / predefined-actions by key).
/// These tests pin down the owner-matching rules that have see-sawed twice:
/// super-admin must resolve BOTH projects stamped with their own id (the write side stamps
/// OwnerFor ?? Id) AND legacy/global null-owner projects — while never resolving another
/// tenant's project by key.
/// </summary>
public class ProjectEnsureResolutionTests
{
    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; }
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
    public async Task SuperAdmin_Resolves_ProjectStampedWithOwnId()
    {
        var db = Guid.NewGuid().ToString();
        var superId = Guid.NewGuid();
        Seed(db, new Project { Key = "clubs", Name = "clubs", OwnerId = superId });

        var svc = Wire(new FakeCurrentUser { Id = superId, IsAdmin = true, IsSuperAdmin = true }, db);
        var result = await svc.EnsureAsync("clubs");

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task SuperAdmin_Resolves_NullOwnerGlobalProject()
    {
        var db = Guid.NewGuid().ToString();
        Seed(db, new Project { Key = "landing", Name = "landing", OwnerId = null });

        var svc = Wire(new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, IsSuperAdmin = true }, db);
        var result = await svc.EnsureAsync("landing");

        Assert.True(result.IsSuccess);
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
    public async Task SuperAdmin_PrefersOwnProject_OnKeyCollisionWithGlobal()
    {
        var db = Guid.NewGuid().ToString();
        var superId = Guid.NewGuid();
        var own = new Project { Key = "clubs", Name = "own", OwnerId = superId };
        Seed(db, new Project { Key = "clubs", Name = "global", OwnerId = null }, own);

        var svc = Wire(new FakeCurrentUser { Id = superId, IsAdmin = true, IsSuperAdmin = true }, db);
        var result = await svc.EnsureAsync("clubs");

        Assert.True(result.IsSuccess);
        Assert.Equal(own.Id, result.Data);
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
