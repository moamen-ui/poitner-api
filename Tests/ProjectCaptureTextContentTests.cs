using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Project;
using Pointer.Application.Services.Implementation;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

public class ProjectCaptureTextContentTests
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

    private static AppDbContext BuildContext(ICurrentUser user, string dbName) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options,
            user,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
        );

    [Fact]
    public async Task Project_CaptureTextContent_DefaultsToTrue()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            IsAdmin = true,
            TenantId = tenant,
        };
        var svc = new ProjectService(
            new UnitOfWork(BuildContext(admin, dbName)),
            admin,
            new PassThroughEntitlements(),
            TestProjectServiceDeps.Settings(),
            TestProjectServiceDeps.Configuration(),
            new FakeAuditWriter()
        );

        var created = (
            await svc.CreateAsync(
                new CreateProjectRequest { Key = "privacy-test", Name = "Privacy Test" }
            )
        ).Data!;

        Assert.True(created.CaptureTextContent);

        var config = (await svc.GetCaptureConfigAsync("privacy-test")).Data!;
        Assert.True(config.CaptureTextContent);
    }

    [Fact]
    public async Task UpdateAsync_ByAdminOrCreator_TogglesCaptureTextContent_AndCaptureConfigReflectsIt()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var creatorId = Guid.NewGuid();
        var creator = new FakeCurrentUser
        {
            Id = creatorId,
            IsAdmin = false,
            TenantId = tenant,
        };
        var uow = new UnitOfWork(BuildContext(creator, dbName));
        var creatorSvc = new ProjectService(
            uow,
            creator,
            new PassThroughEntitlements(),
            TestProjectServiceDeps.Settings(),
            TestProjectServiceDeps.Configuration(),
            new FakeAuditWriter()
        );

        var created = (
            await creatorSvc.CreateAsync(
                new CreateProjectRequest { Key = "privacy-test", Name = "Privacy Test" }
            )
        ).Data!;
        Assert.True(created.CaptureTextContent);

        // Creator toggles off
        var updateResult = await creatorSvc.UpdateAsync(
            created.Id,
            new UpdateProjectRequest { CaptureTextContent = false }
        );
        Assert.True(updateResult.IsSuccess);
        Assert.False(updateResult.Data!.CaptureTextContent);

        var configAfterOff = (await creatorSvc.GetCaptureConfigAsync("privacy-test")).Data!;
        Assert.False(configAfterOff.CaptureTextContent);

        // Admin toggles back on
        var admin = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            IsAdmin = true,
            TenantId = tenant,
        };
        var adminUow = new UnitOfWork(BuildContext(admin, dbName));
        var adminSvc = new ProjectService(
            adminUow,
            admin,
            new PassThroughEntitlements(),
            TestProjectServiceDeps.Settings(),
            TestProjectServiceDeps.Configuration(),
            new FakeAuditWriter()
        );

        var adminUpdateResult = await adminSvc.UpdateAsync(
            created.Id,
            new UpdateProjectRequest { CaptureTextContent = true }
        );
        Assert.True(adminUpdateResult.IsSuccess);
        Assert.True(adminUpdateResult.Data!.CaptureTextContent);

        var configAfterOn = (await adminSvc.GetCaptureConfigAsync("privacy-test")).Data!;
        Assert.True(configAfterOn.CaptureTextContent);
    }

    [Fact]
    public async Task UpdateAsync_ByOtherUser_ReturnsForbidden()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var creatorId = Guid.NewGuid();
        var creator = new FakeCurrentUser
        {
            Id = creatorId,
            IsAdmin = false,
            TenantId = tenant,
        };
        var creatorSvc = new ProjectService(
            new UnitOfWork(BuildContext(creator, dbName)),
            creator,
            new PassThroughEntitlements(),
            TestProjectServiceDeps.Settings(),
            TestProjectServiceDeps.Configuration(),
            new FakeAuditWriter()
        );

        var created = (
            await creatorSvc.CreateAsync(
                new CreateProjectRequest { Key = "privacy-test", Name = "Privacy Test" }
            )
        ).Data!;

        // Other non-admin user in same tenant
        var otherUser = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            IsAdmin = false,
            TenantId = tenant,
        };
        var otherSvc = new ProjectService(
            new UnitOfWork(BuildContext(otherUser, dbName)),
            otherUser,
            new PassThroughEntitlements(),
            TestProjectServiceDeps.Settings(),
            TestProjectServiceDeps.Configuration(),
            new FakeAuditWriter()
        );

        var forbiddenUpdate = await otherSvc.UpdateAsync(
            created.Id,
            new UpdateProjectRequest { CaptureTextContent = false }
        );
        Assert.False(forbiddenUpdate.IsSuccess);
        Assert.True(forbiddenUpdate.IsForbidden);
    }
}
