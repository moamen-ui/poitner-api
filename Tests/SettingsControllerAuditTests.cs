using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Pointer.API.Controllers.Admin;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Settings;
using Pointer.Application.Response;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-12 review finding #2 — <c>settings.updated</c> must name only the keys that actually changed
/// (never a fixed constant list), must still write a row (keys = "") on a no-op save, and the batch
/// of Set*Async calls + the audit write must be one atomic unit.
/// </summary>
public class SettingsControllerAuditTests
{
    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; } = true;
        public bool IsQuickAccess { get; set; }
        public Guid? TenantId { get; set; }
        public int? RoleId { get; set; }
        public string? KeyScopes { get; set; }
        public string? Scope { get; set; }
        public long? ImpersonationSessionId { get; set; }
        public bool IsImpersonating => ImpersonationSessionId != null;
    }

    private static AppDbContext Ctx(string db) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(db)
                .ConfigureWarnings(w =>
                    w.Ignore(
                        Microsoft
                            .EntityFrameworkCore
                            .Diagnostics
                            .InMemoryEventId
                            .TransactionIgnoredWarning
                    )
                )
                .Options,
            new FakeCurrentUser(),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
        );

    private static SettingsController BuildController(AppDbContext ctx, FakeAuditWriter audit) =>
        new(
            new SettingsService(new UnitOfWork(ctx), new MemoryCache(new MemoryCacheOptions())),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            audit,
            new UnitOfWork(ctx)
        );

    private static UpdateSettingsRequest DefaultRequest() =>
        new()
        {
            ScopedAdminSignupEnabled = false,
            AppBaseUrl = string.Empty,
            EmailEnabled = false,
            EmailFromEmail = string.Empty,
            EmailFromName = string.Empty,
            EmailDailyCap = 0,
            DemoMaxActive = 0,
            DemoTtlHours = 0,
            DemoPerEmailPerDay = 0,
            DemoCommentCap = 0,
            ExtensionStoreUrl = string.Empty,
            ExtensionZipUrl = string.Empty,
            QuickAccessInviteEmailEnabled = false,
        };

    [Fact]
    public async Task Update_OnlyChangedFields_AreNamedInKeys()
    {
        var db = Guid.NewGuid().ToString();
        using var ctx = Ctx(db);
        var audit = new FakeAuditWriter();
        var controller = BuildController(ctx, audit);

        var request = DefaultRequest();
        request.EmailEnabled = true; // the only field that differs from the ambient default
        request.EmailFromEmail = "ops@pointer.test";

        var result = await controller.Update(request);

        Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.SettingsUpdated, entry.Action);
        var keys = entry.After!["keys"].Split(',', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(ISettingsService.EmailEnabled, keys);
        Assert.Contains(ISettingsService.EmailFromEmail, keys);
        Assert.DoesNotContain(ISettingsService.DemoMaxActive, keys);
        Assert.DoesNotContain(ISettingsService.ExtensionStoreUrl, keys);
    }

    [Fact]
    public async Task Update_NoActualChange_StillWritesRow_WithEmptyKeys()
    {
        var db = Guid.NewGuid().ToString();
        using var ctx = Ctx(db);
        var seedAudit = new FakeAuditWriter();
        var first = BuildController(ctx, seedAudit);
        await first.Update(DefaultRequest()); // establish the stored values

        var audit = new FakeAuditWriter();
        var controller = BuildController(ctx, audit);

        var result = await controller.Update(DefaultRequest()); // identical values again

        Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.SettingsUpdated, entry.Action);
        Assert.Equal(string.Empty, entry.After!["keys"]);
    }

    /// <summary>
    /// Regression for the R2-05-07 e2e failure: QuickAccessInviteEmailEnabled was added to
    /// ISettingsService (e072d55) and read by InviteService, but was never wired into
    /// UpdateSettingsRequest/SettingsResponse/SettingsController, so PUT silently dropped it and
    /// GET never returned it (property absent from the JSON, not just false).
    /// </summary>
    [Fact]
    public async Task Update_QuickAccessInviteEmailEnabled_PersistsAndIsReturnedByGet()
    {
        var db = Guid.NewGuid().ToString();
        using var ctx = Ctx(db);
        var audit = new FakeAuditWriter();
        var controller = BuildController(ctx, audit);

        var request = DefaultRequest();
        request.QuickAccessInviteEmailEnabled = true;
        var updateResult = await controller.Update(request);

        var updateOk = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(updateResult);
        var updateBody = Assert.IsType<Result<SettingsResponse>>(updateOk.Value);
        Assert.True(updateBody.Data!.QuickAccessInviteEmailEnabled);

        var entry = Assert.Single(audit.Entries);
        var keys = entry.After!["keys"].Split(',', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(ISettingsService.QuickAccessInviteEmailEnabled, keys);

        // A fresh controller instance (new cache) proves the value was actually persisted, not just
        // echoed back from the in-request DTO.
        var getController = BuildController(ctx, new FakeAuditWriter());
        var getResult = await getController.Get();
        var getOk = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(getResult);
        var getBody = Assert.IsType<Result<SettingsResponse>>(getOk.Value);
        Assert.True(getBody.Data!.QuickAccessInviteEmailEnabled);
    }
}
