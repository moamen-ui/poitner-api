namespace Pointer.Application.DTOs.Auth;

public class MeResponse
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int RoleId { get; set; }
    public string RoleName { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }
    public bool IsSuperAdmin { get; set; }
    public bool IsQuickAccess { get; set; }
    public string? Language { get; set; }
    public string? Theme { get; set; }

    /// <summary>Per-user "add comment" widget shortcut, e.g. "ctrl+alt+shift+KeyC". Null = widget default.</summary>
    public string? AddCommentShortcut { get; set; }

    /// <summary>The workspace's own name (<c>workspaces.name</c>, DB-03). Null for super admins, who
    /// have no workspace.</summary>
    public string? TenantName { get; set; }

    /// <summary>Current workspace id (JWT <c>tenant</c> claim). Null for super admins.</summary>
    public Guid? WorkspaceId { get; set; }

    /// <summary>Every live, approved, active membership of this identity (DB-11b). Empty for super admins.</summary>
    public List<WorkspaceChoice> Workspaces { get; set; } = new();

    /// <summary>DB-14: <c>EmailVerifiedAt != null || IsDemo || IsSuperAdmin || PasswordlessOnly</c> —
    /// true for every identity the admin-write gate does not block.</summary>
    public bool EmailVerified { get; set; }

    /// <summary>DB-14: <c>!EmailVerified &amp;&amp; IsAdmin</c> — the banner is loud only for people the
    /// gate actually blocks; a stakeholder gets a soft hint instead.</summary>
    public bool EmailVerificationRequired { get; set; }

    /// <summary>R5-61: true once TOTP MFA is enabled (<c>users.totp_enabled_at IS NOT NULL</c>).
    /// Always false for a non-super-admin — MFA is scoped to the super-admin account only (§3.4).</summary>
    public bool MfaEnabled { get; set; }

    /// <summary>DB-17: non-null = the current workspace is a live demo; the dashboard's countdown
    /// reads this, not sessionStorage.</summary>
    public DateTime? DemoExpiresAt { get; set; }

    /// <summary>DB-17: true when the current workspace is a live, unconverted, not-yet-extended demo.</summary>
    public bool DemoCanExtend { get; set; }

    /// <summary>DB-18: non-null = the current workspace is paused (self or operator).</summary>
    public DateTime? WorkspacePausedAt { get; set; }

    /// <summary>DB-18: true when only the operator can resume the current workspace.</summary>
    public bool WorkspacePausedByOperator { get; set; }

    /// <summary>DB-18: non-null = the current workspace has a scheduled deletion.</summary>
    public DateTime? WorkspaceDeletionScheduledFor { get; set; }
}
