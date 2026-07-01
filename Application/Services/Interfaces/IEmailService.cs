namespace Pointer.Application.Services.Interfaces;

/// <summary>
/// Application-facing email facade. Enforces a per-UTC-day send cap (to stay safely under the
/// free-tier provider limit) then delegates to the transport. Returns true only if actually sent
/// (false when capped, disabled, or the send failed) — callers treat email as best-effort.
/// </summary>
public interface IEmailService
{
    Task<bool> SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default);
}
