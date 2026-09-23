using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.AppEnvironment;
using Pointer.Application.Services.Implementation;
using Pointer.Domain.Entity;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

public class AppEnvironmentEnabledFlagTests
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

    private static int SeedGlobalEnvironment(string dbName, string name = "prod")
    {
        using var db = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        var env = new AppEnvironment
        {
            Name = name,
            OwnerId = null,
            IsEnabled = true,
        };
        db.AppEnvironments.Add(env);
        db.SaveChanges();
        return env.Id;
    }

    [Fact]
    public async Task CreateAsync_DefaultsToEnabled()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { IsAdmin = true, TenantId = tenant };
        var svc = new AppEnvironmentService(new UnitOfWork(BuildContext(admin, dbName)), admin);

        var created = await svc.CreateAsync(new CreateAppEnvironmentRequest { Name = "my-env" });
        Assert.True(created.IsSuccess);
        Assert.True(created.Data!.IsEnabled);
    }

    [Fact]
    public async Task UpdateAsync_TogglesEnabledUnderCanManage()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { IsAdmin = true, TenantId = tenant };
        var svc = new AppEnvironmentService(new UnitOfWork(BuildContext(admin, dbName)), admin);

        var created = await svc.CreateAsync(new CreateAppEnvironmentRequest { Name = "my-env" });

        var update = await svc.UpdateAsync(
            created.Data!.Id,
            new UpdateAppEnvironmentRequest { IsEnabled = false }
        );
        Assert.True(update.IsSuccess);
        Assert.False(update.Data!.IsEnabled);
    }

    [Fact]
    public async Task UpdateAsync_CannotToggleGlobalEnvironment()
    {
        var dbName = Guid.NewGuid().ToString();
        var globalId = SeedGlobalEnvironment(dbName);
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { IsAdmin = true, TenantId = tenant };
        var svc = new AppEnvironmentService(new UnitOfWork(BuildContext(admin, dbName)), admin);

        var update = await svc.UpdateAsync(
            globalId,
            new UpdateAppEnvironmentRequest { IsEnabled = false }
        );
        Assert.True(update.IsForbidden);
    }

    [Fact]
    public async Task ListAsync_IncludesProjectUrlCountAndSortsCorrectly()
    {
        var dbName = Guid.NewGuid().ToString();
        var globalId = SeedGlobalEnvironment(dbName, "global");
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            IsAdmin = true,
            TenantId = tenant,
        };

        using (var db = BuildContext(admin, dbName))
        {
            var project = new Project
            {
                Key = "p",
                Name = "P",
                IsActiveLocal = true,
                IsActiveStaging = true,
                IsActiveProduction = true,
                OwnerId = tenant,
            };
            db.Projects.Add(project);
            db.SaveChanges();

            db.AppEnvironments.Add(
                new AppEnvironment
                {
                    Name = "a-tenant",
                    OwnerId = tenant,
                    IsEnabled = true,
                }
            );
            db.AppEnvironments.Add(
                new AppEnvironment
                {
                    Name = "z-tenant",
                    OwnerId = tenant,
                    IsEnabled = true,
                }
            );
            db.SaveChanges();

            var aEnvId = await db
                .AppEnvironments.Where(e => e.Name == "a-tenant")
                .Select(e => e.Id)
                .FirstAsync();
            db.ProjectAppUrls.Add(
                new ProjectAppUrl
                {
                    ProjectId = project.Id,
                    AppEnvironmentId = aEnvId,
                    Url = "u",
                    OwnerId = tenant,
                    IsActive = true,
                }
            );
            db.ProjectAppUrls.Add(
                new ProjectAppUrl
                {
                    ProjectId = project.Id,
                    AppEnvironmentId = globalId,
                    Url = "u2",
                    OwnerId = tenant,
                    IsActive = true,
                }
            );
            db.SaveChanges();
        }

        var svc = new AppEnvironmentService(new UnitOfWork(BuildContext(admin, dbName)), admin);
        var list = (await svc.ListAsync()).Data!;

        // Sort order: Global vs Tenant (Global first), then Name.
        // So: global, a-tenant, z-tenant.
        Assert.Equal("global", list[0].Name);
        Assert.Equal("a-tenant", list[1].Name);
        Assert.Equal("z-tenant", list[2].Name);

        Assert.Equal(1, list[0].ProjectUrlCount);
        Assert.Equal(1, list[1].ProjectUrlCount);
        Assert.Equal(0, list[2].ProjectUrlCount);
    }
}
