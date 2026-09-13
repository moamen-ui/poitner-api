using Pointer.Domain.Enums;
using Pointer.Domain.ValueObjects;
namespace Pointer.Domain.Entity;

public class Comment : BaseEntity
{
    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public EnvironmentTag Environment { get; set; }
    public CommentStatus Status { get; set; } = CommentStatus.Open;
    public Guid AuthorId { get; set; }
    public string Body { get; set; } = string.Empty;
    public bool IsPrivate { get; set; }
    public ElementCapture Element { get; set; } = new();
    public DateTime? AppliedAt { get; set; }
    public Guid? AppliedBy { get; set; }
    public string? AppliedByLabel { get; set; }
    // Link to the commit that applied this comment (constructed client-side from the local commit
    // SHA + the repo's remote URL — see skill.md's apply flow). Null for comments applied before
    // this field existed, or by a flow that doesn't track it; the widget shows "#" in that case.
    public string? CommitUrl { get; set; }

    /// <summary>
    /// The commit that applied this comment, as a raw sha. Distinct from <see cref="CommitUrl"/>,
    /// which is a display link: this is the value deploy-detection compares against, since only a
    /// sha can be tested for ancestry against a deployed build.
    /// </summary>
    public string? CommitSha { get; set; }

    /// <summary>
    /// Stamped the first time a build containing <see cref="CommitSha"/> is reported as deployed.
    /// Write-once — a later report of another build must not move it, or "when did this go live"
    /// would answer with the most recent deploy rather than the first one that carried the fix.
    /// </summary>
    public DateTime? DeployedAt { get; set; }

    /// <summary>The build sha that carried this comment's fix live.</summary>
    public string? DeployedSha { get; set; }
    // Stamped when the author or admin verifies an applied comment (thumbs up).
    public DateTime? VerifiedAt { get; set; }
    // Edit trace: stamped when the author edits the comment body / removes its image.
    public DateTime? EditedAt { get; set; }
    public Guid? EditedBy { get; set; }
    public Guid? OwnerId { get; set; }

    // "Report as a bug" checkbox state, stamped regardless of whether PageContextSnapshot ended up
    // non-empty — a cheap triage signal on its own. See docs/superpowers/specs/2026-08-25-page-context-capture-design.md.
    public bool IsBugReport { get; set; }
    public int? PageContextSnapshotId { get; set; }
    public PageContextSnapshot? PageContextSnapshot { get; set; }

    // Predefined-action snapshots (multi-select). We snapshot {text, prompt} at create time
    // rather than FK to PredefinedAction so (1) the prompt never has to be re-resolved (and never
    // reaches the browser via a join), and (2) editing/deleting an action definition later does not
    // rewrite historical comments. Stored as a JSON collection column (picked_actions).
    public List<CommentPickedAction> PickedActions { get; set; } = new();

    public ICollection<Reply> Replies { get; set; } = new List<Reply>();

    /// <summary>Advisory: the comment body looked like it contained a credential or payload.</summary>
    /// <remarks>Computed server-side on write. Never exposed to AI-facing surfaces — see R2-06.</remarks>
    public bool HasPayloadFlag { get; set; }

    /// <summary>Names of the matched detector patterns; empty when clean.</summary>
    public List<string> PayloadFlags { get; set; } = new();

}
