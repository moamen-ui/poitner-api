using Pointer.Domain.Enums;

namespace Pointer.Domain.Entity;

public class User : BaseEntity
{
    public Guid PublicId { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int RoleId { get; set; }
    public Role Role { get; set; } = null!;
    public bool IsActive { get; set; } = true;
    public ApprovalStatus ApprovalStatus { get; set; } = ApprovalStatus.Approved;
    public string? Language { get; set; }
    public string? Theme { get; set; }

    /// <summary>
    /// Per-user "add comment" widget keyboard shortcut, e.g. "alt+shift+c" — synced to the
    /// account (not per-browser) so it follows the user across machines/browsers. Null = the
    /// widget's built-in default (Ctrl+Alt+Shift+C / Control+Option+Shift+C).
    /// </summary>
    public string? AddCommentShortcut { get; set; }

    /// <summary>
    /// Rotating session/token invalidation stamp (H1/H2). Embedded as the JWT <c>stamp</c> claim and
    /// in reset-token payloads; bumped (new Guid) on password change, disable, and reject so existing
    /// access tokens and outstanding reset links stop validating. Enforcement is gated by
    /// <c>Auth:ValidateSecurityStamp</c> — see AuthenticationExtensions.
    /// </summary>
    public Guid SecurityStamp { get; set; } = Guid.NewGuid();

    public Guid? OwnerId { get; set; }
    public bool IsDemo { get; set; }
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Whether a super-admin has already used their one-time demo extension for this user.</summary>
    public bool DemoExtended { get; set; }

    /// <summary>Per-tenant override of the demo comment cap. Null = use the global setting.</summary>
    public int? DemoCommentCapOverride { get; set; }

    /// <summary>Per-tenant override of the demo TTL (hours), used when extending. Null = use the global setting.</summary>
    public int? DemoTtlHoursOverride { get; set; }

    /// <summary>The real human email entered at demo provisioning time. Null for non-demo users. Cleared on upgrade.</summary>
    public string? RecipientEmail { get; set; }
}
