using Pointer.Application.DTOs.PredefinedAction;

namespace Pointer.Application.DTOs.Project;

public class CreateProjectRequest
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>Project-scoped predefined actions to create alongside the project.</summary>
    public List<PredefinedActionInput> PredefinedActions { get; set; } = new();
}
