using Pointer.Domain.Enums;

namespace Pointer.Domain.Entity;

public class User : BaseEntity
{
    public Guid PublicId { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// <b>Legacy (DB-11a).</b> Written once at identity creation (first workspace / super-admin
    /// role); never read by application code after DB-11a. Dropped by DB-11e.
    /// </summary>
    public int RoleId { get; set; }

    /// <summary>See <see cref="RoleId"/> — legacy, DB-11a.</summary>
    public Role Role { get; set; } = null!;

    /// <summary>
    /// Identity-level switch (false only after erase/merge). Per-workspace enable/disable is
    /// <see cref="WorkspaceMembership.IsActive"/>.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Legacy for non-super-admins; per-workspace approval is
    /// <see cref="WorkspaceMembership.ApprovalStatus"/>.
    /// </summary>
    public ApprovalStatus ApprovalStatus { get; set; } = ApprovalStatus.Approved;
    public string? Language { get; set; }
    public string? Theme { get; set; }

    /// <summary>
    /// Per-user "add comment" widget keyboard shortcut, e.g. "ctrl+alt+shift+KeyC" — synced to the
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

    /// <summary>
    /// This account has no usable password and signs in only via a quick-access magic link.
    /// </summary>
    /// <remarks>
    /// Its PasswordHash is deliberately random and unusable. The flag exists so password login can
    /// refuse the account explicitly rather than relying on the hash never matching — a future
    /// "set your password" path must not silently turn a link-only client into a password account.
    /// </remarks>
    public bool PasswordlessOnly { get; set; }

    /// <summary>
    /// Non-null ⇔ this row was merged into another identity by the DB-11a same-e-mail merge and is
    /// soft-deleted. Points at the canonical row's <see cref="Id"/>.
    /// </summary>
    public int? MergedIntoUserId { get; set; }

    /// <summary>Every workspace this identity has ever joined (live and ended). DB-11a.</summary>
    public ICollection<WorkspaceMembership> Memberships { get; set; } = new List<WorkspaceMembership>();

    /// <summary>
    /// Non-null = tombstone (DB-11c): e-mail/name/secrets replaced, memberships ended, <c>DeletedAt</c>
    /// set; <see cref="PublicId"/> is kept so authored content still resolves to "Deleted user".
    /// </summary>
    public DateTime? ErasedAt { get; set; }
}
