using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Tenant;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;

namespace Pointer.Application.Services.Implementation;

public class TenantService : ITenantService
{
    private const int DefaultDemoTtlHours = 24;

    // "Workspace Admin" identifies the CURRENT canonical tenant owner by role name — see
    // UserService's identical constant/comment for the full rationale (OwnerId==PublicId only ever
    // held for the founding admin; TransferOwnershipAsync can change who holds this role without
    // ever rewriting OwnerId).
    private const string WorkspaceAdminRoleName = "Workspace Admin";

    private readonly IUnitOfWork _unitOfWork;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IFileStorage _fileStorage;
    private readonly ISettingsService _settings;
    private readonly IBillingProvider _billing;
    private readonly IMembershipService _memberships;
    private readonly IAuditWriter _audit;
    private readonly ICurrentUser? _currentUser;

    public TenantService(
        IUnitOfWork unitOfWork,
        IPasswordHasher passwordHasher,
        IFileStorage fileStorage,
        ISettingsService settings,
        IBillingProvider billing,
        IMembershipService memberships,
        IAuditWriter? audit = null,
        // DB-20: nullable-with-default, same seam as `audit` above — only used to stamp
        // ChangePlanAsync's CompedBy (the operator's public_id) on a complimentary grant; a null
        // value (every existing hand-rolled test construction) means that write is skipped, which
        // none of those tests assert on.
        ICurrentUser? currentUser = null
    )
    {
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
        _fileStorage = fileStorage;
        _settings = settings;
        _billing = billing;
        _memberships = memberships;
        _audit = audit ?? NoopAuditWriter.Instance;
        _currentUser = currentUser;
    }

    public async Task<Result<List<TenantResponse>>> ListAsync()
    {
        // DB-11a: enumerate WORKSPACES (the stable tenant identifier), left-joined to each one's
        // current live Workspace Admin membership — never `users.owner_id`. A workspace with no live
        // admin (e.g. every admin left/was removed) is listed with Id=0/PublicId=Guid.Empty/
        // Email=""/DisplayName=""/IsActive=false (dashboard task: disable row actions when id==0).
        var workspaces = await _unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(w => w.DeletedAt == null)
            .OrderBy(w => w.CreatedAt)
            .ToListAsync();

        if (workspaces.Count == 0)
            return Result<List<TenantResponse>>.Success(new List<TenantResponse>());

        var workspaceIds = workspaces.Select(w => w.Id).ToList();

        var admins = await _unitOfWork
            .Repository<WorkspaceMembership>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(m => m.User)
            .Include(m => m.Role)
            .Where(m =>
                workspaceIds.Contains(m.OwnerId)
                && m.LeftAt == null
                && m.Role.Name == WorkspaceAdminRoleName
            )
            .ToListAsync();
        var adminMap = admins.GroupBy(m => m.OwnerId).ToDictionary(g => g.Key, g => g.First());

        // Count projects per tenant (IgnoreQueryFilters — super-admin operator path).
        var projectCounts = await _unitOfWork
            .Repository<Project>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p =>
                p.DeletedAt == null && p.OwnerId != null && workspaceIds.Contains(p.OwnerId!.Value)
            )
            .GroupBy(p => p.OwnerId)
            .Select(g => new { OwnerId = g.Key, Count = g.Count() })
            .ToListAsync();

        // Count comments per tenant (IgnoreQueryFilters — super-admin operator path).
        var commentCounts = await _unitOfWork
            .Repository<Comment>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c =>
                c.DeletedAt == null && c.OwnerId != null && workspaceIds.Contains(c.OwnerId!.Value)
            )
            .GroupBy(c => c.OwnerId)
            .Select(g => new { OwnerId = g.Key, Count = g.Count() })
            .ToListAsync();

        var projectMap = projectCounts
            .Where(x => x.OwnerId.HasValue)
            .ToDictionary(x => x.OwnerId!.Value, x => x.Count);
        var commentMap = commentCounts
            .Where(x => x.OwnerId.HasValue)
            .ToDictionary(x => x.OwnerId!.Value, x => x.Count);

        // Batch-load each workspace's subscription (+ plan name). A missing subscription ⇒ Free.
        var subs = await _unitOfWork
            .Repository<Subscription>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.DeletedAt == null && workspaceIds.Contains(s.OwnerId))
            .Select(s => new
            {
                s.OwnerId,
                s.Status,
                PlanName = s.Plan.Name,
                s.RequestedPlanId,
                s.QuotedPrice,
                s.QuotedCurrency,
                s.CurrentPeriodEnd,
                s.IsComplimentary,
                s.CompEndsAt,
            })
            .ToListAsync();
        var subMap = subs.ToDictionary(x => x.OwnerId, x => x);

        // DB-20 §3.9: RequestedPlanId carries no navigation (§3.3 task 2) — batch-load names.
        var requestedPlanIds = subs.Where(s => s.RequestedPlanId != null)
            .Select(s => s.RequestedPlanId!.Value)
            .Distinct()
            .ToList();
        var requestedPlanNames =
            requestedPlanIds.Count == 0
                ? new Dictionary<int, string>()
                : await _unitOfWork
                    .Repository<Plan>()
                    .Query()
                    .AsNoTracking()
                    .Where(p => requestedPlanIds.Contains(p.Id))
                    .ToDictionaryAsync(p => p.Id, p => p.Name);

        var responses = workspaces
            .Select(w =>
            {
                adminMap.TryGetValue(w.Id, out var admin);
                var hasSub = subMap.TryGetValue(w.Id, out var s);
                var planName = hasSub ? s!.PlanName : "Free"; // missing subscription ⇒ Free
                var status = hasSub ? s!.Status.ToString() : (string?)null;
                return new TenantResponse
                {
                    Id = admin?.User.Id ?? 0,
                    PublicId = admin?.User.PublicId ?? Guid.Empty,
                    OwnerId = w.Id,
                    WorkspaceId = w.Id,
                    Email = admin?.User.Email ?? string.Empty,
                    DisplayName = admin?.User.DisplayName ?? string.Empty,
                    WorkspaceName = w.Name,
                    ApprovalStatus = admin?.ApprovalStatus.ToString() ?? string.Empty,
                    IsActive = admin?.IsActive ?? false,
                    Projects = projectMap.GetValueOrDefault(w.Id, 0),
                    Comments = commentMap.GetValueOrDefault(w.Id, 0),
                    PlanName = planName,
                    SubscriptionStatus = status,
                    // DB-17 §3.3: the WORKSPACE is the demo authority now (DTO names unchanged — no
                    // dashboard churn).
                    IsDemo = w.DemoExpiresAt != null,
                    ExpiresAt = w.DemoExpiresAt,
                    DemoExtended = w.DemoExtendedAt != null,
                    DemoCommentCapOverride = w.DemoCommentCapOverride,
                    DemoTtlHoursOverride = w.DemoTtlHoursOverride,
                    // DB-18: operator view of the self-service lifecycle.
                    PausedAt = w.PausedAt,
                    PausedByOperator = w.PausedByOperator,
                    DeletionScheduledFor = w.DeletionScheduledFor,
                    // DB-20 §3.9: billing v1 at-a-glance.
                    RequestedPlanName =
                        hasSub && s!.RequestedPlanId is int rpid
                            ? requestedPlanNames.GetValueOrDefault(rpid)
                            : null,
                    QuotedPrice = hasSub ? s!.QuotedPrice : null,
                    QuotedCurrency = hasSub ? s!.QuotedCurrency : null,
                    CurrentPeriodEnd = hasSub ? s!.CurrentPeriodEnd : null,
                    IsComplimentary = hasSub && s!.IsComplimentary,
                    CompEndsAt = hasSub ? s!.CompEndsAt : null,
                };
            })
            .ToList();

        return Result<List<TenantResponse>>.Success(responses);
    }

    public async Task<Result<TenantResponse>> CreateAsync(CreateTenantRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
            return Result<TenantResponse>.Failure("Email is required.");
        // DB-14 §3.6: replaces the old short-password check + literal message.
        if (PasswordPolicy.Validate(request.Password, request.Email) is string pwErr)
            return Result<TenantResponse>.Failure(pwErr);
        if (string.IsNullOrWhiteSpace(request.DisplayName))
            return Result<TenantResponse>.Failure("Display name is required.");

        var emailNormalized = EmailNormalizer.NormalizeRequired(request.Email);

        // Find the global "Workspace Admin" role (GrantsAdmin=true, IsSuperAdmin=false, OwnerId=null).
        var workspaceAdminRole = await _unitOfWork
            .Repository<Role>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(r =>
                r.DeletedAt == null
                && r.IsActive
                && r.GrantsAdmin
                && !r.IsSuperAdmin
                && r.OwnerId == null
                && r.Name == "Workspace Admin"
            );

        if (workspaceAdminRole == null)
            return Result<TenantResponse>.Failure(
                "System role 'Workspace Admin' not found. Ensure the database is seeded."
            );

        // DB-11a: join-or-create, admin-driven (super admin) — no password check, an existing
        // identity is just joined into a brand-new workspace. workspaces.id no longer needs to
        // equal anyone's public_id.
        var workspaceId = Guid.NewGuid();
        var identity = await _memberships.FindIdentityByEmailAsync(emailNormalized);
        var isNewIdentity = identity == null;

        // The Workspace row must exist before any row that references it via a workspace-id FK
        // (workspace_memberships.owner_id, etc. — DB-11f: users.owner_id/fk_users_workspaces_owner_id
        // no longer exist) — save it first.
        await _unitOfWork.Workspaces.AddAsync(
            new Workspace
            {
                Id = workspaceId,
                Name = Workspace.PlaceholderName,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = workspaceId,
            }
        );
        await _unitOfWork.SaveChangesAsync();

        if (isNewIdentity)
        {
            identity = _memberships.NewIdentity(
                emailNormalized,
                _passwordHasher.Hash(request.Password),
                request.DisplayName.Trim(),
                workspaceAdminRole,
                workspaceId
            );
            // DB-14 §3.2 D14.3: the operator vouches for the address.
            identity.EmailVerifiedAt = DateTime.UtcNow;
            await _unitOfWork.Repository<User>().AddAsync(identity);
        }

        var membership = await _memberships.JoinAsync(
            identity!,
            workspaceId,
            workspaceAdminRole,
            ApprovalStatus.Approved,
            isActive: true,
            inviteId: null
        );
        await _unitOfWork.SaveChangesAsync();

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.TenantCreated,
                AuditTargets.Workspace,
                workspaceId.ToString(),
                workspaceId
            )
        );
        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.WorkspaceCreated,
                AuditTargets.Workspace,
                workspaceId.ToString(),
                workspaceId,
                After: new Dictionary<string, string> { ["source"] = "super_admin" }
            )
        );

        return Result<TenantResponse>.Success(
            new TenantResponse
            {
                Id = identity!.Id,
                PublicId = identity.PublicId,
                OwnerId = workspaceId, // a freshly created tenant's stable identifier
                WorkspaceId = workspaceId,
                Email = identity.Email,
                DisplayName = identity.DisplayName,
                WorkspaceName = Workspace.PlaceholderName,
                ApprovalStatus = membership.ApprovalStatus.ToString(),
                IsActive = membership.IsActive,
                Projects = 0,
                Comments = 0,
            }
        );
    }

    /// <summary>
    /// F9 (DB-11a cross-review): the super-admin tenant endpoints key on the WORKSPACE id, never on
    /// an admin's `users.id` — the previous "exactly one admin membership" resolution returned a
    /// null membership (404) for any identity administering more than one workspace, the exact
    /// capability this release ships (D13). Resolves the workspace directly and its current live
    /// Workspace Admin membership (`IMembershipService.CurrentAdminAsync`, §3.5) — no ambiguity, no
    /// ternary: a workspace has at most one row satisfying "current admin" and this asks for it by
    /// the one identifier that can never collide.
    /// </summary>
    private async Task<(
        bool WorkspaceExists,
        WorkspaceMembership? AdminMembership
    )> ResolveWorkspaceAdminAsync(Guid workspaceId)
    {
        var workspaceExists = await _unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .AnyAsync(w => w.Id == workspaceId && w.DeletedAt == null);
        if (!workspaceExists)
            return (false, null);

        var membership = await _memberships.CurrentAdminAsync(workspaceId);
        return (true, membership);
    }

    public async Task<Result> SetStatusAsync(Guid workspaceId, string action)
    {
        if (string.IsNullOrWhiteSpace(action))
            return Result.Failure("Action is required. Valid values: approve, enable, disable.");

        var (workspaceExists, membership) = await ResolveWorkspaceAdminAsync(workspaceId);
        if (!workspaceExists || membership == null)
            return Result.NotFound("Tenant not found.");

        switch (action.Trim().ToLower())
        {
            case "approve":
                membership.ApprovalStatus = ApprovalStatus.Approved;
                membership.IsActive = true;
                break;
            case "enable":
                membership.IsActive = true;
                break;
            case "disable":
                membership.IsActive = false;
                // H1/R16: revoke the disabled tenant-admin's live access token FOR THIS WORKSPACE.
                membership.SecurityStamp = Guid.NewGuid();
                break;
            default:
                return Result.Failure("Invalid action. Valid values: approve, enable, disable.");
        }

        _unitOfWork.Repository<WorkspaceMembership>().Update(membership);
        await _unitOfWork.SaveChangesAsync();

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.TenantStatusChanged,
                AuditTargets.Membership,
                membership.Id.ToString(),
                workspaceId,
                After: new Dictionary<string, string> { ["action"] = action.Trim().ToLower() }
            )
        );

        return Result.Success();
    }

    public async Task<Result> ExtendDemoAsync(Guid workspaceId)
    {
        // DB-17 §3.3 / DB-11e: the WORKSPACE is the only demo authority.
        var workspace = await _unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.Id == workspaceId && w.DeletedAt == null);

        if (workspace?.DemoExpiresAt == null)
            return Result.NotFound("Demo tenant not found.");

        // DB-17 review finding #6 (LOW): with Demo:ConvertRequiresVerification on, a converted
        // workspace can still have a non-null DemoExpiresAt (the 72h re-verification grace) — that
        // is not "still a demo" for extension purposes, it is already upgraded.
        if (workspace.DemoConvertedAt != null)
            return Result.Failure(MessageKeys.Demo.AlreadyUpgraded);

        if (workspace.DemoExtendedAt != null)
            return Result.Failure("This demo has already been extended once.");

        // Per-tenant TTL override wins; otherwise the global setting.
        var ttlHours =
            workspace.DemoTtlHoursOverride
            ?? await _settings.GetIntAsync(ISettingsService.DemoTtlHours, DefaultDemoTtlHours);

        // Extend from whichever is later — now or the current (possibly future) expiry — so an
        // already-expired demo gets a full fresh period rather than one anchored in the past.
        var anchor =
            workspace.DemoExpiresAt is DateTime exp && exp > DateTime.UtcNow
                ? exp
                : DateTime.UtcNow;
        workspace.DemoExpiresAt = anchor.AddHours(ttlHours);
        workspace.DemoExtendedAt = DateTime.UtcNow;
        _unitOfWork.Workspaces.Update(workspace);

        await _unitOfWork.SaveChangesAsync();

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.TenantDemoExtended,
                AuditTargets.Workspace,
                workspaceId.ToString(),
                workspaceId,
                After: new Dictionary<string, string>
                {
                    ["expires_at"] = workspace.DemoExpiresAt.Value.ToString("O"),
                }
            )
        );

        return Result.Success();
    }

    public async Task<Result> SetDemoConfigAsync(
        Guid workspaceId,
        int? commentCapOverride,
        int? ttlHoursOverride
    )
    {
        // Per-tenant demo overrides. Null clears an override (revert to the global default);
        // any positive value sets it. Non-positive values are rejected as invalid.
        if (commentCapOverride is <= 0)
            return Result.Failure(
                "Comment cap must be a positive number, or empty to use the global default."
            );
        if (ttlHoursOverride is <= 0)
            return Result.Failure(
                "TTL (hours) must be a positive number, or empty to use the global default."
            );

        var workspace = await _unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.Id == workspaceId && w.DeletedAt == null);

        if (workspace?.DemoExpiresAt == null)
            return Result.NotFound("Demo tenant not found.");

        var before = new Dictionary<string, string>
        {
            ["count"] = workspace.DemoCommentCapOverride?.ToString() ?? string.Empty,
            ["minutes"] = workspace.DemoTtlHoursOverride?.ToString() ?? string.Empty,
        };

        workspace.DemoCommentCapOverride = commentCapOverride;
        workspace.DemoTtlHoursOverride = ttlHoursOverride;
        _unitOfWork.Workspaces.Update(workspace);

        await _unitOfWork.SaveChangesAsync();

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.TenantDemoConfigChanged,
                AuditTargets.Workspace,
                workspaceId.ToString(),
                workspaceId,
                Before: before,
                After: new Dictionary<string, string>
                {
                    ["count"] = commentCapOverride?.ToString() ?? string.Empty,
                    ["minutes"] = ttlHoursOverride?.ToString() ?? string.Empty,
                }
            )
        );

        return Result.Success();
    }

    public async Task<Result> ChangePlanAsync(
        Guid workspaceId,
        int planId,
        string? compReason = null,
        DateTime? compEndsAt = null
    )
    {
        // F9: the subscription is already keyed by the workspace id directly — no admin membership
        // needs resolving at all here (unlike SetStatusAsync, which acts ON the admin's membership).
        var workspaceExists = await _unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .AnyAsync(w => w.Id == workspaceId && w.DeletedAt == null);
        if (!workspaceExists)
            return Result.NotFound("Tenant not found.");

        var tenantOwnerId = workspaceId;

        // DB-20 §3.6e (F-B11): relaxed from IsActive to DeletedAt == null — an operator may assign
        // ANY non-deleted plan, including hidden/inactive (e.g. a private enterprise plan).
        var plan = await _unitOfWork
            .Repository<Plan>()
            .Query()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == planId && p.DeletedAt == null);

        if (plan == null)
            return Result.NotFound(MessageKeys.Plan.NotFound);

        // Upsert the tenant's subscription (one per tenant). Bypass the filter + match OwnerId.
        var sub = await _unitOfWork
            .Repository<Subscription>()
            .Query()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.OwnerId == tenantOwnerId && s.DeletedAt == null);

        var previousPlanId = sub?.PlanId;
        var isNew = sub == null;
        if (sub == null)
        {
            sub = new Subscription { OwnerId = tenantOwnerId, PlanId = plan.Id };
            await _unitOfWork.Repository<Subscription>().AddAsync(sub);
        }
        else
        {
            sub.PlanId = plan.Id;
        }

        // DB-20 §3.6e: release this workspace's own Pending redemption (if any) either way — an
        // operator plan override supersedes whatever the workspace was quoted.
        if (!isNew)
        {
            var pending = await _unitOfWork
                .DiscountRedemptions.Where(r =>
                    r.OwnerId == tenantOwnerId && r.Status == DiscountRedemptionStatus.Pending
                )
                .FirstOrDefaultAsync();
            if (pending != null)
            {
                pending.Status = DiscountRedemptionStatus.Released;
                pending.ReleasedAt = DateTime.UtcNow;
                pending.ReleaseReason = RedemptionReleaseReason.OperatorPlanOverride;
            }
        }
        sub.RequestedPlanId = null;
        sub.RequestedAt = null;
        sub.RequestedBy = null;
        sub.QuotedPrice = null;
        sub.QuotedCurrency = null;

        string kind;
        if (plan.PriceMonthly > 0)
        {
            sub.IsComplimentary = true;
            sub.CompedAt = DateTime.UtcNow;
            sub.CompedBy = _currentUser?.Id;
            sub.CompReason = compReason ?? "Assigned by operator";
            sub.CompEndsAt = compEndsAt;
            sub.Status = SubscriptionStatus.Active;
            sub.CurrentPeriodEnd = null;
            sub.RenewalReminderSentAt = null;
            kind = "complimentary";
        }
        else
        {
            sub.IsComplimentary = false;
            sub.CompedAt = null;
            sub.CompedBy = null;
            sub.CompReason = null;
            sub.CompEndsAt = null;
            sub.CurrentPeriodEnd = null;
            if (sub.Status is SubscriptionStatus.None or SubscriptionStatus.Canceled)
                sub.Status = SubscriptionStatus.Active;
            kind = plan.Slug == "free" ? "free" : "assigned";
        }

        if (!isNew)
            _unitOfWork.Repository<Subscription>().Update(sub);

        // Route through the billing seam (Noop today — no HTTP; a real gateway plugs in here later).
        await _billing.ChangePlanAsync(sub, plan.Id);

        await _unitOfWork.SaveChangesAsync();

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.TenantPlanChanged,
                AuditTargets.Workspace,
                workspaceId.ToString(),
                workspaceId,
                Before: previousPlanId is int prev
                    ? new Dictionary<string, string> { ["plan_id"] = prev.ToString() }
                    : null,
                After: new Dictionary<string, string>
                {
                    ["plan_id"] = plan.Id.ToString(),
                    ["kind"] = kind,
                }
            )
        );

        return Result.Success(MessageKeys.Plan.SubscriptionUpdated);
    }

    public async Task<Result> HardDeleteAsync(Guid workspaceId, string reason = "admin")
    {
        var workspaceExists = await _unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .AnyAsync(w => w.Id == workspaceId);

        if (!workspaceExists)
            return Result.NotFound("Tenant not found.");

        // DB-18: "owner_requested" (self-service scheduled delete) joins "demo_expired" as a guarded
        // reason — both have a locked re-check inside the transaction (below) and write their audit
        // row only once every row is actually gone.
        var isGuardedReason = reason is "demo_expired" or "owner_requested";

        // DB-17 §3.3 (Gemini Pro #2) / DB-18: re-check the reason's own precondition right here,
        // before anything observable happens (and again under FOR UPDATE inside the transaction,
        // below) — a conversion/extension (demo) or a cancel/operator-pause (owner_requested) landing
        // in that gap must not destroy the workspace's data.
        Task<bool> StillDueAsync() =>
            reason switch
            {
                "demo_expired" => _unitOfWork
                    .Workspaces.IgnoreQueryFilters()
                    .AnyAsync(w =>
                        w.Id == workspaceId
                        && w.DemoExpiresAt != null
                        && w.DemoExpiresAt < DateTime.UtcNow
                    ),
                // Opus HIGH 3: an operator pause during the grace period holds the delete.
                // Gemini MEDIUM: DeletedAt == null for consistency with every other lifecycle method
                // (ExecuteDueDeletionAsync's own due query included) — defense in depth, even though
                // nothing currently soft-deletes a workspace row ahead of the hard delete.
                "owner_requested" => _unitOfWork
                    .Workspaces.IgnoreQueryFilters()
                    .AnyAsync(w =>
                        w.Id == workspaceId
                        && w.DeletedAt == null
                        && w.DeletionScheduledFor != null
                        && w.DeletionScheduledFor <= DateTime.UtcNow
                        && !w.PausedByOperator
                    ),
                _ => Task.FromResult(true),
            };

        if (isGuardedReason)
        {
            var stillDue = await StillDueAsync();
            if (!stillDue)
                return Result.Failure(
                    reason == "demo_expired"
                        ? "Not an expired demo (converted or extended meanwhile)."
                        : "Not a due scheduled deletion (cancelled meanwhile)."
                );
        }

        // Snapshot the comment count before anything is deleted — the audit row (below) records how
        // much content this deletion removed.
        var commentCount = await _unitOfWork
            .Repository<Comment>()
            .Query()
            .IgnoreQueryFilters()
            .CountAsync(c => c.OwnerId == workspaceId);

        AuditEntry BuildHardDeletedAuditEntry() =>
            new(
                AuditActions.TenantHardDeleted,
                AuditTargets.Workspace,
                workspaceId.ToString(),
                null,
                After: new Dictionary<string, string>
                {
                    ["reason"] = reason,
                    ["count"] = commentCount.ToString(),
                },
                // §3.3: the demo-cleanup / workspace-deletion hosted jobs are a System actor,
                // explicitly — not left to the (correct, but implicit) fallback of ICurrentUser.Id
                // being null in that scope.
                ActorKindOverride: isGuardedReason ? AuditActorKind.System : null
            );

        if (!isGuardedReason)
        {
            // Every reason OTHER than demo_expired keeps today's order (DB-17 review finding #2
            // narrows the reorder below to demo_expired only — an ordinary admin-initiated delete
            // has no locked re-check to race against). Written BEFORE the delete, with OwnerId =
            // null deliberately — the row must not be detached mid-transaction, and it must survive
            // the workspace it is about (DB-12 §3.6). Best-effort: a failed audit write never
            // blocks the deletion itself.
            await _audit.WriteAsync(BuildHardDeletedAuditEntry());

            // Delete owner files first (outside transaction — filesystem side effect).
            await _fileStorage.DeleteOwnerFilesAsync(workspaceId.ToString("N"));
        }

        // Hard-delete inside a transaction using the execution strategy wrapper
        // (required by Npgsql's NpgsqlRetryingExecutionStrategy).
        await _unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            // DB-17 §3.3 / DB-18: FOR UPDATE-lock the workspace row and re-check inside the
            // transaction — a conversion (demo) or a cancel/operator-pause (owner_requested) that
            // committed between the pre-check above and this point must abort the delete entirely
            // (the transaction rolls back; the hosted loop logs it and moves on).
            if (isGuardedReason)
            {
                await _unitOfWork.ExecuteSqlRawAsync(
                    "SELECT id FROM workspaces WHERE id = {0} FOR UPDATE",
                    workspaceId
                );
                var stillDueLocked = await StillDueAsync();
                if (!stillDueLocked)
                    // Opus MEDIUM: a DEDICATED exception type — never the bare InvalidOperationException
                    // — so the hosted sweep loops can catch exactly this benign race as a "skip", without
                    // also silently swallowing an unrelated bug that happens to throw the base type.
                    throw new DeletionPreconditionChangedException(
                        reason == "demo_expired"
                            ? "demo converted during delete"
                            : "scheduled deletion cancelled during delete"
                    );
            }

            // Hard-delete in FK-safe order (children before parents; DB-03).
            await DeleteOwnedAsync<Notification>(x => x.OwnerId == workspaceId);
            await DeleteOwnedAsync<Reply>(x => x.OwnerId == workspaceId);
            await DeleteOwnedAsync<Comment>(x => x.OwnerId == workspaceId);
            await DeleteOwnedAsync<PageContextSnapshot>(x => x.OwnerId == workspaceId);
            await DeleteOwnedAsync<ProjectBuild>(x => x.OwnerId == workspaceId);
            await DeleteOwnedAsync<ProjectAppUrl>(x => x.OwnerId == workspaceId);
            await DeleteOwnedAsync<PredefinedActionSuggestion>(x => x.OwnerId == workspaceId);
            await DeleteOwnedAsync<PredefinedAction>(x => x.OwnerId == workspaceId);
            await DeleteOwnedAsync<AiRule>(x => x.OwnerId == workspaceId);
            await DeleteOwnedAsync<QuickAccessLink>(x => x.OwnerId == workspaceId);
            await DeleteOwnedAsync<Project>(x => x.OwnerId == workspaceId);
            await DeleteOwnedAsync<ExtensionSite>(x => x.OwnerId == workspaceId);
            await DeleteOwnedAsync<Invite>(x => x.OwnerId == workspaceId);
            await DeleteOwnedAsync<StatusPresentation>(x => x.OwnerId == workspaceId);
            await DeleteOwnedAsync<RoleTenantOverride>(x => x.OwnerId == workspaceId);
            await DeleteOwnedAsync<WorkspaceSetting>(x => x.OwnerId == workspaceId);

            // DB-20 §3.6g: release every Pending redemption of this workspace BEFORE the subscription
            // row goes — billing_payments/discount_redemptions themselves survive (R8.9, FK SET NULL)
            // and are NOT added to HardDeleteOrder.
            var pendingRedemptions = await _unitOfWork
                .DiscountRedemptions.IgnoreQueryFilters()
                .Where(r =>
                    r.OwnerId == workspaceId && r.Status == DiscountRedemptionStatus.Pending
                )
                .ToListAsync();
            var now = DateTime.UtcNow;
            foreach (var r in pendingRedemptions)
            {
                r.Status = DiscountRedemptionStatus.Released;
                r.ReleasedAt = now;
                r.ReleaseReason = RedemptionReleaseReason.WorkspaceDeleted;
            }

            await DeleteOwnedAsync<Subscription>(x => x.OwnerId == workspaceId);
            await DeleteOwnedAsync<ApiKey>(x => x.OwnerId == workspaceId);
            await DeleteOwnedAsync<DeviceLogin>(x => x.OwnerId == workspaceId);

            // DB-11a: end/remove every membership of this workspace BEFORE touching `users`. Staged only — the
            // queries below still see them (they run against the store), which the delete rule needs.
            await DeleteOwnedAsync<WorkspaceMembership>(x => x.OwnerId == workspaceId);

            // DB-11f: the delete set is the ONE shared, membership-based query (also the deletion preview).
            var deleteIds = await IdentitiesDeletedWithWorkspace(_unitOfWork, workspaceId)
                .Select(u => u.Id)
                .ToListAsync();
            if (deleteIds.Count > 0)
            {
                var doomed = await _unitOfWork
                    .Repository<User>()
                    .Query()
                    .IgnoreQueryFilters()
                    .Where(u => deleteIds.Contains(u.Id))
                    .ToListAsync();
                // api_keys, user_aliases, user_recovery_codes cascade.
                _unitOfWork.Repository<User>().RemoveRange(doomed);
            }

            await DeleteOwnedAsync<Role>(x => x.OwnerId == workspaceId);
            await DeleteOwnedAsync<AppEnvironment>(x => x.OwnerId == workspaceId);

            // Finally, the workspace row itself.
            var workspace = await _unitOfWork
                .Workspaces.IgnoreQueryFilters()
                .FirstOrDefaultAsync(w => w.Id == workspaceId);
            if (workspace != null)
                _unitOfWork.Workspaces.Remove(workspace);

            await _unitOfWork.SaveChangesAsync();

            if (isGuardedReason)
            {
                // DB-17 review finding #2 (Opus/Gemini): the audit row is written HERE — inside the
                // same transaction, after the locked re-check above passed and every row is gone —
                // never before it, so a delete the locked re-check aborts (the throw above) leaves
                // no tenant.hard_deleted row at all.
                await _audit.WriteAsync(BuildHardDeletedAuditEntry());
            }
        });

        if (isGuardedReason)
        {
            // DB-17 review finding #2 (Opus/Gemini): files are deleted only AFTER the transaction
            // commits — this used to run before the transaction, so a delete the locked re-check
            // aborted (workspace/rows survive) had already destroyed the screenshots. If this
            // post-commit delete itself fails (e.g. a transient filesystem error), the rows are
            // already gone but the files are left orphaned — DB-16's orphan sweep reclaims exactly
            // that leftover on its own schedule (verified there), so nothing is silently lost by
            // moving this here.
            await _fileStorage.DeleteOwnerFilesAsync(workspaceId.ToString("N"));
        }

        return Result.Success();
    }

    /// <summary>
    /// DB-11f. The exact "which accounts are deleted WITH this workspace" rule (owner decision D18.10:
    /// accounts that belong only to this workspace) — membership-only, never users.owner_id:
    /// an identity with a membership row of ANY state here and NO membership row of any state
    /// (ended/soft-deleted included, IgnoreQueryFilters) in another workspace, never a super admin;
    /// plus DB-11a merged tombstones whose canonical is deleted here or whose alias records this
    /// workspace as their source (0 in production). Soft-deleted/erased identities are included (the
    /// DB-18 Gemini BLOCKER). Shared by HardDeleteAsync (the delete set) and WorkspaceLifecycleService's
    /// deletion preview (count only) — the two must never diverge
    /// (Db18WorkspaceLifecycleTests.Preview_AccountsCount_EqualsRowsActuallyDeleted).
    /// </summary>
    public static IQueryable<User> IdentitiesDeletedWithWorkspace(IUnitOfWork uow, Guid workspaceId)
    {
        var memberships = uow.Repository<WorkspaceMembership>().Query().IgnoreQueryFilters();
        var superAdminRoleIds = uow.Repository<Role>()
            .Query()
            .IgnoreQueryFilters()
            .Where(r => r.IsSuperAdmin)
            .Select(r => r.Id);
        var users = uow.Repository<User>().Query().IgnoreQueryFilters();

        var coreIds = users
            .Where(u =>
                memberships.Any(m => m.UserId == u.Id && m.OwnerId == workspaceId)
                && !memberships.Any(m => m.UserId == u.Id && m.OwnerId != workspaceId)
                && !superAdminRoleIds.Any(id => id == u.RoleId)
            )
            .Select(u => u.Id);

        return users.Where(u =>
            coreIds.Contains(u.Id)
            || (
                u.MergedIntoUserId != null
                && (
                    coreIds.Contains(u.MergedIntoUserId.Value)
                    || uow.UserAliases.Any(a =>
                        a.AliasPublicId == u.PublicId && a.SourceWorkspaceId == workspaceId
                    )
                )
            )
        );
    }

    /// <summary>
    /// Loads every <typeparamref name="T"/> row matching <paramref name="ownedBy"/> (bypassing query
    /// filters — this runs during tenant deletion, with no tenant context to filter by) and marks
    /// them for removal. One generic helper compiles for both the entities whose <c>OwnerId</c> is
    /// <see cref="Guid"/> and those whose is <see cref="Guid?"/> — no <c>EF.Property</c>, no reflection.
    /// </summary>
    private async Task DeleteOwnedAsync<T>(Expression<Func<T, bool>> ownedBy)
        where T : BaseEntity
    {
        var repo = _unitOfWork.Repository<T>();
        var rows = await repo.Query().IgnoreQueryFilters().Where(ownedBy).ToListAsync();
        if (rows.Count > 0)
            repo.RemoveRange(rows);
    }

    // The 22 owner-carrying types, in the same order as the DeleteOwnedAsync<T> calls above. User is
    // not in it since DB-11f (it carries no owner_id): identities are removed by
    // IdentitiesDeletedWithWorkspace, after memberships. Documentation + test input for
    // WorkspaceTests.HardDeleteOrder_CoversEveryOwnerCarryingEntity. Never loop over this in
    // production code.
    public static readonly Type[] HardDeleteOrder =
    {
        typeof(Notification),
        typeof(Reply),
        typeof(Comment),
        typeof(PageContextSnapshot),
        typeof(ProjectBuild),
        typeof(ProjectAppUrl),
        typeof(PredefinedActionSuggestion),
        typeof(PredefinedAction),
        typeof(AiRule),
        typeof(QuickAccessLink),
        typeof(Project),
        typeof(ExtensionSite),
        typeof(Invite),
        typeof(StatusPresentation),
        typeof(RoleTenantOverride),
        typeof(WorkspaceSetting),
        typeof(Subscription),
        typeof(ApiKey),
        typeof(DeviceLogin),
        typeof(WorkspaceMembership),
        typeof(Role),
        typeof(AppEnvironment),
    };
}
