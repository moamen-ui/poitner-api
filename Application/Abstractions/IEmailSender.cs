namespace Pointer.Application.Abstractions;

/// <summary>
/// Low-level transactional email transport (Brevo HTTP API). Returns true on a successful
/// send; never throws to callers. Swap the implementation to change providers.
/// </summary>
public interface IEmailSender
{
    Task<bool> SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default);
}
