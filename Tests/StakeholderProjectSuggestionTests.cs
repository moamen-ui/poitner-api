using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.InMemory.Infrastructure.Internal;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.PredefinedAction;
using Pointer.Application.DTOs.Project;
using Pointer.Application.DTOs.Suggestion;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// Server-side authorization + suggestion flow for the stakeholder-projects feature:
///  - Project update/delete authz (owner / non-owner / cross-tenant / admin cascade).
///  - Admin cascade deletes ONLY that project's comments (a sibling project's survive).
///  - Widget read never carries a prompt.
///  - Suggestion create → approve mints a tenant-scoped action + re-validates project; reject; isolation.
/// </summary>
public class StakeholderProjectSuggestionTests
{
    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; }
        public Guid? TenantId { get; set; }
    }

    private sealed class FakeFileStorage : IFileStorage
    {
        public Task<string> SaveAsync(string ownerSegment, string project, Stream content, string extension) => Task.FromResult("uploads/x");
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
        public Task<bool> GetBoolAsync(string key, bool fallback = false) => Task.FromResult(fallback);
        public Task SetBoolAsync(string key, bool value) => Task.CompletedTask;
        public Task<string> GetStringAsync(string key, string fallback = "") => Task.FromResult(fallback);
        public Task SetStringAsync(string key, string value) => Task.CompletedTask;
        public Task<int> GetIntAsync(string key, int fallback = 0) => Task.FromResult(fallback);
        public Task SetIntAsync(string key, int value) => Task.CompletedTask;
    }

    private sealed class FakeEmail : IEmailService
    {
        public int Sent { get; private set; }
        public Task<bool> SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
        {
            Sent++;
            return Task.FromResult(true);
        }
    }

    // InMemory provider throws on BeginTransactionAsync unless the transaction warning is ignored —
    // required because the admin delete cascade runs inside ExecuteInTransactionAsync.
    private static AppDbContext BuildContext(ICurrentUser user, string dbName) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options, user);

    private static (ProjectService project, UnitOfWork uow, AppDbContext db) Wire(ICurrentUser user, string dbName)
    {
        var db = BuildContext(user, dbName);
        var uow = new UnitOfWork(db);
        return (new ProjectService(uow, user), uow, db);
    }

    // Seed a tenant with one active project. Returns (tenantId, projectId, creatorId).
    private static (Guid tenant, int projectId, Guid creator) SeedProject(string dbName, string key = "proj", Guid? creatorOverride = null)
    {
        var tenant = Guid.NewGuid();
        var creator = creatorOverride ?? Guid.NewGuid();
        // Create the project under the creator's identity so CreatedBy is stamped.
        var user = new FakeCurrentUser { Id = creator, TenantId = tenant, IsSuperAdmin = false };
        var (svc, _, db) = Wire(user, dbName);
        var res = svc.CreateAsync(new CreateProjectRequest { Key = key, Name = key }).GetAwaiter().GetResult();
        Assert.True(res.IsSuccess);
        db.Dispose();
        return (tenant, res.Data!.Id, creator);
    }

    private static CreateProjectRequest ProjReq(string key) => new() { Key = key, Name = key };

    // ── Project UPDATE authz ────────────────────────────────────────────────────

    [Fact]
    public async Task Owner_CanUpdate_OwnProject()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, pid, creator) = SeedProject(dbName);

        var user = new FakeCurrentUser { Id = creator, TenantId = tenant };
        var (svc, _, _) = Wire(user, dbName);
        var res = await svc.UpdateAsync(pid, new UpdateProjectRequest { Name = "Renamed" });
        Assert.True(res.IsSuccess);
        Assert.Equal("Renamed", res.Data!.Name);
        Assert.True(res.Data.CanEdit);
    }

    [Fact]
    public async Task NonOwner_SameTenant_CannotUpdate_Forbidden()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, pid, _) = SeedProject(dbName);

        // Different stakeholder in the SAME tenant.
        var other = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant };
        var (svc, _, _) = Wire(other, dbName);
        var res = await svc.UpdateAsync(pid, new UpdateProjectRequest { Name = "Hijack" });
        Assert.True(res.IsForbidden);
    }

    [Fact]
    public async Task CrossTenant_Update_NotFound()
    {
        var dbName = Guid.NewGuid().ToString();
        var (_, pid, _) = SeedProject(dbName);

        // A user in a DIFFERENT tenant — the project is invisible via the query filter → NotFound.
        var alien = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = Guid.NewGuid() };
        var (svc, _, _) = Wire(alien, dbName);
        var res = await svc.UpdateAsync(pid, new UpdateProjectRequest { Name = "Nope" });
        Assert.True(res.IsNotFound);
    }

    [Fact]
    public async Task Admin_CanUpdate_AnyTenantProject()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, pid, _) = SeedProject(dbName);

        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant, IsAdmin = true };
        var (svc, _, _) = Wire(admin, dbName);
        var res = await svc.UpdateAsync(pid, new UpdateProjectRequest { Name = "AdminRename" });
        Assert.True(res.IsSuccess);
    }

    // ── Project DELETE authz ────────────────────────────────────────────────────

    [Fact]
    public async Task Owner_CanDelete_ProjectWithNoComments()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, pid, creator) = SeedProject(dbName);

        var user = new FakeCurrentUser { Id = creator, TenantId = tenant };
        var (svc, _, db) = Wire(user, dbName);
        var res = await svc.DeleteAsync(pid);
        Assert.True(res.IsSuccess);
        var proj = db.Projects.IgnoreQueryFilters().Single(p => p.Id == pid);
        Assert.NotNull(proj.DeletedAt);
    }

    [Fact]
    public async Task Owner_CannotDelete_WhenProjectHasComments_Conflict()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, pid, creator) = SeedProject(dbName);

        // Add a comment to the project (as a stakeholder).
        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            seed.Comments.Add(new Comment { ProjectId = pid, OwnerId = tenant, Body = "hi", Element = new() });
            await seed.SaveChangesAsync();
        }

        var user = new FakeCurrentUser { Id = creator, TenantId = tenant };
        var (svc, _, _) = Wire(user, dbName);
        var res = await svc.DeleteAsync(pid);
        Assert.True(res.IsConflict);
    }

    [Fact]
    public async Task NonOwner_Delete_Forbidden()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, pid, _) = SeedProject(dbName);

        var other = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant };
        var (svc, _, _) = Wire(other, dbName);
        var res = await svc.DeleteAsync(pid);
        Assert.True(res.IsForbidden);
    }

    [Fact]
    public async Task Admin_Cascade_DeletesOnlyThatProjectsComments_SiblingSurvives()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var creator = Guid.NewGuid();

        // Two projects in the SAME tenant, each with comments/replies/actions.
        int pidA, pidB;
        var stakeholder = new FakeCurrentUser { Id = creator, TenantId = tenant };
        {
            var (svc, _, db) = Wire(stakeholder, dbName);
            pidA = (await svc.CreateAsync(ProjReq("aaa"))).Data!.Id;
            pidB = (await svc.CreateAsync(ProjReq("bbb"))).Data!.Id;
            db.Dispose();
        }

        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            // Project A: 2 comments (one with a reply) + a predefined action + a suggestion.
            var cA1 = new Comment { ProjectId = pidA, OwnerId = tenant, Body = "a1", Element = new() };
            var cA2 = new Comment { ProjectId = pidA, OwnerId = tenant, Body = "a2", Element = new() };
            seed.Comments.AddRange(cA1, cA2);
            await seed.SaveChangesAsync();
            seed.Replies.Add(new Reply { CommentId = cA1.Id, OwnerId = tenant, Body = "reply", AuthorId = creator });
            seed.PredefinedActions.Add(new PredefinedAction { OwnerId = tenant, ProjectId = pidA, Text = "A act", Prompt = "p", IsActive = true });
            seed.PredefinedActionSuggestions.Add(new PredefinedActionSuggestion { OwnerId = tenant, ProjectId = pidA, Text = "A sug", Prompt = "p", Status = SuggestionStatus.Pending });

            // Project B (sibling): 1 comment that MUST survive.
            seed.Comments.Add(new Comment { ProjectId = pidB, OwnerId = tenant, Body = "b1", Element = new() });
            await seed.SaveChangesAsync();
        }

        // Admin deletes project A (cascade).
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant, IsAdmin = true };
        var (delSvc, _, adb) = Wire(admin, dbName);
        var res = await delSvc.DeleteAsync(pidA);
        Assert.True(res.IsSuccess);

        // Project A + all its children soft-deleted.
        Assert.NotNull(adb.Projects.IgnoreQueryFilters().Single(p => p.Id == pidA).DeletedAt);
        Assert.All(adb.Comments.IgnoreQueryFilters().Where(c => c.ProjectId == pidA), c => Assert.NotNull(c.DeletedAt));
        Assert.All(adb.Replies.IgnoreQueryFilters(), r => Assert.NotNull(r.DeletedAt));
        Assert.All(adb.PredefinedActions.IgnoreQueryFilters().Where(a => a.ProjectId == pidA), a => Assert.NotNull(a.DeletedAt));
        Assert.All(adb.PredefinedActionSuggestions.IgnoreQueryFilters().Where(s => s.ProjectId == pidA), s => Assert.NotNull(s.DeletedAt));

        // BINDING #2: the sibling project B and its comment are UNTOUCHED.
        Assert.Null(adb.Projects.IgnoreQueryFilters().Single(p => p.Id == pidB).DeletedAt);
        Assert.All(adb.Comments.IgnoreQueryFilters().Where(c => c.ProjectId == pidB), c => Assert.Null(c.DeletedAt));
    }

    // ── ProjectResponse hints ────────────────────────────────────────────────────

    [Fact]
    public async Task List_ComputesHints_And_CommentsCount()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, pid, creator) = SeedProject(dbName);
        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            seed.Comments.Add(new Comment { ProjectId = pid, OwnerId = tenant, Body = "c", Element = new() });
            await seed.SaveChangesAsync();
        }

        // Owner: canEdit true, canDelete false (has a comment).
        var owner = new FakeCurrentUser { Id = creator, TenantId = tenant };
        var (ownerSvc, _, _) = Wire(owner, dbName);
        var list = await ownerSvc.ListAsync();
        var row = list.Data!.Single(p => p.Id == pid);
        Assert.Equal(1, row.CommentsCount);
        Assert.True(row.CanEdit);
        Assert.False(row.CanDelete);

        // Non-owner stakeholder: neither.
        var other = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant };
        var (otherSvc, _, _) = Wire(other, dbName);
        var otherRow = (await otherSvc.ListAsync()).Data!.Single(p => p.Id == pid);
        Assert.False(otherRow.CanEdit);
        Assert.False(otherRow.CanDelete);

        // Admin: both.
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant, IsAdmin = true };
        var (adminSvc, _, _) = Wire(admin, dbName);
        var adminRow = (await adminSvc.ListAsync()).Data!.Single(p => p.Id == pid);
        Assert.True(adminRow.CanEdit);
        Assert.True(adminRow.CanDelete);
    }

    // ── BINDING #3: widget read never returns Prompt ─────────────────────────────

    [Fact]
    public async Task WidgetRead_ReturnsOption_WithoutPrompt()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, pid, creator) = SeedProject(dbName, "wproj");
        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            seed.PredefinedActions.Add(new PredefinedAction { OwnerId = tenant, ProjectId = pid, Text = "Widget label", Prompt = "SECRET-PROMPT", IsActive = true });
            await seed.SaveChangesAsync();
        }

        var user = new FakeCurrentUser { Id = creator, TenantId = tenant };
        var db = BuildContext(user, dbName);
        var uow = new UnitOfWork(db);
        var projectSvc = new ProjectService(uow, user);
        var actionSvc = new PredefinedActionService(uow, projectSvc, user);

        var res = await actionSvc.GetEffectiveForProjectAsync("wproj", creator);
        Assert.True(res.IsSuccess);
        var opt = Assert.Single(res.Data!);
        Assert.Equal("Widget label", opt.Text);

        // PredefinedActionOption has NO Prompt property, and serialization never carries the secret.
        var json = System.Text.Json.JsonSerializer.Serialize(res.Data);
        Assert.DoesNotContain("SECRET-PROMPT", json);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
        Assert.False(typeof(PredefinedActionOption).GetProperties().Any(p => p.Name.Equals("Prompt", StringComparison.OrdinalIgnoreCase)));
    }

    // ── Suggestions ───────────────────────────────────────────────────────────────

    private static (SuggestionService svc, AppDbContext db, FakeEmail email) WireSuggestion(ICurrentUser user, string dbName)
    {
        var db = BuildContext(user, dbName);
        var uow = new UnitOfWork(db);
        var email = new FakeEmail();
        return (new SuggestionService(uow, user, email), db, email);
    }

    [Fact]
    public async Task Suggest_ThenApprove_MintsTenantScopedAction_AndRevalidatesProject()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, pid, creator) = SeedProject(dbName, "sproj");

        // A stakeholder who is NOT the owner suggests.
        var suggester = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant };
        var (sugSvc, _, email) = WireSuggestion(suggester, dbName);
        var create = await sugSvc.SuggestAsync(pid, new CreateSuggestionRequest { Text = "New idea", Prompt = "LLM-PROMPT" });
        Assert.True(create.IsSuccess);
        Assert.Equal(SuggestionStatus.Pending, create.Data!.Status);

        int suggestionId;
        using (var db2 = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
            suggestionId = db2.PredefinedActionSuggestions.IgnoreQueryFilters().Single().Id;

        // Admin lists pending → sees it.
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant, IsAdmin = true };
        var (adminSvc, adb, _) = WireSuggestion(admin, dbName);
        var pending = await adminSvc.ListPendingAsync();
        Assert.Single(pending.Data!);

        // Approve → mints a real project-scoped action.
        var approve = await adminSvc.ApproveAsync(suggestionId);
        Assert.True(approve.IsSuccess);
        Assert.Equal(SuggestionStatus.Approved, approve.Data!.Status);

        var action = adb.PredefinedActions.IgnoreQueryFilters().Single(a => a.ProjectId == pid && a.Text == "New idea");
        Assert.Equal(tenant, action.OwnerId);
        Assert.Equal("LLM-PROMPT", action.Prompt);
        Assert.True(action.IsActive);

        // Suggestion marked reviewed.
        var reviewed = adb.PredefinedActionSuggestions.IgnoreQueryFilters().Single();
        Assert.Equal(SuggestionStatus.Approved, reviewed.Status);
        Assert.NotNull(reviewed.ReviewedAt);
    }

    [Fact]
    public async Task Approve_Fails_WhenProjectDeleted_Conflict()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, pid, _) = SeedProject(dbName, "gone");

        var suggester = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant };
        var (sugSvc, _, _) = WireSuggestion(suggester, dbName);
        await sugSvc.SuggestAsync(pid, new CreateSuggestionRequest { Text = "t", Prompt = "p" });

        // Soft-delete the project out from under the suggestion.
        using (var db = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            var proj = db.Projects.IgnoreQueryFilters().Single(p => p.Id == pid);
            proj.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        int sid;
        using (var db2 = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
            sid = db2.PredefinedActionSuggestions.IgnoreQueryFilters().Single().Id;

        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant, IsAdmin = true };
        var (adminSvc, _, _) = WireSuggestion(admin, dbName);
        var res = await adminSvc.ApproveAsync(sid);
        Assert.True(res.IsConflict);
    }

    [Fact]
    public async Task Reject_MarksRejected_NoActionCreated()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, pid, _) = SeedProject(dbName, "rproj");

        var suggester = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant };
        var (sugSvc, _, _) = WireSuggestion(suggester, dbName);
        await sugSvc.SuggestAsync(pid, new CreateSuggestionRequest { Text = "no", Prompt = "p" });

        int sid;
        using (var db2 = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
            sid = db2.PredefinedActionSuggestions.IgnoreQueryFilters().Single().Id;

        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant, IsAdmin = true };
        var (adminSvc, adb, _) = WireSuggestion(admin, dbName);
        var res = await adminSvc.RejectAsync(sid);
        Assert.True(res.IsSuccess);
        Assert.Equal(SuggestionStatus.Rejected, adb.PredefinedActionSuggestions.IgnoreQueryFilters().Single().Status);
        Assert.DoesNotContain(adb.PredefinedActions.IgnoreQueryFilters().ToList(), a => a.ProjectId == pid);
    }

    [Fact]
    public async Task Suggest_Rejected_WhenCallerCanEdit()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, pid, creator) = SeedProject(dbName, "own");

        // The OWNER tries to suggest — should be told to add directly.
        var owner = new FakeCurrentUser { Id = creator, TenantId = tenant };
        var (sugSvc, _, _) = WireSuggestion(owner, dbName);
        var res = await sugSvc.SuggestAsync(pid, new CreateSuggestionRequest { Text = "t", Prompt = "p" });
        Assert.False(res.IsSuccess);

        // Admin too.
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant, IsAdmin = true };
        var (adminSug, _, _) = WireSuggestion(admin, dbName);
        var res2 = await adminSug.SuggestAsync(pid, new CreateSuggestionRequest { Text = "t", Prompt = "p" });
        Assert.False(res2.IsSuccess);
    }

    [Fact]
    public async Task Suggest_CrossTenantProject_NotFound()
    {
        var dbName = Guid.NewGuid().ToString();
        var (_, pid, _) = SeedProject(dbName, "iso");

        // A user from a DIFFERENT tenant cannot even see the project → NotFound.
        var alien = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = Guid.NewGuid() };
        var (sugSvc, _, _) = WireSuggestion(alien, dbName);
        var res = await sugSvc.SuggestAsync(pid, new CreateSuggestionRequest { Text = "t", Prompt = "p" });
        Assert.True(res.IsNotFound);
    }

    [Fact]
    public async Task CrossTenant_CannotSeeOrReviewAnotherTenantsSuggestion()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, pid, _) = SeedProject(dbName, "xproj");

        var suggester = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant };
        var (sugSvc, _, _) = WireSuggestion(suggester, dbName);
        await sugSvc.SuggestAsync(pid, new CreateSuggestionRequest { Text = "t", Prompt = "p" });

        int sid;
        using (var db2 = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
            sid = db2.PredefinedActionSuggestions.IgnoreQueryFilters().Single().Id;

        // Admin of ANOTHER tenant: pending list empty + cannot approve/reject by id.
        var alienAdmin = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), IsAdmin = true };
        var (alienSvc, _, _) = WireSuggestion(alienAdmin, dbName);
        Assert.Empty((await alienSvc.ListPendingAsync()).Data!);
        Assert.True((await alienSvc.ApproveAsync(sid)).IsNotFound);
        Assert.True((await alienSvc.RejectAsync(sid)).IsNotFound);
    }
}
