using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Project;
using Pointer.Application.Services.Implementation;
using Pointer.Domain.Entity;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// Project.EnvironmentSelectorRoleIds — which roles see the widget's environment switcher.
/// Unconfigured (null) defaults to "everyone except Client (QuickAccess)"; configured to a
/// specific role-id list, only those roles see it. Surfaced to the widget via
/// CaptureConfigResponse.ShowEnvironmentSelector (GetCaptureConfigAsync), computed for the
/// CURRENT caller.
/// </summary>
public class EnvironmentSelectorRoleTests
{
    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; }
        public bool IsQuickAccess { get; set; }
        public Guid? TenantId { get; set; }
        public int? RoleId { get; set; }
    }

    private static AppDbContext BuildContext(ICurrentUser user, string dbName) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options, user,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    [Fact]
    public async Task Unconfigured_NonQuickAccessCaller_SeesSelector()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant, RoleId = 1 };
        var svc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements(), TestProjectServiceDeps.Settings(), TestProjectServiceDeps.Configuration());
        await svc.CreateAsync(new CreateProjectRequest { Key = "site", Name = "Site" });

        var config = await svc.GetCaptureConfigAsync("site");
        Assert.True(config.IsSuccess);
        Assert.True(config.Data!.ShowEnvironmentSelector);
    }

    [Fact]
    public async Task Unconfigured_QuickAccessCaller_DoesNotSeeSelector()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant, RoleId = 1 };
        var svc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements(), TestProjectServiceDeps.Settings(), TestProjectServiceDeps.Configuration());
        await svc.CreateAsync(new CreateProjectRequest { Key = "site", Name = "Site" });

        var client = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant, IsQuickAccess = true, RoleId = 7 };
        var clientSvc = new ProjectService(new UnitOfWork(BuildContext(client, dbName)), client, new PassThroughEntitlements(), TestProjectServiceDeps.Settings(), TestProjectServiceDeps.Configuration());

        var config = await clientSvc.GetCaptureConfigAsync("site");
        Assert.True(config.IsSuccess);
        Assert.False(config.Data!.ShowEnvironmentSelector);
    }

    [Fact]
    public async Task Configured_CallerRoleInList_SeesSelector()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant, RoleId = 1 };
        var svc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements(), TestProjectServiceDeps.Settings(), TestProjectServiceDeps.Configuration());
        var created = (await svc.CreateAsync(new CreateProjectRequest { Key = "site", Name = "Site" })).Data!;

        await svc.UpdateAsync(created.Id, new UpdateProjectRequest { EnvironmentSelectorRoleIds = new List<int> { 5, 9 } });

        var roleFive = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant, RoleId = 5 };
        var roleFiveSvc = new ProjectService(new UnitOfWork(BuildContext(roleFive, dbName)), roleFive, new PassThroughEntitlements(), TestProjectServiceDeps.Settings(), TestProjectServiceDeps.Configuration());
        var config = await roleFiveSvc.GetCaptureConfigAsync("site");
        Assert.True(config.IsSuccess);
        Assert.True(config.Data!.ShowEnvironmentSelector);
    }

    [Fact]
    public async Task Configured_CallerRoleNotInList_DoesNotSeeSelector()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant, RoleId = 1 };
        var svc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements(), TestProjectServiceDeps.Settings(), TestProjectServiceDeps.Configuration());
        var created = (await svc.CreateAsync(new CreateProjectRequest { Key = "site", Name = "Site" })).Data!;

        await svc.UpdateAsync(created.Id, new UpdateProjectRequest { EnvironmentSelectorRoleIds = new List<int> { 5, 9 } });

        // Not in the configured list — even though not quick-access, it's excluded once configured.
        var roleTwo = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant, RoleId = 2 };
        var roleTwoSvc = new ProjectService(new UnitOfWork(BuildContext(roleTwo, dbName)), roleTwo, new PassThroughEntitlements(), TestProjectServiceDeps.Settings(), TestProjectServiceDeps.Configuration());
        var config = await roleTwoSvc.GetCaptureConfigAsync("site");
        Assert.True(config.IsSuccess);
        Assert.False(config.Data!.ShowEnvironmentSelector);
    }

    [Fact]
    public async Task UpdateAsync_EmptyList_ClearsBackToDefault()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant, RoleId = 1 };
        var svc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements(), TestProjectServiceDeps.Settings(), TestProjectServiceDeps.Configuration());
        var created = (await svc.CreateAsync(new CreateProjectRequest { Key = "site", Name = "Site" })).Data!;

        await svc.UpdateAsync(created.Id, new UpdateProjectRequest { EnvironmentSelectorRoleIds = new List<int> { 5 } });
        var afterSet = await svc.UpdateAsync(created.Id, new UpdateProjectRequest { EnvironmentSelectorRoleIds = new List<int>() });

        Assert.True(afterSet.IsSuccess);
        Assert.Null(afterSet.Data!.EnvironmentSelectorRoleIds);

        // Back to default: a non-quick-access caller (any role) sees it again.
        var roleTwo = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant, RoleId = 2 };
        var roleTwoSvc = new ProjectService(new UnitOfWork(BuildContext(roleTwo, dbName)), roleTwo, new PassThroughEntitlements(), TestProjectServiceDeps.Settings(), TestProjectServiceDeps.Configuration());
        var config = await roleTwoSvc.GetCaptureConfigAsync("site");
        Assert.True(config.Data!.ShowEnvironmentSelector);
    }

    [Fact]
    public async Task UpdateAsync_NullField_LeavesConfigurationUntouched()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant, RoleId = 1 };
        var svc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements(), TestProjectServiceDeps.Settings(), TestProjectServiceDeps.Configuration());
        var created = (await svc.CreateAsync(new CreateProjectRequest { Key = "site", Name = "Site" })).Data!;

        await svc.UpdateAsync(created.Id, new UpdateProjectRequest { EnvironmentSelectorRoleIds = new List<int> { 5 } });
        var afterUnrelatedPatch = await svc.UpdateAsync(created.Id, new UpdateProjectRequest { Name = "Renamed" });

        Assert.True(afterUnrelatedPatch.IsSuccess);
        Assert.Equal(new List<int> { 5 }, afterUnrelatedPatch.Data!.EnvironmentSelectorRoleIds);
    }
}
