namespace Pointer.Application.DTOs.Auth;

public class RegisterAdminRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Optional chosen plan (workspace signup only; stakeholders never carry this). Null/Free →
    /// today's flow (no subscription row; effective plan ⇒ Free). A paid, active, visible plan →
    /// a Subscription(Status=PendingActivation) is created; a super-admin activates it later.
    /// </summary>
    public int? PlanId { get; set; }
}
