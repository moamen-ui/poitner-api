using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.InMemory.Infrastructure.Internal;
using Pointer.Application.Abstractions;
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
/// The suggestion "changes requested" flow + its in-app notifications: submit notifies admins,
/// request-changes notifies the submitter, resubmit notifies admins again, and ListMine.
/// </summary>
public class SuggestionChangesRequestedTests
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

    private sealed class FakeEmail : IEmailService
    {
        public int Sent { get; private set; }
        public Task<bool> SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
        {
            Sent++;
            return Task.FromResult(true);
        }
    }

    private static AppDbContext BuildContext(ICurrentUser user, string dbName) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options, user, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    private static (ProjectService project, UnitOfWork uow, AppDbContext db) WireProject(ICurrentUser user, string dbName)
    {
        var db = BuildContext(user, dbName);
        var uow = new UnitOfWork(db);
        return (new ProjectService(uow, user, new PassThroughEntitlements(), TestProjectServiceDeps.Settings(), TestProjectServiceDeps.Configuration()), uow, db);
    }

    private static (SuggestionService svc, AppDbContext db, NotificationService notifications) WireSuggestion(ICurrentUser user, string dbName)
    {
        var db = BuildContext(user, dbName);
        var uow = new UnitOfWork(db);
        var notifications = new NotificationService(uow, user);
        return (new SuggestionService(uow, user, new FakeEmail(), notifications), db, notifications);
    }

    // Seeds a tenant with one active project + one admin user (GrantsAdmin role) in that tenant.
    // Returns (tenantId, projectId, creatorId, adminId).
    private static (Guid tenant, int projectId, Guid creator, Guid adminId) SeedProjectWithAdmin(string dbName, string key = "proj")
    {
        var tenant = Guid.NewGuid();
        var creator = Guid.NewGuid();
        var adminId = Guid.NewGuid();

        var user = new FakeCurrentUser { Id = creator, TenantId = tenant, IsSuperAdmin = false };
        var (svc, _, db) = WireProject(user, dbName);
        var res = svc.CreateAsync(new CreateProjectRequest { Key = key, Name = key }).GetAwaiter().GetResult();
        Assert.True(res.IsSuccess);
        var projectId = res.Data!.Id;
        db.Dispose();

        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            var adminRole = new Role { Name = "Workspace Admin", GrantsAdmin = true, IsActive = true, OwnerId = tenant };
            seed.Roles.Add(adminRole);
            seed.SaveChanges();

            seed.Users.Add(new User
            {
                Email = "admin@tenant.com",
                PasswordHash = "x",
                DisplayName = "Tenant Admin",
                RoleId = adminRole.Id,
                PublicId = adminId,
                ApprovalStatus = ApprovalStatus.Approved,
                IsActive = true,
                OwnerId = tenant
            });
            seed.SaveChanges();
        }

        return (tenant, projectId, creator, adminId);
    }

    [Fact]
    public async Task Suggest_NotifiesAdmin_SuggestionSubmitted()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, pid, _, adminId) = SeedProjectWithAdmin(dbName, "s1");

        var suggester = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant };
        var (sugSvc, _, _) = WireSuggestion(suggester, dbName);
        var create = await sugSvc.SuggestAsync(pid, new CreateSuggestionRequest { Text = "Idea", Prompt = "P" });
        Assert.True(create.IsSuccess);

        var adminCtx = new FakeCurrentUser { Id = adminId, TenantId = tenant, IsAdmin = true };
        var db = BuildContext(adminCtx, dbName);
        var notifSvc = new NotificationService(new UnitOfWork(db), adminCtx);

        var countRes = await notifSvc.GetUnreadCountAsync();
        Assert.Equal(1, countRes.Data!.Count);

        var listRes = await notifSvc.ListAsync();
        var notif = Assert.Single(listRes.Data!.Items);
        Assert.Equal(NotificationType.SuggestionSubmitted, notif.Type);
        Assert.Equal(create.Data!.Id, notif.SuggestionId);
    }

    [Fact]
    public async Task RequestChanges_OnPending_SetsChangesRequested_AndNotifiesSubmitter()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, pid, _, adminId) = SeedProjectWithAdmin(dbName, "s2");

        var suggester = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant };
        var (sugSvc, _, _) = WireSuggestion(suggester, dbName);
        var create = await sugSvc.SuggestAsync(pid, new CreateSuggestionRequest { Text = "Idea", Prompt = "P" });
        Assert.True(create.IsSuccess);
        var suggestionId = create.Data!.Id;

        var admin = new FakeCurrentUser { Id = adminId, TenantId = tenant, IsAdmin = true };
        var (adminSvc, adb, _) = WireSuggestion(admin, dbName);
        var rc = await adminSvc.RequestChangesAsync(suggestionId, new RequestChangesRequest { Feedback = "Please clarify the wording." });
        Assert.True(rc.IsSuccess);
        Assert.Equal(SuggestionStatus.ChangesRequested, rc.Data!.Status);
        Assert.Equal("Please clarify the wording.", rc.Data.AdminFeedback);

        var stored = adb.PredefinedActionSuggestions.IgnoreQueryFilters().Single(s => s.Id == suggestionId);
        Assert.Equal(SuggestionStatus.ChangesRequested, stored.Status);
        Assert.Equal("Please clarify the wording.", stored.AdminFeedback);

        // Submitter gets a SuggestionChangesRequested notification with AdminFeedback in the payload.
        var submitterDb = BuildContext(suggester, dbName);
        var submitterNotif = new NotificationService(new UnitOfWork(submitterDb), suggester);
        var listRes = await submitterNotif.ListAsync();
        var notif = Assert.Single(listRes.Data!.Items);
        Assert.Equal(NotificationType.SuggestionChangesRequested, notif.Type);
        Assert.Equal(suggestionId, notif.SuggestionId);
        Assert.NotNull(notif.Payload);
        Assert.Equal("Please clarify the wording.", notif.Payload!.AdminFeedback);
    }

    [Fact]
    public async Task RequestChanges_EmptyFeedback_Fails()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, pid, _, adminId) = SeedProjectWithAdmin(dbName, "s3");

        var suggester = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant };
        var (sugSvc, _, _) = WireSuggestion(suggester, dbName);
        var create = await sugSvc.SuggestAsync(pid, new CreateSuggestionRequest { Text = "Idea", Prompt = "P" });

        var admin = new FakeCurrentUser { Id = adminId, TenantId = tenant, IsAdmin = true };
        var (adminSvc, _, _) = WireSuggestion(admin, dbName);
        var rc = await adminSvc.RequestChangesAsync(create.Data!.Id, new RequestChangesRequest { Feedback = "   " });
        Assert.False(rc.IsSuccess);
        Assert.False(rc.IsConflict);
        Assert.False(rc.IsNotFound);
    }

    [Fact]
    public async Task RequestChanges_OnNonPending_Conflict()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, pid, _, adminId) = SeedProjectWithAdmin(dbName, "s4");

        var suggester = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant };
        var (sugSvc, _, _) = WireSuggestion(suggester, dbName);
        var create = await sugSvc.SuggestAsync(pid, new CreateSuggestionRequest { Text = "Idea", Prompt = "P" });

        var admin = new FakeCurrentUser { Id = adminId, TenantId = tenant, IsAdmin = true };
        var (adminSvc, _, _) = WireSuggestion(admin, dbName);
        var reject = await adminSvc.RejectAsync(create.Data!.Id);
        Assert.True(reject.IsSuccess);

        var rc = await adminSvc.RequestChangesAsync(create.Data!.Id, new RequestChangesRequest { Feedback = "fix" });
        Assert.True(rc.IsConflict);
    }

    [Fact]
    public async Task Update_ByOriginalSubmitter_WhenChangesRequested_ResetsToPending_AndNotifiesAdmins()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, pid, _, adminId) = SeedProjectWithAdmin(dbName, "s5");

        var suggester = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant };
        var (sugSvc, _, _) = WireSuggestion(suggester, dbName);
        var create = await sugSvc.SuggestAsync(pid, new CreateSuggestionRequest { Text = "Old text", Prompt = "Old prompt" });
        var suggestionId = create.Data!.Id;

        var admin = new FakeCurrentUser { Id = adminId, TenantId = tenant, IsAdmin = true };
        var (adminSvc, _, _) = WireSuggestion(admin, dbName);
        await adminSvc.RequestChangesAsync(suggestionId, new RequestChangesRequest { Feedback = "Tweak it" });

        var (sugSvc2, sdb, _) = WireSuggestion(suggester, dbName);
        var update = await sugSvc2.UpdateAsync(suggestionId, new UpdateSuggestionRequest { Text = "New text", Prompt = "New prompt" });
        Assert.True(update.IsSuccess);
        Assert.Equal(SuggestionStatus.Pending, update.Data!.Status);
        Assert.Null(update.Data.AdminFeedback);
        Assert.Equal("New text", update.Data.Text);
        Assert.Equal("New prompt", update.Data.Prompt);

        var stored = sdb.PredefinedActionSuggestions.IgnoreQueryFilters().Single(s => s.Id == suggestionId);
        Assert.Equal(SuggestionStatus.Pending, stored.Status);
        Assert.Null(stored.AdminFeedback);
        Assert.Null(stored.ReviewedAt);

        // Admin gets a SuggestionResubmitted notification.
        var adminDb = BuildContext(admin, dbName);
        var adminNotif = new NotificationService(new UnitOfWork(adminDb), admin);
        var listRes = await adminNotif.ListAsync();
        Assert.Contains(listRes.Data!.Items, n => n.Type == NotificationType.SuggestionResubmitted && n.SuggestionId == suggestionId);
    }

    [Fact]
    public async Task Update_WhenPending_Conflict()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, pid, _, _) = SeedProjectWithAdmin(dbName, "s6");

        var suggester = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant };
        var (sugSvc, _, _) = WireSuggestion(suggester, dbName);
        var create = await sugSvc.SuggestAsync(pid, new CreateSuggestionRequest { Text = "Idea", Prompt = "P" });

        var update = await sugSvc.UpdateAsync(create.Data!.Id, new UpdateSuggestionRequest { Text = "x", Prompt = "y" });
        Assert.True(update.IsConflict);
    }

    [Fact]
    public async Task Update_ByDifferentUser_NotFound()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, pid, _, adminId) = SeedProjectWithAdmin(dbName, "s7");

        var suggester = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant };
        var (sugSvc, _, _) = WireSuggestion(suggester, dbName);
        var create = await sugSvc.SuggestAsync(pid, new CreateSuggestionRequest { Text = "Idea", Prompt = "P" });

        var admin = new FakeCurrentUser { Id = adminId, TenantId = tenant, IsAdmin = true };
        var (adminSvc, _, _) = WireSuggestion(admin, dbName);
        await adminSvc.RequestChangesAsync(create.Data!.Id, new RequestChangesRequest { Feedback = "fix" });

        var other = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant };
        var (otherSvc, _, _) = WireSuggestion(other, dbName);
        var update = await otherSvc.UpdateAsync(create.Data!.Id, new UpdateSuggestionRequest { Text = "x", Prompt = "y" });
        Assert.True(update.IsNotFound);
    }

    [Fact]
    public async Task ListMine_ReturnsOnlyCallersOwnSuggestions_AllStatuses()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, pid, _, adminId) = SeedProjectWithAdmin(dbName, "s8");

        var suggesterA = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant };
        var (svcA, _, _) = WireSuggestion(suggesterA, dbName);
        var createA1 = await svcA.SuggestAsync(pid, new CreateSuggestionRequest { Text = "A1", Prompt = "p" });
        Assert.True(createA1.IsSuccess);

        var suggesterB = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant };
        var (svcB, _, _) = WireSuggestion(suggesterB, dbName);
        var createB1 = await svcB.SuggestAsync(pid, new CreateSuggestionRequest { Text = "B1", Prompt = "p" });
        Assert.True(createB1.IsSuccess);

        // Admin rejects A's suggestion — should still show up in A's ListMine (all statuses).
        var admin = new FakeCurrentUser { Id = adminId, TenantId = tenant, IsAdmin = true };
        var (adminSvc, _, _) = WireSuggestion(admin, dbName);
        await adminSvc.RejectAsync(createA1.Data!.Id);

        var (svcAList, _, _) = WireSuggestion(suggesterA, dbName);
        var mine = await svcAList.ListMineAsync();
        Assert.True(mine.IsSuccess);
        var row = Assert.Single(mine.Data!);
        Assert.Equal("A1", row.Text);
        Assert.Equal(SuggestionStatus.Rejected, row.Status);
    }
}
