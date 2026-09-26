using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Workspace;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;

namespace Pointer.Application.Services.Implementation;

/// <inheritdoc cref="IWorkspaceCreationService"/>
/// <remarks>
/// DB-19 §3.3/§3.4. The levers are resolved directly from the entitlement bag — never via
/// <c>IEntitlementService.CheckCountAsync</c>/<c>EnforceFlagAsync</c> — because those are no-ops
/// while the <c>enforcement_enabled</c> kill-switch is off, and these two are abuse controls that
/// must hold regardless (D19.5).
/// </remarks>
public class WorkspaceCreationService(
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IMembershipService memberships,
    IEntitlementService entitlements,
    IAuditWriter audit
) : IWorkspaceCreationService
{
    private const string WorkspaceAdminRoleName = "Workspace Admin";

    /// <summary>
    /// DB-19 §3.4b: thrown inside the transaction when the cap is hit, caught just outside it so
    /// the failure surfaces as <c>Result.LimitReached</c> instead of a rolled-back 500.
    /// </summary>
    private sealed class OwnedLimitSentinelException(int owned) : Exception
    {
        public int Owned { get; } = owned;
    }

    public async Task<Result<CreateWorkspaceResponse>> CreateForCurrentIdentityAsync(
        CreateWorkspaceRequest request
    )
    {
        // 1. Name — the rename rules, verbatim (task 6). Null-conditional: a null request (e.g. a
        //    JSON body of `null`) must surface as a 400 validation error, not a NullReferenceException.
        if (WorkspaceNameRules.Validate(request?.Name, out var trimmed) is string nameError)
            return Result<CreateWorkspaceResponse>.Failure(nameError);

        // 2. §3.3 gate (WorkspaceLifecycleGuard rule on the caller's CURRENT workspace + the two
        //    identity checks of item 4).
        var gate = await PassesGateAsync();
        if (gate.Error is string forbidden)
            return Result<CreateWorkspaceResponse>.Forbidden(forbidden);
        var identity = gate.Identity!;

        // 3. Levers, resolved directly (D19.5 — never CheckCountAsync/EnforceFlagAsync).
        var tenantId = currentUser.TenantId!.Value;
        var ent = await entitlements.GetForTenantAsync(tenantId);
        var max = EntitlementCatalog.ResolveInt(ent, EntitlementCatalog.MaxOwnedWorkspaces);
        var requiresApproval = EntitlementCatalog.ResolveBool(
            ent,
            EntitlementCatalog.NewWorkspaceRequiresApproval
        );

        // 4. The global Workspace Admin role — resolved exactly as RegisterAdminAsync does.
        var role = await unitOfWork
            .Repository<Role>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(r =>
                r.DeletedAt == null && r.Name == WorkspaceAdminRoleName && r.OwnerId == null
            );
        if (role == null)
            return Result<CreateWorkspaceResponse>.Failure("Workspace Admin role not found.");

        // 5. One transaction: row-lock → count → create → join → audit (R17: inside the block,
        //    after the saves).
        CreateWorkspaceResponse? created = null;
        try
        {
            await unitOfWork.ExecuteInTransactionAsync(async () =>
            {
                // a. Serialise concurrent creates by the same identity. READ COMMITTED: the count
                //    below is a new statement and sees a committed concurrent insert (repo
                //    precedent: UserService.cs:80-84, WorkspaceLifecycleService.cs:206-209).
                await unitOfWork.ExecuteSqlRawAsync(
                    "SELECT id FROM users WHERE id = {0} FOR UPDATE",
                    identity.Id
                );

                // b. Owned per §3.2; the cap is checked BEFORE creating, never removes anything
                //    (grandfather-safe).
                var owned = await CountOwnedAsync(identity.Id);
                if (max != -1 && owned >= max)
                    throw new OwnedLimitSentinelException(owned);

                // c. The Workspace row must exist before any row referencing it via a workspace-id
                //    FK (same order as RegisterAdminAsync). CreatedBy is the caller's public_id
                //    (R14 content reference) — the creator is known here.
                var workspaceId = Guid.NewGuid();
                await unitOfWork.Workspaces.AddAsync(
                    new Workspace
                    {
                        Id = workspaceId,
                        Name = trimmed,
                        CreatedAt = DateTime.UtcNow,
                        CreatedBy = identity.PublicId,
                    }
                );
                await unitOfWork.SaveChangesAsync();

                // d. The caller's own admin membership: Pending/inactive when the plan requires
                //    approval (the self-signup state), Approved/active otherwise.
                await memberships.JoinAsync(
                    identity,
                    workspaceId,
                    role,
                    requiresApproval ? ApprovalStatus.Pending : ApprovalStatus.Approved,
                    isActive: !requiresApproval,
                    inviteId: null
                );
                await unitOfWork.SaveChangesAsync();

                // e. No subscription row — Free, exactly like register-admin without a plan (D19.9).

                // f. Audit (one row; whitelisted keys only; owner = the NEW workspace).
                await audit.WriteAsync(
                    new AuditEntry(
                        AuditActions.WorkspaceCreated,
                        AuditTargets.Workspace,
                        workspaceId.ToString(),
                        workspaceId,
                        After: new Dictionary<string, string>
                        {
                            ["source"] = "signed_in",
                            ["approval_status"] = requiresApproval ? "Pending" : "Approved",
                            ["count"] = (owned + 1).ToString(),
                        }
                    )
                );

                created = new CreateWorkspaceResponse
                {
                    WorkspaceId = workspaceId,
                    Name = trimmed,
                    Status = requiresApproval ? "pending_approval" : "active",
                };
            });
        }
        catch (OwnedLimitSentinelException ex)
        {
            // currentPlanId from the same subscription lookup (§3.4b).
            var currentPlanId = await entitlements.GetPlanIdForTenantAsync(tenantId);
            return Result<CreateWorkspaceResponse>.LimitReached(
                MessageKeys.Workspace.OwnedLimitReached,
                new PlanLimit(EntitlementCatalog.MaxOwnedWorkspaces, ex.Owned, max, currentPlanId)
            );
        }

        // 6. Nothing is written to the current workspace, to users, or to any stamp (R16).
        return Result<CreateWorkspaceResponse>.Success(created!);
    }

    public async Task<Result<WorkspaceAllowanceResponse>> GetAllowanceAsync()
    {
        var gate = await PassesGateAsync();
        var ent = currentUser.TenantId is Guid tenantId
            ? await entitlements.GetForTenantAsync(tenantId)
            : new Domain.ValueObjects.PlanEntitlements();
        var max = EntitlementCatalog.ResolveInt(ent, EntitlementCatalog.MaxOwnedWorkspaces);
        var requiresApproval = EntitlementCatalog.ResolveBool(
            ent,
            EntitlementCatalog.NewWorkspaceRequiresApproval
        );

        var owned = gate.Identity == null ? 0 : await CountOwnedAsync(gate.Identity.Id);

        return Result<WorkspaceAllowanceResponse>.Success(
            new WorkspaceAllowanceResponse
            {
                Owned = owned,
                Max = max,
                RequiresApproval = requiresApproval,
                CanCreate = gate.Error == null && (max == -1 || owned < max),
            }
        );
    }

    /// <summary>
    /// DB-19 §3.3: the WorkspaceLifecycleGuard.CanManageAsync rule applied to the caller's current
    /// workspace (API-key sessions, quick access, super admin, impersonation, tenant mismatch,
    /// non-admin role and live unconverted demos all fail), plus the two identity checks of item 4
    /// (live, active, non-demo, e-mail verified). The CURRENT workspace being paused or scheduled
    /// for deletion does NOT block (§3.6 — the endpoint writes only to the new workspace).
    /// </summary>
    private async Task<(User? Identity, string? Error)> PassesGateAsync()
    {
        // 1.
        if (currentUser.Id is not Guid publicId || currentUser.TenantId is not Guid tenantId)
            return (null, MessageKeys.Common.Forbidden);
        if (currentUser.IsSuperAdmin || currentUser.IsImpersonating || currentUser.IsQuickAccess)
            return (null, MessageKeys.Common.Forbidden);
        if (currentUser.KeyScopes != null)
            return (null, MessageKeys.Common.Forbidden);

        // 2. The current workspace row: live, and not a live unconverted demo. Explicit predicate
        //    with IgnoreQueryFilters — the row belongs to the caller's tenant claim by construction.
        var workspace = await unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == tenantId && w.DeletedAt == null);
        if (workspace == null)
            return (null, MessageKeys.Common.Forbidden);

        // 3. The guard re-checks 1–2 plus: live, active, approved membership whose role name is
        //    exactly "Workspace Admin" (Deputy excluded — D19.3).
        var identity = await memberships.FindIdentityByPublicIdAsync(publicId);
        if (identity == null)
            return (null, MessageKeys.Common.Forbidden);
        if (!await WorkspaceLifecycleGuard.CanManageAsync(currentUser, memberships, workspace))
            return (identity, MessageKeys.Common.Forbidden);

        // 4.
        if (!identity.IsActive || identity.IsDemo)
            return (identity, MessageKeys.Common.Forbidden);
        if (identity.EmailVerifiedAt == null)
            return (identity, MessageKeys.Auth.EmailNotVerified);

        return (identity, null);
    }

    /// <summary>
    /// DB-19 §3.2 (R8.7 — a membership count, never users.owner_id): live memberships of the
    /// identity on live workspaces, role exactly "Workspace Admin", Approved or Pending (Rejected
    /// excluded), regardless of is_active (a suspension is recoverable). Paused and
    /// deletion-scheduled workspaces count (reversible states); ended/deleted rows never do.
    /// </summary>
    private static async Task<int> CountOwnedAsync(IUnitOfWork unitOfWork, int userId)
    {
        var liveWorkspaceIds = unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .Where(w => w.DeletedAt == null)
            .Select(w => w.Id);

        return await unitOfWork
            .Repository<WorkspaceMembership>()
            .Query()
            .IgnoreQueryFilters()
            .CountAsync(m =>
                m.UserId == userId
                && m.LeftAt == null
                && m.DeletedAt == null
                && m.Role.Name == WorkspaceAdminRoleName
                && (
                    m.ApprovalStatus == ApprovalStatus.Approved
                    || m.ApprovalStatus == ApprovalStatus.Pending
                )
                && liveWorkspaceIds.Contains(m.OwnerId)
            );
    }

    private Task<int> CountOwnedAsync(int userId) => CountOwnedAsync(unitOfWork, userId);
}
