using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;

namespace Pointer.Application.Services.Implementation;

/// <summary>
/// Guards every send with a per-UTC-day counter persisted in AppSetting (key
/// "email_sent_{yyyyMMdd}") so we never exceed the provider's free daily limit. The cap is the
/// AppSetting "email_daily_cap" (default 250 — safely under Brevo's 300/day); a super-admin can
/// tune it via settings. Delegates to IEmailSender; whether email is actually enabled is decided
/// by the transport (Email:Enabled config). Auto-registered as scoped (name ends with "Service").
/// </summary>
public class EmailService(IEmailSender sender, IUnitOfWork unitOfWork) : IEmailService
{
    private const int DefaultDailyCap = 250;

    public async Task<bool> SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        var repo = unitOfWork.Repository<AppSetting>();

        var capSetting = await repo.Query().AsNoTracking()
            .FirstOrDefaultAsync(s => s.DeletedAt == null && s.Key == "email_daily_cap", ct);
        var cap = int.TryParse(capSetting?.Value, out var c) ? c : DefaultDailyCap;

        var counterKey = $"email_sent_{DateTime.UtcNow:yyyyMMdd}";
        var counter = await repo.Query()
            .FirstOrDefaultAsync(s => s.DeletedAt == null && s.Key == counterKey, ct);
        var sentToday = int.TryParse(counter?.Value, out var n) ? n : 0;

        if (sentToday >= cap) return false; // capped — never exceed the free daily limit

        var ok = await sender.SendAsync(to, subject, htmlBody, ct);
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
