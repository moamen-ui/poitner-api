using Microsoft.Extensions.Configuration;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.PredefinedAction;
using Pointer.Application.DTOs.Project;
using Pointer.Application.DTOs.Workspace;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;

namespace Pointer.Application.Services.Implementation;

public class ProjectService : IProjectService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;
    private readonly IEntitlementService _entitlements;
    private readonly ICommentFieldService _commentFields;

    public ProjectService(
        IUnitOfWork unitOfWork,
        ICurrentUser currentUser,
        IEntitlementService entitlements,
        ISettingsService settings,
        IConfiguration configuration,
        ICommentFieldService? commentFields = null)
    {
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
        _entitlements = entitlements;
        _settings = settings;
        _configuration = configuration;
        _commentFields = commentFields ?? new CommentFieldService(unitOfWork, currentUser);
    }

    private readonly ISettingsService _settings;
    private readonly IConfiguration _configuration;

    /// <summary>
    /// Hosts that are always allowed to post, whatever a project's app-URL rows say: the dashboard
    /// itself (staff reply and change status from there) and any extra front-end hosts an operator
    /// lists. Without this, turning enforcement on would 403 every reply sent from the dashboard.
    /// </summary>
    private async Task<HashSet<string>> TrustedOriginsAsync()
    {
        var trusted = new HashSet<string>(StringComparer.Ordinal);

        // Defaulted, not bare. Reading the raw setting returns null until an operator saves
        // branding, and then the dashboard's own origin is not in this set — so turning
        // enforcement on would 403 the admin out of the UI they turned it on from.
        var brandApp = await _settings.GetStringAsync(ISettingsService.BrandUrlApp, BrandingDefaults.UrlApp);
        if (!string.IsNullOrWhiteSpace(brandApp))
            trusted.Add(OriginNormalizer.Normalize(brandApp));

        // GetChildren rather than Get<string[]>(): the Application project references only
        // Configuration.Abstractions, and the binder extension lives in Configuration.Binder.
        foreach (var extra in _configuration.GetSection("Security:TrustedDashboardOrigins").GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(extra.Value))
                trusted.Add(OriginNormalizer.Normalize(extra.Value));
        }

        return trusted;
    }

    /// <summary>Loopback names a browser can actually send as an Origin. 0.0.0.0 is not one of them.</summary>
    private static bool IsLocalhostOrigin(string normalisedOrigin)
    {
        if (!Uri.TryCreate(normalisedOrigin, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host.ToLowerInvariant().Trim('[', ']');
        return host is "localhost" or "127.0.0.1" or "::1" || host.EndsWith(".localhost", StringComparison.Ordinal);
    }

    /// <summary>
    /// Which environment a request came from, decided by its Origin rather than by what the page
    /// claims.
    ///
    /// An app does not have an environment; a deployment does. Baking one into the markup means the
    /// same built file tags staging and production feedback identically, and the only cure is
    /// rebuilding every app whenever a URL changes. The dashboard already stores a URL per
    /// environment per project, so that mapping — kept current by the people who own it — is the
    /// answer.
    ///
    /// Returns <see cref="EnvironmentTag.Unknown"/> when nothing matches. See the enum for why that
    /// is preferable to a plausible guess.
    /// </summary>
    public async Task<EnvironmentTag> ResolveEnvironmentAsync(int projectId, string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
            return EnvironmentTag.Unknown;

        var normalised = OriginNormalizer.Normalize(origin);

        // A dev server is never registered and should not have to be: its port changes far more
        // often than anyone updates a URL list.
        if (IsLocalhostOrigin(normalised))
            return EnvironmentTag.Local;

        // Only rows whose project mapping AND whose workspace environment are both enabled take
        // part — the same pair of switches ProjectAppUrl.IsActive and AppEnvironment.IsEnabled
        // document themselves as controlling.
        var rows = await _unitOfWork.Repository<ProjectAppUrl>()
            .Query()
            .AsNoTracking()
            .Include(u => u.AppEnvironment)
            .Where(u => u.ProjectId == projectId && u.IsActive && u.DeletedAt == null
                        && u.AppEnvironment.IsEnabled && u.AppEnvironment.DeletedAt == null)
            .Select(u => new { u.Url, EnvName = u.AppEnvironment.Name })
            .ToListAsync();

        // Exact origins before wildcard patterns, so `https://app.example.com` registered on
        // production wins over a `https://*.example.com` on staging. Without an explicit order the
        // answer is whatever order the database returned, which can differ between calls — and an
        // environment that changes under you is worse than one that is merely wrong.
        foreach (var row in rows.OrderBy(r => r.Url.Contains('*') ? 1 : 0).ThenBy(r => r.Url))
        {
            if (!OriginNormalizer.Matches(row.Url, normalised))
                continue;

            var tag = TagFromEnvironmentName(row.EnvName);
            // A matched URL whose environment is named something outside the three tags (a tenant
            // may name environments freely — "qa", "preview", "default") is still Unknown: the
            // comment's tag is a fixed enum and inventing a mapping would be the same guess this
            // method exists to refuse.
            if (tag != EnvironmentTag.Unknown)
                return tag;
        }

        return EnvironmentTag.Unknown;
    }

    /// <summary>Maps a free-form workspace environment name onto the fixed comment tag.</summary>
    private static EnvironmentTag TagFromEnvironmentName(string? name) =>
        (name ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "local" or "localhost" or "development" or "dev" => EnvironmentTag.Local,
            "staging" or "stage" or "test" or "qa" => EnvironmentTag.Staging,
            "production" or "prod" or "live" => EnvironmentTag.Production,
            _ => EnvironmentTag.Unknown,
        };

    public async Task<bool> IsOriginAllowedAsync(
        int projectId,
        string? origin,
        EnvironmentTag environment,
        bool isQuickAccess)
    {
        var project = await _unitOfWork.Repository<Project>()
            .Query()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId && p.DeletedAt == null);

        // Unknown project, or the owner never opted in: unchanged behaviour.
        if (project is null || !project.EnforceAllowedOrigins)
            return true;

        if (string.IsNullOrWhiteSpace(origin))
        {
            // No Origin/Referer at all. Browsers always send one on a cross-origin POST, so this is
            // automation — allowed for staff keys (the CLI and AI agents), refused for a client
            // token, which has no legitimate non-browser path.
            return !isQuickAccess;
        }

        var normalised = OriginNormalizer.Normalize(origin);

        // A developer's dev server is never in the allow-list, and should not have to be.
        if (environment == EnvironmentTag.Local && IsLocalhostOrigin(normalised))
            return true;

        if ((await TrustedOriginsAsync()).Contains(normalised))
            return true;

        var urls = await _unitOfWork.Repository<ProjectAppUrl>()
            .Query()
            .AsNoTracking()
            .Where(u => u.ProjectId == projectId && u.IsActive && u.DeletedAt == null)
            .Select(u => u.Url)
            .ToListAsync();

        return urls.Any(u => OriginNormalizer.Matches(u, normalised));
    }


    public async Task<Result<ProjectResponse>> CreateAsync(CreateProjectRequest request)
    {
        var keyNormalized = request.Key.Trim().ToLower();

        // EF query filter already scopes this to the caller's tenant;
        // the check is still correct — a scoped admin cannot key-conflict with another tenant.
        var exists = await _unitOfWork.Repository<Project>()
            .Query()
            .AsNoTracking()
            .Where(p => p.DeletedAt == null && p.Key == keyNormalized)
            .AnyAsync();

        if (exists)
            return Result<ProjectResponse>.Conflict(MessageKeys.Project.KeyTaken);

        // Super admins are platform-management only — they never self-own tenant-scoped resources.
        // Someone who wants to use the product signs in with a real tenant account instead.
        if (_currentUser.IsSuperAdmin)
            return Result<ProjectResponse>.Forbidden(MessageKeys.Project.SuperAdminNotAllowed);

        // Quick-access (e.g. "Client") accounts exist only to comment on the ONE project they were
        // invited to — they must never reach project management, even though ProjectsController is
        // broadly [Authorize] (not admin-gated) for ordinary stakeholders.
        if (_currentUser.IsQuickAccess)
            return Result<ProjectResponse>.Forbidden(MessageKeys.Project.QuickAccessNotAllowed);

        // OwnerId stamps the tenant. S-14: a non-super-admin without a tenant claim is Forbidden,
        // never minted a tenant from its own id.
        var ownerId = TenantStamp.OwnerFor(_currentUser);
        if (ownerId is not Guid owner)
            return Result<ProjectResponse>.Forbidden(MessageKeys.Common.Forbidden);

        // MaxProjects: count active projects owned by this tenant (grandfather-safe — checked only on
        // create, counts only DeletedAt == null rows). Explicit OwnerId + IgnoreQueryFilters so the
        // count is the tenant's real total regardless of the caller.
        if (ownerId is Guid projectOwner)
        {
            var activeProjects = await _unitOfWork.Repository<Project>()
                .Query()
                .IgnoreQueryFilters()
                .CountAsync(p => p.OwnerId == projectOwner && p.DeletedAt == null);
            var check = await _entitlements.CheckCountAsync(projectOwner, EntitlementCatalog.MaxProjects, activeProjects);
            if (!check.IsSuccess)
                return Result<ProjectResponse>.LimitReached(check.Message ?? MessageKeys.Plan.LimitReached, check.Limit!);
        }

        // Same URL rules as SetAppUrlAsync. The create path accepted anything at all — a project
        // could be born with `definitely not a url` on its local environment, which then matched
        // nothing forever.
        if (!string.IsNullOrWhiteSpace(request.AppUrl))
        {
            var createUrlError = OriginNormalizer.ValidatePattern(request.AppUrl.Trim());
            if (createUrlError != null)
                return Result<ProjectResponse>.Failure(createUrlError);
        }

        Domain.Entity.AppEnvironment? environment = null;
        if (!string.IsNullOrWhiteSpace(request.AppUrl) && request.AppEnvironmentId.HasValue)
        {
            environment = await _unitOfWork.Repository<Domain.Entity.AppEnvironment>().GetByIdAsync(request.AppEnvironmentId.Value);
            if (environment == null || environment.DeletedAt != null)
                return Result<ProjectResponse>.NotFound(MessageKeys.AppEnvironment.NotFound);

            if (!environment.IsEnabled || environment.IsRetired)
                return Result<ProjectResponse>.Failure(MessageKeys.AppEnvironment.NotEnabled);
        }

        var project = new Project
        {
            Key = keyNormalized,
            Name = request.Name,
            // IsActiveLocal/Staging/Production default true on the entity — a new project starts
            // fully active in every environment.
            AppUrl = string.IsNullOrWhiteSpace(request.AppUrl) ? null : request.AppUrl.Trim(),
            OwnerId = ownerId
        };

        await _unitOfWork.Repository<Project>().AddAsync(project);
        await _unitOfWork.SaveChangesAsync(); // need the project Id before attaching actions

        // A project created with just "AppUrl" (e.g. via the browser extension, which has no concept
        // of multiple environments) lands on "local" — the one environment every tenant has out of
        // the box. See ExtensionService.FindProjectForOriginAsync, which reads from ProjectAppUrl now.
        if (project.AppUrl != null)
        {
            if (environment != null)
            {
                await _unitOfWork.Repository<ProjectAppUrl>().AddAsync(new ProjectAppUrl
                {
                    ProjectId = project.Id,
                    AppEnvironmentId = environment.Id,
                    Url = project.AppUrl,
                    OwnerId = project.OwnerId ?? Guid.Empty,
                    IsActive = true
                });
                await _unitOfWork.SaveChangesAsync();
            }
            else
            {
                await SyncPrimaryAppUrlAsync(project, project.AppUrl);
            }
        }

        if (request.PredefinedActions.Count > 0)
        {
            // MaxPredefinedActionsPerProject: a freshly-created project starts at 0 actions, so the
            // running count is just how many we've added so far this loop. Owner tenant = the project's
            // owner (fall back to the caller for a null-owner project so the check still resolves).
            var actionTenant = project.OwnerId ?? throw new InvalidOperationException("project without owner");
            var sort = 0;
            var addedSoFar = 0;
            foreach (var input in request.PredefinedActions)
            {
                var actionCheck = await _entitlements.CheckCountAsync(
                    actionTenant, EntitlementCatalog.MaxPredefinedActionsPerProject, addedSoFar);
                if (!actionCheck.IsSuccess)
                    return Result<ProjectResponse>.LimitReached(
                        actionCheck.Message ?? MessageKeys.Plan.LimitReached, actionCheck.Limit!);

                await _unitOfWork.Repository<PredefinedAction>().AddAsync(new PredefinedAction
                {
                    OwnerId = project.OwnerId, // inherit the project's owner (may be null = global)
                    ProjectId = project.Id,
                    UserId = null,
                    Text = input.Text.Trim(),
                    Prompt = input.Prompt,
                    IsActive = input.IsActive,
                    SortOrder = input.SortOrder != 0 ? input.SortOrder : sort++
                });
                addedSoFar++;
            }
            await _unitOfWork.SaveChangesAsync();
        }

        var actions = await LoadProjectActionsAsync(project.Id);
        var appUrls = await LoadProjectAppUrlsAsync(project.Id);
        // Freshly-created project: no comments; creator is the caller.
        return Result<ProjectResponse>.Success(MapToResponse(project, actions, appUrls, 0,
            await ResolveCreatorNameAsync(project.CreatedBy)));
    }

    public async Task<Result<List<ProjectResponse>>> ListAsync()
    {
        // See CreateAsync's QuickAccessNotAllowed comment — quick-access accounts never reach
        // project management. (They never need to: PointerDogfoodService's own use of this endpoint
        // is gated behind an admin-tier login on the dashboard, and the widget's own runtime uses
        // EnsureAsync/the comments endpoints instead, not this one.)
        if (_currentUser.IsQuickAccess)
            return Result<List<ProjectResponse>>.Forbidden(MessageKeys.Project.QuickAccessNotAllowed);

        var projects = await _unitOfWork.Repository<Project>()
            .Query()
            .AsNoTracking()
            .Where(p => p.DeletedAt == null)
            .OrderByDescending(p => p.CreatedAt)
            .ThenByDescending(p => p.Id)
            .ToListAsync();

        var projectIds = projects.Select(p => p.Id).ToList();

        // One batched query for all project-scoped actions; group in memory.
        var actions = await _unitOfWork.Repository<PredefinedAction>()
            .Query()
            .AsNoTracking()
            .Where(a => a.DeletedAt == null && a.ProjectId != null && projectIds.Contains(a.ProjectId.Value))
            .OrderBy(a => a.SortOrder)
            .ToListAsync();

        var byProject = actions.GroupBy(a => a.ProjectId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        // BINDING #6: batch comment counts in ONE GroupBy query (no N+1).
        var commentCounts = await _unitOfWork.Repository<Comment>()
            .Query()
            .AsNoTracking()
            .Where(c => c.DeletedAt == null && projectIds.Contains(c.ProjectId))
            .GroupBy(c => c.ProjectId)
            .Select(g => new { ProjectId = g.Key, Count = g.Count() })
            .ToListAsync();
        var countByProject = commentCounts.ToDictionary(x => x.ProjectId, x => x.Count);

        // Fetch app urls in batch
        var appUrls = await _unitOfWork.Repository<ProjectAppUrl>()
            .Query()
            .AsNoTracking()
            .Include(u => u.AppEnvironment)
            .Where(u => u.DeletedAt == null && projectIds.Contains(u.ProjectId))
            .ToListAsync();
        var appUrlsByProject = appUrls.GroupBy(u => u.ProjectId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Batch-resolve creator display names.
        var creatorNames = await ResolveCreatorNamesAsync(projects.Select(p => p.CreatedBy));

        var responses = projects
            .Select(p => MapToResponse(
                p,
                byProject.GetValueOrDefault(p.Id) ?? new List<PredefinedAction>(),
                appUrlsByProject.GetValueOrDefault(p.Id) ?? new List<ProjectAppUrl>(),
                countByProject.GetValueOrDefault(p.Id, 0),
                creatorNames.GetValueOrDefault(p.CreatedBy)))
            .ToList();

        return Result<List<ProjectResponse>>.Success(responses);
    }

    public async Task<Result<ProjectResponse>> UpdateAsync(int id, UpdateProjectRequest request)
    {
        // Authz load uses the NORMAL query filter (in-tenant projects are already visible in List).
        var project = await _unitOfWork.Repository<Project>().GetByIdAsync(id);

        if (project == null || project.DeletedAt != null)
            return Result<ProjectResponse>.NotFound(MessageKeys.Project.NotFound);

        // Only an admin or the project's creator may edit. Forbidden (not NotFound) because the
        // project is already visible to the caller in List.
        if (!(_currentUser.IsAdmin || project.CreatedBy == _currentUser.Id))
            return Result<ProjectResponse>.Forbidden(MessageKeys.Project.NotFound);

        if (request.Name != null)
            project.Name = request.Name;

        if (request.IsActiveLocal.HasValue)
            project.IsActiveLocal = request.IsActiveLocal.Value;

        if (request.IsActiveStaging.HasValue)
            project.IsActiveStaging = request.IsActiveStaging.Value;

        if (request.IsActiveProduction.HasValue)
            project.IsActiveProduction = request.IsActiveProduction.Value;

        if (request.PageContextCaptureEnabled.HasValue)
            project.PageContextCaptureEnabled = request.PageContextCaptureEnabled.Value;

        if (request.EnforceAllowedOrigins.HasValue)
            project.EnforceAllowedOrigins = request.EnforceAllowedOrigins.Value;

        if (request.CaptureTextContent.HasValue)
            project.CaptureTextContent = request.CaptureTextContent.Value;

        if (request.CommitStyle.HasValue)
            project.CommitStyle = request.CommitStyle.Value;

        // null (property omitted) → leave untouched. An empty list is NOT the same as omitted —
        // it explicitly clears back to the default (stored as null), rather than storing "[]"
        // forever (which would otherwise mean "no role at all may see it").
        if (request.EnvironmentSelectorRoleIds != null)
        {
            project.EnvironmentSelectorRoleIds = request.EnvironmentSelectorRoleIds.Count == 0
                ? null
                : JsonSerializer.Serialize(request.EnvironmentSelectorRoleIds);
        }

        if (request.AppUrl != null)
        {
            project.AppUrl = request.AppUrl.Trim();
            await SyncPrimaryAppUrlAsync(project, project.AppUrl);
        }

        // NOTE: intentionally do NOT mutate project.OwnerId here. A null owner is legitimate for
        // global projects (e.g. the marketing landing); rewriting it would break the widget for
        // that project's null-owner stakeholders. Predefined actions on a null-owner project are a
        // known limitation pending the nullable-owner follow-up.
        _unitOfWork.Repository<Project>().Update(project);

        // Reconcile project-scoped predefined actions when the caller sends the list.
        // null (property omitted) → leave actions untouched. Actions inherit the project's owner
        // (which may be null for a global/null-owner project).
        if (request.PredefinedActions != null)
        {
            var reconcile = await ReconcileActionsAsync(project.Id, project.OwnerId, request.PredefinedActions);
            if (!reconcile.IsSuccess)
                return Result<ProjectResponse>.LimitReached(
                    reconcile.Message ?? MessageKeys.Plan.LimitReached, reconcile.Limit!);
        }

        await _unitOfWork.SaveChangesAsync();

        var actions = await LoadProjectActionsAsync(project.Id);
        var appUrls = await LoadProjectAppUrlsAsync(project.Id);
        var commentsCount = await _unitOfWork.Repository<Comment>()
            .Query().AsNoTracking()
            .CountAsync(c => c.ProjectId == project.Id && c.DeletedAt == null);
        return Result<ProjectResponse>.Success(MapToResponse(project, actions, appUrls, commentsCount,
            await ResolveCreatorNameAsync(project.CreatedBy)));
    }

    public async Task<Result<List<ProjectAppUrlResponse>>> ListAppUrlsAsync(int projectId)
    {
        var project = await _unitOfWork.Repository<Project>().GetByIdAsync(projectId);
        if (project == null || project.DeletedAt != null)
            return Result<List<ProjectAppUrlResponse>>.NotFound(MessageKeys.Project.NotFound);

        var urls = await _unitOfWork.Repository<ProjectAppUrl>()
            .Query()
            .AsNoTracking()
            .Include(u => u.AppEnvironment)
            .Where(u => u.ProjectId == projectId && u.DeletedAt == null)
            .OrderBy(u => u.AppEnvironment.Name)
            .ToListAsync();

        return Result<List<ProjectAppUrlResponse>>.Success(urls.Select(u => new ProjectAppUrlResponse
        {
            AppEnvironmentId = u.AppEnvironmentId,
            EnvironmentName = u.AppEnvironment.Name,
            Url = u.Url,
            IsActive = u.IsActive,
            EnvironmentIsEnabled = u.AppEnvironment.IsEnabled
        }).ToList());
    }

    public async Task<Result<ProjectAppUrlResponse>> SetAppUrlAsync(int projectId, int environmentId, SetProjectAppUrlRequest request)
    {
        var project = await _unitOfWork.Repository<Project>().GetByIdAsync(projectId);
        if (project == null || project.DeletedAt != null)
            return Result<ProjectAppUrlResponse>.NotFound(MessageKeys.Project.NotFound);

        if (!(_currentUser.IsAdmin || project.CreatedBy == _currentUser.Id))
            return Result<ProjectAppUrlResponse>.Forbidden(MessageKeys.Project.NotFound);

        var url = request.Url.Trim();
        if (string.IsNullOrEmpty(url))
            return Result<ProjectAppUrlResponse>.Failure("URL is required.");

        // The wildcard rules are only a guard if they run on the WRITE path. OriginNormalizer
        // refuses a bare `*` on shared hosting precisely because `https://*.vercel.app` would
        // authorise every other tenant on that platform to post into this project — but nothing
        // called it here, so such a pattern saved happily and then matched at request time.
        var patternError = OriginNormalizer.ValidatePattern(url);
        if (patternError != null)
            return Result<ProjectAppUrlResponse>.Failure(patternError);

        // The environment must be visible to this tenant (own or global) — the query filter already
        // enforces that; a foreign/other-tenant environment id simply won't be found.
        var environment = await _unitOfWork.Repository<Domain.Entity.AppEnvironment>().GetByIdAsync(environmentId);
        if (environment == null || environment.DeletedAt != null)
            return Result<ProjectAppUrlResponse>.NotFound(MessageKeys.AppEnvironment.NotFound);

        if (!environment.IsEnabled || environment.IsRetired)
            return Result<ProjectAppUrlResponse>.Failure(MessageKeys.AppEnvironment.NotEnabled);

        // One origin, one environment — per project.
        //
        // Two environments sharing a URL makes "which environment is this comment from?"
        // unanswerable: resolution matches rows in whatever order the database returns them, so the
        // same deployment could be filed as staging today and production tomorrow. The write is the
        // only place this can be settled — by read time the information needed to disambiguate is
        // already gone.
        var normalisedNew = OriginNormalizer.Normalize(url);
        var others = await _unitOfWork.Repository<ProjectAppUrl>()
            .Query()
            .AsNoTracking()
            .Include(u => u.AppEnvironment)
            .Where(u => u.ProjectId == projectId && u.AppEnvironmentId != environmentId && u.DeletedAt == null)
            .Select(u => new { u.Url, EnvName = u.AppEnvironment.Name })
            .ToListAsync();

        var conflicting = others.FirstOrDefault(o => OriginNormalizer.Normalize(o.Url) == normalisedNew);
        if (conflicting != null)
            return Result<ProjectAppUrlResponse>.Failure(
                $"That URL is already registered for the \"{conflicting.EnvName}\" environment of this project. " +
                "Each environment needs its own URL, otherwise a comment cannot be attributed to one of them.");

        // Including soft-deleted rows on purpose. Delete is a soft delete, and (ProjectId,
        // AppEnvironmentId) is unique, so "remove the local URL, then add it again" must revive the
        // old row — inserting a second one threw a duplicate-key error the user saw as a bare 500
        // (prod, project 70, 2026-09-16). Live row → update; soft-deleted row → undelete + update.
        var existing = await _unitOfWork.Repository<ProjectAppUrl>()
            .Query()
            .Where(u => u.ProjectId == projectId && u.AppEnvironmentId == environmentId)
            .OrderBy(u => u.DeletedAt == null ? 0 : 1)
            .FirstOrDefaultAsync();

        if (existing != null)
        {
            existing.Url = url;
            existing.IsActive = request.IsActive;
            existing.DeletedAt = null;
            existing.DeletedBy = null;
            _unitOfWork.Repository<ProjectAppUrl>().Update(existing);
        }
        else
        {
            existing = new ProjectAppUrl
            {
                ProjectId = projectId,
                AppEnvironmentId = environmentId,
                Url = url,
                IsActive = request.IsActive,
                OwnerId = project.OwnerId
            };
            await _unitOfWork.Repository<ProjectAppUrl>().AddAsync(existing);
        }
        await _unitOfWork.SaveChangesAsync();

        // Keep the legacy single field in sync for "local" so old readers (widget install
        // instructions, anything not yet updated) still see a sensible value.
        if (environment.Name == "local")
        {
            project.AppUrl = url;
            _unitOfWork.Repository<Project>().Update(project);
            await _unitOfWork.SaveChangesAsync();
        }

        return Result<ProjectAppUrlResponse>.Success(new ProjectAppUrlResponse
        {
            AppEnvironmentId = environmentId,
            EnvironmentName = environment.Name,
            Url = url,
            IsActive = existing.IsActive
        });
    }

    public async Task<Result> DeleteAppUrlAsync(int projectId, int environmentId)
    {
        var project = await _unitOfWork.Repository<Project>().GetByIdAsync(projectId);
        if (project == null || project.DeletedAt != null)
            return Result.NotFound(MessageKeys.Project.NotFound);

        if (!(_currentUser.IsAdmin || project.CreatedBy == _currentUser.Id))
            return Result.Forbidden(MessageKeys.Project.NotFound);

        var existing = await _unitOfWork.Repository<ProjectAppUrl>()
            .Query()
            .Where(u => u.ProjectId == projectId && u.AppEnvironmentId == environmentId && u.DeletedAt == null)
            .FirstOrDefaultAsync();
        if (existing == null)
            return Result.NotFound(MessageKeys.Project.NotFound);

        existing.DeletedAt = DateTime.UtcNow;
        _unitOfWork.Repository<ProjectAppUrl>().Update(existing);
        await _unitOfWork.SaveChangesAsync();

        return Result.Success();
    }

    public async Task<Result> DeleteAsync(int id)
    {
        // Authz load uses the NORMAL query filter (IgnoreQueryFilters only inside the cascade below).
        var project = await _unitOfWork.Repository<Project>().GetByIdAsync(id);

        if (project == null || project.DeletedAt != null)
            return Result.NotFound(MessageKeys.Project.NotFound);

        // BINDING #6: re-check comment count + ownership server-side regardless of any client hint.
        var commentsCount = await _unitOfWork.Repository<Comment>()
            .Query().AsNoTracking()
            .CountAsync(c => c.ProjectId == id && c.DeletedAt == null);

        if (_currentUser.IsAdmin)
        {
            // Admin cascade soft-delete — BINDING #2: keyed strictly off ProjectId, NEVER OwnerId
            // (an OwnerId scope would wipe the whole tenant). All inside the retry-safe transaction.
            await _unitOfWork.ExecuteInTransactionAsync(async () =>
            {
                var now = DateTime.UtcNow;
                var actorId = _currentUser.Id;

                var comments = await _unitOfWork.Repository<Comment>()
                    .Query()
                    .IgnoreQueryFilters()
                    .Where(c => c.ProjectId == id && c.DeletedAt == null)
                    .ToListAsync();
                var commentIds = comments.Select(c => c.Id).ToList();

                foreach (var c in comments)
                {
                    c.DeletedAt = now; c.DeletedBy = actorId;
                    _unitOfWork.Repository<Comment>().Update(c);
                }

                if (commentIds.Count > 0)
                {
                    var replies = await _unitOfWork.Repository<Reply>()
                        .Query()
                        .IgnoreQueryFilters()
                        .Where(r => commentIds.Contains(r.CommentId) && r.DeletedAt == null)
                        .ToListAsync();
                    foreach (var r in replies)
                    {
                        r.DeletedAt = now; r.DeletedBy = actorId;
                        _unitOfWork.Repository<Reply>().Update(r);
                    }
                }

                var actions = await _unitOfWork.Repository<PredefinedAction>()
                    .Query()
                    .IgnoreQueryFilters()
                    .Where(a => a.ProjectId == id && a.DeletedAt == null)
                    .ToListAsync();
                foreach (var a in actions)
                {
                    a.DeletedAt = now; a.DeletedBy = actorId;
                    _unitOfWork.Repository<PredefinedAction>().Update(a);
                }

                var suggestions = await _unitOfWork.Repository<PredefinedActionSuggestion>()
                    .Query()
                    .IgnoreQueryFilters()
                    .Where(s => s.ProjectId == id && s.DeletedAt == null)
                    .ToListAsync();
                foreach (var s in suggestions)
                {
                    s.DeletedAt = now; s.DeletedBy = actorId;
                    _unitOfWork.Repository<PredefinedActionSuggestion>().Update(s);
                }

                project.DeletedAt = now; project.DeletedBy = actorId;
                _unitOfWork.Repository<Project>().Update(project);

                await _unitOfWork.SaveChangesAsync();
            });

            return Result.Success();
        }

        // Non-admin: only the owner, and only when the project has no comments.
        if (project.CreatedBy != _currentUser.Id)
            return Result.Forbidden(MessageKeys.Project_Delete.NotOwner);

        if (commentsCount != 0)
            return Result.Conflict(MessageKeys.Project_Delete.HasComments);

        // Owner + 0 comments: soft-delete the project + its predefined actions + suggestions
        // (no comments/replies exist by definition).
        var ownActions = await _unitOfWork.Repository<PredefinedAction>()
            .Query()
            .Where(a => a.ProjectId == id && a.DeletedAt == null)
            .ToListAsync();
        foreach (var a in ownActions)
        {
            a.DeletedAt = DateTime.UtcNow;
            _unitOfWork.Repository<PredefinedAction>().Update(a);
        }

        var ownSuggestions = await _unitOfWork.Repository<PredefinedActionSuggestion>()
            .Query()
            .Where(s => s.ProjectId == id && s.DeletedAt == null)
            .ToListAsync();
        foreach (var s in ownSuggestions)
        {
            s.DeletedAt = DateTime.UtcNow;
            _unitOfWork.Repository<PredefinedActionSuggestion>().Update(s);
        }

        project.DeletedAt = DateTime.UtcNow;
        _unitOfWork.Repository<Project>().Update(project);
        await _unitOfWork.SaveChangesAsync();

        return Result.Success();
    }

    /// <summary>
    /// Reconcile the desired set of project-scoped actions against what exists (last-write-wins):
    ///   id present    → update in place
    ///   id absent      → add
    ///   existing row absent from the payload → soft-delete
    /// All queries add DeletedAt == null so soft-deleted rows never resurface or double-delete.
    /// </summary>
    private async Task<Result> ReconcileActionsAsync(int projectId, Guid? owner, List<PredefinedActionInput> desired)
    {
        var existing = await _unitOfWork.Repository<PredefinedAction>()
            .Query()
            .Where(a => a.DeletedAt == null && a.ProjectId == projectId)
            .ToListAsync();

        // MaxPredefinedActionsPerProject: the resulting active count = desired entries that resolve to a
        // real row (a new id-less entry, or an existing id still present). Block if that would exceed the
        // limit. Grandfather-safe: existing rows are never touched by the check itself.
        var existingIds = existing.Select(a => a.Id).ToHashSet();
        var resultingCount = desired.Count(d => d.Id is not int did || existingIds.Contains(did));
        if (resultingCount > 0)
        {
            var actionTenant = owner ?? throw new InvalidOperationException("project without owner");
            // resultingCount-1 >= limit  ⇔  resultingCount > limit (block when the final count exceeds it).
            var check = await _entitlements.CheckCountAsync(
                actionTenant, EntitlementCatalog.MaxPredefinedActionsPerProject, resultingCount - 1);
            if (!check.IsSuccess)
                return check;
        }

        var keptIds = new HashSet<int>();

        foreach (var input in desired)
        {
            if (input.Id is int existingId)
            {
                var row = existing.FirstOrDefault(a => a.Id == existingId);
                if (row == null)
                    continue; // stale/foreign id — ignore (last-write-wins, no cross-tenant edit)

                row.Text = input.Text.Trim();
                row.Prompt = input.Prompt;
                row.IsActive = input.IsActive;
                row.SortOrder = input.SortOrder;
                _unitOfWork.Repository<PredefinedAction>().Update(row);
                keptIds.Add(row.Id);
            }
            else
            {
                await _unitOfWork.Repository<PredefinedAction>().AddAsync(new PredefinedAction
                {
                    OwnerId = owner,
                    ProjectId = projectId,
                    UserId = null,
                    Text = input.Text.Trim(),
                    Prompt = input.Prompt,
                    IsActive = input.IsActive,
                    SortOrder = input.SortOrder
                });
            }
        }

        // Soft-delete rows absent from the payload.
        foreach (var row in existing.Where(a => !keptIds.Contains(a.Id)))
        {
            row.DeletedAt = DateTime.UtcNow;
            _unitOfWork.Repository<PredefinedAction>().Update(row);
        }

        return Result.Success();
    }

    // Upserts a ProjectAppUrl row for the tenant's "local" AppEnvironment — prefers the tenant's
    // own "local" if it ever creates one, else falls back to the super-admin-seeded global one.
    // Keeps the legacy single Project.AppUrl field and the new per-environment table in sync so
    // ExtensionService.FindProjectForOriginAsync (which reads the new table) never regresses for
    // callers that still only set Project.AppUrl (the extension, the old dashboard dialog).
    private async Task<Result> SyncPrimaryAppUrlAsync(Project project, string appUrl)
    {
        var owner = project.OwnerId;
        var defaultEnv = await _unitOfWork.Repository<Domain.Entity.AppEnvironment>()
            .Query()
            .Where(e => e.DeletedAt == null && e.Name == "local" && (e.OwnerId == owner || e.OwnerId == null))
            .OrderByDescending(e => e.OwnerId != null) // tenant's own "local" wins over the global one
            .FirstOrDefaultAsync();
        if (defaultEnv == null)
            return Result.Success(); // no "local" environment exists (e.g. it was deleted) — nothing to sync

        var existing = await _unitOfWork.Repository<ProjectAppUrl>()
            .Query()
            .Where(u => u.ProjectId == project.Id && u.AppEnvironmentId == defaultEnv.Id && u.DeletedAt == null)
            .FirstOrDefaultAsync();

        if (existing != null)
        {
            existing.Url = appUrl;
            _unitOfWork.Repository<ProjectAppUrl>().Update(existing);
        }
        else
        {
            await _unitOfWork.Repository<ProjectAppUrl>().AddAsync(new ProjectAppUrl
            {
                ProjectId = project.Id,
                AppEnvironmentId = defaultEnv.Id,
                Url = appUrl,
                OwnerId = owner
            });
        }

        await _unitOfWork.SaveChangesAsync();
        return Result.Success();
    }

    private async Task<List<PredefinedAction>> LoadProjectActionsAsync(int projectId) =>
        await _unitOfWork.Repository<PredefinedAction>()
            .Query()
            .AsNoTracking()
            .Where(a => a.DeletedAt == null && a.ProjectId == projectId)
            .OrderBy(a => a.SortOrder)
            .ToListAsync();

    private async Task<List<ProjectAppUrl>> LoadProjectAppUrlsAsync(int projectId) =>
        await _unitOfWork.Repository<ProjectAppUrl>()
            .Query()
            .AsNoTracking()
            .Include(u => u.AppEnvironment)
            .Where(u => u.DeletedAt == null && u.ProjectId == projectId)
            .ToListAsync();

    public async Task<Result<int>> EnsureAsync(string key)
    {
        var keyNormalized = key.Trim().ToLower();

        // Super admins never own or create projects (ProjectService.CreateAsync forbids it) — they
        // resolve NO project by key, full stop. Explicit, rather than relying on
        // TenantStamp.OwnerFor(_currentUser) incidentally being null for them too: that null also
        // means "a real stakeholder with no tenant of their own," a distinct, still-legitimate case
        // (see NullOwnerProject_ActionResolvesOnCommentCreate) that must keep resolving null-owner
        // rows below — conflating the two by relying on the same null sentinel would be exactly the
        // kind of fragile special-casing this method used to get wrong.
        if (_currentUser.IsSuperAdmin)
            return Result<int>.NotFound(MessageKeys.Project.NotFound);

        // Resolve by the caller's own scope. Widget stakeholders are registered UNDER the project's
        // owner, so their OwnerFor (= tenant) equals the project's owner. Never key-only: that would
        // resolve other tenants' projects (the explicit OwnerId match is belt-and-suspenders alongside
        // the EF query filter, which is the primary tenant boundary).
        var ownerId = TenantStamp.OwnerFor(_currentUser);
        var project = await _unitOfWork.Repository<Project>()
            .Query()
            .AsNoTracking()
            .Where(p => p.DeletedAt == null && p.Key == keyNormalized && p.OwnerId == ownerId)
            .Select(p => new { p.Id, p.IsActiveLocal, p.IsActiveStaging, p.IsActiveProduction })
            .FirstOrDefaultAsync();

        // STRICT: projects must be pre-defined in the dashboard. No lazy self-create.
        // Missing → NotFound (widget hides silently on 404). Disabled → Conflict (below).
        if (project == null)
            return Result<int>.NotFound(MessageKeys.Project.NotFound);

        // "Fully inactive" = disabled in every environment — parity with the old single IsActive
        // flag, for every caller that has no particular environment in scope (predefined actions,
        // extension listing, export/import, capture-config, stack registration). A project that's
        // merely PARTIALLY active (disabled in only some environments) still resolves here; only
        // the environment-aware overload below can catch that finer-grained case.
        if (!(project.IsActiveLocal || project.IsActiveStaging || project.IsActiveProduction))
            return Result<int>.Conflict(MessageKeys.Project.Disabled);

        return Result<int>.Success(project.Id);
    }

    public async Task<Result<int>> EnsureAsync(string key, EnvironmentTag environment)
    {
        var baseResult = await EnsureAsync(key);
        if (!baseResult.IsSuccess)
            return baseResult;

        // Re-select just the 3 flags for the specific-environment check — the project is already
        // known to exist and be resolvable to this tenant from the base call above.
        var keyNormalized = key.Trim().ToLower();
        var ownerId = TenantStamp.OwnerFor(_currentUser);
        var flags = await _unitOfWork.Repository<Project>()
            .Query()
            .AsNoTracking()
            .Where(p => p.DeletedAt == null && p.Key == keyNormalized && p.OwnerId == ownerId)
            .Select(p => new { p.IsActiveLocal, p.IsActiveStaging, p.IsActiveProduction })
            .FirstOrDefaultAsync();

        var activeForEnvironment = environment switch
        {
            EnvironmentTag.Local => flags!.IsActiveLocal,
            EnvironmentTag.Staging => flags!.IsActiveStaging,
            EnvironmentTag.Production => flags!.IsActiveProduction,
            _ => true,
        };

        if (!activeForEnvironment)
            return Result<int>.Conflict(MessageKeys.Project.Disabled);

        return baseResult;
    }

    public async Task<Result<CaptureConfigResponse>> GetCaptureConfigAsync(string key, string? origin = null)
    {
        var projectResult = await EnsureAsync(key);
        if (!projectResult.IsSuccess)
            return projectResult.IsConflict
                ? Result<CaptureConfigResponse>.Conflict(projectResult.Message ?? MessageKeys.Project.Disabled)
                : Result<CaptureConfigResponse>.NotFound(projectResult.Message ?? MessageKeys.Project.NotFound);

        var info = await _unitOfWork.Repository<Project>()
            .Query()
            .AsNoTracking()
            .Where(p => p.Id == projectResult.Data)
            .Select(p => new { p.PageContextCaptureEnabled, p.CaptureTextContent, p.Name, p.EnvironmentSelectorRoleIds, p.CommitStyle, p.CreatedBy, p.OwnerId })
            .FirstAsync();

        // R4-01: the widget's "Add more fields" panel is driven by the PROJECT's workspace
        // definitions — enabled only, resolved server-side from the owner.
        var commentFieldDefs = await _commentFields.GetDefinitionsForOwnerAsync(info.OwnerId, enabledOnly: true);

        return Result<CaptureConfigResponse>.Success(new CaptureConfigResponse
        {
            Id = projectResult.Data,
            // Resolved from THIS request's origin, so the same built file reports `staging` on
            // staging and `production` on production without being rebuilt.
            ResolvedEnvironment = await ResolveEnvironmentAsync(projectResult.Data, origin),
            PageContextCaptureEnabled = info.PageContextCaptureEnabled,
            CaptureTextContent = info.CaptureTextContent,
            Name = info.Name,
            ShowEnvironmentSelector = ShowEnvironmentSelectorFor(info.EnvironmentSelectorRoleIds),
            CommitStyle = info.CommitStyle,
            // Same gate as UpdateAsync (line ~192) — the widget hides/disables the commit-style
            // control entirely for a caller who couldn't actually save a change to it.
            CanEditSettings = _currentUser.IsAdmin || info.CreatedBy == _currentUser.Id,
            CommentFields = commentFieldDefs.Select(CommentFieldDefinitionDto.FromDomain).ToList(),
        });
    }

    public async Task<Result<ProjectStackResponse>> SetStackAsync(string key, SetProjectStackRequest request)
    {
        var projectResult = await EnsureAsync(key);
        if (!projectResult.IsSuccess)
            return projectResult.IsConflict
                ? Result<ProjectStackResponse>.Conflict(projectResult.Message ?? MessageKeys.Project.Disabled)
                : Result<ProjectStackResponse>.NotFound(projectResult.Message ?? MessageKeys.Project.NotFound);

        var project = await _unitOfWork.Repository<Project>().GetByIdAsync(projectResult.Data);
        if (project == null) return Result<ProjectStackResponse>.NotFound(MessageKeys.Project.NotFound);

        var changed = false;

        // frontend/backend: the LATEST non-empty detection wins. Stacks migrate (this product's own
        // dashboard went Angular → React) and a write-once record silently kept reporting the old
        // framework to `init`, the stack insights and the landing page. An empty detection — `init`
        // run from a repo root with no package.json, say — is ignored so it can never wipe a real
        // record, which keeps the call safe regardless of which developer/machine triggers it.
        var hasFrontend = request.Frontend is { Count: > 0 };
        var hasBackend = request.Backend is { Count: > 0 };
        if (hasFrontend || hasBackend)
        {
            var next = JsonSerializer.Serialize(new { frontend = request.Frontend ?? new List<string>(), backend = request.Backend });
            if (!string.Equals(project.TechStack, next, StringComparison.Ordinal))
            {
                project.TechStack = next;
                changed = true;
            }
        }

        // aiTool: append-if-new to the growing set — a project can legitimately be touched by more
        // than one AI tool over its lifetime, so this is never write-once.
        var aiTools = ParseStringList(project.AiToolsUsed);
        if (!string.IsNullOrWhiteSpace(request.AiTool))
        {
            var normalized = request.AiTool.Trim().ToLowerInvariant();
            if (!aiTools.Contains(normalized))
            {
                aiTools.Add(normalized);
                project.AiToolsUsed = JsonSerializer.Serialize(aiTools);
                changed = true;
            }
        }

        if (changed)
        {
            _unitOfWork.Repository<Project>().Update(project);
            await _unitOfWork.SaveChangesAsync();
        }

        return Result<ProjectStackResponse>.Success(BuildStackResponse(project.TechStack, project.AiToolsUsed));
    }

    public async Task<Result<ProjectStackResponse>> GetStackAsync(string key)
    {
        var projectResult = await EnsureAsync(key);
        if (!projectResult.IsSuccess)
            return projectResult.IsConflict
                ? Result<ProjectStackResponse>.Conflict(projectResult.Message ?? MessageKeys.Project.Disabled)
                : Result<ProjectStackResponse>.NotFound(projectResult.Message ?? MessageKeys.Project.NotFound);

        var info = await _unitOfWork.Repository<Project>()
            .Query()
            .AsNoTracking()
            .Where(p => p.Id == projectResult.Data)
            .Select(p => new { p.TechStack, p.AiToolsUsed })
            .FirstAsync();

        return Result<ProjectStackResponse>.Success(BuildStackResponse(info.TechStack, info.AiToolsUsed));
    }

    // Anonymous, cross-tenant aggregate — the only method on this service that bypasses the tenant
    // query filter. Returns anonymized counts only; never project names, tenant IDs, or emails.
    public async Task<Result<StacksSummaryResponse>> GetStacksSummaryAsync()
    {
        var projects = await _unitOfWork.Repository<Project>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => p.DeletedAt == null && (p.IsActiveLocal || p.IsActiveStaging || p.IsActiveProduction)
                && (p.TechStack != null || p.AiToolsUsed != null))
            .Select(p => new { p.TechStack, p.AiToolsUsed })
            .ToListAsync();

        var summary = new StacksSummaryResponse { TotalProjects = projects.Count };

        foreach (var p in projects)
        {
            var stack = BuildStackResponse(p.TechStack, p.AiToolsUsed);
            foreach (var token in stack.Frontend ?? new List<string>())
                summary.Frontend[token] = summary.Frontend.GetValueOrDefault(token) + 1;
            foreach (var token in stack.Backend ?? new List<string>())
                summary.Backend[token] = summary.Backend.GetValueOrDefault(token) + 1;
            foreach (var tool in stack.AiTools)
                summary.AiTools[tool] = summary.AiTools.GetValueOrDefault(tool) + 1;
        }

        return Result<StacksSummaryResponse>.Success(summary);
    }

    public async Task<Result<WidgetActivationResponse>> CheckWidgetActiveAsync(string key, string? origin)
    {
        var keyNormalized = key.Trim().ToLower();

        // Anonymous, pre-auth path — no tenant claim, so the global query filter would hide every
        // tenant's rows. Bypass with IgnoreQueryFilters() and scope manually, same as AuthService's
        // anonymous registration lookup. Project keys are unique only per (key, owner_id): fetch up
        // to two matches and treat an ambiguous key the same as "not active" — a bare FirstOrDefault
        // would arbitrarily bind this check to the WRONG tenant's project on a key collision.
        var projectMatches = await _unitOfWork.Repository<Project>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => p.DeletedAt == null && p.Key == keyNormalized)
            .Select(p => new { p.Id, p.IsActiveLocal, p.IsActiveStaging, p.IsActiveProduction })
            .Take(2)
            .ToListAsync();

        if (projectMatches.Count != 1)
            return Result<WidgetActivationResponse>.Success(new WidgetActivationResponse { Active = false });

        var project = projectMatches[0];
        if (!(project.IsActiveLocal || project.IsActiveStaging || project.IsActiveProduction))
            return Result<WidgetActivationResponse>.Success(new WidgetActivationResponse { Active = false });

        if (string.IsNullOrWhiteSpace(origin))
            return Result<WidgetActivationResponse>.Success(new WidgetActivationResponse { Active = true });

        var normalized = OriginNormalizer.Normalize(origin);
        
        // Rows on DISABLED environments are included deliberately — see the block below.
        var urls = await _unitOfWork.Repository<ProjectAppUrl>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(u => u.AppEnvironment)
            .Where(u => u.ProjectId == project.Id && u.DeletedAt == null)
            .Select(u => new { u.Url, u.IsActive, EnvironmentEnabled = u.AppEnvironment.IsEnabled })
            .ToListAsync();

        // No configured mapping for this origin → not blocked. Most projects never configure "other
        // environments" at all, and a site nobody described is not a site anybody turned off.
        //
        // A mapping that DOES describe this origin blocks it when either the mapping itself is
        // deactivated, or the environment it belongs to is disabled. Disabling an environment takes
        // the widget off the sites that environment describes, and only those: production's origin
        // matches production's own row, so turning off staging cannot reach it.
        //
        // This reverses the original Decision 7 ("a disabled environment is treated as if the row
        // did not exist"), on the product owner's call: disabling an environment should mean the
        // widget stops appearing there, not that the setting is quietly ignored.
        var match = urls.FirstOrDefault(u => OriginNormalizer.Normalize(u.Url) == normalized);
        var active = match == null || (match.IsActive && match.EnvironmentEnabled);
        return Result<WidgetActivationResponse>.Success(new WidgetActivationResponse { Active = active });
    }

    private static ProjectStackResponse BuildStackResponse(string? techStack, string? aiToolsUsed)
    {
        List<string>? frontend = null;
        List<string>? backend = null;

        if (!string.IsNullOrEmpty(techStack))
        {
            try
            {
                using var doc = JsonDocument.Parse(techStack);
                if (doc.RootElement.TryGetProperty("frontend", out var f) && f.ValueKind == JsonValueKind.Array)
                    frontend = f.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();
                if (doc.RootElement.TryGetProperty("backend", out var b) && b.ValueKind == JsonValueKind.Array)
                    backend = b.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();
            }
            catch (JsonException)
            {
                // Corrupt row (should never happen via SetStackAsync's own writes) — surface as
                // unset rather than 500.
            }
        }

        return new ProjectStackResponse { Frontend = frontend, Backend = backend, AiTools = ParseStringList(aiToolsUsed) };
    }

    private static List<string> ParseStringList(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return new List<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(raw) ?? new List<string>();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }

    // Batch-resolve creator display names (Project.CreatedBy is a User.PublicId). One query.
    private Task<Dictionary<Guid, string>> ResolveCreatorNamesAsync(IEnumerable<Guid> ids) =>
        UserNameResolver.ResolveAsync(_unitOfWork, ids);

    private async Task<string?> ResolveCreatorNameAsync(Guid id) =>
        (await ResolveCreatorNamesAsync(new[] { id })).GetValueOrDefault(id);

    private static ProjectActivationState ComputeActivationState(bool local, bool staging, bool production)
    {
        if (local && staging && production) return ProjectActivationState.Active;
        if (!local && !staging && !production) return ProjectActivationState.Inactive;
        return ProjectActivationState.Partial;
    }

    private static List<int>? ParseRoleIds(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            var list = JsonSerializer.Deserialize<List<int>>(raw);
            return list is { Count: > 0 } ? list : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the CURRENT caller should see the widget's environment switcher for this project.
    /// Unconfigured (null/empty EnvironmentSelectorRoleIds) → everyone except Client (QuickAccess)
    /// roles. Configured → only the listed Role.Id values (a caller with no resolvable RoleId,
    /// which shouldn't happen for a real authenticated user, is excluded rather than guessed at).
    /// </summary>
    private bool ShowEnvironmentSelectorFor(string? environmentSelectorRoleIds)
    {
        var roleIds = ParseRoleIds(environmentSelectorRoleIds);
        if (roleIds == null)
            return !_currentUser.IsQuickAccess;
        return _currentUser.RoleId.HasValue && roleIds.Contains(_currentUser.RoleId.Value);
    }

    private ProjectResponse MapToResponse(Project project, List<PredefinedAction> actions, List<ProjectAppUrl> appUrls, int commentsCount, string? createdByName)
    {
        var canEdit = _currentUser.IsAdmin || project.CreatedBy == _currentUser.Id;
        var canDelete = _currentUser.IsAdmin || (project.CreatedBy == _currentUser.Id && commentsCount == 0);

        return new ProjectResponse
        {
            Id = project.Id,
            Key = project.Key,
            Name = project.Name,
            IsActiveLocal = project.IsActiveLocal,
            IsActiveStaging = project.IsActiveStaging,
            IsActiveProduction = project.IsActiveProduction,
            ActivationState = ComputeActivationState(project.IsActiveLocal, project.IsActiveStaging, project.IsActiveProduction),
            AppUrl = project.AppUrl,
            AppUrls = appUrls.OrderBy(u => u.AppEnvironment?.Name).Select(u => new ProjectAppUrlResponse
            {
                AppEnvironmentId = u.AppEnvironmentId,
                EnvironmentName = u.AppEnvironment?.Name ?? "",
                Url = u.Url,
                IsActive = u.IsActive,
                EnvironmentIsEnabled = u.AppEnvironment?.IsEnabled ?? true
            }).ToList(),
            PageContextCaptureEnabled = project.PageContextCaptureEnabled,
            EnforceAllowedOrigins = project.EnforceAllowedOrigins,
            CaptureTextContent = project.CaptureTextContent,
            EnvironmentSelectorRoleIds = ParseRoleIds(project.EnvironmentSelectorRoleIds),
            CommitStyle = project.CommitStyle,
            PredefinedActions = actions
                .OrderBy(a => a.SortOrder)
                .Select(a => new PredefinedActionResponse
                {
                    Id = a.Id,
                    ProjectId = a.ProjectId,
                    Text = a.Text,
                    Prompt = a.Prompt,
                    IsActive = a.IsActive,
                    SortOrder = a.SortOrder
                })
                .ToList(),
            CreatedByName = createdByName,
            CommentsCount = commentsCount,
            CanEdit = canEdit,
            CanDelete = canDelete
        };
    }
}
