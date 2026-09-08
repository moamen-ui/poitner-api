namespace Pointer.Application.DTOs.AiRule;

public class AiRuleResponse
{
    public int Id { get; set; }
    public int? ProjectId { get; set; }
    public string? ProjectName { get; set; }
    public Guid? UserId { get; set; }
    public string? UserName { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsTenantWide => ProjectId == null && UserId == null;
    public bool IsProjectAdminRule => ProjectId != null && UserId == null;
    public bool IsPersonal => UserId != null;
    public bool CanEdit { get; set; }
    public bool CanDelete { get; set; }
}
