namespace Pointer.Domain.Entity;

/// <summary>
/// One build of a project that has been reported as deployed.
/// </summary>
/// <remarks>
/// Recorded so a repeat report of the same sha can answer "already seen" without re-scanning
/// comments, and so an admin can see which builds a project has actually shipped.
///
/// The row is the FACT that a sha was deployed; what it deployed is on the comments, which carry
/// their own DeployedAt/DeployedSha. Keeping them separate means a comment applied long after a
/// build was first seen still gets marked by the next report, rather than being invisible because
/// the build row already existed.
/// </remarks>
public class ProjectBuild : BaseEntity
{
    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    /// <summary>Lowercased git sha, 7–40 hex. Unique per project.</summary>
    public string Sha { get; set; } = string.Empty;

    public DateTime FirstSeenAt { get; set; }

    public BuildSource Source { get; set; }

    /// <summary>
    /// Tenant. Stamped from the resolved PROJECT's owner, never from the caller — the caller may
    /// legitimately be a member of the tenant rather than its owner, and taking it from them would
    /// let the row drift away from the project it belongs to.
    /// </summary>
    public Guid? OwnerId { get; set; }
}

public enum BuildSource
{
    Widget = 1,
    Cli = 2,
}
