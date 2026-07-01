using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;

namespace Pointer.Application.Services.Implementation;

/// <summary>
/// Application-facing email facade. The DB toggle "email_enabled" is authoritative — when off, every
/// send is a no-op. Guards each send with a per-UTC-day counter (AppSetting "email_sent_{yyyyMMdd}")
/// capped at "email_daily_cap" (default 250 — safely under Brevo's 300/day) so we never exceed the
/// free limit. The sender name/address come from settings (falling back to env in the transport).
/// Auto-registered as scoped (name ends with "Service").
/// </summary>
public class EmailService(IEmailSender sender, IUnitOfWork unitOfWork, ISettingsService settings) : IEmailService
{
    private const int DefaultDailyCap = 250;

    public async Task<bool> SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        // The super-admin DB toggle is the single source of truth for whether email is on.
        if (!await settings.GetBoolAsync(ISettingsService.EmailEnabled)) return false;

        var repo = unitOfWork.Repository<AppSetting>();

        var cap = await settings.GetIntAsync(ISettingsService.EmailDailyCap, DefaultDailyCap);

        var counterKey = $"email_sent_{DateTime.UtcNow:yyyyMMdd}";
        var counter = await repo.Query()
            .FirstOrDefaultAsync(s => s.DeletedAt == null && s.Key == counterKey, ct);
        var sentToday = int.TryParse(counter?.Value, out var n) ? n : 0;

        if (sentToday >= cap) return false; // capped — never exceed the free daily limit

        var fromEmail = await settings.GetStringAsync(ISettingsService.EmailFromEmail);
        var fromName = await settings.GetStringAsync(ISettingsService.EmailFromName);

        var ok = await sender.SendAsync(to, subject, htmlBody,
            fromEmail: string.IsNullOrWhiteSpace(fromEmail) ? null : fromEmail,
            fromName: string.IsNullOrWhiteSpace(fromName) ? null : fromName,
            ct: ct);
        if (!ok) return false;

        // Increment the day's counter. Single-process, low volume — a read-modify-write race is
        // acceptable because the cap is a safety margin under the free limit, not an exact quota.
        if (counter == null)
        {
            await repo.AddAsync(new AppSetting { Key = counterKey, Value = "1" });
        }
        else
        {
            counter.Value = (sentToday + 1).ToString();
            repo.Update(counter);
        }
        await unitOfWork.SaveChangesAsync();
        return true;
    }
}
