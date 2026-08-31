namespace Pointer.Application.DTOs.Project;

/// <summary>
/// Anonymous, cross-tenant aggregate for the public landing page (GET /api/public/stacks-summary).
/// Counts only — never project names, tenant IDs, emails, or any other identifying data.
/// </summary>
public class StacksSummaryResponse
{
    public int TotalProjects { get; set; }
    public Dictionary<string, int> Frontend { get; set; } = new();
    public Dictionary<string, int> Backend { get; set; } = new();
    public Dictionary<string, int> AiTools { get; set; } = new();
}
