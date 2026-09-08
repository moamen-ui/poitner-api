namespace Pointer.Application.DTOs.AiRule;

public class ProjectAiRulesResponse
{
    public int ProjectId { get; set; }
    public string ProjectKey { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>Tenant-wide and project-level admin rules (read-only for non-admins, applied to all comments).</summary>
    public List<AiRuleResponse> AdminRules { get; set; } = new();

    /// <summary>Personal rules of the current user for this project (applied only to their comments).</summary>
    public List<AiRuleResponse> MyRules { get; set; } = new();
}
