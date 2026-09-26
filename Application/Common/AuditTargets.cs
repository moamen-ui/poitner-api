namespace Pointer.Application.Common;

/// <summary>
/// The <c>target_type</c> vocabulary for audit rows (DB-12 §3.1). Append-only — add a constant,
/// never repurpose one (R10).
/// </summary>
public static class AuditTargets
{
    public const string Workspace = "workspace";
    public const string User = "user";
    public const string Membership = "membership";
    public const string Invite = "invite";
    public const string TenantInvite = "tenant_invite";
    public const string ApiKey = "api_key";
    public const string DeviceLogin = "device_login";
    public const string Project = "project";
    public const string ProjectAppUrl = "project_app_url";
    public const string Role = "role";
    public const string Environment = "environment";
    public const string Status = "status";
    public const string PredefinedAction = "predefined_action";
    public const string Suggestion = "suggestion";
    public const string AiRule = "ai_rule";
    public const string Plan = "plan";
    public const string Settings = "settings";
    public const string Branding = "branding";
    public const string Export = "export";
    public const string Import = "import";

    /// <summary>DB-13: impersonation_sessions rows.</summary>
    public const string ImpersonationSession = "impersonation_session";

    /// <summary>An anonymous path with no resolved identity (login failure, reset requested).</summary>
    public const string EmailHash = "email_hash";

    /// <summary>DB-20: discount_codes rows (billing rows themselves target <see cref="Workspace"/>).</summary>
    public const string DiscountCode = "discount_code";
}
