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

    /// <summary>Mirrors the `local` environment URL; use `AppUrls`.</summary>
    public string? AppUrl { get; set; }

    public List<ProjectAppUrlResponse> AppUrls { get; set; } = new();

    /// <summary>Opt-in, default off: whether the widget may capture console/network context for this
    /// project's bug-flagged comments.</summary>
    public bool PageContextCaptureEnabled { get; set; }

    /// <summary>Whether comments are restricted to this project's registered app URLs.</summary>
    public bool EnforceAllowedOrigins { get; set; }

    /// <summary>When false, the widget emits no text content in the DOM snapshot for any element.</summary>
    public bool CaptureTextContent { get; set; } = true;

    /// <summary>Which Role.Id values see the widget's environment switcher. Null/empty = the
    /// default (everyone except Client/QuickAccess roles) — not yet customized for this project.</summary>
    public List<int>? EnvironmentSelectorRoleIds { get; set; }

    /// <summary>Whether the AI apply flow bundles applied comments into one commit or commits each
    /// one separately with its own Comment.CommitUrl. Default Single.</summary>
    public CommitStyle CommitStyle { get; set; }

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
