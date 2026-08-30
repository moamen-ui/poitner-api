using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Preferences;
using Pointer.Application.Services.Implementation;
using Pointer.Domain.Entity;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// User.AddCommentShortcut — the widget's per-account "add comment" keyboard shortcut, synced via
/// PATCH /api/me/preferences so it follows the user across browsers/machines instead of living in
/// localStorage. See docs/superpowers/specs/2026-08-25-page-context-capture-design.md context and
/// web-component/src/shortcut.ts for the "alt+shift+c"-style storage format.
/// </summary>
public class PreferencesServiceTests
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
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options, user, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    private static (AppDbContext Db, PreferencesService Service, Guid UserId) BuildHarness(string dbName)
    {
        var tenant = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var user = new FakeCurrentUser { Id = userId, TenantId = tenant, IsSuperAdmin = false };
        var db = BuildContext(user, dbName);
        db.Roles.Add(new Role { Id = 1, Name = "Member", OwnerId = tenant });
        db.Users.Add(new User
        {
            PublicId = userId, Email = "u@test.com", PasswordHash = "x",
            DisplayName = "U", RoleId = 1, OwnerId = tenant,
        });
        db.SaveChanges();

        var uow = new UnitOfWork(db);
        return (db, new PreferencesService(uow, user), userId);
    }

    [Fact]
    public async Task Update_SetsShortcut_AndReturnsIt()
    {
        var (db, svc, userId) = BuildHarness(Guid.NewGuid().ToString());

        var result = await svc.UpdateAsync(new UpdatePreferencesRequest { AddCommentShortcut = "alt+shift+c" });

        Assert.True(result.IsSuccess);
        Assert.Equal("alt+shift+c", result.Data!.AddCommentShortcut);
        var stored = db.Users.IgnoreQueryFilters().Single(u => u.PublicId == userId).AddCommentShortcut;
        Assert.Equal("alt+shift+c", stored);
    }

    [Fact]
    public async Task Update_EmptyString_ClearsShortcutBackToNull()
    {
        var (db, svc, userId) = BuildHarness(Guid.NewGuid().ToString());
        await svc.UpdateAsync(new UpdatePreferencesRequest { AddCommentShortcut = "ctrl+alt+m" });

        var result = await svc.UpdateAsync(new UpdatePreferencesRequest { AddCommentShortcut = "" });

        Assert.True(result.IsSuccess);
        Assert.Null(result.Data!.AddCommentShortcut);
        var stored = db.Users.IgnoreQueryFilters().Single(u => u.PublicId == userId).AddCommentShortcut;
        Assert.Null(stored);
    }

    [Fact]
    public async Task Update_NullProperty_LeavesShortcutUntouched()
    {
        var (db, svc, userId) = BuildHarness(Guid.NewGuid().ToString());
        await svc.UpdateAsync(new UpdatePreferencesRequest { AddCommentShortcut = "alt+shift+c" });

        // Omitting the field (Language-only update) must not clear the previously-set shortcut.
        var result = await svc.UpdateAsync(new UpdatePreferencesRequest { Language = "en" });

        Assert.True(result.IsSuccess);
        Assert.Equal("alt+shift+c", result.Data!.AddCommentShortcut);
        var stored = db.Users.IgnoreQueryFilters().Single(u => u.PublicId == userId).AddCommentShortcut;
        Assert.Equal("alt+shift+c", stored);
    }

    [Fact]
    public async Task Update_RejectsOverlongShortcut()
    {
        var (_, svc, _) = BuildHarness(Guid.NewGuid().ToString());

        var result = await svc.UpdateAsync(new UpdatePreferencesRequest { AddCommentShortcut = new string('x', 41) });

        Assert.False(result.IsSuccess);
    }
}
