using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Workspace;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;

namespace Pointer.Application.Services.Implementation;

/// <inheritdoc cref="IWorkspaceLifecycleService"/>
public class WorkspaceLifecycleService : IWorkspaceLifecycleService
{
    private const string WorkspaceAdminRoleName = "Workspace Admin";

    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;
    private readonly IMembershipService _memberships;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IResetTokenService _resetTokens;
    private readonly IEmailService _emailService;
    private readonly IBrandingService _branding;
    private readonly IWorkspaceStateService _workspaceState;
    private readonly IWorkspaceService _workspaces;
    private readonly ITenantService _tenantService;
    private readonly ILoginAttemptLimiter _lockout;
    private readonly IConfiguration? _config;
    private readonly IAuditWriter _audit;

    public WorkspaceLifecycleService(
        IUnitOfWork unitOfWork,
        ICurrentUser currentUser,
        IMembershipService memberships,
        IPasswordHasher passwordHasher,
        IResetTokenService resetTokens,
        IEmailService emailService,
        IBrandingService branding,
        IWorkspaceStateService workspaceState,
        IWorkspaceService workspaces,
        ITenantService tenantService,
        ILoginAttemptLimiter lockout,
        IConfiguration? config = null,
        IAuditWriter? audit = null
    )
    {
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
        _memberships = memberships;
        _passwordHasher = passwordHasher;
        _resetTokens = resetTokens;
        _emailService = emailService;
        _branding = branding;
        _workspaceState = workspaceState;
        _workspaces = workspaces;
        _tenantService = tenantService;
        _lockout = lockout;
        _config = config;
        _audit = audit ?? NoopAuditWriter.Instance;
    }

    private sealed record LifecycleContext(
        Guid WorkspaceId,
        User Identity,
        WorkspaceMembership Membership,
        Workspace Workspace
    );

    // ── The common session guard (§3.4) ─────────────────────────────────────────────────────

    private async Task<Result<LifecycleContext>> RequireLifecycleAdminAsync()
    {
        if (_currentUser.KeyScopes != null)
            return Result<LifecycleContext>.Forbidden(MessageKeys.Workspace.KeySessionCannotManage);
        if (_currentUser.IsQuickAccess || _currentUser.IsSuperAdmin || _currentUser.IsImpersonating)
            return Result<LifecycleContext>.Forbidden(MessageKeys.Common.Forbidden);
        if (!TenantStamp.TryRequireOwner(_currentUser, out var ws))
            return Result<LifecycleContext>.Forbidden(MessageKeys.Common.Forbidden);
        if (_currentUser.Id is not Guid publicId)
            return Result<LifecycleContext>.Forbidden(MessageKeys.Common.Forbidden);

        var identity = await _memberships.FindIdentityByPublicIdAsync(publicId);
        if (identity == null)
            return Result<LifecycleContext>.Forbidden(MessageKeys.Common.Forbidden);

        var membership = await _memberships.GetMembershipAsync(identity.Id, ws);
        if (
            membership == null
            || !membership.IsActive
            || membership.ApprovalStatus != ApprovalStatus.Approved
            || membership.Role?.Name != WorkspaceAdminRoleName
        )
            return Result<LifecycleContext>.Forbidden(MessageKeys.Workspace.AdminOnly);

        // AsNoTracking — precondition reads only. Every write path below loads its own tracked copy
        // (never reusing this one), which is what keeps the confirm/pause-instead/cancel/reminder
        // locked re-checks honest (§3.4 concurrency rule: never reuse an entity tracked before a lock).
        var workspace = await _unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == ws && w.DeletedAt == null);
        if (workspace == null)
            return Result<LifecycleContext>.NotFound(MessageKeys.Workspace.NotFound);

        // D18.9 (Opus LOW): a converted workspace still inside the 72h re-verify window carries a
        // TTL but is a real workspace — only a LIVE, UNCONVERTED demo is blocked.
        if (workspace.DemoExpiresAt != null && workspace.DemoConvertedAt == null)
            return Result<LifecycleContext>.Failure(MessageKeys.Workspace.DemoCannotPauseOrDelete);

        return Result<LifecycleContext>.Success(
            new LifecycleContext(ws, identity, membership, workspace)
        );
    }

    private static Result<T> Propagate<T>(Result source)
    {
        if (source.IsNotFound)
            return Result<T>.NotFound(source.Message ?? MessageKeys.Common.Forbidden);
        if (source.IsConflict)
            return Result<T>.Conflict(source.Message ?? MessageKeys.Common.Forbidden);
        if (source.IsForbidden)
            return Result<T>.Forbidden(source.Message ?? MessageKeys.Common.Forbidden);
        return Result<T>.Failure(source.Message ?? MessageKeys.Common.Forbidden);
    }

    private static string NormalizeName(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return string.Empty;
        var normalized = s.Normalize(System.Text.NormalizationForm.FormC);
        var sb = new System.Text.StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            // Bidi/formatting marks (LRM, RLM, ALM, ZWSP) that a copy-paste from a bidi UI can carry
            // invisibly — stripped before comparison so an Arabic workspace name still matches.
            if (ch is '‎' or '‏' or '؜' or '​')
                continue;
            sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    private static DateTime TruncateToMs(DateTime t) =>
        new(t.Ticks - t.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

    // ── Session methods ──────────────────────────────────────────────────────────────────────

    public async Task<Result<WorkspaceResponse>> PauseAsync()
    {
        var guard = await RequireLifecycleAdminAsync();
        if (!guard.IsSuccess)
            return Propagate<WorkspaceResponse>(guard);
        var ctx = guard.Data!;

        if (ctx.Workspace.PausedAt != null)
            return Result<WorkspaceResponse>.Conflict(MessageKeys.Workspace.AlreadyPaused);
        if (ctx.Workspace.DeletionScheduledFor != null)
            return Result<WorkspaceResponse>.Conflict(
                MessageKeys.Workspace.DeletionAlreadyScheduled
            );

        var tracked = await _unitOfWork.Workspaces.FirstOrDefaultAsync(w =>
            w.Id == ctx.WorkspaceId
        );
        if (tracked == null)
            return Result<WorkspaceResponse>.Conflict(MessageKeys.Workspace.AlreadyDeleted);

        tracked.PausedAt = DateTime.UtcNow;
        tracked.PausedBy = ctx.Identity.PublicId;
        tracked.PausedByOperator = false;
        tracked.UpdatedAt = DateTime.UtcNow;
        tracked.UpdatedBy = ctx.Identity.PublicId;
        await _unitOfWork.SaveChangesAsync();
        _workspaceState.Invalidate(ctx.WorkspaceId);

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.WorkspacePaused,
                AuditTargets.Workspace,
                ctx.WorkspaceId.ToString(),
                ctx.WorkspaceId,
                After: new Dictionary<string, string> { ["source"] = "admin" }
            )
        );

        return await _workspaces.GetAsync();
    }

    public async Task<Result<WorkspaceResponse>> ResumeAsync()
    {
        var guard = await RequireLifecycleAdminAsync();
        if (!guard.IsSuccess)
            return Propagate<WorkspaceResponse>(guard);
        var ctx = guard.Data!;

        if (ctx.Workspace.PausedAt == null)
            return Result<WorkspaceResponse>.Conflict(MessageKeys.Workspace.NotPaused);
        if (ctx.Workspace.PausedByOperator)
            return Result<WorkspaceResponse>.Forbidden(MessageKeys.Workspace.PausedByOperator);
        if (ctx.Workspace.DeletionScheduledFor != null)
            return Result<WorkspaceResponse>.Conflict(MessageKeys.Workspace.CancelDeletionFirst);

        var tracked = await _unitOfWork.Workspaces.FirstOrDefaultAsync(w =>
            w.Id == ctx.WorkspaceId
        );
        if (tracked == null)
            return Result<WorkspaceResponse>.Conflict(MessageKeys.Workspace.AlreadyDeleted);

        tracked.PausedAt = null;
        tracked.PausedBy = null;
        tracked.PausedByOperator = false;
        tracked.UpdatedAt = DateTime.UtcNow;
        tracked.UpdatedBy = ctx.Identity.PublicId;
        await _unitOfWork.SaveChangesAsync();
        _workspaceState.Invalidate(ctx.WorkspaceId);

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.WorkspaceResumed,
                AuditTargets.Workspace,
                ctx.WorkspaceId.ToString(),
                ctx.WorkspaceId,
                After: new Dictionary<string, string> { ["source"] = "admin" }
            )
        );

        return await _workspaces.GetAsync();
    }

    public async Task<Result<WorkspaceDeletionRequestResponse>> RequestDeletionAsync()
    {
        var guard = await RequireLifecycleAdminAsync();
        if (!guard.IsSuccess)
            return Propagate<WorkspaceDeletionRequestResponse>(guard);
        var ctx = guard.Data!;

        if (ctx.Workspace.DeletionScheduledFor != null)
            return Result<WorkspaceDeletionRequestResponse>.Conflict(
                MessageKeys.Workspace.DeletionAlreadyScheduled
            );
        if (ctx.Workspace.PausedByOperator)
            return Result<WorkspaceDeletionRequestResponse>.Forbidden(
                MessageKeys.Workspace.PausedByOperator
            );

        var now = DateTime.UtcNow;
        if (
            ctx.Workspace.DeletionRequestedAt is DateTime lastRequested
            && lastRequested > now.AddMinutes(-5)
        )
            return Result<WorkspaceDeletionRequestResponse>.Failure(
                MessageKeys.Workspace.DeletionEmailJustSent
            );

        // Opus MEDIUM: a per-workspace daily cap so one workspace cannot drain the 250/day global
        // e-mail budget (EmailService.cs).
        var dailyCount = await _unitOfWork
            .AuditEvents.IgnoreQueryFilters()
            .CountAsync(e =>
                e.OwnerId == ctx.WorkspaceId
                && e.Action == AuditActions.WorkspaceDeletionRequested
                && e.OccurredAt > now.AddHours(-24)
            );
        if (dailyCount >= 5)
            return Result<WorkspaceDeletionRequestResponse>.Failure(
                MessageKeys.Workspace.DeletionDailyLimit
            );

        var requestedAt = TruncateToMs(now);

        var tracked = await _unitOfWork.Workspaces.FirstOrDefaultAsync(w =>
            w.Id == ctx.WorkspaceId
        );
        if (tracked == null)
            return Result<WorkspaceDeletionRequestResponse>.Conflict(
                MessageKeys.Workspace.AlreadyDeleted
            );

        tracked.DeletionRequestedAt = requestedAt;
        tracked.DeletionRequestedBy = ctx.Identity.PublicId;
        await _unitOfWork.SaveChangesAsync();
        _workspaceState.Invalidate(ctx.WorkspaceId);

        var expiresAt = requestedAt.AddMinutes(30);

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.WorkspaceDeletionRequested,
                AuditTargets.Workspace,
                ctx.WorkspaceId.ToString(),
                ctx.WorkspaceId,
                After: new Dictionary<string, string> { ["expires_at"] = expiresAt.ToString("O") }
            )
        );

        // D18.2: the requester only. Token payload binds workspaceId|membershipStamp|requestedAtMs
        // (§3.4) — a newer request or a cancel voids any older outstanding link.
        var payload =
            $"{ctx.WorkspaceId:N}|{ctx.Membership.SecurityStamp:N}|{new DateTimeOffset(requestedAt, TimeSpan.Zero).ToUnixTimeMilliseconds()}";
        var token = _resetTokens.CreateScoped(
            ctx.Identity.PublicId,
            ctx.Identity.SecurityStamp,
            TokenPurposes.DeleteWorkspace,
            payload
        );
        var brand = await _branding.BuildResponseAsync("", new HashSet<string>());
        var link =
            $"{brand.Urls.App.TrimEnd('/')}/confirm-workspace-deletion?token={Uri.EscapeDataString(token)}";
        var graceDays = WorkspaceDeletionConfig.GraceDays(_config);

        try
        {
            var (subject, html) = WorkspaceLifecycleEmails.Build(
                WorkspaceLifecycleEmailKind.ConfirmDeletion,
                ctx.Identity.Language,
                new WorkspaceLifecycleEmailModel(
                    tracked.Name,
                    brand.ProductName,
                    brand.Urls.App,
                    Link: link,
                    GraceDays: graceDays
                )
            );
            await _emailService.SendAsync(ctx.Identity.Email, subject, html);
        }
        catch
        { /* best-effort; sender logs failures */
        }

        return Result<WorkspaceDeletionRequestResponse>.Success(
            new WorkspaceDeletionRequestResponse { ExpiresAt = expiresAt }
        );
    }

    public async Task<Result<WorkspaceResponse>> CancelDeletionAsync()
    {
        var guard = await RequireLifecycleAdminAsync();
        if (!guard.IsSuccess)
            return Propagate<WorkspaceResponse>(guard);
        var ctx = guard.Data!;

        if (ctx.Workspace.DeletionScheduledFor == null && ctx.Workspace.DeletionRequestedAt == null)
            return Result<WorkspaceResponse>.Conflict(MessageKeys.Workspace.NoDeletionScheduled);

        Result<WorkspaceResponse>? outcome = null;
        var wasScheduled = false;
        var stillPaused = false;

        try
        {
            await _unitOfWork.ExecuteInTransactionAsync(async () =>
            {
                await _unitOfWork.ExecuteSqlRawAsync(
                    "SELECT id FROM workspaces WHERE id = {0} FOR UPDATE",
                    ctx.WorkspaceId
                );

                var fresh = await _unitOfWork
                    .Workspaces.IgnoreQueryFilters()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(w => w.Id == ctx.WorkspaceId && w.DeletedAt == null);
                if (fresh == null)
                {
                    outcome = Result<WorkspaceResponse>.Conflict(
                        MessageKeys.Workspace.AlreadyDeleted
                    );
                    return;
                }
                if (fresh.DeletionScheduledFor == null && fresh.DeletionRequestedAt == null)
                {
                    outcome = Result<WorkspaceResponse>.Conflict(
                        MessageKeys.Workspace.NoDeletionScheduled
                    );
                    return;
                }

                wasScheduled = fresh.DeletionScheduledFor != null;
                stillPaused = fresh.PausedAt != null;

                var tracked = await _unitOfWork.Workspaces.FirstOrDefaultAsync(w =>
                    w.Id == ctx.WorkspaceId
                );
                if (tracked == null)
                {
                    outcome = Result<WorkspaceResponse>.Conflict(
                        MessageKeys.Workspace.AlreadyDeleted
                    );
                    return;
                }

                tracked.DeletionRequestedAt = null;
                tracked.DeletionRequestedBy = null;
                tracked.DeletionConfirmedAt = null;
                tracked.DeletionScheduledFor = null;
                tracked.DeletionReminderSentAt = null;
                tracked.UpdatedAt = DateTime.UtcNow;
                tracked.UpdatedBy = ctx.Identity.PublicId;
                await _unitOfWork.SaveChangesAsync();
            });
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result<WorkspaceResponse>.Conflict(MessageKeys.Workspace.StateChanged);
        }

        if (outcome != null)
            return outcome;

        _workspaceState.Invalidate(ctx.WorkspaceId);

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.WorkspaceDeletionCancelled,
                AuditTargets.Workspace,
                ctx.WorkspaceId.ToString(),
                ctx.WorkspaceId,
                After: new Dictionary<string, string>
                {
                    ["source"] = wasScheduled ? "admin" : "admin_pending",
                }
            )
        );

        if (wasScheduled)
            await SendToAllAdminsAsync(
                ctx.WorkspaceId,
                WorkspaceLifecycleEmailKind.Cancelled,
                ctx.Identity.DisplayName,
                stillPaused
            );

        return await _workspaces.GetAsync();
    }

    // ── Anonymous token methods ──────────────────────────────────────────────────────────────

    /// <summary>§3.4 — one failure message (<c>DeletionLinkInvalid</c>) for every reason the token
    /// or the state it is bound to could be wrong (Opus enumeration-resistance precedent).</summary>
    private async Task<
        Result<(User Identity, WorkspaceMembership Membership, Workspace Workspace)>
    > ValidateTokenAsync(string token)
    {
        Result<(User, WorkspaceMembership, Workspace)> Invalid() =>
            Result<(User, WorkspaceMembership, Workspace)>.Failure(
                MessageKeys.Workspace.DeletionLinkInvalid
            );

        if (
            !_resetTokens.TryValidateScoped(
                token,
                TokenPurposes.DeleteWorkspace,
                out var publicId,
                out var stamp,
                out var payload
            )
            || payload == null
        )
            return Invalid();

        var parts = payload.Split('|');
        if (parts.Length != 3)
            return Invalid();
        if (!Guid.TryParseExact(parts[0], "N", out var workspaceId))
            return Invalid();
        if (!Guid.TryParseExact(parts[1], "N", out var membershipStamp))
            return Invalid();
        if (!long.TryParse(parts[2], out var requestedAtMs))
            return Invalid();

        var identity = await _memberships.FindIdentityByPublicIdAsync(publicId);
        if (identity == null || identity.SecurityStamp != stamp)
            return Invalid();

        var workspace = await _unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == workspaceId);
        if (workspace == null || workspace.DeletedAt != null)
            return Invalid();

        if (workspace.DeletionRequestedAt is not DateTime requestedAt)
            return Invalid();
        var storedMs = new DateTimeOffset(
            DateTime.SpecifyKind(requestedAt, DateTimeKind.Utc)
        ).ToUnixTimeMilliseconds();
        if (storedMs != requestedAtMs)
            return Invalid();
        if (workspace.DeletionRequestedBy != identity.PublicId)
            return Invalid();
        if (workspace.DeletionScheduledFor != null)
            return Invalid();

        var membership = await _memberships.GetMembershipAsync(identity.Id, workspaceId);
        if (
            membership == null
            || !membership.IsActive
            || membership.ApprovalStatus != ApprovalStatus.Approved
            || membership.Role?.Name != WorkspaceAdminRoleName
        )
            return Invalid();
        if (membership.SecurityStamp != membershipStamp)
            return Invalid();

        if (workspace.DemoExpiresAt != null && workspace.DemoConvertedAt == null)
            return Invalid();

        return Result<(User, WorkspaceMembership, Workspace)>.Success(
            (identity, membership, workspace)
        );
    }

    public async Task<Result<WorkspaceDeletionPreviewResponse>> PreviewDeletionAsync(string token)
    {
        var validated = await ValidateTokenAsync(token);
        if (!validated.IsSuccess)
            return Propagate<WorkspaceDeletionPreviewResponse>(validated);
        var (identity, _, workspace) = validated.Data;

        var projectCount = await _unitOfWork
            .Repository<Project>()
            .Query()
            .IgnoreQueryFilters()
            .CountAsync(p => p.OwnerId == workspace.Id && p.DeletedAt == null);
        var commentCount = await _unitOfWork
            .Repository<Comment>()
            .Query()
            .IgnoreQueryFilters()
            .CountAsync(c => c.OwnerId == workspace.Id && c.DeletedAt == null);
        var memberCount = await _memberships
            .InWorkspace(workspace.Id)
            .CountAsync(m => m.LeftAt == null);

        // Opus HIGH 1: the SAME shared query TenantService.HardDeleteAsync uses for the delete set.
        var deletedWithWorkspace = TenantService.IdentitiesDeletedWithWorkspace(
            _unitOfWork,
            workspace.Id
        );
        var accountsCount = await deletedWithWorkspace.CountAsync();
        var requesterDeleted = await deletedWithWorkspace.AnyAsync(u => u.Id == identity.Id);

        var graceDays = WorkspaceDeletionConfig.GraceDays(_config);

        return Result<WorkspaceDeletionPreviewResponse>.Success(
            new WorkspaceDeletionPreviewResponse
            {
                WorkspaceId = workspace.Id,
                WorkspaceName = workspace.Name,
                ProjectCount = projectCount,
                CommentCount = commentCount,
                MemberCount = memberCount,
                AccountsDeletedWithWorkspace = accountsCount,
                RequesterAccountDeleted = requesterDeleted,
                RequiresPassword = !identity.PasswordlessOnly,
                GraceDays = graceDays,
                WouldBeDeletedOn = DateTime.UtcNow.AddDays(graceDays),
                IsPaused = workspace.PausedAt != null,
            }
        );
    }

    public async Task<Result<WorkspaceDeletionScheduledResponse>> ConfirmDeletionAsync(
        ConfirmWorkspaceDeletionRequest request
    )
    {
        var validated = await ValidateTokenAsync(request.Token);
        if (!validated.IsSuccess)
            return Propagate<WorkspaceDeletionScheduledResponse>(validated);
        var (identity, _, workspace) = validated.Data;

        if (workspace.PausedByOperator)
            return Result<WorkspaceDeletionScheduledResponse>.Forbidden(
                MessageKeys.Workspace.PausedByOperator
            );

        if (
            !string.Equals(
                NormalizeName(request.WorkspaceName),
                NormalizeName(workspace.Name),
                StringComparison.Ordinal
            )
        )
            return Result<WorkspaceDeletionScheduledResponse>.Failure(
                MessageKeys.Workspace.DeletionNameMismatch
            );

        if (!identity.PasswordlessOnly)
        {
            // Opus MEDIUM (R5-59): reuse the same per-identity lockout as password login — a
            // guessed link must not become an unbounded password-guessing oracle.
            if (await _lockout.IsLockedAsync(identity.Email))
                return Result<WorkspaceDeletionScheduledResponse>.Failure(
                    MessageKeys.Auth.TooManyAttempts
                );

            if (
                string.IsNullOrEmpty(request.Password)
                || !_passwordHasher.Verify(request.Password, identity.PasswordHash)
            )
            {
                await _lockout.RecordFailureAsync(identity.Email);

                // D18.5: after the 5th failure on this link, the link itself is spent.
                if (await _lockout.IsLockedAsync(identity.Email))
                    await SpendLinkAsync(workspace.Id);

                return Result<WorkspaceDeletionScheduledResponse>.Failure(
                    MessageKeys.User.CurrentPasswordIncorrect
                );
            }
        }

        Result<WorkspaceDeletionScheduledResponse>? outcome = null;
        var scheduledFor = default(DateTime);

        try
        {
            await _unitOfWork.ExecuteInTransactionAsync(async () =>
            {
                await _unitOfWork.ExecuteSqlRawAsync(
                    "SELECT id FROM workspaces WHERE id = {0} FOR UPDATE",
                    workspace.Id
                );

                // Re-run the token *state* checks on the fresh, locked row — a racing confirm/
                // pause-instead/cancel makes this fail cleanly with DeletionLinkInvalid.
                var revalidated = await ValidateTokenAsync(request.Token);
                if (!revalidated.IsSuccess)
                {
                    outcome = Propagate<WorkspaceDeletionScheduledResponse>(revalidated);
                    return;
                }

                var tracked = await _unitOfWork.Workspaces.FirstOrDefaultAsync(w =>
                    w.Id == workspace.Id
                );
                if (tracked == null)
                {
                    outcome = Result<WorkspaceDeletionScheduledResponse>.Conflict(
                        MessageKeys.Workspace.AlreadyDeleted
                    );
                    return;
                }

                var graceDays = WorkspaceDeletionConfig.GraceDays(_config);
                var now = DateTime.UtcNow;
                scheduledFor = now.AddDays(graceDays);

                tracked.DeletionConfirmedAt = now;
                tracked.DeletionScheduledFor = scheduledFor;
                tracked.DeletionReminderSentAt = null;
                await _unitOfWork.SaveChangesAsync();
            });
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result<WorkspaceDeletionScheduledResponse>.Conflict(
                MessageKeys.Workspace.StateChanged
            );
        }

        if (outcome != null)
            return outcome;

        if (!identity.PasswordlessOnly)
            await _lockout.ResetAsync(identity.Email);

        _workspaceState.Invalidate(workspace.Id);

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.WorkspaceDeletionConfirmed,
                AuditTargets.Workspace,
                workspace.Id.ToString(),
                workspace.Id,
                After: new Dictionary<string, string>
                {
                    ["scheduled_for"] = scheduledFor.ToString("O"),
                    ["with_password"] = (!identity.PasswordlessOnly).ToString().ToLowerInvariant(),
                },
                ActorUserIdOverride: identity.PublicId,
                ActorKindOverride: AuditActorKind.User
            )
        );

        await SendToAllAdminsAsync(
            workspace.Id,
            WorkspaceLifecycleEmailKind.Scheduled,
            identity.DisplayName,
            false,
            scheduledFor
        );

        return Result<WorkspaceDeletionScheduledResponse>.Success(
            new WorkspaceDeletionScheduledResponse
            {
                WorkspaceId = workspace.Id,
                DeletionScheduledFor = scheduledFor,
            }
        );
    }

    /// <summary>D18.5 (5th wrong-password failure): clears the pending request so the link dies —
    /// its own locked transaction (the confirm attempt that triggered this has already failed).</summary>
    private async Task SpendLinkAsync(Guid workspaceId)
    {
        try
        {
            await _unitOfWork.ExecuteInTransactionAsync(async () =>
            {
                await _unitOfWork.ExecuteSqlRawAsync(
                    "SELECT id FROM workspaces WHERE id = {0} FOR UPDATE",
                    workspaceId
                );
                var tracked = await _unitOfWork.Workspaces.FirstOrDefaultAsync(w =>
                    w.Id == workspaceId
                );
                if (tracked == null)
                    return;
                tracked.DeletionRequestedAt = null;
                tracked.DeletionRequestedBy = null;
                await _unitOfWork.SaveChangesAsync();
            });
            _workspaceState.Invalidate(workspaceId);
        }
        catch (DbUpdateConcurrencyException)
        { /* already gone or changed by someone else — nothing to spend */
        }
    }

    public async Task<Result> PauseInsteadAsync(string token)
    {
        var validated = await ValidateTokenAsync(token);
        if (!validated.IsSuccess)
            return validated;
        var (identity, _, workspace) = validated.Data;

        Result? outcome = null;
        var paused = false;

        try
        {
            await _unitOfWork.ExecuteInTransactionAsync(async () =>
            {
                await _unitOfWork.ExecuteSqlRawAsync(
                    "SELECT id FROM workspaces WHERE id = {0} FOR UPDATE",
                    workspace.Id
                );

                var revalidated = await ValidateTokenAsync(token);
                if (!revalidated.IsSuccess)
                {
                    outcome = revalidated;
                    return;
                }

                var tracked = await _unitOfWork.Workspaces.FirstOrDefaultAsync(w =>
                    w.Id == workspace.Id
                );
                if (tracked == null)
                {
                    outcome = Result.Conflict(MessageKeys.Workspace.AlreadyDeleted);
                    return;
                }

                paused = tracked.PausedAt == null;
                if (paused)
                {
                    tracked.PausedAt = DateTime.UtcNow;
                    tracked.PausedBy = identity.PublicId;
                    tracked.PausedByOperator = false;
                }

                // Always spends the link, whether it paused or merely found the workspace already paused.
                tracked.DeletionRequestedAt = null;
                tracked.DeletionRequestedBy = null;
                tracked.UpdatedAt = DateTime.UtcNow;
                tracked.UpdatedBy = identity.PublicId;
                await _unitOfWork.SaveChangesAsync();
            });
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Conflict(MessageKeys.Workspace.StateChanged);
        }

        if (outcome != null)
            return outcome;

        _workspaceState.Invalidate(workspace.Id);

        // Opus LOW: exactly one audit row either way (AuditCoverageFilter's StrictCoverage 500).
        await _audit.WriteAsync(
            new AuditEntry(
                paused ? AuditActions.WorkspacePaused : AuditActions.WorkspaceDeletionCancelled,
                AuditTargets.Workspace,
                workspace.Id.ToString(),
                workspace.Id,
                After: new Dictionary<string, string> { ["source"] = "delete_link" },
                ActorUserIdOverride: identity.PublicId,
                ActorKindOverride: AuditActorKind.User
            )
        );

        return Result.Success();
    }

    // ── Operator methods ─────────────────────────────────────────────────────────────────────

    private Result RequireOperator() =>
        !_currentUser.IsSuperAdmin || _currentUser.IsImpersonating
            ? Result.Forbidden(MessageKeys.Common.Forbidden)
            : Result.Success();

    public async Task<Result> OperatorPauseAsync(Guid workspaceId)
    {
        var guard = RequireOperator();
        if (!guard.IsSuccess)
            return guard;

        var workspace = await _unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.Id == workspaceId && w.DeletedAt == null);
        if (workspace == null)
            return Result.NotFound(MessageKeys.Workspace.NotFound);

        var operatorId = _currentUser.Id ?? Guid.Empty;
        // D18.6: an operator pause overrides a self-pause and admins cannot lift it.
        workspace.PausedAt = DateTime.UtcNow;
        workspace.PausedBy = operatorId;
        workspace.PausedByOperator = true;
        workspace.UpdatedAt = DateTime.UtcNow;
        workspace.UpdatedBy = operatorId;
        await _unitOfWork.SaveChangesAsync();
        _workspaceState.Invalidate(workspaceId);

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.WorkspacePaused,
                AuditTargets.Workspace,
                workspaceId.ToString(),
                workspaceId,
                After: new Dictionary<string, string> { ["source"] = "operator" }
            )
        );

        return Result.Success();
    }

    public async Task<Result> OperatorResumeAsync(Guid workspaceId)
    {
        var guard = RequireOperator();
        if (!guard.IsSuccess)
            return guard;

        var workspace = await _unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.Id == workspaceId && w.DeletedAt == null);
        if (workspace == null)
            return Result.NotFound(MessageKeys.Workspace.NotFound);

        var operatorId = _currentUser.Id ?? Guid.Empty;
        // Opus NIT (documented, not changed — D18.6): operator resume also clears an earlier admin
        // self-pause; the admin can pause again.
        workspace.PausedAt = null;
        workspace.PausedBy = null;
        workspace.PausedByOperator = false;
        workspace.UpdatedAt = DateTime.UtcNow;
        workspace.UpdatedBy = operatorId;
        await _unitOfWork.SaveChangesAsync();
        _workspaceState.Invalidate(workspaceId);

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.WorkspaceResumed,
                AuditTargets.Workspace,
                workspaceId.ToString(),
                workspaceId,
                After: new Dictionary<string, string> { ["source"] = "operator" }
            )
        );

        return Result.Success();
    }

    public async Task<Result> OperatorCancelDeletionAsync(Guid workspaceId)
    {
        var guard = RequireOperator();
        if (!guard.IsSuccess)
            return guard;

        var exists = await _unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .AnyAsync(w => w.Id == workspaceId && w.DeletedAt == null);
        if (!exists)
            return Result.NotFound(MessageKeys.Workspace.NotFound);

        var operatorId = _currentUser.Id ?? Guid.Empty;
        Result? outcome = null;
        var wasScheduled = false;
        var stillPaused = false;

        try
        {
            await _unitOfWork.ExecuteInTransactionAsync(async () =>
            {
                await _unitOfWork.ExecuteSqlRawAsync(
                    "SELECT id FROM workspaces WHERE id = {0} FOR UPDATE",
                    workspaceId
                );

                var fresh = await _unitOfWork
                    .Workspaces.IgnoreQueryFilters()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(w => w.Id == workspaceId && w.DeletedAt == null);
                if (fresh == null)
                {
                    outcome = Result.Conflict(MessageKeys.Workspace.AlreadyDeleted);
                    return;
                }
                if (fresh.DeletionScheduledFor == null && fresh.DeletionRequestedAt == null)
                {
                    outcome = Result.Conflict(MessageKeys.Workspace.NoDeletionScheduled);
                    return;
                }

                wasScheduled = fresh.DeletionScheduledFor != null;
                stillPaused = fresh.PausedAt != null;

                var tracked = await _unitOfWork.Workspaces.FirstOrDefaultAsync(w =>
                    w.Id == workspaceId
                );
                if (tracked == null)
                {
                    outcome = Result.Conflict(MessageKeys.Workspace.AlreadyDeleted);
                    return;
                }

                tracked.DeletionRequestedAt = null;
                tracked.DeletionRequestedBy = null;
                tracked.DeletionConfirmedAt = null;
                tracked.DeletionScheduledFor = null;
                tracked.DeletionReminderSentAt = null;
                tracked.UpdatedAt = DateTime.UtcNow;
                tracked.UpdatedBy = operatorId;
                await _unitOfWork.SaveChangesAsync();
            });
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Conflict(MessageKeys.Workspace.StateChanged);
        }

        if (outcome != null)
            return outcome;

        _workspaceState.Invalidate(workspaceId);

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.WorkspaceDeletionCancelled,
                AuditTargets.Workspace,
                workspaceId.ToString(),
                workspaceId,
                After: new Dictionary<string, string> { ["source"] = "operator" }
            )
        );

        if (wasScheduled)
            // R17 spirit: the workspace never learns which operator — ActorName null renders as
            // "the platform operator" / "مشغّل المنصة".
            await SendToAllAdminsAsync(
                workspaceId,
                WorkspaceLifecycleEmailKind.Cancelled,
                null,
                stillPaused
            );

        return Result.Success();
    }

    // ── Job methods (no tenant context — every read below is IgnoreQueryFilters + explicit id) ──

    public async Task SendDueRemindersAsync(DateTime now)
    {
        var candidateIds = await _unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(w =>
                w.DeletedAt == null
                && w.DeletionScheduledFor != null
                && w.DeletionScheduledFor <= now.AddHours(24)
                && w.DeletionReminderSentAt == null
                && w.DeletionConfirmedAt != null
                // Grace < 2 days: no reminder — E2 (confirmed) is the only notice (Opus NIT).
                && w.DeletionScheduledFor.Value - w.DeletionConfirmedAt.Value
                    >= TimeSpan.FromHours(48)
            )
            .Select(w => w.Id)
            .ToListAsync();

        foreach (var id in candidateIds)
        {
            DateTime? scheduledForOut = null;
            var missed = false;

            try
            {
                await _unitOfWork.ExecuteInTransactionAsync(async () =>
                {
                    await _unitOfWork.ExecuteSqlRawAsync(
                        "SELECT id FROM workspaces WHERE id = {0} FOR UPDATE",
                        id
                    );

                    // Opus LOW: re-run the SAME predicate on the fresh, locked row — a racing cancel
                    // must not stamp DeletionReminderSentAt on a now-unscheduled row (check constraint).
                    var stillDue = await _unitOfWork
                        .Workspaces.IgnoreQueryFilters()
                        .AsNoTracking()
                        .AnyAsync(w =>
                            w.Id == id
                            && w.DeletedAt == null
                            && w.DeletionScheduledFor != null
                            && w.DeletionScheduledFor <= now.AddHours(24)
                            && w.DeletionReminderSentAt == null
                            && w.DeletionConfirmedAt != null
                            && w.DeletionScheduledFor.Value - w.DeletionConfirmedAt.Value
                                >= TimeSpan.FromHours(48)
                        );
                    if (!stillDue)
                        return;

                    var tracked = await _unitOfWork.Workspaces.FirstOrDefaultAsync(w => w.Id == id);
                    if (tracked == null)
                        return;

                    tracked.DeletionReminderSentAt = now;

                    // Opus LOW: downtime across T-24h — reschedule rather than delete without a reminder.
                    if (tracked.DeletionScheduledFor <= now.AddHours(1))
                    {
                        tracked.DeletionScheduledFor = now.AddHours(24);
                        missed = true;
                    }

                    await _unitOfWork.SaveChangesAsync();
                    scheduledForOut = tracked.DeletionScheduledFor;
                });
            }
            catch (DbUpdateConcurrencyException)
            {
                continue; // gone or changed — the delete job / a cancel already handled it
            }

            if (scheduledForOut == null)
                continue; // no longer due — skipped

            _workspaceState.Invalidate(id);

            if (missed)
            {
                await _audit.WriteAsync(
                    new AuditEntry(
                        AuditActions.WorkspaceDeletionRescheduled,
                        AuditTargets.Workspace,
                        id.ToString(),
                        id,
                        After: new Dictionary<string, string>
                        {
                            ["scheduled_for"] = scheduledForOut.Value.ToString("O"),
                            ["reason"] = "reminder_missed",
                        },
                        ActorKindOverride: AuditActorKind.System
                    )
                );
            }

            await SendToAllAdminsAsync(
                id,
                WorkspaceLifecycleEmailKind.Reminder,
                null,
                false,
                scheduledForOut
            );
        }
    }

    public async Task<Result> ExecuteDueDeletionAsync(Guid workspaceId)
    {
        var now = DateTime.UtcNow;

        var due = await _unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(w =>
                w.Id == workspaceId
                && w.DeletedAt == null
                && w.DeletionScheduledFor != null
                && w.DeletionScheduledFor <= now
                // Opus HIGH 3: an operator pause during the grace period holds the delete.
                && !w.PausedByOperator
                && (
                    w.DeletionReminderSentAt != null
                    || w.DeletionScheduledFor.Value - w.DeletionConfirmedAt!.Value
                        < TimeSpan.FromHours(48)
                )
            )
            .Select(w => new { w.Name })
            .FirstOrDefaultAsync();

        if (due == null)
        {
            // Distinguish "held by an operator pause" from "cancelled/reminder not yet sent" so the
            // hosted job can log the exact line DB-18 §5 task 14 / §9.5 names.
            var heldByOperator = await _unitOfWork
                .Workspaces.IgnoreQueryFilters()
                .AsNoTracking()
                .AnyAsync(w =>
                    w.Id == workspaceId
                    && w.DeletedAt == null
                    && w.DeletionScheduledFor != null
                    && w.DeletionScheduledFor <= now
                    && w.PausedByOperator
                );
            return Result.Failure(
                heldByOperator
                    ? "held by operator pause"
                    : "Not a due scheduled deletion (cancelled meanwhile, or reminder not yet sent)."
            );
        }

        // Opus NIT: an AsNoTracking projection captured BEFORE the delete — nothing tracked in the
        // context HardDeleteAsync uses.
        var recipients = await _memberships
            .InWorkspace(workspaceId)
            .Where(m =>
                m.LeftAt == null
                && m.IsActive
                && m.ApprovalStatus == ApprovalStatus.Approved
                && m.Role.Name == WorkspaceAdminRoleName
                && m.User.DeletedAt == null
                && !m.User.IsDemo
            )
            .AsNoTracking()
            .Select(m => new { m.User.Email, m.User.Language })
            .ToListAsync();

        var result = await _tenantService.HardDeleteAsync(workspaceId, "owner_requested");
        if (!result.IsSuccess)
            return result;

        _workspaceState.Invalidate(workspaceId);

        var brand = await _branding.BuildResponseAsync("", new HashSet<string>());
        foreach (var r in recipients)
        {
            try
            {
                var (subject, html) = WorkspaceLifecycleEmails.Build(
                    WorkspaceLifecycleEmailKind.Deleted,
                    r.Language,
                    new WorkspaceLifecycleEmailModel(
                        due.Name,
                        brand.ProductName,
                        brand.Urls.App,
                        ScheduledFor: now
                    )
                );
                await _emailService.SendAsync(r.Email, subject, html);
            }
            catch
            { /* best-effort */
            }
        }

        return Result.Success();
    }

    // ── Shared "all live admins" e-mail fan-out ──────────────────────────────────────────────

    /// <summary>"All live admins of W" (R8.7 — never <c>users.owner_id</c>): live, active, approved
    /// Workspace Admin memberships, excluding demo/deleted identities.</summary>
    private async Task SendToAllAdminsAsync(
        Guid workspaceId,
        WorkspaceLifecycleEmailKind kind,
        string? actorName,
        bool stillPaused,
        DateTime? scheduledFor = null
    )
    {
        var recipients = await _memberships
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

        var workspaceName =
            await _unitOfWork
                .Workspaces.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(w => w.Id == workspaceId)
                .Select(w => w.Name)
                .FirstOrDefaultAsync()
            ?? Workspace.PlaceholderName;
        var brand = await _branding.BuildResponseAsync("", new HashSet<string>());
        var graceDays = WorkspaceDeletionConfig.GraceDays(_config);

        foreach (var r in recipients)
        {
            try
            {
                var (subject, html) = WorkspaceLifecycleEmails.Build(
                    kind,
                    r.Language,
                    new WorkspaceLifecycleEmailModel(
                        workspaceName,
                        brand.ProductName,
                        brand.Urls.App,
                        GraceDays: graceDays,
                        ScheduledFor: scheduledFor,
                        ActorName: actorName,
                        StillPaused: stillPaused
                    )
                );
                await _emailService.SendAsync(r.Email, subject, html);
            }
            catch
            { /* best-effort */
            }
        }
    }
}
