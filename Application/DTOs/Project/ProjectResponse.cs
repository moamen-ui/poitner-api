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
}
