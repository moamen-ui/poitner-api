namespace Pointer.Domain.Entity;

/// <summary>
/// A named deployment stage (e.g. "default", "prod", "staging") used to give a project a distinct
/// AppUrl per environment. Global (OwnerId == null) environments are seeded/managed by a super
/// admin and visible to every tenant; a tenant may also define its own — mirrors Role's
/// own-plus-global pattern (see AppDbContext's query filter for Role).
/// Named "AppEnvironment", not "Environment", to avoid colliding with System.Environment.
/// </summary>
public class AppEnvironment : BaseEntity
{
    public string Name { get; set; } = string.Empty;

    // Whether this workspace environment may receive new project URLs and take part in origin
    // resolution. NOT the same thing as ProjectAppUrl.IsActive, which toggles ONE project's mapping
    // for ONE environment — this toggles the environment itself for the whole workspace.
    // Disabling blocks writes and resolution; it never hides or deletes existing URLs.
    public bool IsEnabled { get; set; } = true;

    // Set only on the global "default" row (R1-09): the environment the old single-AppUrl model used
    // as a dumping ground. Retired rows are hidden from the "add URL" picker but still render for
    // any URL still attached to them. A tenant's own environment named "default" is never retired.
    public bool IsRetired { get; set; } = false;

    public Guid? OwnerId { get; set; }
    public ICollection<ProjectAppUrl> ProjectAppUrls { get; set; } = new List<ProjectAppUrl>();
}
