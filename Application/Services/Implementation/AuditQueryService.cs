using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Audit;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;

namespace Pointer.Application.Services.Implementation;

/// <summary>
/// DB-12 §3.8a — the audit read API. Scope: the workspace view resolves the owner with
/// <see cref="TenantStamp.TryRequireOwner"/> (a super admin is Forbidden — they use /all) and then
/// ALSO filters <c>OwnerId == owner</c> explicitly on top of the query filter (R8 belt-and-braces,
/// as WorkspaceSetting reads do). Redaction (D12.5): the operator is never named to a workspace —
/// rows with ActorKind SuperAdmin/Impersonation are emitted with ActorUserId = null and
/// ActorName = "Operator" when the caller is not a super admin; /all keeps both.
/// </summary>
public class AuditQueryService(IUnitOfWork unitOfWork, ICurrentUser currentUser)
    : IAuditQueryService
{
    private const int MaxPageSize = 200;

    public async Task<Result<PagedData<AuditEventDto>>> ListForWorkspaceAsync(AuditQuery q)
    {
        if (q.PageSize > MaxPageSize)
            return Result<PagedData<AuditEventDto>>.Failure(MessageKeys.Audit.PageSizeTooLarge);

        // DB-13: impersonating operator → owner = TenantId
        if (!TenantStamp.TryRequireOwner(currentUser, out var owner))
            return Result<PagedData<AuditEventDto>>.Forbidden(MessageKeys.Common.Forbidden);

        var query = unitOfWork.AuditEvents.AsNoTracking().Where(e => e.OwnerId == owner); // explicit, on top of the query filter (R8)

        // WorkspaceId is an /all-only filter — the workspace view is ALWAYS the caller's own
        // workspace; a caller-supplied WorkspaceId is ignored here.
        query = ApplyFilters(query, q, includeWorkspaceFilter: false);

        return await PageAsync(query, q, callerIsSuperAdmin: false);
    }

    public async Task<Result<PagedData<AuditEventDto>>> ListAllAsync(AuditQuery q)
    {
        if (q.PageSize > MaxPageSize)
            return Result<PagedData<AuditEventDto>>.Failure(MessageKeys.Audit.PageSizeTooLarge);

        // No IgnoreQueryFilters() needed: the super-admin branch of the AuditEvent filter admits
        // everything (audit rows are metadata).
        var query = unitOfWork.AuditEvents.AsNoTracking();

        query = ApplyFilters(query, q, includeWorkspaceFilter: true);

        return await PageAsync(query, q, callerIsSuperAdmin: true);
    }

    private static IQueryable<AuditEvent> ApplyFilters(
        IQueryable<AuditEvent> query,
        AuditQuery q,
        bool includeWorkspaceFilter
    )
    {
        if (q.Since is DateTime since)
            query = query.Where(e => e.OccurredAt >= since);
        if (q.Until is DateTime until)
            query = query.Where(e => e.OccurredAt <= until);
        // Prefix match — "member." lists every member.* action.
        if (!string.IsNullOrWhiteSpace(q.Action))
            query = query.Where(e => e.Action.StartsWith(q.Action));
        if (q.Actor is Guid actor)
            query = query.Where(e => e.ActorUserId == actor);
        if (!string.IsNullOrWhiteSpace(q.TargetType))
            query = query.Where(e => e.TargetType == q.TargetType);
        if (!string.IsNullOrWhiteSpace(q.TargetId))
            query = query.Where(e => e.TargetId == q.TargetId);
        if (includeWorkspaceFilter && q.WorkspaceId is Guid workspace)
            query = query.Where(e => e.OwnerId == workspace);
        return query;
    }

    private async Task<Result<PagedData<AuditEventDto>>> PageAsync(
        IQueryable<AuditEvent> query,
        AuditQuery q,
        bool callerIsSuperAdmin
    )
    {
        var page = q.Page < 1 ? 1 : q.Page;
        var pageSize = q.PageSize < 1 ? 50 : q.PageSize;

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        // Resolve names only for rows that will still carry an actor uuid after redaction — the
        // operator's identity is never looked up for a workspace caller (D12.5).
        var visibleActorIds = items
            .Where(e =>
                e.ActorUserId is not null
                && (
                    callerIsSuperAdmin
                    || e.ActorKind
                        is not (AuditActorKind.SuperAdmin or AuditActorKind.Impersonation)
                )
            )
            .Select(e => e.ActorUserId!.Value)
            .Distinct()
            .ToList();
        var names = await UserNameResolver.ResolveAsync(unitOfWork, visibleActorIds);

        var dtos = items.Select(e => Map(e, names, callerIsSuperAdmin)).ToList();

        return Result<PagedData<AuditEventDto>>.Success(
            new PagedData<AuditEventDto>(
                dtos,
                new Pagination
                {
                    PageNumber = page,
                    PageSize = pageSize,
                    TotalItems = total,
                    TotalPages = (int)Math.Ceiling(total / (double)pageSize),
                }
            )
        );
    }

    /// <summary>
    /// The one mapping path — the D12.5 redaction lives here and nowhere else: a non-super-admin
    /// caller never sees which operator acted. /all additionally carries UserAgent/IpHash.
    /// </summary>
    private static AuditEventDto Map(
        AuditEvent e,
        IReadOnlyDictionary<Guid, string> names,
        bool callerIsSuperAdmin
    )
    {
        var redactOperator =
            !callerIsSuperAdmin
            && e.ActorKind is AuditActorKind.SuperAdmin or AuditActorKind.Impersonation;

        Guid? actorUserId = redactOperator ? null : e.ActorUserId;
        string actorName;
        if (redactOperator)
            actorName = AuditEventDto.OperatorLabel; // the operator is never named to a workspace (D12.5)
        else if (actorUserId is Guid id)
            actorName = names.TryGetValue(id, out var name) ? name : "Deleted user";
        else
            actorName = AuditEventDto.SystemLabel; // hosted job / anonymous failure

        return new AuditEventDto
        {
            Id = e.Id,
            OccurredAt = e.OccurredAt,
            WorkspaceId = e.OwnerId,
            ActorUserId = actorUserId,
            ActorName = actorName,
            ActorKind = e.ActorKind.ToString(),
            Action = e.Action,
            TargetType = e.TargetType,
            TargetId = e.TargetId,
            Before = e.Before,
            After = e.After,
            RequestId = e.RequestId,
            ImpersonationSessionId = e.ImpersonationSessionId,
            UserAgent = callerIsSuperAdmin ? e.UserAgent : null,
            IpHash = callerIsSuperAdmin ? e.IpHash : null,
        };
    }
}
