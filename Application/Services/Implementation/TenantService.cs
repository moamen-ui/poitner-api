using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Tenant;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;

namespace Pointer.Application.Services.Implementation;

public class TenantService : ITenantService
{
    private const int DefaultDemoTtlHours = 24;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IFileStorage _fileStorage;
    private readonly ISettingsService _settings;

    public TenantService(IUnitOfWork unitOfWork, IPasswordHasher passwordHasher, IFileStorage fileStorage, ISettingsService settings)
    {
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
        _fileStorage = fileStorage;
        _settings = settings;
    }

    public async Task<Result<List<TenantResponse>>> ListAsync()
    {
        // Super-admin operator view: bypass all query filters.
        var tenants = await _unitOfWork.Repository<User>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(u => u.Role)
            .Where(u => u.DeletedAt == null && u.Role.GrantsAdmin && !u.Role.IsSuperAdmin)
            .OrderBy(u => u.Id)
            .ToListAsync();

        if (tenants.Count == 0)
            return Result<List<TenantResponse>>.Success(new List<TenantResponse>());

        var tenantIds = tenants.Select(t => (Guid?)t.PublicId).ToList();

        // Count projects per tenant (IgnoreQueryFilters — super-admin operator path).
        var projectCounts = await _unitOfWork.Repository<Project>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => p.DeletedAt == null && tenantIds.Contains(p.OwnerId))
            .GroupBy(p => p.OwnerId)
            .Select(g => new { OwnerId = g.Key, Count = g.Count() })
            .ToListAsync();

        // Count comments per tenant (IgnoreQueryFilters — super-admin operator path).
        var commentCounts = await _unitOfWork.Repository<Comment>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => c.DeletedAt == null && tenantIds.Contains(c.OwnerId))
            .GroupBy(c => c.OwnerId)
            .Select(g => new { OwnerId = g.Key, Count = g.Count() })
            .ToListAsync();

        var projectMap = projectCounts
            .Where(x => x.OwnerId.HasValue)
            .ToDictionary(x => x.OwnerId!.Value, x => x.Count);
        var commentMap = commentCounts
            .Where(x => x.OwnerId.HasValue)
            .ToDictionary(x => x.OwnerId!.Value, x => x.Count);

        var responses = tenants.Select(t => new TenantResponse
        {
            Id = t.Id,
            PublicId = t.PublicId,
            Email = t.Email,
            DisplayName = t.DisplayName,
            ApprovalStatus = t.ApprovalStatus.ToString(),
            IsActive = t.IsActive,
            Projects = projectMap.GetValueOrDefault(t.PublicId, 0),
            Comments = commentMap.GetValueOrDefault(t.PublicId, 0),
            IsDemo = t.IsDemo,
            ExpiresAt = t.ExpiresAt,
            DemoExtended = t.DemoExtended,
            DemoCommentCapOverride = t.DemoCommentCapOverride,
            DemoTtlHoursOverride = t.DemoTtlHoursOverride
        }).ToList();

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

        var emailNormalized = request.Email.Trim().ToLower();

        // Duplicate email check — global scope (no OwnerId filter needed here, emails are global-unique).
        var exists = await _unitOfWork.Repository<User>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(u => u.DeletedAt == null && u.Email.ToLower() == emailNormalized);

        if (exists)
            return Result<TenantResponse>.Conflict("Email already in use.");

        // Find the global "Workspace Admin" role (GrantsAdmin=true, IsSuperAdmin=false, OwnerId=null).
        var workspaceAdminRole = await _unitOfWork.Repository<Role>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(r =>
                r.DeletedAt == null && r.IsActive &&
                r.GrantsAdmin && !r.IsSuperAdmin &&
                r.OwnerId == null &&
                r.Name == "Workspace Admin");

        if (workspaceAdminRole == null)
            return Result<TenantResponse>.Failure("System role 'Workspace Admin' not found. Ensure the database is seeded.");

        var publicId = Guid.NewGuid();

        var user = new User
        {
            Email = emailNormalized,
            PasswordHash = _passwordHasher.Hash(request.Password),
            DisplayName = request.DisplayName.Trim(),
            RoleId = workspaceAdminRole.Id,
            PublicId = publicId,
            IsActive = true,
            ApprovalStatus = ApprovalStatus.Approved,
            // A tenant owns itself: OwnerId == its own PublicId.
            OwnerId = publicId
        };

        await _unitOfWork.Repository<User>().AddAsync(user);
        await _unitOfWork.SaveChangesAsync();

        return Result<TenantResponse>.Success(new TenantResponse
        {
            Id = user.Id,
            PublicId = user.PublicId,
            Email = user.Email,
            DisplayName = user.DisplayName,
            ApprovalStatus = user.ApprovalStatus.ToString(),
            IsActive = user.IsActive,
            Projects = 0,
            Comments = 0
        });
    }

    public async Task<Result> SetStatusAsync(int id, string action)
    {
        if (string.IsNullOrWhiteSpace(action))
            return Result.Failure("Action is required. Valid values: approve, enable, disable.");

        // Load the user bypassing query filters (super-admin operator path).
        var user = await _unitOfWork.Repository<User>()
            .Query()
            .IgnoreQueryFilters()
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == id && u.DeletedAt == null);

        if (user == null)
            return Result.NotFound("Tenant not found.");

        // Must be a scoped-admin (GrantsAdmin=true, IsSuperAdmin=false).
        if (!user.Role.GrantsAdmin || user.Role.IsSuperAdmin)
            return Result.NotFound("Tenant not found.");

        switch (action.Trim().ToLower())
        {
            case "approve":
                user.ApprovalStatus = ApprovalStatus.Approved;
                user.IsActive = true;
                break;
            case "enable":
                user.IsActive = true;
                break;
            case "disable":
                user.IsActive = false;
                break;
            default:
                return Result.Failure("Invalid action. Valid values: approve, enable, disable.");
        }

        _unitOfWork.Repository<User>().Update(user);
        await _unitOfWork.SaveChangesAsync();

        return Result.Success();
    }

    public async Task<Result> ExtendDemoAsync(int id)
    {
        // One-time super-admin extension of a demo workspace by one more configured demo period.
        var user = await _unitOfWork.Repository<User>()
            .Query()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == id && u.DeletedAt == null);

        if (user == null || !user.IsDemo)
            return Result.NotFound("Demo tenant not found.");

        if (user.DemoExtended)
            return Result.Failure("This demo has already been extended once.");

        // Per-tenant TTL override wins; otherwise the global setting.
        var ttlHours = user.DemoTtlHoursOverride
            ?? await _settings.GetIntAsync(ISettingsService.DemoTtlHours, DefaultDemoTtlHours);

        // Extend from whichever is later — now or the current (possibly future) expiry — so an
        // already-expired demo gets a full fresh period rather than one anchored in the past.
        var anchor = user.ExpiresAt is DateTime exp && exp > DateTime.UtcNow ? exp : DateTime.UtcNow;
        user.ExpiresAt = anchor.AddHours(ttlHours);
        user.DemoExtended = true;

        _unitOfWork.Repository<User>().Update(user);
        await _unitOfWork.SaveChangesAsync();

        return Result.Success();
    }

    public async Task<Result> SetDemoConfigAsync(int id, int? commentCapOverride, int? ttlHoursOverride)
    {
        // Per-tenant demo overrides. Null clears an override (revert to the global default);
        // any positive value sets it. Non-positive values are rejected as invalid.
        if (commentCapOverride is <= 0)
            return Result.Failure("Comment cap must be a positive number, or empty to use the global default.");
        if (ttlHoursOverride is <= 0)
            return Result.Failure("TTL (hours) must be a positive number, or empty to use the global default.");

        var user = await _unitOfWork.Repository<User>()
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

    public async Task<Result> HardDeleteAsync(Guid tenantId)
    {
        // Verify the tenant exists before starting the transaction.
        var tenantUser = await _unitOfWork.Repository<User>()
            .Query()
            .IgnoreQueryFilters()
            .Include(u => u.Role)
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.PublicId == tenantId && u.DeletedAt == null);

        if (tenantUser == null)
            return Result.NotFound("Tenant not found.");

        if (!tenantUser.Role.GrantsAdmin || tenantUser.Role.IsSuperAdmin)
            return Result.NotFound("Tenant not found.");

        // Delete owner files first (outside transaction — filesystem side effect).
        await _fileStorage.DeleteOwnerFilesAsync(tenantId.ToString("N"));

        // Hard-delete inside a transaction using the execution strategy wrapper
        // (required by Npgsql's NpgsqlRetryingExecutionStrategy).
        await _unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            // Hard-delete in FK-safe order:
            // 1. Replies (reference Comments via FK)
            var replies = await _unitOfWork.Repository<Reply>()
                .Query()
                .IgnoreQueryFilters()
                .Where(r => r.OwnerId == tenantId)
                .ToListAsync();
            if (replies.Count > 0)
                _unitOfWork.Repository<Reply>().RemoveRange(replies);

            // 2. Comments (reference Projects via FK)
            var comments = await _unitOfWork.Repository<Comment>()
                .Query()
                .IgnoreQueryFilters()
                .Where(c => c.OwnerId == tenantId)
                .ToListAsync();
            if (comments.Count > 0)
                _unitOfWork.Repository<Comment>().RemoveRange(comments);

            // 3. Projects
            var projects = await _unitOfWork.Repository<Project>()
                .Query()
                .IgnoreQueryFilters()
                .Where(p => p.OwnerId == tenantId)
                .ToListAsync();
            if (projects.Count > 0)
                _unitOfWork.Repository<Project>().RemoveRange(projects);

            // 4. StatusPresentations
            var statuses = await _unitOfWork.Repository<StatusPresentation>()
                .Query()
                .IgnoreQueryFilters()
                .Where(s => s.OwnerId == tenantId)
                .ToListAsync();
            if (statuses.Count > 0)
                _unitOfWork.Repository<StatusPresentation>().RemoveRange(statuses);

            // 5. Users (stakeholders + the scoped-admin user itself)
            var users = await _unitOfWork.Repository<User>()
                .Query()
                .IgnoreQueryFilters()
                .Where(u => u.OwnerId == tenantId)
                .ToListAsync();
            if (users.Count > 0)
                _unitOfWork.Repository<User>().RemoveRange(users);

            // 6. Roles owned by this tenant
            var roles = await _unitOfWork.Repository<Role>()
                .Query()
                .IgnoreQueryFilters()
                .Where(r => r.OwnerId == tenantId)
                .ToListAsync();
            if (roles.Count > 0)
                _unitOfWork.Repository<Role>().RemoveRange(roles);

            await _unitOfWork.SaveChangesAsync();
        });

        return Result.Success();
    }
}
