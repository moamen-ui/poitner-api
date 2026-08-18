namespace Pointer.Application.DTOs.Invite;

/// <summary>
/// Admin-facing invite row (create + list). Carries the shareable <see cref="Url"/> and
/// <see cref="Code"/>. Never exposes the tenant GUID.
/// </summary>
public class InviteResponse
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;

    /// <summary>The shareable join link: <c>{app}/join?code=…</c>.</summary>
    public string Url { get; set; } = string.Empty;

    public int? RoleId { get; set; }
    public string? RoleName { get; set; }
    public string? Email { get; set; }
    public DateTime ExpiresAt { get; set; }
    public int? MaxUses { get; set; }
    public int Uses { get; set; }

    /// <summary>
    /// True only on the response to POST (create): the invite email was just sent to
    /// <see cref="Email"/>. Always false on list rows — sending is a one-time side effect of
    /// creation, not a persisted/recomputed invite property. False also means "share the link
    /// yourself" — either no email was set, email is globally disabled, or the send failed.
    /// </summary>
    public bool EmailSent { get; set; }
}
