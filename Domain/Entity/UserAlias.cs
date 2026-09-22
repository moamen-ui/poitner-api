namespace Pointer.Domain.Entity;

/// <summary>
/// Maps a <see cref="User.PublicId"/> that existed before a DB-11a same-e-mail merge to the identity
/// it now belongs to, so every <c>author_id</c>/<c>actor_id</c>/audit column written before the
/// merge still resolves to a display name (<c>UserNameResolver</c>). Not a <see cref="BaseEntity"/>:
/// a lookup table keyed by a uuid the caller already holds, joined to <c>users</c>, which is
/// filtered — same reasoning as <see cref="Workspace"/>. No query filter.
/// </summary>
public class UserAlias
{
    /// <summary>A <c>public_id</c> that existed before the merge.</summary>
    public Guid AliasPublicId { get; set; }

    /// <summary>The identity it now belongs to.</summary>
    public int UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>Informational only; no FK (the workspace may be hard-deleted later).</summary>
    public Guid? SourceWorkspaceId { get; set; }

    public DateTime MergedAt { get; set; }
}
