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
}
