using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Impersonation;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;

namespace Pointer.Application.Services.Implementation;

/// <summary>
/// DB-13 §3.6 (F2) — starts/ends/lists audited, time-boxed impersonation sessions. A session's
/// existence is what widens the six content query filters for the operator (§3.3/§3.4): this
/// service only manages the row and the token, never touches comment/reply/etc. content itself.
/// </summary>
public class ImpersonationService(
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    ITokenService tokenService,
    IAuditWriter audit,
    IMembershipService memberships,
    IEmailService emailService,
    IBrandingService branding,
    ILogger<ImpersonationService> logger
) : IImpersonationService
{
    public async Task<Result<ImpersonationStartResponse>> StartAsync(
        Guid workspaceId,
        StartImpersonationRequest request
    )
    {
        if (!currentUser.IsSuperAdmin || currentUser.IsImpersonating)
            return Result<ImpersonationStartResponse>.Forbidden(MessageKeys.Common.Forbidden);

        if (currentUser.Id is not Guid operatorPublicId)
            return Result<ImpersonationStartResponse>.Forbidden(MessageKeys.Common.Forbidden);

        var workspace = await unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == workspaceId && w.DeletedAt == null);
        if (workspace is null)
            return Result<ImpersonationStartResponse>.NotFound(MessageKeys.Workspace.NotFound);

        // Friendly pre-check — the unique partial index (ux_impersonation_sessions_operator_live)
        // is the actual race guard, caught below.
        var alreadyLive = await unitOfWork
            .ImpersonationSessions.IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(s => s.OperatorUserId == operatorPublicId && s.EndedAt == null);
        if (alreadyLive)
            return Result<ImpersonationStartResponse>.Conflict(
                MessageKeys.Impersonation.AlreadyActive
            );

        var operatorUser = await unitOfWork
            .Repository<User>()
            .Query()
            .IgnoreQueryFilters()
            .Include(u => u.Role)
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.PublicId == operatorPublicId && u.DeletedAt == null);
        if (operatorUser is null)
            return Result<ImpersonationStartResponse>.Forbidden(MessageKeys.Common.Forbidden);

        var now = DateTime.UtcNow;
        var expiresAt = now.AddMinutes(request.Minutes);

        var session = new ImpersonationSession
        {
            OwnerId = workspaceId,
            OperatorUserId = operatorPublicId,
            Reason = request.Reason,
            StartedAt = now,
            ExpiresAt = expiresAt,
            RequestCount = 0,
        };
        unitOfWork.ImpersonationSessions.Add(session);

        try
        {
            await unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" }
                || (
                    ex.InnerException != null
                    && ex.InnerException.GetType().Name == "SqliteException"
                    && (int)
                        ex
                            .InnerException.GetType()
                            .GetProperty("SqliteErrorCode")!
                            .GetValue(ex.InnerException)! == 19
                )
            )
        {
            // The unique partial index (GLM DB-13 #4) caught a race the pre-check missed.
            unitOfWork.ClearChangeTracker();
            return Result<ImpersonationStartResponse>.Conflict(
                MessageKeys.Impersonation.AlreadyActive
            );
        }

        var token = tokenService.IssueImpersonation(
            operatorUser,
            workspaceId,
            session.Id,
            expiresAt
        );

        await audit.WriteAsync(
            new AuditEntry(
                AuditActions.ImpersonationStarted,
                AuditTargets.ImpersonationSession,
                session.Id.ToString(),
                workspaceId,
                After: new Dictionary<string, string>
                {
                    ["reason"] = request.Reason,
                    ["minutes"] = request.Minutes.ToString(),
                    ["session_id"] = session.Id.ToString(),
                },
                // DB-13 review fix #3: the caller is still on a PLAIN super-admin token here — the
                // session this row is about doesn't exist on any token yet — so ICurrentUser has no
                // "imp" claim to fall back on.
                ImpersonationSessionIdOverride: session.Id
            )
        );

        await NotifyAdminsAsync(workspaceId, workspace.Name, request.Reason, request.Minutes, now);

        return Result<ImpersonationStartResponse>.Success(
            new ImpersonationStartResponse
            {
                Token = token,
                SessionId = session.Id,
                ExpiresAt = expiresAt,
                WorkspaceId = workspaceId,
                WorkspaceName = workspace.Name,
            }
        );
    }

    public async Task<Result> EndAsync(long? sessionId)
    {
        if (currentUser.Id is not Guid operatorPublicId)
            return Result.Forbidden(MessageKeys.Common.Forbidden);

        var targetSessionId = currentUser.ImpersonationSessionId ?? sessionId;
        if (targetSessionId is not long id)
            return Result.Failure(MessageKeys.Impersonation.NoSession);

        var session = await unitOfWork
            .ImpersonationSessions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == id && s.OperatorUserId == operatorPublicId);
        if (session is null || session.EndedAt is not null)
            return Result.NotFound(MessageKeys.Impersonation.NoSession);

        var now = DateTime.UtcNow;
        // DB-13 review fix #6: a caller who ends a session AFTER its time box has already elapsed
        // (but before the sweep got to it) recorded a false "manual" end — mirror the sweep's own
        // classification here so end_reason (and the audit row) always reflect why the session
        // actually ended, not just who happened to close it out.
        var expired = now >= session.ExpiresAt;
        session.EndedAt = now;
        session.EndReason = expired ? ImpersonationEndReason.Expired : ImpersonationEndReason.Manual;
        await unitOfWork.SaveChangesAsync();

        var durationSeconds = (int)(now - session.StartedAt).TotalSeconds;
        await audit.WriteAsync(
            new AuditEntry(
                AuditActions.ImpersonationEnded,
                AuditTargets.ImpersonationSession,
                session.Id.ToString(),
                session.OwnerId,
                After: new Dictionary<string, string>
                {
                    ["session_id"] = session.Id.ToString(),
                    ["request_count"] = session.RequestCount.ToString(),
                    ["duration_seconds"] = durationSeconds.ToString(),
                    ["reason"] = expired ? "expired" : "manual",
                },
                // DB-13 review fix #3: `end` also accepts a PLAIN super-admin token with a body
                // {SessionId} (§3.6) — currentUser.ImpersonationSessionId is null in that case even
                // though this row is unambiguously about `session.Id`.
                ImpersonationSessionIdOverride: session.Id
            )
        );

        return Result.Success(MessageKeys.Impersonation.Ended);
    }

    public async Task<Result<PagedData<ImpersonationSessionDto>>> ListAsync(
        Guid? workspaceId,
        int page,
        int pageSize
    )
    {
        IQueryable<ImpersonationSession> query;
        if (currentUser.IsSuperAdmin)
        {
            query = unitOfWork.ImpersonationSessions.IgnoreQueryFilters().AsNoTracking();
            if (workspaceId is Guid target)
                query = query.Where(s => s.OwnerId == target);
        }
        else
        {
            if (!TenantStamp.TryRequireOwner(currentUser, out var owner))
                return Result<PagedData<ImpersonationSessionDto>>.Forbidden(
                    MessageKeys.Common.Forbidden
                );
            query = unitOfWork.ImpersonationSessions.AsNoTracking().Where(s => s.OwnerId == owner);
        }

        var pageNumber = page < 1 ? 1 : page;
        var size = pageSize < 1 ? 50 : pageSize;

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(s => s.StartedAt)
            .ThenByDescending(s => s.Id)
            .Skip((pageNumber - 1) * size)
            .Take(size)
            .ToListAsync();

        var workspaceIds = items
            .Where(s => s.OwnerId is not null)
            .Select(s => s.OwnerId!.Value)
            .Distinct()
            .ToList();
        var names =
            workspaceIds.Count > 0
                ? await unitOfWork
                    .Workspaces.IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(w => workspaceIds.Contains(w.Id))
                    .ToDictionaryAsync(w => w.Id, w => w.Name)
                : new Dictionary<Guid, string>();

        var dtos = items
            .Select(s => new ImpersonationSessionDto
            {
                Id = s.Id,
                WorkspaceId = s.OwnerId ?? Guid.Empty,
                WorkspaceName =
                    s.OwnerId is Guid w && names.TryGetValue(w, out var n)
                        ? n
                        : "(deleted workspace)",
                StartedAt = s.StartedAt,
                ExpiresAt = s.ExpiresAt,
                EndedAt = s.EndedAt,
                EndReason = s.EndReason?.ToString(),
                RequestCount = s.RequestCount,
                Reason = s.Reason,
            })
            .ToList();

        return Result<PagedData<ImpersonationSessionDto>>.Success(
            new PagedData<ImpersonationSessionDto>(
                dtos,
                new Pagination
                {
                    PageNumber = pageNumber,
                    PageSize = size,
                    TotalItems = total,
                    TotalPages = (int)Math.Ceiling(total / (double)size),
                }
            )
        );
    }

    /// <summary>
    /// D13.4: one best-effort e-mail to every live admin of the workspace. A workspace with zero
    /// live admins gets no e-mail at all — the start is still recorded as an audit row (GLM DB-13
    /// #2). No operator name/e-mail anywhere in the body (D13.6).
    /// </summary>
    private async Task NotifyAdminsAsync(
        Guid workspaceId,
        string workspaceName,
        string reason,
        int minutes,
        DateTime startedAt
    )
    {
        try
        {
            var admins = await memberships
                .InWorkspace(workspaceId)
                .Where(m =>
                    m.LeftAt == null
                    && m.IsActive
                    && m.ApprovalStatus == ApprovalStatus.Approved
                    && m.Role.GrantsAdmin
                    && !m.Role.IsSuperAdmin
                )
                .Select(m => new { m.User.Email, m.User.DisplayName })
                .ToListAsync();

            if (admins.Count == 0)
            {
                logger.LogInformation(
                    "impersonation of {WorkspaceId}: no admin to notify",
                    workspaceId
                );
                return;
            }

            var brand = await branding.BuildResponseAsync("", new HashSet<string>());
            var subject = $"An operator is viewing your {brand.ProductName} workspace";
            var reasonEncoded = System.Net.WebUtility.HtmlEncode(reason);
            var workspaceNameEncoded = System.Net.WebUtility.HtmlEncode(workspaceName);

            foreach (var admin in admins)
            {
                var nameEncoded = System.Net.WebUtility.HtmlEncode(admin.DisplayName);
                var html =
                    $"<p>Hi {nameEncoded}, the {brand.ProductName} operator opened a read-only view "
                    + $"of the <strong>{workspaceNameEncoded}</strong> workspace at {startedAt:u} for up to "
                    + $"{minutes} minutes.</p>"
                    + $"<p>Reason given: <em>{reasonEncoded}</em>.</p>"
                    + "<p>This is logged in your Security log (Settings &rarr; Security log), where you will "
                    + "also see when it ended. If you did not expect this, reply to this e-mail.</p>";

                try
                {
                    await emailService.SendAsync(admin.Email, subject, html);
                }
                catch (Exception ex)
                {
                    // DB-13 review fix #10: never log an admin's e-mail address in plaintext — the
                    // workspace id is enough to find the row/correlate the failure.
                    logger.LogWarning(
                        ex,
                        "impersonation of {WorkspaceId}: failed to notify an admin",
                        workspaceId
                    );
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "impersonation of {WorkspaceId}: notify-admins failed",
                workspaceId
            );
        }
    }
}
