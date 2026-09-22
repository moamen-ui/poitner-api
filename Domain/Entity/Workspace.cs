namespace Pointer.Domain.Entity;

/// <summary>
/// One row per workspace. <see cref="Id"/> equals every <c>owner_id</c> that belongs to it and the
/// JWT <c>tenant</c> claim. <see cref="Name"/> is the workspace's own name, never a person's. Not a
/// <see cref="BaseEntity"/>: the PK is a uuid chosen before insert.
/// </summary>
public class Workspace
{
    /// <summary>
    /// Seeded by the DB-03 backfill and used by every mint point that has no workspace name to
    /// offer. <c>Name == PlaceholderName</c> means 'not yet named by an admin' (DB-03b shows a
    /// prompt).
    /// </summary>
    public const string PlaceholderName = "Workspace";

    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
    public DateTime? DeletedAt { get; set; }
    public Guid? DeletedBy { get; set; }
}
