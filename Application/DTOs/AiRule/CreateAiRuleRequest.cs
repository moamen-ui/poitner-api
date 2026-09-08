namespace Pointer.Application.DTOs.AiRule;

public class CreateAiRuleRequest
{
    public int? ProjectId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public int? SortOrder { get; set; }
    /// <summary>Set to true if a non-admin user is creating a personal rule for their own comments.</summary>
    public bool IsPersonal { get; set; }
}
