namespace Pointer.Application.DTOs.Tenant;

public class TenantResponse
{
    public int Id { get; set; }
    public Guid PublicId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ApprovalStatus { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public int Projects { get; set; }
    public int Comments { get; set; }
}
