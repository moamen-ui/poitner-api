using Pointer.Application.DTOs.PredefinedAction;
using Pointer.Domain.Enums;

namespace Pointer.Application.DTOs.Project;

public class ProjectResponse
{
    public int Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>Per-environment activation — keyed by the fixed EnvironmentTag enum
    /// (Local/Staging/Production), not the tenant-defined AppEnvironment catalog.</summary>
    public bool IsActiveLocal { get; set; }
    public bool IsActiveStaging { get; set; }
    public bool IsActiveProduction { get; set; }

    /// <summary>Computed server-side from the 3 flags above — Active (all on), Inactive (all off),
    /// or Partial (mixed) — so every dashboard framework renders the same derived state.</summary>
    public ProjectActivationState ActivationState { get; set; }

    /// <summary>Where this project's widget is embedded — required to send a quick-access client invite.</summary>
    public string? AppUrl { get; set; }

    /// <summary>Opt-in, default off: whether the widget may capture console/network context for this
    /// project's bug-flagged comments.</summary>
    public bool PageContextCaptureEnabled { get; set; }

    /// <summary>Active project-scoped predefined actions (admin view — includes prompt).</summary>
    public List<PredefinedActionResponse> PredefinedActions { get; set; } = new();

    /// <summary>Display name of the project's creator (resolved from CreatedBy — never the raw Guid).</summary>
    public string? CreatedByName { get; set; }

    /// <summary>Active (non-deleted) comment count for this project.</summary>
    public int CommentsCount { get; set; }

    /// <summary>UI HINT ONLY (re-enforced server-side): IsAdmin || CreatedBy == caller.</summary>
    public bool CanEdit { get; set; }

    /// <summary>UI HINT ONLY (re-enforced server-side): IsAdmin || (CreatedBy == caller &amp;&amp; CommentsCount == 0).</summary>
    public bool CanDelete { get; set; }
}
