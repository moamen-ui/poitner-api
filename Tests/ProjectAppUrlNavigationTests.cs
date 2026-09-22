using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Domain.Entity;
using Pointer.Infrastructure;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-04: ProjectAppUrlMapping named the Project&lt;-&gt;ProjectAppUrl inverse explicitly instead of
/// leaving `WithMany()` empty, which used to make EF add a second, shadow relationship keyed on a
/// shadow "ProjectId1" column that nothing ever wrote to. Fixture copied from
/// Tests/CommentFieldsTests.cs (in-memory AppDbContext + FakeCurrentUser, super-admin so the
/// tenant query filters do not interfere with this navigation-only check).
/// </summary>
public class ProjectAppUrlNavigationTests
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

    private static AppDbContext BuildContext(string dbName) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options,
            new FakeCurrentUser { IsSuperAdmin = true },
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
        );

    [Fact]
    public void Project_ProjectAppUrls_LoadsThroughProjectId()
    {
        var dbName = Guid.NewGuid().ToString();
        var owner = Guid.NewGuid();

        using (var db = BuildContext(dbName))
        {
            var env = new AppEnvironment { Name = "default", OwnerId = owner };
            var project = new Project
            {
                Key = "proj",
                Name = "Proj",
                OwnerId = owner,
            };
            db.AppEnvironments.Add(env);
            db.Projects.Add(project);
            db.SaveChanges();

            db.ProjectAppUrls.Add(
                new ProjectAppUrl
                {
                    ProjectId = project.Id,
                    AppEnvironmentId = env.Id,
                    Url = "https://example.test",
                    OwnerId = owner,
                }
            );
            db.SaveChanges();
        }

        using var fresh = BuildContext(dbName);
        var loaded = fresh.Projects.Include(p => p.ProjectAppUrls).Single();

        Assert.Single(loaded.ProjectAppUrls);
    }

    [Fact]
    public void ProjectAppUrl_Model_HasNoShadowProjectId1()
    {
        using var db = BuildContext(Guid.NewGuid().ToString());

        var entityType = db.Model.FindEntityType(typeof(ProjectAppUrl))!;

        Assert.DoesNotContain("ProjectId1", entityType.GetProperties().Select(p => p.Name));
        // 3, not the historical 2: DB-03 added fk_project_app_urls_workspaces_owner_id alongside the
        // existing Project and AppEnvironment FKs. The DoesNotContain assertion above is this test's
        // actual DB-04 regression guard (no shadow ProjectId1 reappears).
        Assert.Equal(3, entityType.GetForeignKeys().Count());
    }
}
