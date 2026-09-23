using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Comment;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// Predefined-action snapshot on comment-create:
///  (b) serialized comment JSON must contain no prompt / PickedActionPrompt key.
///  (c) an out-of-scope / inactive predefinedActionId is rejected (not silently dropped).
/// </summary>
public class PredefinedActionCommentTests
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

    private sealed class FakeFileStorage : IFileStorage
    {
        public Task<string> SaveAsync(
            string ownerSegment,
            string project,
            Stream content,
            string extension
        ) => Task.FromResult("uploads/x");

        public Task DeleteAsync(string relativePathOrUrl) => Task.CompletedTask;

        public Task DeleteOwnerFilesAsync(string ownerSegment) => Task.CompletedTask;
    }

    private sealed class FakeUploadSigner : IUploadSigner
    {
        public string SignedUrl(string relPath) => relPath;

        public bool Validate(string relPath, long exp, string sig) => true;

        public string ExtractRelPath(string stored) => stored;
    }

    private sealed class FakeSettings : ISettingsService
    {
        public Task<bool> GetBoolAsync(string key, bool fallback = false) =>
            Task.FromResult(fallback);

        public Task SetBoolAsync(string key, bool value) => Task.CompletedTask;

        public Task<string> GetStringAsync(string key, string fallback = "") =>
            Task.FromResult(fallback);

        public Task SetStringAsync(string key, string value) => Task.CompletedTask;

        public Task<int> GetIntAsync(string key, int fallback = 0) => Task.FromResult(fallback);

        public Task SetIntAsync(string key, int value) => Task.CompletedTask;
    }

    private static AppDbContext BuildContext(ICurrentUser user, string dbName) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options,
            user,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
        );

    private sealed class Harness
    {
        public required AppDbContext Db { get; init; }
        public required CommentService CommentService { get; init; }
        public required Guid TenantId { get; init; }
        public required Guid AuthorId { get; init; }
    }

    // Builds a tenant, an active project, and returns a CommentService wired to that tenant's context.
    private static Harness BuildHarness(string dbName)
    {
        var tenant = Guid.NewGuid();
        var author = Guid.NewGuid();

        // Seed the project via a super-admin context (bypasses filters cleanly on insert).
        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            seed.Projects.Add(
                new Project
                {
                    Key = "proj",
                    Name = "Proj",
                    IsActiveLocal = true,
                    IsActiveStaging = true,
                    IsActiveProduction = true,
                    OwnerId = tenant,
                }
            );
            seed.SaveChanges();
        }

        var user = new FakeCurrentUser
        {
            Id = author,
            TenantId = tenant,
            IsSuperAdmin = false,
        };
        var db = BuildContext(user, dbName);
        var uow = new UnitOfWork(db);
        var projectService = new ProjectService(
            uow,
            user,
            new PassThroughEntitlements(),
            TestProjectServiceDeps.Settings(),
            TestProjectServiceDeps.Configuration(),
            new FakeAuditWriter()
        );
        var actionService = new PredefinedActionService(
            uow,
            projectService,
            user,
            new PassThroughEntitlements()
        );
        var commentService = new CommentService(
            uow,
            projectService,
            actionService,
            new FakeFileStorage(),
            user,
            new FakeUploadSigner(),
            new FakeSettings(),
            new PassThroughEntitlements()
        );

        return new Harness
        {
            Db = db,
            CommentService = commentService,
            TenantId = tenant,
            AuthorId = author,
        };
    }

    private static CreateCommentRequest Req(params int[] actionIds) =>
        new()
        {
            Body = "hello",
            Environment = EnvironmentTag.Local,
            PredefinedActionIds = actionIds.Length > 0 ? actionIds.ToList() : null,
            Element = new ElementCaptureDto(),
        };

    // ── (b) prompt never serializes ────────────────────────────────────────────

    [Fact]
    public async Task CommentJson_ContainsNo_PromptKey()
    {
        var h = BuildHarness(Guid.NewGuid().ToString());

        // Seed an active, in-scope tenant-wide action and pick it.
        h.Db.PredefinedActions.Add(
            new PredefinedAction
            {
                OwnerId = h.TenantId,
                Text = "Make it pop",
                Prompt = "SECRET-LLM-PROMPT",
                IsActive = true,
            }
        );
        await h.Db.SaveChangesAsync();
        var actionId = h.Db.PredefinedActions.Single().Id;

        var result = await h.CommentService.CreateAsync("proj", Req(actionId), h.AuthorId);
        Assert.True(result.IsSuccess);
        Assert.Contains("Make it pop", result.Data!.PickedActionTexts);

        // Serialize the way the API would (envelope + inner DTO).
        var json = JsonSerializer.Serialize(result.Data);
        Assert.DoesNotContain("SECRET-LLM-PROMPT", json);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PickedActionPrompt", json);

        // The label (text) still round-trips.
        Assert.Contains("Make it pop", json);
    }

    // ── (c) invalid / out-of-scope / inactive id is rejected ────────────────────

    [Fact]
    public async Task CommentCreate_RejectsInactiveAction()
    {
        var h = BuildHarness(Guid.NewGuid().ToString());
        h.Db.PredefinedActions.Add(
            new PredefinedAction
            {
                OwnerId = h.TenantId,
                Text = "Disabled",
                Prompt = "p",
                IsActive = false,
            }
        );
        await h.Db.SaveChangesAsync();
        var id = h.Db.PredefinedActions.Single().Id;

        var result = await h.CommentService.CreateAsync("proj", Req(id), h.AuthorId);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task CommentCreate_RejectsNonexistentActionId()
    {
        var h = BuildHarness(Guid.NewGuid().ToString());
        var result = await h.CommentService.CreateAsync("proj", Req(999999), h.AuthorId);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task CommentCreate_RejectsAction_ScopedToDifferentProject()
    {
        var dbName = Guid.NewGuid().ToString();
        var h = BuildHarness(dbName);

        // A project-scoped action bound to a DIFFERENT project id (999) — out of scope for "proj".
        h.Db.PredefinedActions.Add(
            new PredefinedAction
            {
                OwnerId = h.TenantId,
                ProjectId = 999,
                Text = "Other",
                Prompt = "p",
                IsActive = true,
            }
        );
        await h.Db.SaveChangesAsync();
        var id = h.Db.PredefinedActions.Single().Id;

        var result = await h.CommentService.CreateAsync("proj", Req(id), h.AuthorId);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task CommentCreate_RejectsAction_OwnedByAnotherTenant()
    {
        var dbName = Guid.NewGuid().ToString();
        var h = BuildHarness(dbName);
        var otherTenant = Guid.NewGuid();

        h.Db.PredefinedActions.Add(
            new PredefinedAction
            {
                OwnerId = otherTenant,
                Text = "Foreign",
                Prompt = "p",
                IsActive = true,
            }
        );
        await h.Db.SaveChangesAsync();
        var id = h
            .Db.PredefinedActions.IgnoreQueryFilters()
            .Single(a => a.OwnerId == otherTenant)
            .Id;

        var result = await h.CommentService.CreateAsync("proj", Req(id), h.AuthorId);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task CommentCreate_AllowsValidInScopeAction()
    {
        var h = BuildHarness(Guid.NewGuid().ToString());
        h.Db.PredefinedActions.Add(
            new PredefinedAction
            {
                OwnerId = h.TenantId,
                Text = "Valid",
                Prompt = "p",
                IsActive = true,
            }
        );
        await h.Db.SaveChangesAsync();
        var id = h.Db.PredefinedActions.Single().Id;

        var result = await h.CommentService.CreateAsync("proj", Req(id), h.AuthorId);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task CommentGetById_And_ApplyQueue_CarriesAiRules_WithStrictPriority()
    {
        var h = BuildHarness(Guid.NewGuid().ToString());
        var proj = h.Db.Projects.Single(p => p.Key == "proj");

        // 1. Create a comment
        var createRes = await h.CommentService.CreateAsync("proj", Req(), h.AuthorId);
        Assert.True(createRes.IsSuccess);
        var commentId = createRes.Data!.Id;

        // 2. Add AI rules in reverse order
        h.Db.AiRules.AddRange(
            new AiRule
            {
                OwnerId = h.TenantId,
                ProjectId = proj.Id,
                UserId = h.AuthorId,
                Title = "Dev Personal",
                Prompt = "p-dev",
                IsActive = true,
                SortOrder = 1,
            },
            new AiRule
            {
                OwnerId = h.TenantId,
                ProjectId = proj.Id,
                UserId = null,
                Title = "Project Rule",
                Prompt = "p-proj",
                IsActive = true,
                SortOrder = 2,
            },
            new AiRule
            {
                OwnerId = h.TenantId,
                ProjectId = null,
                UserId = null,
                Title = "Workspace Rule",
                Prompt = "p-ws",
                IsActive = true,
                SortOrder = 3,
            }
        );
        await h.Db.SaveChangesAsync();

        // 3. Verify GetByIdAsync
        var getRes = await h.CommentService.GetByIdAsync(commentId, h.AuthorId);
        Assert.True(getRes.IsSuccess);
        var rules = getRes.Data!.AiRules;
        Assert.Equal(3, rules.Count);
        Assert.Equal("Workspace Rule", rules[0].Title);
        Assert.Equal("Workspace", rules[0].Scope);
        Assert.Equal(1, rules[0].Priority);

        Assert.Equal("Project Rule", rules[1].Title);
        Assert.Equal("Project", rules[1].Scope);
        Assert.Equal(2, rules[1].Priority);

        Assert.Equal("Dev Personal", rules[2].Title);
        Assert.Equal("Personal", rules[2].Scope);
        Assert.Equal(3, rules[2].Priority);

        // 4. Verify ListApplyQueueAsync
        var queueRes = await h.CommentService.ListApplyQueueAsync("proj", new CommentFilter());
        Assert.True(queueRes.IsSuccess);
        var queueItem = queueRes.Data!.Items.Single(c => c.Id == commentId);
        Assert.Equal(3, queueItem.AiRules.Count);
        Assert.Equal("Workspace Rule", queueItem.AiRules[0].Title);
        Assert.Equal("Workspace", queueItem.AiRules[0].Scope);
        Assert.Equal(1, queueItem.AiRules[0].Priority);

        Assert.Equal("Project Rule", queueItem.AiRules[1].Title);
        Assert.Equal("Project", queueItem.AiRules[1].Scope);
        Assert.Equal(2, queueItem.AiRules[1].Priority);

        Assert.Equal("Dev Personal", queueItem.AiRules[2].Title);
        Assert.Equal("Personal", queueItem.AiRules[2].Scope);
        Assert.Equal(3, queueItem.AiRules[2].Priority);
    }

    // TenantAdmin_CannotEdit_NullOwnerGlobalAction and NullOwnerProject_ActionResolvesOnCommentCreate
    // were removed: both seeded a null-owner PredefinedAction/Project, which is no longer a
    // constructible state at all — projects.owner_id and predefined_actions.owner_id are DB-enforced
    // NOT NULL (super admins can no longer create/own either; see ProjectService.CreateAsync /
    // PredefinedActionService.CreateTenantAsync and the EnforceOwnerIdNotNull migration). The
    // isolation concern the first test guarded (a tenant admin editing a global resource by guessing
    // its id) no longer applies to this entity — there's no null-owner row left to guess.
}
