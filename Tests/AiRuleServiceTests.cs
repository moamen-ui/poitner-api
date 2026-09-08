using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.AiRule;
using Pointer.Application.Services.Implementation;
using Pointer.Domain.Entity;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

public class AiRuleServiceTests
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
    public async Task Admin_CanCreateTenantAndProjectRules()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var adminUser = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = adminUser, IsAdmin = true, TenantId = tenant };

        using (var db = BuildContext(admin, dbName))
        {
            db.Projects.Add(new Project { Id = 1, Key = "proj-1", Name = "Project 1", OwnerId = tenant });
            db.SaveChanges();
        }

        var svc = new AiRuleService(new UnitOfWork(BuildContext(admin, dbName)), admin);

        // 1. Tenant admin rule
        var tenantRes = await svc.CreateAsync(new CreateAiRuleRequest
        {
            Title = "Tenant Tailwind Clean",
            Prompt = "Use Tailwind classes",
            IsPersonal = false
        });
        Assert.True(tenantRes.IsSuccess);
        Assert.True(tenantRes.Data!.IsTenantWide);

        // 2. Project admin rule
        var projRes = await svc.CreateAsync(new CreateAiRuleRequest
        {
            ProjectId = 1,
            Title = "Project Angular Signals",
            Prompt = "Use signals",
            IsPersonal = false
        });
        Assert.True(projRes.IsSuccess);
        Assert.True(projRes.Data!.IsProjectAdminRule);
    }

    [Fact]
    public async Task NonAdmin_CannotCreateTenantOrProjectRule()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var dev = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = false, TenantId = tenant };

        var svc = new AiRuleService(new UnitOfWork(BuildContext(dev, dbName)), dev);

        var res = await svc.CreateAsync(new CreateAiRuleRequest
        {
            Title = "Hacked Admin Rule",
            Prompt = "Forbidden",
            IsPersonal = false
        });

        Assert.False(res.IsSuccess);
        Assert.True(res.IsForbidden);
    }

    [Fact]
    public async Task Developer_CanCreatePersonalRule_AppliedOnlyToTheirComments()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var dev1Id = Guid.NewGuid();
        var dev2Id = Guid.NewGuid();

        var admin = new FakeCurrentUser { Id = adminId, IsAdmin = true, TenantId = tenant };
        var dev1 = new FakeCurrentUser { Id = dev1Id, IsAdmin = false, TenantId = tenant };

        // 1. Seed project and admin rule
        using (var db = BuildContext(admin, dbName))
        {
            db.Projects.Add(new Project { Id = 10, Key = "alpha", Name = "Alpha", OwnerId = tenant });
            db.SaveChanges();
        }

        var adminSvc = new AiRuleService(new UnitOfWork(BuildContext(admin, dbName)), admin);
        await adminSvc.CreateAsync(new CreateAiRuleRequest
        {
            ProjectId = 10,
            Title = "Admin Rule",
            Prompt = "Clean HTML",
            IsPersonal = false
        });

        // 2. Dev 1 adds personal rule
        var dev1Svc = new AiRuleService(new UnitOfWork(BuildContext(dev1, dbName)), dev1);
        var dev1RuleRes = await dev1Svc.CreateAsync(new CreateAiRuleRequest
        {
            ProjectId = 10,
            Title = "Dev 1 Rule",
            Prompt = "Personal style preference",
            IsPersonal = true
        });
        Assert.True(dev1RuleRes.IsSuccess);
        Assert.True(dev1RuleRes.Data!.IsPersonal);

        // 3. Check effective rules for dev 1 comment
        var dev1Rules = await adminSvc.GetEffectiveRulesForCommentAsync(10, dev1Id);
        Assert.Equal(2, dev1Rules.Count);
        Assert.Contains(dev1Rules, r => r.Title == "Admin Rule" && !r.IsPersonal);
        Assert.Contains(dev1Rules, r => r.Title == "Dev 1 Rule" && r.IsPersonal);

        // 4. Check effective rules for dev 2 comment (should NOT have Dev 1's rule)
        var dev2Rules = await adminSvc.GetEffectiveRulesForCommentAsync(10, dev2Id);
        Assert.Single(dev2Rules);
        Assert.Equal("Admin Rule", dev2Rules[0].Title);
        Assert.False(dev2Rules[0].IsPersonal);
    }

    [Fact]
    public async Task Developer_CannotEditOrDeleteAdminRule()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var devId = Guid.NewGuid();

        var admin = new FakeCurrentUser { Id = adminId, IsAdmin = true, TenantId = tenant };
        var dev = new FakeCurrentUser { Id = devId, IsAdmin = false, TenantId = tenant };

        var adminSvc = new AiRuleService(new UnitOfWork(BuildContext(admin, dbName)), admin);
        var created = (await adminSvc.CreateAsync(new CreateAiRuleRequest
        {
            Title = "Admin Rule",
            Prompt = "Clean code",
            IsPersonal = false
        })).Data!;

        var devSvc = new AiRuleService(new UnitOfWork(BuildContext(dev, dbName)), dev);
        var updateRes = await devSvc.UpdateAsync(created.Id, new UpdateAiRuleRequest { Title = "Hacked" });
        Assert.False(updateRes.IsSuccess);
        Assert.True(updateRes.IsForbidden);

        var deleteRes = await devSvc.DeleteAsync(created.Id);
        Assert.False(deleteRes.IsSuccess);
        Assert.True(deleteRes.IsForbidden);
    }

    [Fact]
    public async Task Insights_AggregatesCountsAndToolUsage()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };

        using (var db = BuildContext(admin, dbName))
        {
            db.Projects.Add(new Project { Id = 1, Key = "p1", Name = "P1", OwnerId = tenant, AiToolsUsed = "[\"claude-code\", \"cursor\"]" });
            db.Projects.Add(new Project { Id = 2, Key = "p2", Name = "P2", OwnerId = tenant, AiToolsUsed = "[\"claude-code\"]" });
            db.SaveChanges();
        }

        var svc = new AiRuleService(new UnitOfWork(BuildContext(admin, dbName)), admin);
        await svc.CreateAsync(new CreateAiRuleRequest { Title = "T1", Prompt = "P1", IsPersonal = false });
        await svc.CreateAsync(new CreateAiRuleRequest { ProjectId = 1, Title = "P1", Prompt = "P2", IsPersonal = false });
        await svc.CreateAsync(new CreateAiRuleRequest { Title = "UserRule", Prompt = "P3", IsPersonal = true });

        var insights = (await svc.GetInsightsAsync()).Data!;

        Assert.Equal(3, insights.TotalRulesCount);
        Assert.Equal(1, insights.TenantRulesCount);
        Assert.Equal(1, insights.ProjectRulesCount);
        Assert.Equal(1, insights.UserPersonalRulesCount);

        var claudeStat = insights.ToolUsage.FirstOrDefault(t => t.ToolName == "claude-code");
        Assert.NotNull(claudeStat);
        Assert.Equal(2, claudeStat.ProjectCount);

        var cursorStat = insights.ToolUsage.FirstOrDefault(t => t.ToolName == "cursor");
        Assert.NotNull(cursorStat);
        Assert.Equal(1, cursorStat.ProjectCount);
    }
}
