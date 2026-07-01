using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Pointer.Application.Abstractions;

namespace Pointer.Infrastructure.Email;

/// <summary>
/// Sends transactional email via Brevo's HTTP API (POST https://api.brevo.com/v3/smtp/email).
/// No-ops (logs + returns false) when Email:Enabled != true or the API key / from-address is
/// missing, so the app runs fine locally without email configured. Never throws to callers.
/// </summary>
public class BrevoEmailSender : IEmailSender
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<BrevoEmailSender> _log;

    public BrevoEmailSender(HttpClient http, IConfiguration config, ILogger<BrevoEmailSender> log)
    {
        _http = http;
        _config = config;
        _log = log;
    }

    public async Task<bool> SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        var enabled = string.Equals(_config["Email:Enabled"], "true", StringComparison.OrdinalIgnoreCase);
        var apiKey = _config["Email:ApiKey"];
        var fromEmail = _config["Email:FromEmail"];
        var fromName = _config["Email:FromName"];
        if (string.IsNullOrWhiteSpace(fromName)) fromName = "Pointer";

        if (!enabled || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(fromEmail))
        {
            _log.LogInformation("Email disabled or unconfigured; skipping send to {To} (subject: {Subject}).", to, subject);
            return false;
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.brevo.com/v3/smtp/email");
            req.Headers.Add("api-key", apiKey);
            req.Content = JsonContent.Create(new
            {
                sender = new { name = fromName, email = fromEmail },
                to = new[] { new { email = to } },
                subject,
                htmlContent = htmlBody,
            });

            using var res = await _http.SendAsync(req, ct);
            if (res.IsSuccessStatusCode) return true;

            var body = await res.Content.ReadAsStringAsync(ct);
            _log.LogWarning("Brevo send failed ({Status}) to {To}: {Body}", (int)res.StatusCode, to, body);
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Brevo send threw for {To}.", to);
            return false;
        }
    }
}
