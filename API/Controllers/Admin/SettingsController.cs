using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.API.Auth;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Settings;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers.Admin;

[ApiController]
[Route("api/admin/settings")]
[Authorize(Policy = Policies.SuperAdmin)]
[Tags("Settings")]
public class SettingsController(
    ISettingsService settingsService,
    IConfiguration configuration,
    IAuditWriter audit,
    IUnitOfWork unitOfWork)
    : ControllerBase
{
    // AuditFields.Sanitize truncates any single value to 200 chars — beyond that a joined "keys"
    // list would be silently cut off mid-key rather than switching to the "<n> keys" summary form
    // (review finding #2).
    private const int MaxKeysValueLength = 200;

    private const int DefaultDailyCap = 250;
    private const int DefaultDemoMaxActive = 100;
    private const int DefaultDemoTtlHours = 24;
    private const int DefaultDemoPerEmailPerDay = 3;
    private const int DefaultDemoCommentCap = 10;
    // The extension zip is a landing-domain artifact served by Caddy (see DEPLOY.md/Caddyfile) —
    // matches the dashboards' EXTENSION_ZIP_URL fallback until this is ever overridden here.
    private const string DefaultExtensionZipUrl = "https://pointer.moamen.work/pointer-extension.zip";
    // Must match InviteService.DefaultAppBaseUrl — the last resort when neither app_base_url nor
    // brand_url_app is set. Only used to show the effective value on this page.
    private const string DefaultAppBaseUrl = "https://app.pointer.moamen.work";

    [HttpGet]
    [ProducesResponseType(typeof(Result<SettingsResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get()
    {
        return Ok(Result<SettingsResponse>.Success(await BuildResponseAsync()));
    }

    [HttpPut]
    [Audited(AuditActions.SettingsUpdated)]
    [ProducesResponseType(typeof(Result<SettingsResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update([FromBody] UpdateSettingsRequest request)
    {
        // Normalized target values — computed up front so they can both be written AND compared
        // against the current stored value to learn which keys actually change.
        var newAppBaseUrl = request.AppBaseUrl?.Trim().TrimEnd('/') ?? string.Empty;
        var newEmailFromEmail = request.EmailFromEmail?.Trim() ?? string.Empty;
        var newEmailFromName = request.EmailFromName?.Trim() ?? string.Empty;
        var newEmailDailyCap = request.EmailDailyCap > 0 ? request.EmailDailyCap : DefaultDailyCap;
        var newDemoMaxActive = request.DemoMaxActive > 0 ? request.DemoMaxActive : DefaultDemoMaxActive;
        var newDemoTtlHours = request.DemoTtlHours > 0 ? request.DemoTtlHours : DefaultDemoTtlHours;
        var newDemoPerEmailPerDay = request.DemoPerEmailPerDay > 0 ? request.DemoPerEmailPerDay : DefaultDemoPerEmailPerDay;
        var newDemoCommentCap = request.DemoCommentCap > 0 ? request.DemoCommentCap : DefaultDemoCommentCap;
        var newExtensionStoreUrl = request.ExtensionStoreUrl?.Trim() ?? string.Empty;
        var newExtensionZipUrl = request.ExtensionZipUrl?.Trim() ?? string.Empty;

        // Read the CURRENT value of every key before touching any of them (review finding #2): the
        // audit row must name only the keys that actually changed, never the full list on a no-op
        // save. Fallbacks mirror BuildResponseAsync's so "effectively unset" compares equal to the
        // default the UI already shows.
        var changedKeys = new List<string>();
        void TrackChange(string key, bool changed)
        {
            if (changed)
                changedKeys.Add(key);
        }

        TrackChange(
            ISettingsService.ScopedAdminSignupEnabled,
            await settingsService.GetBoolAsync(ISettingsService.ScopedAdminSignupEnabled) != request.ScopedAdminSignupEnabled
        );
        TrackChange(
            ISettingsService.AppBaseUrl,
            (await settingsService.GetStringAsync(ISettingsService.AppBaseUrl)).Trim() != newAppBaseUrl
        );
        TrackChange(
            ISettingsService.EmailEnabled,
            await settingsService.GetBoolAsync(ISettingsService.EmailEnabled) != request.EmailEnabled
        );
        TrackChange(
            ISettingsService.EmailFromEmail,
            (await settingsService.GetStringAsync(ISettingsService.EmailFromEmail)).Trim() != newEmailFromEmail
        );
        TrackChange(
            ISettingsService.EmailFromName,
            (await settingsService.GetStringAsync(ISettingsService.EmailFromName)).Trim() != newEmailFromName
        );
        TrackChange(
            ISettingsService.EmailDailyCap,
            await settingsService.GetIntAsync(ISettingsService.EmailDailyCap, DefaultDailyCap) != newEmailDailyCap
        );
        TrackChange(
            ISettingsService.DemoMaxActive,
            await settingsService.GetIntAsync(ISettingsService.DemoMaxActive, DefaultDemoMaxActive) != newDemoMaxActive
        );
        TrackChange(
            ISettingsService.DemoTtlHours,
            await settingsService.GetIntAsync(ISettingsService.DemoTtlHours, DefaultDemoTtlHours) != newDemoTtlHours
        );
        TrackChange(
            ISettingsService.DemoPerEmailPerDay,
            await settingsService.GetIntAsync(ISettingsService.DemoPerEmailPerDay, DefaultDemoPerEmailPerDay) != newDemoPerEmailPerDay
        );
        TrackChange(
            ISettingsService.DemoCommentCap,
            await settingsService.GetIntAsync(ISettingsService.DemoCommentCap, DefaultDemoCommentCap) != newDemoCommentCap
        );
        TrackChange(
            ISettingsService.ExtensionStoreUrl,
            (await settingsService.GetStringAsync(ISettingsService.ExtensionStoreUrl)).Trim() != newExtensionStoreUrl
        );
        TrackChange(
            ISettingsService.ExtensionZipUrl,
            (await settingsService.GetStringAsync(ISettingsService.ExtensionZipUrl)).Trim() != newExtensionZipUrl
        );

        // The whole batch of writes + the audit row are one atomic unit (review finding #2): a
        // mid-batch failure must roll back every Set*Async already applied rather than leave a
        // partially-applied, un-audited change.
        await unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            await settingsService.SetBoolAsync(ISettingsService.ScopedAdminSignupEnabled, request.ScopedAdminSignupEnabled);

            // Invitation join-link base. Optional override; empty means "use branding's urls.app".
            await settingsService.SetStringAsync(ISettingsService.AppBaseUrl, newAppBaseUrl);

            // Email
            await settingsService.SetBoolAsync(ISettingsService.EmailEnabled, request.EmailEnabled);
            await settingsService.SetStringAsync(ISettingsService.EmailFromEmail, newEmailFromEmail);
            await settingsService.SetStringAsync(ISettingsService.EmailFromName, newEmailFromName);
            await settingsService.SetIntAsync(ISettingsService.EmailDailyCap, newEmailDailyCap);

            // Demo (clamp to sane minimums so a bad value can't disable the demo entirely).
            await settingsService.SetIntAsync(ISettingsService.DemoMaxActive, newDemoMaxActive);
            await settingsService.SetIntAsync(ISettingsService.DemoTtlHours, newDemoTtlHours);
            await settingsService.SetIntAsync(ISettingsService.DemoPerEmailPerDay, newDemoPerEmailPerDay);
            await settingsService.SetIntAsync(ISettingsService.DemoCommentCap, newDemoCommentCap);

            // Extension
            await settingsService.SetStringAsync(ISettingsService.ExtensionStoreUrl, newExtensionStoreUrl);
            await settingsService.SetStringAsync(ISettingsService.ExtensionZipUrl, newExtensionZipUrl);

            // DB-12: one row per batch update, naming the setting KEYS that actually changed — never
            // the values (some of which are effectively secrets-adjacent, e.g. from-email) — and
            // written even when nothing changed (`keys` = "").
            var joinedKeys = string.Join(',', changedKeys);
            var after = new Dictionary<string, string>();
            if (joinedKeys.Length > MaxKeysValueLength)
            {
                after["keys"] = $"{changedKeys.Count} keys";
                after["count"] = changedKeys.Count.ToString();
            }
            else
            {
                after["keys"] = joinedKeys;
            }

            await audit.WriteAsync(
                new AuditEntry(AuditActions.SettingsUpdated, AuditTargets.Settings, "global", null, After: after)
            );
        });

        return Ok(Result<SettingsResponse>.Success(await BuildResponseAsync()));
    }

    private async Task<SettingsResponse> BuildResponseAsync()
    {
        // From-email/name fall back to the env config when not yet overridden in the DB, so the
        // page shows the effective value. The API key is a secret — only its presence is reported.
        var appBaseUrl = (await settingsService.GetStringAsync(ISettingsService.AppBaseUrl)).Trim();
        var brandUrlApp = (await settingsService.GetStringAsync(ISettingsService.BrandUrlApp)).Trim();

        return new SettingsResponse
        {
            ScopedAdminSignupEnabled = await settingsService.GetBoolAsync(ISettingsService.ScopedAdminSignupEnabled),
            AppBaseUrl = appBaseUrl,
            EffectiveAppBaseUrl = !string.IsNullOrWhiteSpace(appBaseUrl)
                ? appBaseUrl
                : (!string.IsNullOrWhiteSpace(brandUrlApp) ? brandUrlApp : DefaultAppBaseUrl),
            EmailEnabled = await settingsService.GetBoolAsync(ISettingsService.EmailEnabled),
            EmailFromEmail = await settingsService.GetStringAsync(ISettingsService.EmailFromEmail, configuration["Email:FromEmail"] ?? string.Empty),
            EmailFromName = await settingsService.GetStringAsync(ISettingsService.EmailFromName, configuration["Email:FromName"] ?? "Pointer"),
            EmailDailyCap = await settingsService.GetIntAsync(ISettingsService.EmailDailyCap, DefaultDailyCap),
            EmailApiKeyConfigured = !string.IsNullOrWhiteSpace(configuration["Email:ApiKey"]),
            DemoMaxActive = await settingsService.GetIntAsync(ISettingsService.DemoMaxActive, DefaultDemoMaxActive),
            DemoTtlHours = await settingsService.GetIntAsync(ISettingsService.DemoTtlHours, DefaultDemoTtlHours),
            DemoPerEmailPerDay = await settingsService.GetIntAsync(ISettingsService.DemoPerEmailPerDay, DefaultDemoPerEmailPerDay),
            DemoCommentCap = await settingsService.GetIntAsync(ISettingsService.DemoCommentCap, DefaultDemoCommentCap),
            ExtensionStoreUrl = await settingsService.GetStringAsync(ISettingsService.ExtensionStoreUrl, string.Empty),
            ExtensionZipUrl = await settingsService.GetStringAsync(ISettingsService.ExtensionZipUrl, DefaultExtensionZipUrl),
        };
    }
}
