namespace Pointer.Application.Abstractions;

/// <summary>
/// Throttles failed password login attempts per normalized e-mail address (R5-59 §12).
/// Successful logins reset the counter. Unknown e-mails are recorded identically to avoid
/// account-enumeration side channels.
/// </summary>
public interface ILoginAttemptLimiter
{
    Task<bool> IsLockedAsync(string email);
    Task<int> GetRetryAfterSecondsAsync(string email);
    Task RecordFailureAsync(string email);
    Task ResetAsync(string email);
}
