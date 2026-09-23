namespace Pointer.Application.DTOs.Settings;

public class SettingsResponse
{
    public bool ScopedAdminSignupEnabled { get; set; }

    // Raw override (empty when unset — the effective value then comes from branding's urls.app).
    public string AppBaseUrl { get; set; } = string.Empty;

    // The URL invitation links will actually use, after the app_base_url -> brand_url_app ->
    // default fallback. Read-only: shown so a super admin can see where invites will point.
    public string EffectiveAppBaseUrl { get; set; } = string.Empty;

    // Email (super-admin editable). The API key itself is never returned — only whether one is set.
    public bool EmailEnabled { get; set; }
    public string EmailFromEmail { get; set; } = string.Empty;
    public string EmailFromName { get; set; } = string.Empty;
    public int EmailDailyCap { get; set; }
    public bool EmailApiKeyConfigured { get; set; }

    // Demo (super-admin editable).
    public int DemoMaxActive { get; set; }
    public int DemoTtlHours { get; set; }
    public int DemoPerEmailPerDay { get; set; }
    public int DemoCommentCap { get; set; }

    // Browser extension (super-admin editable).
    public string ExtensionStoreUrl { get; set; } = string.Empty;
    public string ExtensionZipUrl { get; set; } = string.Empty;

    // Quick-access invites (super-admin editable). Opt-in: e-mail the magic link instead of
    // link-copy only. See ISettingsService.QuickAccessInviteEmailEnabled.
    public bool QuickAccessInviteEmailEnabled { get; set; }
}
