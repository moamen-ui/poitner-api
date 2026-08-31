namespace Pointer.Application.DTOs.Project;

/// <summary>
/// A project's detected tech stack + the AI coding tool(s) that have registered against it.
/// Returned by both the write (POST) and read (GET) `/api/projects/{key}/stack` endpoints, and by
/// the anonymous cross-tenant `/api/public/stacks-summary` aggregate (as per-token counts instead
/// of these lists — see StacksSummaryResponse).
/// </summary>
public class ProjectStackResponse
{
    /// <summary>null = not yet detected. Empty list is not used — absence is always null.</summary>
    public List<string>? Frontend { get; set; }

    /// <summary>null = not detected, or the backend lives in a separate repo/is an external API.</summary>
    public List<string>? Backend { get; set; }

    /// <summary>Every AI tool that has ever registered against this project — grows over time,
    /// never write-once (see Project.AiToolsUsed).</summary>
    public List<string> AiTools { get; set; } = new();
}
