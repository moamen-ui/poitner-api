using System.Reflection;

namespace Pointer.Application.Common;

/// <summary>
/// The audit action catalogue (DB-12 §3.6). One constant per row — call sites pass these, never a
/// literal (<c>AuditWriter</c> validates against <see cref="All"/>; a typo is a bug, not an audit
/// row). Spelling is frozen (DB-RULES R10): strings marked *reserved* are the vocabulary for a
/// later doc (DB-11b/c/d, DB-13, DB-14) — whichever lands second writes the row; the spelling
/// never changes.
/// </summary>
public static class AuditActions
{
    // ── Auth ────────────────────────────────────────────────────────────────────────────────

    public const string AuthLoginSucceeded = "auth.login.succeeded";
    public const string AuthLoginFailed = "auth.login.failed";

    /// <summary>Reserved: DB-11b <c>SwitchWorkspaceAsync</c>.</summary>
    public const string AuthWorkspaceSwitched = "auth.workspace_switched";

    public const string AuthPasswordResetRequested = "auth.password.reset_requested";
    public const string AuthPasswordReset = "auth.password.reset";
    public const string AuthPasswordChanged = "auth.password.changed";

    /// <summary>Reserved: DB-11d.</summary>
    public const string AuthEmailChangeRequested = "auth.email.change_requested";

    /// <summary>Reserved: DB-11d.</summary>
    public const string AuthEmailChanged = "auth.email.changed";

    /// <summary>Reserved: DB-14.</summary>
    public const string AuthEmailVerified = "auth.email.verified";

    public const string AuthRegisterStakeholder = "auth.register.stakeholder";
    public const string AuthRegisterAdmin = "auth.register.admin";

    /// <summary>R5-61: POST /api/me/mfa/verify completed enrollment (TotpEnabledAt set, recovery
    /// codes minted). The initial POST /api/me/mfa/enrol (secret generated, not yet enabled) is
    /// deliberately [NoAudit] — nothing security-relevant is enforced until this row exists.</summary>
    public const string AuthMfaEnrolled = "auth.mfa.enrolled";

    /// <summary>R5-61: POST /api/me/mfa/disable — MFA turned back off (secret + recovery codes cleared).</summary>
    public const string AuthMfaDisabled = "auth.mfa.disabled";

    /// <summary>R5-61: a TOTP/recovery code was rejected — either mid-enrollment (POST
    /// /api/me/mfa/verify), on disable (POST /api/me/mfa/disable), or completing a login (POST
    /// /api/auth/mfa/verify). Counts toward the same per-e-mail lockout as a wrong password.</summary>
    public const string AuthMfaChallengeFailed = "auth.mfa.challenge_failed";

    public const string AuthDemoProvisioned = "auth.demo.provisioned";
    public const string AuthDemoUpgraded = "auth.demo.upgraded";

    /// <summary>DB-17: POST /api/demo/extend — the demo admin's own one-time extension (the operator's is tenant.demo_extended).</summary>
    public const string DemoExtended = "demo.extended";
    public const string DeviceApproved = "device.approved";
    public const string DeviceDenied = "device.denied";
    public const string ApikeyCreated = "apikey.created";
    public const string ApikeyRegenerated = "apikey.regenerated";

    // ── Members and invites ─────────────────────────────────────────────────────────────────

    public const string MemberCreated = "member.created";
    public const string MemberApproved = "member.approved";
    public const string MemberRejected = "member.rejected";
    public const string MemberUpdated = "member.updated";
    public const string MemberRemoved = "member.removed";
    public const string OwnershipTransferred = "ownership.transferred";

    /// <summary>DB-11c: POST /api/me/leave-workspace — the member's own action.</summary>
    public const string MemberLeft = "member.left";

    /// <summary>DB-11c: POST /api/me/request-erase — a passwordless identity requests its scoped e-mailed erase link.</summary>
    public const string IdentityEraseRequested = "identity.erase_requested";

    /// <summary>DB-11c: DELETE /api/me, DELETE /api/admin/identities/{publicId} and POST /api/auth/confirm-erase — the identity was tombstoned.</summary>
    public const string IdentityErased = "identity.erased";

    public const string InviteCreated = "invite.created";
    public const string InviteRevoked = "invite.revoked";
    public const string InviteQuickLinkRotated = "invite.quick_link_rotated";
    public const string InviteResent = "invite.resent";
    public const string InviteAccepted = "invite.accepted";
    public const string TenantInviteCreated = "tenant_invite.created";
    public const string TenantInviteResent = "tenant_invite.resent";
    public const string TenantInviteRevoked = "tenant_invite.revoked";

    // ── Workspace and tenants ───────────────────────────────────────────────────────────────

    public const string WorkspaceRenamed = "workspace.renamed";
    public const string WorkspaceCommentFieldsUpdated = "workspace.comment_fields_updated";
    public const string WorkspaceCreated = "workspace.created";
    public const string TenantCreated = "tenant.created";
    public const string TenantStatusChanged = "tenant.status_changed";
    public const string TenantDemoExtended = "tenant.demo_extended";
    public const string TenantDemoConfigChanged = "tenant.demo_config_changed";
    public const string TenantPlanChanged = "tenant.plan_changed";
    public const string TenantHardDeleted = "tenant.hard_deleted";

    // ── Projects, roles, environments, statuses, actions, suggestions, AI rules ─────────────

    public const string ProjectCreated = "project.created";
    public const string ProjectUpdated = "project.updated";
    public const string ProjectDeleted = "project.deleted";
    public const string ProjectAppUrlSet = "project.app_url_set";
    public const string ProjectAppUrlDeleted = "project.app_url_deleted";
    public const string RoleCreated = "role.created";
    public const string RoleUpdated = "role.updated";
    public const string RoleDeleted = "role.deleted";
    public const string EnvironmentCreated = "environment.created";
    public const string EnvironmentUpdated = "environment.updated";
    public const string EnvironmentDeleted = "environment.deleted";
    public const string StatusUpdated = "status.updated";
    public const string StatusReset = "status.reset";
    public const string PredefinedActionCreated = "predefined_action.created";
    public const string PredefinedActionUpdated = "predefined_action.updated";
    public const string PredefinedActionDeleted = "predefined_action.deleted";
    public const string SuggestionApproved = "suggestion.approved";
    public const string SuggestionRejected = "suggestion.rejected";
    public const string SuggestionChangesRequested = "suggestion.changes_requested";
    public const string AiRuleCreated = "ai_rule.created";
    public const string AiRuleUpdated = "ai_rule.updated";
    public const string AiRuleDeleted = "ai_rule.deleted";
    public const string ImportCompleted = "import.completed";
    public const string ExportDownloaded = "export.downloaded";

    // ── Operator surfaces ───────────────────────────────────────────────────────────────────

    public const string PlanCreated = "plan.created";
    public const string PlanUpdated = "plan.updated";
    public const string PlanDeleted = "plan.deleted";
    public const string SettingsUpdated = "settings.updated";
    public const string BrandingUpdated = "branding.updated";
    public const string BrandingAssetUploaded = "branding.asset_uploaded";
    public const string BrandingAssetDeleted = "branding.asset_deleted";

    /// <summary>DB-13: a super admin opened a read-only "View as…" session on a workspace.</summary>
    public const string ImpersonationStarted = "impersonation.started";

    /// <summary>DB-13: an impersonation session ended (manually or via the expiry sweep).</summary>
    public const string ImpersonationEnded = "impersonation.ended";

    /// <summary>
    /// Every action string above, by reflection. <c>AuditWriter</c> rejects an entry whose action
    /// is not in here.
    /// </summary>
    public static readonly HashSet<string> All = new(
        typeof(AuditActions)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!),
        StringComparer.Ordinal
    );
}
