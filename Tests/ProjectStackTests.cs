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
/// Project stack registration: `frontend`/`backend` are write-once-if-empty (a second call with a
/// different payload is a no-op), while `aiTool` is append-if-new — a project can legitimately be
/// touched by more than one AI coding tool over its lifetime, so that field is never write-once.
/// </summary>
public class ProjectStackTests
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

    private static ProjectService BuildService(ICurrentUser user, string dbName) =>
        new(new UnitOfWork(BuildContext(user, dbName)), user, new PassThroughEntitlements());

    private static (string dbName, Guid tenant) SeedProject(string key)
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        using var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, db);
        seed.Projects.Add(new Project { Key = key, Name = "Proj", IsActiveLocal = true, IsActiveStaging = true, IsActiveProduction = true, OwnerId = tenant });
        seed.SaveChanges();
        return (db, tenant);
    }

    private static FakeCurrentUser MemberOf(Guid tenant) => new() { Id = Guid.NewGuid(), TenantId = tenant };

    [Fact]
    public async Task SetStack_FirstCall_SetsFrontendAndBackend()
    {
        var (db, tenant) = SeedProject("proj");
        var svc = BuildService(MemberOf(tenant), db);

        var result = await svc.SetStackAsync("proj", new SetProjectStackRequest
        {
            Frontend = new List<string> { "react", "tailwind" },
            Backend = new List<string> { "dotnet", "postgres" }
        });

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "react", "tailwind" }, result.Data!.Frontend);
        Assert.Equal(new[] { "dotnet", "postgres" }, result.Data!.Backend);
    }

    [Fact]
    public async Task SetStack_SecondCallWithDifferentPayload_IsNoOp_ReturnsOriginalValue()
    {
        var (db, tenant) = SeedProject("proj");
        var svc = BuildService(MemberOf(tenant), db);

        await svc.SetStackAsync("proj", new SetProjectStackRequest { Frontend = new() { "react" }, Backend = new() { "dotnet" } });
        var second = await svc.SetStackAsync("proj", new SetProjectStackRequest { Frontend = new() { "vue" }, Backend = new() { "go" } });

        Assert.True(second.IsSuccess);
        Assert.Equal(new[] { "react" }, second.Data!.Frontend); // original value, not overwritten
        Assert.Equal(new[] { "dotnet" }, second.Data!.Backend);
    }

    [Fact]
    public async Task SetStack_AiTool_AppendsRatherThanReplaces()
    {
        var (db, tenant) = SeedProject("proj");
        var svc = BuildService(MemberOf(tenant), db);

        await svc.SetStackAsync("proj", new SetProjectStackRequest { AiTool = "claude-code" });
        var second = await svc.SetStackAsync("proj", new SetProjectStackRequest { AiTool = "opencode-glm" });

        Assert.True(second.IsSuccess);
        Assert.Equal(new[] { "claude-code", "opencode-glm" }, second.Data!.AiTools);
    }

    [Fact]
    public async Task SetStack_AiTool_RepeatedValue_IsDeduplicated()
    {
        var (db, tenant) = SeedProject("proj");
        var svc = BuildService(MemberOf(tenant), db);

        await svc.SetStackAsync("proj", new SetProjectStackRequest { AiTool = "claude-code" });
        var second = await svc.SetStackAsync("proj", new SetProjectStackRequest { AiTool = "claude-code" });
        var third = await svc.SetStackAsync("proj", new SetProjectStackRequest { AiTool = "Claude-Code" }); // case-insensitive dedup

        Assert.Single(third.Data!.AiTools);
    }

    [Fact]
    public async Task SetStack_AiToolOnly_DoesNotTouchFrontendBackend()
    {
        // The lightweight per-apply-run check-in (skill.md sends only aiTool, no frontend/backend)
        // must be a safe no-op on TechStack — confirms the corrected design (aiTool reported on
        // every apply run, not just at init/self-heal time).
        var (db, tenant) = SeedProject("proj");
        var svc = BuildService(MemberOf(tenant), db);

        var result = await svc.SetStackAsync("proj", new SetProjectStackRequest { AiTool = "cursor" });

        Assert.True(result.IsSuccess);
        Assert.Null(result.Data!.Frontend);
        Assert.Null(result.Data!.Backend);
        Assert.Equal(new[] { "cursor" }, result.Data!.AiTools);
    }

    [Fact]
    public async Task GetStack_ReturnsStoredValue()
    {
        var (db, tenant) = SeedProject("proj");
        var setter = BuildService(MemberOf(tenant), db);
        await setter.SetStackAsync("proj", new SetProjectStackRequest { Frontend = new() { "angular" }, AiTool = "windsurf" });

        var reader = BuildService(MemberOf(tenant), db);
        var result = await reader.GetStackAsync("proj");

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "angular" }, result.Data!.Frontend);
        Assert.Equal(new[] { "windsurf" }, result.Data!.AiTools);
    }

    [Fact]
    public async Task GetStack_Unset_ReturnsNullsAndEmptyAiTools()
    {
        var (db, tenant) = SeedProject("proj");
        var svc = BuildService(MemberOf(tenant), db);

        var result = await svc.GetStackAsync("proj");

        Assert.True(result.IsSuccess);
        Assert.Null(result.Data!.Frontend);
        Assert.Null(result.Data!.Backend);
        Assert.Empty(result.Data!.AiTools);
    }

    [Fact]
    public async Task SetStack_NonAdminAuthenticatedMember_CanCallIt()
    {
        // Confirms not admin-gated — matches comment-creation's precedent.
        var (db, tenant) = SeedProject("proj");
        var nonAdmin = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant, IsAdmin = false };
        var svc = BuildService(nonAdmin, db);

        var result = await svc.SetStackAsync("proj", new SetProjectStackRequest { AiTool = "opencode-glm" });

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task GetStacksSummary_AggregatesAcrossTenants_AnonymizedCountsOnly()
    {
        var db = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            seed.Projects.AddRange(
                new Project { Key = "a", Name = "A", IsActiveLocal = true, IsActiveStaging = true, IsActiveProduction = true, OwnerId = tenantA, TechStack = "{\"frontend\":[\"react\"],\"backend\":[\"dotnet\"]}", AiToolsUsed = "[\"claude-code\"]" },
                new Project { Key = "b", Name = "B", IsActiveLocal = true, IsActiveStaging = true, IsActiveProduction = true, OwnerId = tenantB, TechStack = "{\"frontend\":[\"react\",\"tailwind\"],\"backend\":null}", AiToolsUsed = "[\"claude-code\",\"opencode-glm\"]" },
                new Project { Key = "c", Name = "C (no stack)", IsActiveLocal = true, IsActiveStaging = true, IsActiveProduction = true, OwnerId = tenantB });
            await seed.SaveChangesAsync();
        }

        var svc = BuildService(new FakeCurrentUser(), db); // anonymous caller — no tenant claim at all
        var result = await svc.GetStacksSummaryAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Data!.TotalProjects); // project "c" excluded (no stack/aiTools at all)
        Assert.Equal(2, result.Data!.Frontend["react"]);
        Assert.Equal(1, result.Data!.Frontend["tailwind"]);
        Assert.Equal(1, result.Data!.Backend["dotnet"]);
        Assert.Equal(2, result.Data!.AiTools["claude-code"]);
        Assert.Equal(1, result.Data!.AiTools["opencode-glm"]);
    }

    [Fact]
    public async Task GetStacksSummary_ExcludesSoftDeletedAndInactiveProjects()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();

        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            seed.Projects.AddRange(
                new Project { Key = "active", Name = "Active", IsActiveLocal = true, IsActiveStaging = true, IsActiveProduction = true, OwnerId = tenant, TechStack = "{\"frontend\":[\"vue\"]}" },
                new Project { Key = "inactive", Name = "Inactive", IsActiveLocal = false, IsActiveStaging = false, IsActiveProduction = false, OwnerId = tenant, TechStack = "{\"frontend\":[\"vue\"]}" },
                new Project { Key = "deleted", Name = "Deleted", IsActiveLocal = true, IsActiveStaging = true, IsActiveProduction = true, OwnerId = tenant, DeletedAt = DateTime.UtcNow, TechStack = "{\"frontend\":[\"vue\"]}" });
            await seed.SaveChangesAsync();
        }

        var svc = BuildService(new FakeCurrentUser(), db);
        var result = await svc.GetStacksSummaryAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Data!.TotalProjects);
        Assert.Equal(1, result.Data!.Frontend["vue"]);
    }
}
