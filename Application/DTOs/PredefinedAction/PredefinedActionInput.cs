namespace Pointer.Application.DTOs.PredefinedAction;

/// <summary>
/// Nested reconcile item on CreateProjectRequest / UpdateProjectRequest for project-scoped
/// actions. Reconcile rule (last-write-wins): id present → update; id absent → add;
/// existing row absent from the payload → soft-delete.
/// </summary>
public class PredefinedActionInput
{
    public int? Id { get; set; }
    public string Text { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}
