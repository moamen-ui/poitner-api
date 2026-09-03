using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Project;
using Pointer.Application.Services.Implementation;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// Per-environment project activation: the three flags (IsActiveLocal/Staging/Production) default
/// true on create, the base EnsureAsync(key) only conflicts when ALL THREE are false (parity with
/// the old single IsActive=false), the environment-aware EnsureAsync(key, EnvironmentTag) catches
/// a single deactivated environment, and ProjectResponse.ActivationState derives Active/Partial/
/// Inactive from the flags.
/// </summary>
public class ProjectPerEnvironmentActivationTests
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
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options, user,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    [Fact]
    public async Task CreateAsync_WithoutActivationFields_ProjectIsActiveInAllEnvironments()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        var svc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements());

        var created = await svc.CreateAsync(new CreateProjectRequest { Key = "site", Name = "Site" });
        Assert.True(created.IsSuccess);

        using var check = BuildContext(admin, dbName);
        var project = check.Projects.Single(p => p.Key == "site");
        Assert.True(project.IsActiveLocal);
        Assert.True(project.IsActiveStaging);
        Assert.True(project.IsActiveProduction);

        var listed = await svc.ListAsync();
        Assert.True(listed.IsSuccess);
        Assert.Equal(ProjectActivationState.Active, listed.Data!.Single(p => p.Key == "site").ActivationState);
    }

    [Fact]
    public async Task EnsureAsync_AfterStagingDeactivated_BaseSucceeds_StagingConflicts_OthersSucceed()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        var svc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements());
        var created = (await svc.CreateAsync(new CreateProjectRequest { Key = "site", Name = "Site" })).Data!;

        var updated = await svc.UpdateAsync(created.Id, new UpdateProjectRequest { IsActiveStaging = false });
        Assert.True(updated.IsSuccess);

        // Local + Production still active → not fully inactive → base overload still resolves.
        var baseResult = await svc.EnsureAsync("site");
        Assert.True(baseResult.IsSuccess);

        // The environment-aware overload catches the single deactivated environment.
        var staging = await svc.EnsureAsync("site", EnvironmentTag.Staging);
        Assert.True(staging.IsConflict);

        // The untouched environments keep resolving.
        var local = await svc.EnsureAsync("site", EnvironmentTag.Local);
        Assert.True(local.IsSuccess);
        var production = await svc.EnsureAsync("site", EnvironmentTag.Production);
        Assert.True(production.IsSuccess);
    }

    [Fact]
    public async Task EnsureAsync_AllEnvironmentsDeactivated_BaseOverloadConflicts()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        var svc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements());
        var created = (await svc.CreateAsync(new CreateProjectRequest { Key = "site", Name = "Site" })).Data!;

        var updated = await svc.UpdateAsync(created.Id, new UpdateProjectRequest
        {
            IsActiveLocal = false,
            IsActiveStaging = false,
            IsActiveProduction = false
        });
        Assert.True(updated.IsSuccess);

        // All 3 flags false = fully inactive → the base overload conflicts (old IsActive=false parity).
        var baseResult = await svc.EnsureAsync("site");
        Assert.True(baseResult.IsConflict);
    }

    [Fact]
    public async Task UpdateAsync_AllFlagsTrue_ActivationStateIsActive()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        var svc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements());
        var created = (await svc.CreateAsync(new CreateProjectRequest { Key = "site", Name = "Site" })).Data!;

        var updated = await svc.UpdateAsync(created.Id, new UpdateProjectRequest
        {
            IsActiveLocal = true,
            IsActiveStaging = true,
            IsActiveProduction = true
        });
        Assert.True(updated.IsSuccess);
        Assert.Equal(ProjectActivationState.Active, updated.Data!.ActivationState);
    }

    [Fact]
    public async Task UpdateAsync_MixedFlags_ActivationStateIsPartial()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        var svc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements());
        var created = (await svc.CreateAsync(new CreateProjectRequest { Key = "site", Name = "Site" })).Data!;

        var updated = await svc.UpdateAsync(created.Id, new UpdateProjectRequest
        {
            IsActiveLocal = true,
            IsActiveStaging = false,
            IsActiveProduction = true
        });
        Assert.True(updated.IsSuccess);
        Assert.Equal(ProjectActivationState.Partial, updated.Data!.ActivationState);
    }

    [Fact]
    public async Task UpdateAsync_AllFlagsFalse_ActivationStateIsInactive()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        var svc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements());
        var created = (await svc.CreateAsync(new CreateProjectRequest { Key = "site", Name = "Site" })).Data!;

        var updated = await svc.UpdateAsync(created.Id, new UpdateProjectRequest
        {
            IsActiveLocal = false,
            IsActiveStaging = false,
            IsActiveProduction = false
        });
        Assert.True(updated.IsSuccess);
        Assert.Equal(ProjectActivationState.Inactive, updated.Data!.ActivationState);
    }
}
