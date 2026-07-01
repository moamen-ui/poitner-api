namespace Pointer.Application.DTOs.PredefinedAction;

/// <summary>
/// Tenant-wide predefined-action create (ProjectId == null). Project-scoped actions are
/// created via the nested reconcile on project create/edit, not here.
/// </summary>
public class CreatePredefinedActionRequest
{
    public string Text { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public int? SortOrder { get; set; }
}
