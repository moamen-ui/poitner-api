using Pointer.Application.DTOs.PredefinedAction;

namespace Pointer.Application.DTOs.Project;

public class ProjectResponse
{
    public int Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }

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
