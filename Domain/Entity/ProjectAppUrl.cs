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
    public Guid? OwnerId { get; set; }
}
