namespace Pointer.Application.DTOs.AiRule;

public class UpdateAiRuleRequest
{
    public string Title { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public bool? IsActive { get; set; }
    public int? SortOrder { get; set; }
}
