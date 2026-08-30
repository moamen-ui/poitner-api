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
/// Quick-access (e.g. "Client") accounts exist only to comment on the one project they were
/// invited to — they must never reach project management, even though ProjectsController is
/// broadly [Authorize] (not admin-gated) for ordinary stakeholders. See ProjectService.CreateAsync/
/// ListAsync's QuickAccessNotAllowed checks.
/// </summary>
public class ProjectServiceQuickAccessTests
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

    private static ProjectService Wire(ICurrentUser user, AppDbContext ctx) =>
        new(new UnitOfWork(ctx), user, new PassThroughEntitlements());

    [Fact]
    public async Task QuickAccessUser_Cannot_ListProjects()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            seed.Projects.Add(new Project { Key = "acme-app", Name = "Acme App", IsActive = true, OwnerId = tenant });
            seed.SaveChanges();
        }

        var client = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant, IsQuickAccess = true };
        var result = await Wire(client, BuildContext(client, db)).ListAsync();

        Assert.False(result.IsSuccess);
        Assert.True(result.IsForbidden);
    }

    [Fact]
    public async Task QuickAccessUser_Cannot_CreateProject()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var client = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant, IsQuickAccess = true };

        var result = await Wire(client, BuildContext(client, db))
            .CreateAsync(new CreateProjectRequest { Key = "new-project", Name = "New Project" });

        Assert.False(result.IsSuccess);
        Assert.True(result.IsForbidden);
        Assert.Empty(BuildContext(client, db).Projects.IgnoreQueryFilters().Where(p => p.Key == "new-project"));
    }

    [Fact]
    public async Task OrdinaryStakeholder_CanStill_ListAndCreateProjects()
    {
        // Sanity: the QuickAccess guard must not regress the deliberately-broadened access for
        // ordinary (non-admin, non-quick-access) stakeholders like Developer/PM/Tester.
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var stakeholder = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant, IsAdmin = false, IsQuickAccess = false };

        var create = await Wire(stakeholder, BuildContext(stakeholder, db))
            .CreateAsync(new CreateProjectRequest { Key = "stakeholder-project", Name = "Stakeholder Project" });
        Assert.True(create.IsSuccess);

        var list = await Wire(stakeholder, BuildContext(stakeholder, db)).ListAsync();
        Assert.True(list.IsSuccess);
        Assert.Contains(list.Data!, p => p.Key == "stakeholder-project");
    }
}
