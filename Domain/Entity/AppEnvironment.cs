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
    public Guid? OwnerId { get; set; }
    public ICollection<ProjectAppUrl> ProjectAppUrls { get; set; } = new List<ProjectAppUrl>();
}
