namespace Pointer.Application.Services.Interfaces;

public interface ISettingsService
{
    public const string ScopedAdminSignupEnabled = "scoped_admin_signup_enabled";

    /// <summary>Public dashboard base URL used to build links (invite join, etc.). Falls back to the prod app URL.</summary>
    public const string AppBaseUrl = "app_base_url";

    // Email settings (super-admin editable; the API key is NOT here — it stays in env as a secret).
    public const string EmailEnabled = "email_enabled";
    public const string EmailFromEmail = "email_from_email";
    public const string EmailFromName = "email_from_name";
    public const string EmailDailyCap = "email_daily_cap";            // default 250 (< Brevo's 300/day)

    // Demo settings (super-admin editable).
    public const string DemoMaxActive = "demo_max_active";            // default 100
    public const string DemoTtlHours = "demo_ttl_hours";              // default 24
    public const string DemoPerEmailPerDay = "demo_per_email_per_day"; // default 3
    public const string DemoCommentCap = "demo_comment_cap";          // default 10

    // Monetization settings (super-admin editable). NO provider secrets here — those stay env-only.
    /// <summary>Kill-switch for plan-entitlement enforcement. Default false: deploy off, flip on after soak.</summary>
    public const string EnforcementEnabled = "enforcement_enabled";
    /// <summary>Slug of the plan new workspace signups default to when none is chosen.</summary>
    public const string DefaultSignupPlan = "default_signup_plan";    // default "free"
    public const string TrialDays = "trial_days";                     // default 0
    public const string Currency = "currency";                        // default "USD"

    Task<bool> GetBoolAsync(string key, bool fallback = false);
    Task SetBoolAsync(string key, bool value);

    /// <summary>Returns the stored string, or <paramref name="fallback"/> if unset/empty.</summary>
    Task<string> GetStringAsync(string key, string fallback = "");
    Task SetStringAsync(string key, string value);

    /// <summary>Returns the stored int, or <paramref name="fallback"/> if unset/unparseable.</summary>
    Task<int> GetIntAsync(string key, int fallback = 0);
    Task SetIntAsync(string key, int value);
}
