using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Extension;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;

namespace Pointer.Application.Services.Implementation;

/// <summary>
/// Backs the ExtensionEnabled / MaxExtensionSites levers via a per-tenant record of activated origins.
/// Enforced-but-inert: nothing calls the endpoint until the real browser extension exists. No fake data.
/// </summary>
public class ExtensionService : IExtensionService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IProjectService _projectService;
    private readonly IEntitlementService _entitlements;

    public ExtensionService(IUnitOfWork unitOfWork, IProjectService projectService, IEntitlementService entitlements)
    {
        _unitOfWork = unitOfWork;
        _projectService = projectService;
        _entitlements = entitlements;
    }

    public async Task<Result<ExtensionActivateResponse>> ActivateAsync(ExtensionActivateRequest request)
    {
        var origin = NormalizeOrigin(request.Origin);
        if (string.IsNullOrEmpty(origin))
            return Result<ExtensionActivateResponse>.Failure("A valid origin is required.");

        // Resolve the project by key (owner-scoped, like the widget). NotFound/Disabled propagate.
        var projectResult = await _projectService.EnsureAsync(request.ProjectKey);
        if (!projectResult.IsSuccess)
            return projectResult.IsConflict
                ? Result<ExtensionActivateResponse>.Conflict(projectResult.Message ?? MessageKeys.Project.Disabled)
                : Result<ExtensionActivateResponse>.NotFound(projectResult.Message ?? MessageKeys.Project.NotFound);

        // The tenant = the project's owner (a comment/site belongs to whoever owns the project).
        var projectOwner = await _unitOfWork.Repository<Project>()
            .Query()
            .Where(p => p.Id == projectResult.Data)
            .Select(p => p.OwnerId)
            .FirstAsync();

        if (projectOwner is not Guid owner)
            return Result<ExtensionActivateResponse>.NotFound(MessageKeys.Project.NotFound);

        // ExtensionEnabled gate (passes when the kill-switch is off).
        var flag = await _entitlements.EnforceFlagAsync(owner, EntitlementCatalog.ExtensionEnabled);
        if (!flag.IsSuccess)
            return Result<ExtensionActivateResponse>.LimitReached(flag.Message ?? MessageKeys.Plan.ExtensionDisabled, flag.Limit!);

        // Idempotent: an already-recorded origin doesn't consume a new slot.
        var existing = await _unitOfWork.Repository<ExtensionSite>()
            .Query()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.OwnerId == owner && s.Origin == origin && s.DeletedAt == null);

        if (existing != null)
        {
            var currentCount = await ActiveSiteCountAsync(owner);
            return Result<ExtensionActivateResponse>.Success(
                new ExtensionActivateResponse { Origin = origin, SiteCount = currentCount });
        }

        // MaxExtensionSites: distinct active origins for the tenant. Grandfather-safe (checked on add).
        var siteCount = await ActiveSiteCountAsync(owner);
        var check = await _entitlements.CheckCountAsync(owner, EntitlementCatalog.MaxExtensionSites, siteCount);
        if (!check.IsSuccess)
            return Result<ExtensionActivateResponse>.LimitReached(check.Message ?? MessageKeys.Plan.LimitReached, check.Limit!);

        await _unitOfWork.Repository<ExtensionSite>().AddAsync(new ExtensionSite
        {
            OwnerId = owner,
            Origin = origin,
            FirstSeenAt = DateTime.UtcNow
        });
        await _unitOfWork.SaveChangesAsync();

        return Result<ExtensionActivateResponse>.Success(
            new ExtensionActivateResponse { Origin = origin, SiteCount = siteCount + 1 });
    }

    public async Task<Result<ExtensionProjectLookupResponse>> FindProjectForOriginAsync(string rawOrigin)
    {
        var origin = NormalizeOrigin(rawOrigin);
        if (string.IsNullOrEmpty(origin))
            return Result<ExtensionProjectLookupResponse>.Failure("A valid origin is required.");

        // ProjectAppUrl's own query filter already scopes this to the caller's own tenant (strict-own,
        // same as Comment/Invite) — a quick-access client can never see another tenant's projects
        // this way, only match their own, across ANY of that project's environments.
        var urls = await _unitOfWork.Repository<ProjectAppUrl>()
            .Query()
            .AsNoTracking()
            .Include(u => u.Project)
            .Where(u => u.DeletedAt == null && u.Project.DeletedAt == null && u.Project.IsActive)
            .Select(u => new { u.Project.Key, u.Project.Name, u.Url })
            .ToListAsync();

        var match = urls.FirstOrDefault(u => NormalizeOrigin(u.Url) == origin);
        if (match == null)
        {
            // Legacy fallback: a project whose Project.AppUrl was set before the per-environment
            // table existed, and hasn't been re-saved since (SyncDefaultAppUrlAsync backfills it on
            // every create/update, but a project untouched since the migration might still be here).
            var legacy = await _unitOfWork.Repository<Project>()
                .Query()
                .AsNoTracking()
                .Where(p => p.DeletedAt == null && p.IsActive && p.AppUrl != null)
                .Select(p => new { p.Key, p.Name, p.AppUrl })
                .ToListAsync();
            var legacyMatch = legacy.FirstOrDefault(p => NormalizeOrigin(p.AppUrl!) == origin);
            if (legacyMatch == null)
                return Result<ExtensionProjectLookupResponse>.NotFound(MessageKeys.Project.NoneForOrigin);
            return Result<ExtensionProjectLookupResponse>.Success(
                new ExtensionProjectLookupResponse { Key = legacyMatch.Key, Name = legacyMatch.Name });
        }

        return Result<ExtensionProjectLookupResponse>.Success(
            new ExtensionProjectLookupResponse { Key = match.Key, Name = match.Name });
    }

    private async Task<int> ActiveSiteCountAsync(Guid owner) =>
        await _unitOfWork.Repository<ExtensionSite>()
            .Query()
            .IgnoreQueryFilters()
            .CountAsync(s => s.OwnerId == owner && s.DeletedAt == null);

    // Normalize to scheme://host[:port], lower-case, no trailing slash/path.
    private static string NormalizeOrigin(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var trimmed = raw.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
            return $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}{port}";
        }
        return trimmed.ToLowerInvariant().TrimEnd('/');
    }
}
