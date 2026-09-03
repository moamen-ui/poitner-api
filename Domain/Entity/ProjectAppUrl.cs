namespace Pointer.Domain.Entity;

/// <summary>
/// A project's URL for one AppEnvironment (e.g. "prod" -&gt; https://app.example.com). Strict-own
/// (OwnerId is never null): it always matches the owning Project's tenant, same as Comment/Invite.
/// A project created via the browser extension gets its URL written here against "default".
/// </summary>
public class ProjectAppUrl : BaseEntity
{
    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public int AppEnvironmentId { get; set; }
    public AppEnvironment AppEnvironment { get; set; } = null!;
    public string Url { get; set; } = string.Empty;

    // Independent of Project's Local/Staging/Production activation (a different, fixed-enum
    // concept) — this toggles whether THIS SPECIFIC environment+URL mapping is enabled at all
    // (e.g. for browser-extension origin matching), not tied to comment tagging.
    public bool IsActive { get; set; } = true;

    public Guid? OwnerId { get; set; }
}
