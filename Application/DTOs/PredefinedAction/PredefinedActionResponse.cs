namespace Pointer.Application.DTOs.PredefinedAction;

/// <summary>
/// Admin-facing response for predefined-action management. Includes <see cref="Prompt"/>
/// because CRUD is admin-gated (tenant-scoped). This is NOT sent to the widget — the
/// widget-read endpoint returns <see cref="PredefinedActionOption"/> (id + text only).
/// </summary>
public class PredefinedActionResponse
{
    public int Id { get; set; }
    public int? ProjectId { get; set; }
    public string Text { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public int SortOrder { get; set; }
}
