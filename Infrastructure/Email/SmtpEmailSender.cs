using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Pointer.Application.Abstractions;

namespace Pointer.Infrastructure.Email;

/// <summary>
/// Sends transactional email over plain SMTP — the local-dev counterpart to
/// <see cref="BrevoEmailSender"/>. Selected instead of it when <c>Email:Provider</c> is
/// <c>"smtp"</c> (see DependencyInjection); points at a local catch-all mail server
/// (Mailpit by default — see ../../local-mail-server) so invite/reset emails can actually be
/// read during development instead of just being logged. Never throws to callers.
/// </summary>
public class SmtpEmailSender : IEmailSender
{
    private readonly IConfiguration _config;
    private readonly ILogger<SmtpEmailSender> _log;

    public SmtpEmailSender(IConfiguration config, ILogger<SmtpEmailSender> log)
    {
        _config = config;
        _log = log;
    }

    public async Task<bool> SendAsync(string to, string subject, string htmlBody,
        string? fromEmail = null, string? fromName = null, CancellationToken ct = default)
    {
        var host = _config["Email:Smtp:Host"] ?? "localhost";
        var port = int.TryParse(_config["Email:Smtp:Port"], out var p) ? p : 1025;

        if (string.IsNullOrWhiteSpace(fromEmail)) fromEmail = _config["Email:FromEmail"];
        if (string.IsNullOrWhiteSpace(fromEmail)) fromEmail = "dev@pointer.local";
        if (string.IsNullOrWhiteSpace(fromName)) fromName = _config["Email:FromName"];
        if (string.IsNullOrWhiteSpace(fromName)) fromName = "Pointer (local)";

        try
        {
            using var client = new SmtpClient(host, port);
            using var message = new MailMessage
            {
                From = new MailAddress(fromEmail, fromName),
                Subject = subject,
                Body = htmlBody,
                IsBodyHtml = true,
            };
            message.To.Add(to);

            await client.SendMailAsync(message, ct);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SMTP send failed for {To} via {Host}:{Port}.", to, host, port);
            return false;
        }
    }
}
