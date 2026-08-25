using Pointer.Domain.Enums;
using Pointer.Domain.ValueObjects;

namespace Pointer.Domain.Entity;

/// <summary>
/// Page-level console/network context, shared by every bug-flagged comment left on the same page
/// during the same browser-tab visit. Only created when a comment is submitted with
/// IsBugReport=true and the owning project has PageContextCaptureEnabled — see
/// CommentService.CreateAsync. Comments reference this by PageContextSnapshotId rather than each
/// embedding a copy, so the same console/network data is never duplicated across comments.
/// </summary>
public class PageContextSnapshot : BaseEntity
{
    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public EnvironmentTag Environment { get; set; }

    /// <summary>Path only — no query/hash — so /checkout?step=1 and ?step=2 share one snapshot.</summary>
    public string Route { get; set; } = string.Empty;

    /// <summary>Client-generated, one per browser tab; ties comments on the same page/visit together.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Tenant isolation, mirrors Comment.OwnerId.</summary>
    public Guid? OwnerId { get; set; }

    public DateTime LastEventAt { get; set; }
    public List<ConsoleLogEntry> ConsoleEntries { get; set; } = new();
    public List<NetworkFailureEntry> NetworkEntries { get; set; } = new();

    public ICollection<Comment> Comments { get; set; } = new List<Comment>();
}
