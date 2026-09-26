using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;

namespace Pointer.Application.Services.Implementation;

/// <inheritdoc cref="IBillingPeriodService" />
public class BillingPeriodService(
    IUnitOfWork unitOfWork,
    IEntitlementService entitlements,
    IBillingProvider billing,
    ISettingsService settings,
    IMembershipService memberships,
    IBrandingService branding,
    IEmailService emailService,
    ILogger<BillingPeriodService>? logger = null,
    IAuditWriter? audit = null
) : IBillingPeriodService
{
    private const string WorkspaceAdminRoleName = "Workspace Admin";
    private readonly IAuditWriter _audit = audit ?? NoopAuditWriter.Instance;

    public async Task RunOnceAsync(DateTime now)
    {
        await RunCompEndAsync(now);
        await RunReminderAsync(now);
        await RunPastDueAsync(now);
        await RunDowngradeAsync(now);
    }

    // ── h1: comp end ─────────────────────────────────────────────────────────────────────────

    private async Task RunCompEndAsync(DateTime now)
    {
        var candidates = await unitOfWork
            .Repository<Subscription>()
            .Query()
            .IgnoreQueryFilters()
            .Where(s =>
                s.DeletedAt == null && s.IsComplimentary && s.CompEndsAt != null && s.CompEndsAt <= now
            )
            .Select(s => new { s.Id, s.OwnerId })
            .ToListAsync();

        foreach (var c in candidates)
        {
            (Guid WorkspaceId, string WorkspaceName)? notify = null;
            try
            {
                await unitOfWork.ExecuteInTransactionAsync(async () =>
                {
                    await unitOfWork.ExecuteSqlRawAsync(
                        "SELECT id FROM workspaces WHERE id = {0} FOR UPDATE",
                        c.OwnerId
                    );
                    var sub = await unitOfWork
                        .Repository<Subscription>()
                        .Query()
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(s => s.Id == c.Id && s.DeletedAt == null);
                    if (
                        sub == null
                        || !sub.IsComplimentary
                        || sub.CompEndsAt == null
                        || sub.CompEndsAt > now
                    )
                        return;

                    var oldCompEndsAt = sub.CompEndsAt.Value;
                    sub.IsComplimentary = false;
                    sub.CompedAt = null;
                    sub.CompedBy = null;
                    sub.CompReason = null;
                    sub.CompEndsAt = null;
                    sub.CurrentPeriodEnd = oldCompEndsAt;
                    sub.Status = SubscriptionStatus.PastDue;
                    sub.RenewalReminderSentAt = null;
                    await unitOfWork.SaveChangesAsync();

                    await _audit.WriteAsync(
                        new AuditEntry(
                            AuditActions.BillingCompEnded,
                            AuditTargets.Workspace,
                            sub.OwnerId.ToString(),
                            sub.OwnerId,
                            After: new Dictionary<string, string> { ["source"] = "system" },
                            ActorKindOverride: AuditActorKind.System
                        )
                    );
                });

                var name = await WorkspaceNameAsync(c.OwnerId);
                notify = (c.OwnerId, name);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "BillingPeriodService h1 (comp end) failed for {Id}", c.Id);
            }

            if (notify is { } n)
                await SendAsync(n.WorkspaceId, n.WorkspaceName, BillingEmailKind.PastDue);
        }
    }

    // ── h2: renewal reminder (no audit — a notification stamp, like DB-18's reminder) ───────

    private async Task RunReminderAsync(DateTime now)
    {
        var reminderDays = await settings.GetIntAsync(ISettingsService.BillingReminderDays, 3);

        var candidates = await unitOfWork
            .Repository<Subscription>()
            .Query()
            .IgnoreQueryFilters()
            .Where(s =>
                s.DeletedAt == null
                && s.Status == SubscriptionStatus.Active
                && !s.IsComplimentary
                && s.CurrentPeriodEnd != null
                && now >= s.CurrentPeriodEnd.Value.AddDays(-reminderDays)
                && now < s.CurrentPeriodEnd.Value
                && s.RenewalReminderSentAt == null
            )
            .Select(s => new { s.Id, s.OwnerId })
            .ToListAsync();

        foreach (var c in candidates)
        {
            (Guid WorkspaceId, string WorkspaceName)? notify = null;
            try
            {
                await unitOfWork.ExecuteInTransactionAsync(async () =>
                {
                    await unitOfWork.ExecuteSqlRawAsync(
                        "SELECT id FROM workspaces WHERE id = {0} FOR UPDATE",
                        c.OwnerId
                    );
                    var sub = await unitOfWork
                        .Repository<Subscription>()
                        .Query()
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(s => s.Id == c.Id && s.DeletedAt == null);
                    if (
                        sub == null
                        || sub.Status != SubscriptionStatus.Active
                        || sub.IsComplimentary
                        || sub.CurrentPeriodEnd == null
                        || now < sub.CurrentPeriodEnd.Value.AddDays(-reminderDays)
                        || now >= sub.CurrentPeriodEnd.Value
                        || sub.RenewalReminderSentAt != null
                    )
                        return;

                    sub.RenewalReminderSentAt = now;
                    await unitOfWork.SaveChangesAsync();
                });

                var name = await WorkspaceNameAsync(c.OwnerId);
                notify = (c.OwnerId, name);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "BillingPeriodService h2 (reminder) failed for {Id}", c.Id);
            }

            if (notify is { } n)
                await SendAsync(n.WorkspaceId, n.WorkspaceName, BillingEmailKind.Reminder);
        }
    }

    // ── h3: past due ─────────────────────────────────────────────────────────────────────────

    private async Task RunPastDueAsync(DateTime now)
    {
        var candidates = await unitOfWork
            .Repository<Subscription>()
            .Query()
            .IgnoreQueryFilters()
            .Where(s =>
                s.DeletedAt == null
                && s.Status == SubscriptionStatus.Active
                && !s.IsComplimentary
                && s.CurrentPeriodEnd != null
                && s.CurrentPeriodEnd <= now
            )
            .Select(s => new { s.Id, s.OwnerId })
            .ToListAsync();

        foreach (var c in candidates)
        {
            (Guid WorkspaceId, string WorkspaceName)? notify = null;
            try
            {
                await unitOfWork.ExecuteInTransactionAsync(async () =>
                {
                    await unitOfWork.ExecuteSqlRawAsync(
                        "SELECT id FROM workspaces WHERE id = {0} FOR UPDATE",
                        c.OwnerId
                    );
                    var sub = await unitOfWork
                        .Repository<Subscription>()
                        .Query()
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(s => s.Id == c.Id && s.DeletedAt == null);
                    if (
                        sub == null
                        || sub.Status != SubscriptionStatus.Active
                        || sub.IsComplimentary
                        || sub.CurrentPeriodEnd == null
                        || sub.CurrentPeriodEnd > now
                    )
                        return;

                    sub.Status = SubscriptionStatus.PastDue;
                    await unitOfWork.SaveChangesAsync();

                    await _audit.WriteAsync(
                        new AuditEntry(
                            AuditActions.BillingPastDue,
                            AuditTargets.Workspace,
                            sub.OwnerId.ToString(),
                            sub.OwnerId,
                            ActorKindOverride: AuditActorKind.System
                        )
                    );
                });

                var name = await WorkspaceNameAsync(c.OwnerId);
                notify = (c.OwnerId, name);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "BillingPeriodService h3 (past due) failed for {Id}", c.Id);
            }

            if (notify is { } n)
                await SendAsync(n.WorkspaceId, n.WorkspaceName, BillingEmailKind.PastDue);
        }
    }

    // ── h4: downgrade ────────────────────────────────────────────────────────────────────────

    private async Task RunDowngradeAsync(DateTime now)
    {
        var graceDays = await settings.GetIntAsync(ISettingsService.BillingGraceDays, 7);

        var candidates = await unitOfWork
            .Repository<Subscription>()
            .Query()
            .IgnoreQueryFilters()
            .Where(s =>
                s.DeletedAt == null
                && s.Status == SubscriptionStatus.PastDue
                && s.CurrentPeriodEnd != null
                && s.CurrentPeriodEnd.Value.AddDays(graceDays) <= now
            )
            .Select(s => new { s.Id, s.OwnerId })
            .ToListAsync();

        foreach (var c in candidates)
        {
            (Guid WorkspaceId, string WorkspaceName)? notify = null;
            try
            {
                await unitOfWork.ExecuteInTransactionAsync(async () =>
                {
                    await unitOfWork.ExecuteSqlRawAsync(
                        "SELECT id FROM workspaces WHERE id = {0} FOR UPDATE",
                        c.OwnerId
                    );
                    var sub = await unitOfWork
                        .Repository<Subscription>()
                        .Query()
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(s => s.Id == c.Id && s.DeletedAt == null);
                    if (
                        sub == null
                        || sub.Status != SubscriptionStatus.PastDue
                        || sub.CurrentPeriodEnd == null
                        || sub.CurrentPeriodEnd.Value.AddDays(graceDays) > now
                    )
                        return;

                    var oldPlanId = sub.PlanId;
                    var freeId = await entitlements.GetFreePlanIdAsync();
                    sub.PlanId = freeId;
                    sub.Status = SubscriptionStatus.None;
                    sub.CurrentPeriodEnd = null;
                    sub.RenewalReminderSentAt = null;
                    await billing.ChangePlanAsync(sub, freeId);
                    await unitOfWork.SaveChangesAsync();

                    await _audit.WriteAsync(
                        new AuditEntry(
                            AuditActions.BillingDowngraded,
                            AuditTargets.Workspace,
                            sub.OwnerId.ToString(),
                            sub.OwnerId,
                            Before: new Dictionary<string, string> { ["plan_id"] = oldPlanId.ToString() },
                            After: new Dictionary<string, string> { ["plan_id"] = freeId.ToString() },
                            ActorKindOverride: AuditActorKind.System
                        )
                    );
                });

                var name = await WorkspaceNameAsync(c.OwnerId);
                notify = (c.OwnerId, name);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "BillingPeriodService h4 (downgrade) failed for {Id}", c.Id);
            }

            if (notify is { } n)
                await SendAsync(n.WorkspaceId, n.WorkspaceName, BillingEmailKind.Downgraded);
        }
    }

    // ── Shared ────────────────────────────────────────────────────────────────────────────────

    private async Task<string> WorkspaceNameAsync(Guid workspaceId) =>
        await unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .Where(w => w.Id == workspaceId)
            .Select(w => w.Name)
            .FirstOrDefaultAsync() ?? Workspace.PlaceholderName;

    private async Task SendAsync(Guid workspaceId, string workspaceName, BillingEmailKind kind)
    {
        var recipients = await memberships
            .InWorkspace(workspaceId)
            .Where(m =>
                m.LeftAt == null
                && m.IsActive
                && m.ApprovalStatus == ApprovalStatus.Approved
                && m.Role.Name == WorkspaceAdminRoleName
                && m.User.DeletedAt == null
                && !m.User.IsDemo
            )
            .Select(m => new { m.User.Email, m.User.Language })
            .ToListAsync();
        if (recipients.Count == 0)
            return;

        var brand = await branding.BuildResponseAsync("", new HashSet<string>());
        foreach (var r in recipients)
        {
            try
            {
                var (subject, html) = BillingEmails.Build(
                    kind,
                    r.Language,
                    new BillingEmailModel(
                        workspaceName,
                        brand.ProductName,
                        brand.Urls.App,
                        BrandColor: brand.PrimaryColor,
                        LogoUrl: brand.Assets.Logo
                    )
                );
                await emailService.SendAsync(r.Email, subject, html);
            }
            catch
            { /* best-effort; sender logs failures */
            }
        }
    }
}
