namespace Pointer.Application.DTOs.PredefinedAction;

public class UpdatePredefinedActionRequest
{
    public string? Text { get; set; }
    public string? Prompt { get; set; }
    public bool? IsActive { get; set; }
    public int? SortOrder { get; set; }
}
