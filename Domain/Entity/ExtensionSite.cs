namespace Pointer.Domain.Entity;

/// <summary>
/// Records a distinct origin a tenant's browser extension has activated on. TENANT-SCOPED (strict-own
/// query filter like <see cref="Invite"/>), unique <c>(OwnerId, Origin)</c>. Backs the
/// <c>MaxExtensionSites</c> lever. Written by <c>POST /api/extension/activate</c> (enforced-but-inert
/// until the real extension calls it).
/// </summary>
public class ExtensionSite : BaseEntity
{
    /// <summary>Tenant boundary. Strict-own, never null.</summary>
    public Guid OwnerId { get; set; }

    /// <summary>Normalized origin (scheme + host[:port], lower-case).</summary>
    public string Origin { get; set; } = string.Empty;

    public DateTime FirstSeenAt { get; set; }
}
