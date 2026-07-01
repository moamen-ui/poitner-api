namespace Pointer.Application.Abstractions;

/// <summary>
/// Low-level transactional email transport (Brevo HTTP API). Returns true on a successful
/// send; never throws to callers. Swap the implementation to change providers.
/// </summary>
public interface IEmailSender
{
    /// <summary>
    /// Sends one transactional email. <paramref name="fromEmail"/>/<paramref name="fromName"/> override
    /// the configured sender when provided (falls back to env config otherwise).
    /// </summary>
    Task<bool> SendAsync(string to, string subject, string htmlBody,
        string? fromEmail = null, string? fromName = null, CancellationToken ct = default);
}
