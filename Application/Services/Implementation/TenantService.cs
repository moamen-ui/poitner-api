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

    public TenantService(
        IUnitOfWork unitOfWork,
        IPasswordHasher passwordHasher,
        IFileStorage fileStorage,
        ISettingsService settings,
        IBillingProvider billing,
        IMembershipService memberships
    )
    {
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
        _fileStorage = fileStorage;
        _settings = settings;
        _billing = billing;
        _memberships = memberships;
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
                workspaceIds.Contains(m.OwnerId) && m.LeftAt == null && m.Role.Name == WorkspaceAdminRoleName
            )
            .ToListAsync();
        var adminMap = admins
            .GroupBy(m => m.OwnerId)
            .ToDictionary(g => g.Key, g => g.First());

        // Count projects per tenant (IgnoreQueryFilters — super-admin operator path).
        var projectCounts = await _unitOfWork
            .Repository<Project>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => p.DeletedAt == null && p.OwnerId != null && workspaceIds.Contains(p.OwnerId!.Value))
            .GroupBy(p => p.OwnerId)
            .Select(g => new { OwnerId = g.Key, Count = g.Count() })
            .ToListAsync();

        // Count comments per tenant (IgnoreQueryFilters — super-admin operator path).
        var commentCounts = await _unitOfWork
            .Repository<Comment>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => c.DeletedAt == null && c.OwnerId != null && workspaceIds.Contains(c.OwnerId!.Value))
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
            })
            .ToListAsync();
        var subMap = subs.ToDictionary(x => x.OwnerId, x => (x.PlanName, x.Status));

        var responses = workspaces
            .Select(w =>
            {
                adminMap.TryGetValue(w.Id, out var admin);
                var (planName, status) =
                    subMap.TryGetValue(w.Id, out var s)
                        ? (s.PlanName, s.Status.ToString())
                        : ("Free", (string?)null); // missing subscription ⇒ Free
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
                    IsDemo = admin?.User.IsDemo ?? false,
                    ExpiresAt = admin?.User.ExpiresAt,
                    DemoExtended = admin?.User.DemoExtended ?? false,
                    DemoCommentCapOverride = admin?.User.DemoCommentCapOverride,
                    DemoTtlHoursOverride = admin?.User.DemoTtlHoursOverride,
                };
            })
            .ToList();

        return Result<List<TenantResponse>>.Success(responses);
    }

    public async Task<Result<TenantResponse>> CreateAsync(CreateTenantRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
            return Result<TenantResponse>.Failure("Email is required.");
        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8)
            return Result<TenantResponse>.Failure("Password must be at least 8 characters.");
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
        // (users.owner_id ⇒ fk_users_workspaces_owner_id, memberships, etc.) — save it first.
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
            await _unitOfWork.Repository<User>().AddAsync(identity);
            await _unitOfWork.SaveChangesAsync();
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
    private async Task<(bool WorkspaceExists, WorkspaceMembership? AdminMembership)> ResolveWorkspaceAdminAsync(
        Guid workspaceId
    )
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

        return Result.Success();
    }

    public async Task<Result> ExtendDemoAsync(int id)
    {
        // Demo fields (IsDemo/ExpiresAt/DemoExtended/…) stay on `users` (S-8 later) — unchanged.
        var user = await _unitOfWork
            .Repository<User>()
            .Query()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == id && u.DeletedAt == null);

        if (user == null || !user.IsDemo)
            return Result.NotFound("Demo tenant not found.");

        if (user.DemoExtended)
            return Result.Failure("This demo has already been extended once.");

        // Per-tenant TTL override wins; otherwise the global setting.
        var ttlHours =
            user.DemoTtlHoursOverride
            ?? await _settings.GetIntAsync(ISettingsService.DemoTtlHours, DefaultDemoTtlHours);

        // Extend from whichever is later — now or the current (possibly future) expiry — so an
        // already-expired demo gets a full fresh period rather than one anchored in the past.
        var anchor =
            user.ExpiresAt is DateTime exp && exp > DateTime.UtcNow ? exp : DateTime.UtcNow;
        user.ExpiresAt = anchor.AddHours(ttlHours);
        user.DemoExtended = true;

        _unitOfWork.Repository<User>().Update(user);
        await _unitOfWork.SaveChangesAsync();

        return Result.Success();
    }

    public async Task<Result> SetDemoConfigAsync(
        int id,
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

        var user = await _unitOfWork
            .Repository<User>()
            .Query()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == id && u.DeletedAt == null);

        if (user == null || !user.IsDemo)
            return Result.NotFound("Demo tenant not found.");

        user.DemoCommentCapOverride = commentCapOverride;
        user.DemoTtlHoursOverride = ttlHoursOverride;

        _unitOfWork.Repository<User>().Update(user);
        await _unitOfWork.SaveChangesAsync();

        return Result.Success();
    }

    public async Task<Result> ChangePlanAsync(Guid workspaceId, int planId)
    {
        // F9: the subscription is already keyed by the workspace id directly — no admin membership
        // needs resolving at all here (unlike SetStatusAsync, which acts ON the admin's membership).
        var workspaceExists = await _unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .AnyAsync(w => w.Id == workspaceId && w.DeletedAt == null);
        if (!workspaceExists)
            return Result.NotFound("Tenant not found.");

        var tenantOwnerId = workspaceId;

        // Plan is global (no filter) — plain query. Must exist, be active, not deleted.
        var plan = await _unitOfWork
            .Repository<Plan>()
            .Query()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == planId && p.DeletedAt == null && p.IsActive);

        if (plan == null)
            return Result.NotFound(MessageKeys.Plan.NotFound);

        // Upsert the tenant's subscription (one per tenant). Bypass the filter + match OwnerId.
        var sub = await _unitOfWork
            .Repository<Subscription>()
            .Query()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.OwnerId == tenantOwnerId && s.DeletedAt == null);

        if (sub == null)
        {
            sub = new Subscription
            {
                OwnerId = tenantOwnerId,
                PlanId = plan.Id,
                Status = SubscriptionStatus.Active,
            };
            await _unitOfWork.Repository<Subscription>().AddAsync(sub);
        }
        else
        {
            sub.PlanId = plan.Id;
            if (sub.Status is SubscriptionStatus.None or SubscriptionStatus.Canceled)
                sub.Status = SubscriptionStatus.Active;
            _unitOfWork.Repository<Subscription>().Update(sub);
        }

        // Route through the billing seam (Noop today — no HTTP; a real gateway plugs in here later).
        await _billing.ChangePlanAsync(sub, plan.Id);

        await _unitOfWork.SaveChangesAsync();
        return Result.Success(MessageKeys.Plan.SubscriptionUpdated);
    }

    public async Task<Result> HardDeleteAsync(Guid workspaceId)
    {
        var workspaceExists = await _unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .AnyAsync(w => w.Id == workspaceId);

        if (!workspaceExists)
            return Result.NotFound("Tenant not found.");

        // Delete owner files first (outside transaction — filesystem side effect).
        await _fileStorage.DeleteOwnerFilesAsync(workspaceId.ToString("N"));

        // Hard-delete inside a transaction using the execution strategy wrapper
        // (required by Npgsql's NpgsqlRetryingExecutionStrategy).
        await _unitOfWork.ExecuteInTransactionAsync(async () =>
        {
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
            await DeleteOwnedAsync<Subscription>(x => x.OwnerId == workspaceId);
            await DeleteOwnedAsync<ApiKey>(x => x.OwnerId == workspaceId);
            await DeleteOwnedAsync<DeviceLogin>(x => x.OwnerId == workspaceId);

            // DB-11a: end/remove every membership of this workspace BEFORE touching `users` —
            // `users.owner_id` is legacy (written once at creation, never read after DB-11a) and is
            // used below only to decide whether an identity created HERE survives (re-homed to
            // another workspace it still belongs to) or is hard-deleted with it.
            await DeleteOwnedAsync<WorkspaceMembership>(x => x.OwnerId == workspaceId);

            var usersCreatedHere = await _unitOfWork
                .Repository<User>()
                .Query()
                .IgnoreQueryFilters()
                .Where(u => u.OwnerId == workspaceId && u.DeletedAt == null)
                .ToListAsync();

            foreach (var u in usersCreatedHere)
            {
                var otherWorkspaceId = await _unitOfWork
                    .Repository<WorkspaceMembership>()
                    .Query()
                    .IgnoreQueryFilters()
                    .Where(m => m.UserId == u.Id && m.OwnerId != workspaceId)
                    .Select(m => (Guid?)m.OwnerId)
                    .FirstOrDefaultAsync();

                if (otherWorkspaceId is Guid otherOwner)
                {
                    // Re-home: this identity still belongs elsewhere — keep the row (api_keys,
                    // user_aliases, other memberships all reference it), just update the legacy column.
                    u.OwnerId = otherOwner;
                    _unitOfWork.Repository<User>().Update(u);
                }
                else
                {
                    // No membership survives anywhere — hard-delete (api_keys cascade; aliases cascade).
                    _unitOfWork.Repository<User>().Remove(u);
                }
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
        });

        return Result.Success();
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

    // The same 23 types, in the same order as the DeleteOwnedAsync<T> calls above (DB-11a adds
    // WorkspaceMembership before User). Documentation + test input for the reflection test
    // (WorkspaceTests.HardDeleteOrder_CoversEveryOwnerCarryingEntity) that every OwnerId-carrying
    // entity is accounted for here. Never loop over this in production code.
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
        typeof(User),
        typeof(Role),
        typeof(AppEnvironment),
    };
}
