using System.Linq;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Comment;
using Pointer.Application.DTOs.Notification;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Domain.ValueObjects;

namespace Pointer.Application.Services.Implementation;

public class CommentService : ICommentService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IProjectService _projectService;
    private readonly IPredefinedActionService _predefinedActions;
    private readonly IFileStorage _fileStorage;
    private readonly ICurrentUser _currentUser;
    private readonly IUploadSigner _uploadSigner;
    private readonly ISettingsService _settings;
    private readonly IEntitlementService _entitlements;
    private readonly INotificationService _notificationService;
    private readonly ICommentFieldService _commentFields;
    private readonly IMembershipService _memberships;
    private readonly IAuditWriter _audit;

    private readonly ICurrentClient? _currentClient;

    public CommentService(
        IUnitOfWork unitOfWork,
        IProjectService projectService,
        IPredefinedActionService predefinedActions,
        IFileStorage fileStorage,
        ICurrentUser currentUser,
        IUploadSigner uploadSigner,
        ISettingsService settings,
        IEntitlementService entitlements,
        ICurrentClient? currentClient = null,
        INotificationService? notificationService = null,
        ICommentFieldService? commentFields = null,
        IMembershipService? memberships = null,
        IAuditWriter? audit = null)
    {
        _unitOfWork = unitOfWork;
        _projectService = projectService;
        _predefinedActions = predefinedActions;
        _fileStorage = fileStorage;
        _currentUser = currentUser;
        _uploadSigner = uploadSigner;
        _settings = settings;
        _entitlements = entitlements;
        _currentClient = currentClient;
        _audit = audit ?? NoopAuditWriter.Instance;
        _notificationService = notificationService ?? new NotificationService(unitOfWork, currentUser);
        // Mirrors ProjectService: forward the (possibly real) writer instead of letting
        // CommentFieldService silently fall back to Noop when this constructs its own instance
        // (review finding #9).
        _commentFields = commentFields ?? new CommentFieldService(unitOfWork, currentUser, _audit);
        _memberships = memberships ?? new MembershipService(unitOfWork);
    }

    public async Task<Result<CommentResponse>> CreateAsync(string projectKey, CreateCommentRequest request, Guid authorId, string? origin = null)
    {
        // Super admins are platform-management only — they never leave comments under their own
        // identity. Someone who wants to use the product signs in with a real tenant account instead.
        if (_currentUser.IsSuperAdmin)
            return Result<CommentResponse>.Forbidden(MessageKeys.Comment.SuperAdminNotAllowed);

        // Resolve the project first, WITHOUT the environment gate, because the environment may not
        // be known yet — resolving it needs the project's registered URLs.
        var projectResult = await _projectService.EnsureAsync(projectKey);
        if (!projectResult.IsSuccess)
            return projectResult.IsConflict
                ? Result<CommentResponse>.Conflict(projectResult.Message ?? MessageKeys.Project.Disabled)
                : Result<CommentResponse>.NotFound(projectResult.Message ?? MessageKeys.Project.NotFound);

        // An absent environment (the enum's 0) means "you work it out" — the widget no longer bakes
        // one into the page. A widget that does send one is believed, which keeps every existing
        // install behaving exactly as before and leaves `fixed-environment` meaningful.
        var environment = request.Environment == EnvironmentTag.Unknown
            ? await _projectService.ResolveEnvironmentAsync(projectResult.Data, origin)
            : request.Environment;

        // Environment-aware gate: a project deactivated for THIS environment specifically (even
        // while still active for others) must not accept a new comment tagged with it.
        //
        // Skipped when the environment is Unknown, and deliberately so: an origin nobody registered
        // is not yet governed by the per-environment switches, and refusing it would mean feedback
        // silently disappears from a deployment whose URL someone simply forgot to add. Projects
        // that want unregistered origins refused already have that control — it is the opt-in
        // allowed-origins enforcement checked immediately below, which is what "only these URLs may
        // comment" actually means.
        if (environment != EnvironmentTag.Unknown)
        {
            var gated = await _projectService.EnsureAsync(projectKey, environment);
            if (!gated.IsSuccess)
                return gated.IsConflict
                    ? Result<CommentResponse>.Conflict(gated.Message ?? MessageKeys.Project.Disabled)
                    : Result<CommentResponse>.NotFound(gated.Message ?? MessageKeys.Project.NotFound);
        }

        // Origin allow-list (R1-05). Opt-in per project; a project that never enabled it is
        // unaffected. Runs after the project resolves so we know which rows to match against.
        if (!await _projectService.IsOriginAllowedAsync(
                projectResult.Data, origin, environment, _currentUser.IsQuickAccess))
            return Result<CommentResponse>.Forbidden(MessageKeys.Project.OriginNotAllowed);

        // Stamp OwnerId from the PROJECT's tenant: a comment belongs to whoever owns
        // the project, regardless of who authored it. This is correct even when a super
        // admin comments on a tenant-owned project (OwnerFor(caller) would wrongly be null).
        var projectInfo = await _unitOfWork.Repository<Project>().Query()
            .Where(p => p.Id == projectResult.Data)
            .Select(p => new { p.OwnerId, p.PageContextCaptureEnabled, p.CaptureTextContent })
            .FirstAsync();
        var projectOwnerId = projectInfo.OwnerId;

        // Admin-defined fields (R4-01): validated against the PROJECT's workspace definitions —
        // the owner is always derived server-side, never from the request. enabledOnly here: on
        // create a disabled definition behaves like an unknown key (refused); the PATCH path
        // loads all definitions so it can say "disabled" instead.
        var fieldDefs = await _commentFields.GetDefinitionsForOwnerAsync(projectOwnerId, enabledOnly: true);
        var fieldValues = _commentFields.ValidateValues(fieldDefs, request.CustomFields);
        if (!fieldValues.IsSuccess)
            return Result<CommentResponse>.Failure(fieldValues.Message!);

        // Enforce the demo comment cap for demo tenants. A per-tenant override wins; otherwise
        // the global super-admin-tunable setting (default 10) applies.
        if (projectOwnerId is Guid owner)
        {
            // DB-11a: the founding admin's public_id no longer equals the workspace id — resolve
            // via the workspace's current admin membership instead of users.public_id == owner.
            var currentAdmin = await _memberships.CurrentAdminAsync(owner);
            var demoOwner = currentAdmin?.User.IsDemo == true
                ? new { currentAdmin.User.DemoCommentCapOverride }
                : null;

            if (demoOwner != null)
            {
                var cap = demoOwner.DemoCommentCapOverride
                    ?? await _settings.GetIntAsync(ISettingsService.DemoCommentCap, 10);
                var count = await _unitOfWork.Repository<Comment>()
                    .Query()
                    .IgnoreQueryFilters()
                    .CountAsync(c => c.OwnerId == owner && c.DeletedAt == null);

                if (count >= cap)
                    return Result<CommentResponse>.Failure($"Demo limit reached: a demo workspace allows at most {cap} comments.");
            }
        }

        // MaxCommentsPerMonth (plan cap): count this month's active comments owned by the PROJECT owner
        // (a comment counts against whoever owns the project, not the author). COEXISTS with the demo cap
        // above — both run; the tighter one wins. Grandfather-safe: checked only on create.
        if (projectOwnerId is Guid planOwner)
        {
            var now = DateTime.UtcNow;
            var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var monthCount = await _unitOfWork.Repository<Comment>()
                .Query()
                .IgnoreQueryFilters()
                .CountAsync(c => c.OwnerId == planOwner && c.DeletedAt == null && c.CreatedAt >= monthStart);
            var check = await _entitlements.CheckCountAsync(planOwner, EntitlementCatalog.MaxCommentsPerMonth, monthCount);
            if (!check.IsSuccess)
                return Result<CommentResponse>.LimitReached(check.Message ?? MessageKeys.Plan.LimitReached, check.Limit!);
        }

        var comment = new Comment
        {
            ProjectId = projectResult.Data,
            Environment = environment,
            Status = CommentStatus.Open,
            AuthorId = authorId,
            Body = request.Body.Trim(),
            // Advisory only, computed server-side on every write so a client cannot set or clear it.
            PayloadFlags = PayloadFlagDetector.Detect(request.Body).ToList(),
            HasPayloadFlag = PayloadFlagDetector.Detect(request.Body).Count > 0,
            IsPrivate = request.IsPrivate,
            OwnerId = projectOwnerId,
            Element = MapToEntity(request.Element),
            IsBugReport = request.IsBugReport,
            Language = NormalizeLanguage(request.Language),
            CustomFields = fieldValues.Data!
        };
        comment.Element.Snapshot = SnapshotSanitizer.Sanitize(request.Element.Snapshot, projectInfo.CaptureTextContent);
        if (!projectInfo.CaptureTextContent && !string.IsNullOrEmpty(comment.Element.PageTitle))
        {
            comment.Element.PageTitle = "•••";
        }

        // Page context (console/network) is only ever persisted when BOTH the comment is flagged
        // AND the owning project has the feature enabled — the widget hiding the checkbox is a UX
        // optimization, not the security boundary. Console/network data is per-PAGE, not per-comment:
        // dedup against an existing snapshot for the same (project, route, environment, session)
        // before creating a new one, so multiple bug reports on the same page/visit share one row.
        if (request.IsBugReport && projectInfo.PageContextCaptureEnabled
            && request.PageContext is { } capture && !string.IsNullOrWhiteSpace(capture.SessionId))
        {
            var route = NormalizeRoute(request.Element.Route);
            var pageContext = await _unitOfWork.Repository<PageContextSnapshot>()
                .Query()
                .Where(s => s.ProjectId == projectResult.Data
                         && s.Route == route
                         && s.Environment == environment
                         && s.SessionId == capture.SessionId
                         && s.DeletedAt == null)
                .FirstOrDefaultAsync();

            if (pageContext == null)
            {
                pageContext = new PageContextSnapshot
                {
                    ProjectId = projectResult.Data,
                    Environment = environment,
                    Route = route,
                    SessionId = capture.SessionId,
                    OwnerId = projectOwnerId
                };
                MergePageContext(pageContext, capture);
                pageContext.LastEventAt = DateTime.UtcNow;
                await _unitOfWork.Repository<PageContextSnapshot>().AddAsync(pageContext);
            }
            else
            {
                MergePageContext(pageContext, capture);
                pageContext.LastEventAt = DateTime.UtcNow;
                _unitOfWork.Repository<PageContextSnapshot>().Update(pageContext);
            }

            comment.PageContextSnapshot = pageContext;
        }

        // Optional predefined actions (multi-select): validate each is active + in-scope for the
        // resolved project's tenant + this author, then SNAPSHOT {text, prompt} onto the comment
        // (never an FK). Any invalid/out-of-scope id rejects the request — not silently dropped.
        if (request.PredefinedActionIds is { Count: > 0 } actionIds)
        {
            foreach (var actionId in actionIds.Distinct())
            {
                // projectOwnerId may be null (global/null-owner project); the action's owner matches it.
                var action = await _predefinedActions.ResolveInScopeAsync(actionId, projectResult.Data, projectOwnerId, authorId);
                if (action == null)
                    return Result<CommentResponse>.Failure(MessageKeys.Comment.InvalidPredefinedAction);

                comment.PickedActions.Add(new CommentPickedAction { Text = action.Text, Prompt = action.Prompt });
            }
        }

        await _unitOfWork.Repository<Comment>().AddAsync(comment);
        await _unitOfWork.SaveChangesAsync();

        // Server emission: first_comment. Race-safe by constraint.
        try
        {
            var firstCommentEvent = new UsageEvent
            {
                Type = "first_comment",
                Source = "api",
                ProjectId = projectResult.Data,
                OwnerId = projectOwnerId,
                CreatedAt = DateTime.UtcNow
            };
            _unitOfWork.UsageEvents.Add(firstCommentEvent);
            await _unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" } ||
            (ex.InnerException != null && ex.InnerException.GetType().Name == "SqliteException" && (int)ex.InnerException.GetType().GetProperty("SqliteErrorCode")!.GetValue(ex.InnerException)! == 19))
        {
            // Unique violation on the partial index -> someone else was first. Swallow.
            // Clear the change tracker so the failed Added entry doesn't replay on the next SaveChangesAsync.
            _unitOfWork.ClearChangeTracker();
        }

        var names = await ResolveNamesAsync(AuthorIds(comment));
        return Result<CommentResponse>.Success(MapToResponse(comment, names, fieldDefs: fieldDefs), MessageKeys.Comment.Created);
    }

    // Shared, security-sensitive query base for both ListAsync and ListSummaryAsync — status/
    // environment filters and the quick-access (Client) restriction. Deliberately factored out so
    // the lean summary projection can never drift from the full list's access rules. Callers still
    // apply the private-comment visibility filter themselves (they need the hidden-count computed
    // from the query BEFORE that filter is applied).
    private IQueryable<Comment> BuildCommentQuery(int projectId, CommentFilter filter, Guid callerId)
    {
        var query = _unitOfWork.Repository<Comment>()
            .Query()
            .AsNoTracking()
            .Where(c => c.ProjectId == projectId && c.DeletedAt == null);

        if (filter.Status.HasValue)
            query = query.Where(c => c.Status == filter.Status.Value);

        if (filter.Environment.HasValue)
            query = query.Where(c => c.Environment == filter.Environment.Value);

        if (filter.Flagged == true)
            query = query.Where(c => c.HasPayloadFlag);

        // "Live" only makes sense for applied comments: applied + DeployedAt set is live, applied
        // + not deployed is the "applied but not live" triage view.
        if (filter.Live.HasValue)
            query = filter.Live.Value
                ? query.Where(c => c.AppliedAt != null && c.DeployedAt != null)
                : query.Where(c => c.AppliedAt != null && c.DeployedAt == null);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToLower();
            query = query.Where(c => c.Body.ToLower().Contains(term));
        }

        // A quick-access (Client) account only ever sees its own feedback — never the rest of the
        // project's backlog — regardless of status. Every other role keeps seeing everything.
        if (_currentUser.IsQuickAccess)
            query = query.Where(c => c.AuthorId == callerId);

        return query;
    }

    public async Task<Result<PagedData<CommentListItemDto>>> ListAsync(string projectKey, CommentFilter filter, Guid callerId)
    {
        var projectResult = await _projectService.EnsureAsync(projectKey);
        if (!projectResult.IsSuccess)
            return projectResult.IsConflict
                ? Result<PagedData<CommentListItemDto>>.Conflict(projectResult.Message ?? MessageKeys.Project.Disabled)
                : Result<PagedData<CommentListItemDto>>.NotFound(projectResult.Message ?? MessageKeys.Project.NotFound);

        var projectId = projectResult.Data;

        // R4-01: field definitions are loaded ONCE per request, not per row.
        var fieldDefs = await LoadFieldDefinitionsAsync(projectId);

        IQueryable<Comment> query = BuildCommentQuery(projectId, filter, callerId).Include(c => c.Replies);

        // Count private comments owned by someone else: hidden from this caller
        // (computed over the same status/environment filters, before visibility).
        var hiddenPrivateCount = await query
            .CountAsync(c => c.IsPrivate && c.AuthorId != callerId);

        // Visibility: a private comment is only ever returned to its author.
        // Admins get NO bypass.
        query = query.Where(c => !c.IsPrivate || c.AuthorId == callerId);

        var totalItems = await query.CountAsync();

        var pageSize = Math.Min(filter.PageSize, 100);
        var pageNumber = filter.PageNumber < 1 ? 1 : filter.PageNumber;
        var totalPages = totalItems == 0 ? 0 : (int)Math.Ceiling((double)totalItems / pageSize);

        var items = await query
            .OrderByDescending(c => c.CreatedAt)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var pagination = new Pagination
        {
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalItems = totalItems,
            TotalPages = totalPages
        };

        var names = await ResolveNamesAsync(items.SelectMany(AuthorIds));
        var pageContexts = await LoadPageContextsAsync(items.Select(c => c.PageContextSnapshotId));
        return Result<PagedData<CommentListItemDto>>.Success(
            new PagedData<CommentListItemDto>(items.Select(c => MapToListItem(c, names, fieldDefs)).ToList(), pagination, hiddenPrivateCount, pageContexts));
    }

    public async Task<Result<PagedData<CommentSummaryDto>>> ListSummaryAsync(string projectKey, CommentFilter filter, Guid callerId)
    {
        var projectResult = await _projectService.EnsureAsync(projectKey);
        if (!projectResult.IsSuccess)
            return projectResult.IsConflict
                ? Result<PagedData<CommentSummaryDto>>.Conflict(projectResult.Message ?? MessageKeys.Project.Disabled)
                : Result<PagedData<CommentSummaryDto>>.NotFound(projectResult.Message ?? MessageKeys.Project.NotFound);

        var projectId = projectResult.Data;

        // No .Include(Replies) here — the summary shape never carries them.
        var query = BuildCommentQuery(projectId, filter, callerId);

        var hiddenPrivateCount = await query
            .CountAsync(c => c.IsPrivate && c.AuthorId != callerId);

        query = query.Where(c => !c.IsPrivate || c.AuthorId == callerId);

        var totalItems = await query.CountAsync();

        var pageSize = Math.Min(filter.PageSize, 100);
        var pageNumber = filter.PageNumber < 1 ? 1 : filter.PageNumber;
        var totalPages = totalItems == 0 ? 0 : (int)Math.Ceiling((double)totalItems / pageSize);

        // Project straight to the lean shape — Route/SourcePath are the only Element sub-fields
        // touched, skipping Snapshot/ComputedStyles/AppliedCssRules/ParentInfo entirely.
        var rows = await query
            .OrderByDescending(c => c.CreatedAt)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new
            {
                c.Id,
                c.Status,
                c.Environment,
                c.Body,
                c.CreatedAt,
                c.AuthorId,
                Route = c.Element.Route,
                SourcePath = c.Element.SourcePath,
                c.Language
            })
            .ToListAsync();

        var pagination = new Pagination
        {
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalItems = totalItems,
            TotalPages = totalPages
        };

        var names = await ResolveNamesAsync(rows.Select(r => r.AuthorId));
        var items = rows.Select(r => new CommentSummaryDto
        {
            Id = r.Id,
            Status = r.Status,
            Environment = r.Environment,
            Body = r.Body,
            CreatedAt = r.CreatedAt,
            Route = r.Route,
            SourcePath = r.SourcePath,
            AuthorName = names.GetValueOrDefault(r.AuthorId),
            Language = r.Language
        }).ToList();

        return Result<PagedData<CommentSummaryDto>>.Success(new PagedData<CommentSummaryDto>(items, pagination, hiddenPrivateCount));
    }

    public async Task<Result<PagedData<CommentApplyItemDto>>> ListApplyQueueAsync(string projectKey, CommentFilter filter)
    {
        var projectResult = await _projectService.EnsureAsync(projectKey);
        if (!projectResult.IsSuccess)
            return projectResult.IsConflict
                ? Result<PagedData<CommentApplyItemDto>>.Conflict(projectResult.Message ?? MessageKeys.Project.Disabled)
                : Result<PagedData<CommentApplyItemDto>>.NotFound(projectResult.Message ?? MessageKeys.Project.NotFound);

        var projectId = projectResult.Data;

        // R4-01: field definitions once per request (all definitions — Resolve keeps values of
        // disabled/deleted definitions visible).
        var fieldDefs = await LoadFieldDefinitionsAsync(projectId);

        var query = _unitOfWork.Repository<Comment>()
            .Query()
            .AsNoTracking()
            .Include(c => c.Replies)
            .Where(c => c.ProjectId == projectId && c.DeletedAt == null);

        if (filter.Status.HasValue)
            query = query.Where(c => c.Status == filter.Status.Value);

        if (filter.Environment.HasValue)
            query = query.Where(c => c.Environment == filter.Environment.Value);

        // Private comments are personal notes, never automation input — unlike ListAsync/GetByIdAsync
        // (which return a private comment to its own author), the apply-queue has no per-caller
        // identity to grant that exception to, so private comments are excluded outright.
        query = query.Where(c => !c.IsPrivate);

        var totalItems = await query.CountAsync();

        var pageSize = Math.Min(filter.PageSize, 100);
        var pageNumber = filter.PageNumber < 1 ? 1 : filter.PageNumber;
        var totalPages = totalItems == 0 ? 0 : (int)Math.Ceiling((double)totalItems / pageSize);

        var items = await query
            .OrderByDescending(c => c.CreatedAt)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var pagination = new Pagination
        {
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalItems = totalItems,
            TotalPages = totalPages
        };

        var names = await ResolveNamesAsync(items.SelectMany(AuthorIds));
        var pageContexts = await LoadPageContextsAsync(items.Select(c => c.PageContextSnapshotId));
        var (pages, userAgents, pageRefByCommentId) = BuildApplyPageMaps(items);

        var authorIds = items.Select(c => c.AuthorId).Distinct().ToList();
        var aiRules = await _unitOfWork.Repository<AiRule>()
            .Query()
            .AsNoTracking()
            .Where(r => r.DeletedAt == null && r.IsActive &&
                ((r.UserId == null && (r.ProjectId == null || r.ProjectId == projectId)) ||
                 (r.UserId != null && authorIds.Contains(r.UserId.Value) && (r.ProjectId == null || r.ProjectId == projectId))))
            .OrderBy(r => r.UserId == null ? (r.ProjectId == null ? 0 : 1) : 2) // Strict priority: Workspace (0) > Project (1) > Personal (2)
            .ThenBy(r => r.SortOrder)
            .ThenBy(r => r.CreatedAt)
            .ToListAsync();

        var adminRules = aiRules.Where(r => r.UserId == null)
            .OrderBy(r => r.ProjectId == null ? 0 : 1) // Workspace (Priority 1) before Project (Priority 2)
            .ThenBy(r => r.SortOrder)
            .ThenBy(r => r.CreatedAt)
            .Select(r => new AiRuleApplyDto
            {
                Title = r.Title,
                Prompt = r.Prompt,
                Scope = r.ProjectId == null ? "Workspace" : "Project",
                Priority = r.ProjectId == null ? 1 : 2,
                IsPersonal = false
            })
            .ToList();

        var personalRulesByAuthor = aiRules.Where(r => r.UserId != null)
            .GroupBy(r => r.UserId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g
                    .OrderBy(r => r.SortOrder)
                    .ThenBy(r => r.CreatedAt)
                    .Select(r => new AiRuleApplyDto
                    {
                        Title = r.Title,
                        Prompt = r.Prompt,
                        Scope = "Personal",
                        Priority = 3,
                        IsPersonal = true
                    }).ToList()
            );

        return Result<PagedData<CommentApplyItemDto>>.Success(
            new PagedData<CommentApplyItemDto>(
                items.Select(c =>
                {
                    var commentRules = adminRules.Concat(personalRulesByAuthor.GetValueOrDefault(c.AuthorId) ?? Enumerable.Empty<AiRuleApplyDto>()).ToList();
                    return MapToApplyItem(c, names, pageRefByCommentId.GetValueOrDefault(c.Id), commentRules, fieldDefs);
                }).ToList(),
                pagination,
                pageContexts: pageContexts,
                pages: pages,
                userAgents: userAgents));
    }

    public async Task<Result<CommentResponse>> GetByIdAsync(int id, Guid callerId)
    {
        var comment = await _unitOfWork.Repository<Comment>()
            .Query()
            .AsNoTracking()
            .Include(c => c.Replies)
            .Include(c => c.PageContextSnapshot)
            .Where(c => c.Id == id && c.DeletedAt == null)
            .FirstOrDefaultAsync();

        if (comment == null)
            return Result<CommentResponse>.NotFound(MessageKeys.Comment.NotFound);

        // Private comments are visible only to their author (no admin bypass).
        // Return NotFound rather than Forbidden so existence is not revealed.
        if (comment.IsPrivate && comment.AuthorId != callerId)
            return Result<CommentResponse>.NotFound(MessageKeys.Comment.NotFound);

        // A quick-access (Client) account can't see anyone else's comment either, private or not —
        // same NotFound-not-Forbidden reasoning as above.
        if (_currentUser.IsQuickAccess && comment.AuthorId != callerId)
            return Result<CommentResponse>.NotFound(MessageKeys.Comment.NotFound);

        var names = await ResolveNamesAsync(AuthorIds(comment));

        var effectiveRules = await _unitOfWork.Repository<AiRule>()
            .Query()
            .AsNoTracking()
            .Where(r => r.DeletedAt == null && r.IsActive &&
                ((r.UserId == null && (r.ProjectId == null || r.ProjectId == comment.ProjectId)) ||
                 (r.UserId == comment.AuthorId && (r.ProjectId == null || r.ProjectId == comment.ProjectId))))
            .OrderBy(r => r.UserId == null ? (r.ProjectId == null ? 0 : 1) : 2) // Strict priority: Workspace (0) > Project (1) > Personal (2)
            .ThenBy(r => r.SortOrder)
            .ThenBy(r => r.CreatedAt)
            .Select(r => new AiRuleApplyDto
            {
                Title = r.Title,
                Prompt = r.Prompt,
                Scope = r.UserId != null ? "Personal" : (r.ProjectId == null ? "Workspace" : "Project"),
                Priority = r.UserId != null ? 3 : (r.ProjectId == null ? 1 : 2),
                IsPersonal = r.UserId != null
            })
            .ToListAsync();

        // R4-01: resolved against the comment's own workspace owner, once.
        var fieldDefs = await _commentFields.GetDefinitionsForOwnerAsync(comment.OwnerId, enabledOnly: false);

        return Result<CommentResponse>.Success(MapToResponse(comment, names, effectiveRules, fieldDefs));
    }

    public async Task<Result<CommentResponse>> UpdateStatusAsync(int id, UpdateCommentStatusRequest request, Guid actorId)
    {
        // Triaging the backlog (marking things applied/archived/etc.) is a project-team action — a
        // quick-access (Client) account only ever leaves feedback, never manages its lifecycle, even
        // on its own comments.
        if (_currentUser.IsQuickAccess)
            return Result<CommentResponse>.Forbidden(MessageKeys.Comment.QuickAccessCannotChangeStatus);

        var comment = await _unitOfWork.Repository<Comment>()
            .Query()
            .Include(c => c.Replies)
            .Include(c => c.PageContextSnapshot)
            .Where(c => c.Id == id && c.DeletedAt == null)
            .FirstOrDefaultAsync();

        if (comment == null)
            return Result<CommentResponse>.NotFound(MessageKeys.Comment.NotFound);

        comment.Status = request.Status;

        if (request.Status == CommentStatus.Applied)
        {
            comment.AppliedAt = DateTime.UtcNow;
            comment.AppliedBy = actorId;
            comment.AppliedByLabel = request.AppliedByLabel;
            comment.CommitUrl = request.CommitUrl;
            // Normalised on the way in so deploy detection can compare shas directly. A sha that
            // differs only by case or whitespace would never match a reported build, and the
            // comment would sit "applied but never live" with nothing to show why.
            comment.CommitSha = string.IsNullOrWhiteSpace(request.CommitSha)
                ? null
                : request.CommitSha.Trim().ToLowerInvariant();
        }

        if (!string.IsNullOrWhiteSpace(request.Reply))
        {
            // Replies inherit the parent comment's tenant owner.
            var reply = new Reply
            {
                CommentId = comment.Id,
                AuthorId = actorId,
                Body = request.Reply.Trim(),
                PayloadFlags = PayloadFlagDetector.Detect(request.Reply).ToList(),
                HasPayloadFlag = PayloadFlagDetector.Detect(request.Reply).Count > 0,
                OwnerId = comment.OwnerId,
                // Same signal ShowsPayloadFlags/AddReplyAsync use: the widget/dashboard send
                // X-Pointer-Client, the AI apply flow (CLI `apply --mark`/pointer.sh/skill.md) does not.
                IsAi = IsNonHumanCaller,
                AiTool = IsNonHumanCaller ? NormalizeAiTool(request.AiTool) : null,
                AiModel = IsNonHumanCaller ? NormalizeAiModel(request.AiModel) : null
            };
            // comment is tracked (loaded without AsNoTracking); adding to its
            // collection lets EF insert the new reply on save. Do NOT also call
            // AddAsync — that double-adds the reply to the in-memory graph.
            comment.Replies.Add(reply);
        }

        if (request.Status == CommentStatus.Applied && comment.AuthorId != actorId)
        {
            var payload = new NotificationPayloadDto
            {
                CommitUrl = request.CommitUrl,
                AppliedByLabel = request.AppliedByLabel
            };
            var notification = new Notification
            {
                OwnerId = comment.OwnerId,
                UserId = comment.AuthorId,
                Type = NotificationType.CommentApplied,
                CommentId = comment.Id,
                ProjectId = comment.ProjectId,
                ActorId = actorId,
                Payload = JsonSerializer.Serialize(payload),
                CreatedAt = DateTime.UtcNow
            };
            await _notificationService.EnqueueAsync(notification);
        }

        _unitOfWork.Repository<Comment>().Update(comment);
        await _unitOfWork.SaveChangesAsync();

        // Server emission: first_apply. Race-safe by constraint.
        if (request.Status == CommentStatus.Applied)
        {
            try
            {
                var firstApplyEvent = new UsageEvent
                {
                    Type = "first_apply",
                    Source = "api",
                    ProjectId = comment.ProjectId,
                    OwnerId = comment.OwnerId,
                    CreatedAt = DateTime.UtcNow
                };
                _unitOfWork.UsageEvents.Add(firstApplyEvent);
                await _unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" } ||
                (ex.InnerException != null && ex.InnerException.GetType().Name == "SqliteException" && (int)ex.InnerException.GetType().GetProperty("SqliteErrorCode")!.GetValue(ex.InnerException)! == 19))
            {
                // Unique violation on the partial index -> someone else was first. Swallow.
                _unitOfWork.ClearChangeTracker();
            }
        }

        var names = await ResolveNamesAsync(AuthorIds(comment));
        var message = request.Status == CommentStatus.Applied ? MessageKeys.Comment.Applied : null;
        var updatedFieldDefs = await _commentFields.GetDefinitionsForOwnerAsync(comment.OwnerId, enabledOnly: false);
        return Result<CommentResponse>.Success(MapToResponse(comment, names, fieldDefs: updatedFieldDefs), message);
    }

    public async Task<Result<CommentResponse>> VerifyAsync(int id, VerifyCommentRequest request, Guid actorId)
    {
        var comment = await _unitOfWork.Repository<Comment>()
            .Query()
            .Include(c => c.Replies)
            .Include(c => c.PageContextSnapshot)
            .Where(c => c.Id == id && c.DeletedAt == null)
            .FirstOrDefaultAsync();

        if (comment == null)
            return Result<CommentResponse>.NotFound(MessageKeys.Comment.NotFound);

        // Actor must be the author or an admin. Quick-access users are allowed here when verifying their
        // own comments (the author verify loop is the one lifecycle action a Client legitimately owns).
        var isAuthor = comment.AuthorId == actorId;
        var isAdmin = _currentUser.IsAdmin || _currentUser.IsSuperAdmin;
        if (!isAuthor && !isAdmin)
            return Result<CommentResponse>.Forbidden("You do not have permission to verify this comment.");

        if (comment.Status != CommentStatus.Applied)
            return Result<CommentResponse>.Failure(MessageKeys.Comment.VerifyRequiresApplied);

        if (request.Ok)
        {
            comment.VerifiedAt = DateTime.UtcNow;
            var replyText = !string.IsNullOrWhiteSpace(request.Note) ? request.Note.Trim() : "Verified ✓";
            var reply = new Reply
            {
                CommentId = comment.Id,
                AuthorId = actorId,
                Body = replyText,
                PayloadFlags = PayloadFlagDetector.Detect(replyText).ToList(),
                HasPayloadFlag = PayloadFlagDetector.Detect(replyText).Count > 0,
                OwnerId = comment.OwnerId
            };
            comment.Replies.Add(reply);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.Note))
                return Result<CommentResponse>.Failure(MessageKeys.Comment.VerifyNoteRequired);

            comment.Status = CommentStatus.Open;
            comment.VerifiedAt = null;

            var replyText = $"Not fixed: {request.Note.Trim()}";
            var reply = new Reply
            {
                CommentId = comment.Id,
                AuthorId = actorId,
                Body = replyText,
                PayloadFlags = PayloadFlagDetector.Detect(replyText).ToList(),
                HasPayloadFlag = PayloadFlagDetector.Detect(replyText).Count > 0,
                OwnerId = comment.OwnerId
            };
            comment.Replies.Add(reply);

            if (comment.AppliedBy.HasValue)
            {
                var excerpt = request.Note.Trim();
                if (excerpt.Length > 80) excerpt = excerpt[..80];
                var payload = new NotificationPayloadDto
                {
                    ReplyExcerpt = excerpt
                };
                var notification = new Notification
                {
                    OwnerId = comment.OwnerId,
                    UserId = comment.AppliedBy.Value,
                    Type = NotificationType.CommentReopened,
                    CommentId = comment.Id,
                    ProjectId = comment.ProjectId,
                    ActorId = actorId,
                    Payload = JsonSerializer.Serialize(payload),
                    CreatedAt = DateTime.UtcNow
                };
                await _notificationService.EnqueueAsync(notification);
            }
        }

        _unitOfWork.Repository<Comment>().Update(comment);
        await _unitOfWork.SaveChangesAsync();

        var names = await ResolveNamesAsync(AuthorIds(comment));
        var message = request.Ok ? MessageKeys.Comment.Verified : MessageKeys.Comment.Reopened;
        var verifyFieldDefs = await _commentFields.GetDefinitionsForOwnerAsync(comment.OwnerId, enabledOnly: false);
        return Result<CommentResponse>.Success(MapToResponse(comment, names, fieldDefs: verifyFieldDefs), message);
    }

    public async Task<Result<CommentResponse>> EditAsync(int id, EditCommentRequest request, Guid editorId)
    {
        var comment = await _unitOfWork.Repository<Comment>()
            .Query()
            .Include(c => c.Replies)
            .Include(c => c.PageContextSnapshot)
            .Where(c => c.Id == id && c.DeletedAt == null)
            .FirstOrDefaultAsync();

        if (comment == null)
            return Result<CommentResponse>.NotFound(MessageKeys.Comment.NotFound);

        // Own comments only — not even admins edit someone else's content.
        if (comment.AuthorId != editorId)
            return Result<CommentResponse>.Failure("You can only edit your own comments.");

        // Non-empty/length enforced upfront by EditCommentValidator (FluentValidation auto-validation).
        comment.Body = request.Body.Trim();
        // Recomputed, not left alone: editing a flagged comment to remove the secret must clear the
        // badge, and editing a clean one to add a secret must raise it.
        comment.PayloadFlags = PayloadFlagDetector.Detect(comment.Body).ToList();
        comment.HasPayloadFlag = comment.PayloadFlags.Count > 0;

        // Optionally remove the uploaded screenshot (clear the reference + delete the file).
        if (request.RemoveScreenshot && !string.IsNullOrEmpty(comment.Element.ScreenshotUrl))
        {
            await _fileStorage.DeleteAsync(comment.Element.ScreenshotUrl!);
            comment.Element.ScreenshotUrl = null;
        }

        comment.EditedAt = DateTime.UtcNow;
        comment.EditedBy = editorId;

        _unitOfWork.Repository<Comment>().Update(comment);
        await _unitOfWork.SaveChangesAsync();

        var editNames = await ResolveNamesAsync(AuthorIds(comment));
        var editFieldDefs = await _commentFields.GetDefinitionsForOwnerAsync(comment.OwnerId, enabledOnly: false);
        return Result<CommentResponse>.Success(MapToResponse(comment, editNames, fieldDefs: editFieldDefs), "Comment updated.");
    }

    // R4-01: replace a comment's admin-defined field values. Allowed for the AUTHOR (incl.
    // quick-access — they own the comment) or a workspace admin; deliberately unlike EditAsync's
    // author-only body edit. Loaded through the FILTERED query exactly like EditAsync — never
    // IgnoreQueryFilters here: the strict-own filter is what returns 404 to another workspace's
    // admin instead of exposing the comment.
    public async Task<Result<CommentResponse>> UpdateFieldsAsync(int id, UpdateCommentFieldsRequest request, Guid actorId)
    {
        var comment = await _unitOfWork.Repository<Comment>()
            .Query()
            .Include(c => c.Replies)
            .Include(c => c.PageContextSnapshot)
            .Where(c => c.Id == id && c.DeletedAt == null)
            .FirstOrDefaultAsync();

        if (comment == null)
            return Result<CommentResponse>.NotFound(MessageKeys.Comment.NotFound);

        if (!_currentUser.IsAdmin && comment.AuthorId != actorId)
            return Result<CommentResponse>.Forbidden("You do not have permission to edit this comment's fields.");

        // All definitions (incl. disabled) so a disabled key fails with the specific disabled
        // message rather than the generic unknown-key one.
        var fieldDefs = await _commentFields.GetDefinitionsForOwnerAsync(comment.OwnerId, enabledOnly: false);
        var validated = _commentFields.ValidateValues(fieldDefs, request.CustomFields);
        if (!validated.IsSuccess)
            return Result<CommentResponse>.Failure(validated.Message!);

        comment.CustomFields = validated.Data!;
        comment.EditedAt = DateTime.UtcNow;
        comment.EditedBy = actorId;

        _unitOfWork.Repository<Comment>().Update(comment);
        await _unitOfWork.SaveChangesAsync();

        var names = await ResolveNamesAsync(AuthorIds(comment));
        return Result<CommentResponse>.Success(MapToResponse(comment, names, fieldDefs: fieldDefs), "Comment fields updated.");
    }

    public async Task<Result<CommentResponse>> SetVisibilityAsync(int id, Guid callerId, bool isPrivate)
    {
        var comment = await _unitOfWork.Repository<Comment>()
            .Query()
            .Include(c => c.Replies)
            .Include(c => c.PageContextSnapshot)
            .Where(c => c.Id == id && c.DeletedAt == null)
            .FirstOrDefaultAsync();

        if (comment == null)
            return Result<CommentResponse>.NotFound(MessageKeys.Comment.NotFound);

        // Own comments only — privacy is the author's call, not even admins'.
        if (comment.AuthorId != callerId)
            return Result<CommentResponse>.Failure("You can only change the visibility of your own comments.");

        comment.IsPrivate = isPrivate;
        _unitOfWork.Repository<Comment>().Update(comment);
        await _unitOfWork.SaveChangesAsync();

        var names = await ResolveNamesAsync(AuthorIds(comment));
        var visibilityFieldDefs = await _commentFields.GetDefinitionsForOwnerAsync(comment.OwnerId, enabledOnly: false);
        return Result<CommentResponse>.Success(MapToResponse(comment, names, fieldDefs: visibilityFieldDefs));
    }

    public async Task<Result<ReplyResponse>> AddReplyAsync(int commentId, AddReplyRequest request, Guid authorId, string? origin = null)
    {
        // Belt-and-suspenders: auto-validation (AddReplyValidator) rejects empty/oversized bodies on
        // model binding, but guard here too so a direct call / null body returns 400 not a 500.
        var body = (request.Body ?? string.Empty).Trim();
        if (body.Length == 0)
            return Result<ReplyResponse>.Failure(MessageKeys.Comment.BodyRequired);
        if (body.Length > 4000)
            return Result<ReplyResponse>.Failure(MessageKeys.Comment.BodyRequired);

        var comment = await _unitOfWork.Repository<Comment>()
            .Query()
            .AsNoTracking()
            .Where(c => c.Id == commentId && c.DeletedAt == null)
            .FirstOrDefaultAsync();

        if (comment == null)
            return Result<ReplyResponse>.NotFound(MessageKeys.Comment.NotFound);

        // A quick-access (Client) account can't see anyone else's comment (ListAsync/GetByIdAsync
        // already scope to it), so it can't reply on one either.
        if (_currentUser.IsQuickAccess && comment.AuthorId != authorId)
            return Result<ReplyResponse>.NotFound(MessageKeys.Comment.NotFound);

        // Origin allow-list (R1-05). AddReplyRequest carries no environment of its own, so the
        // parent comment's tag decides whether the localhost carve-out applies.
        if (!await _projectService.IsOriginAllowedAsync(
                comment.ProjectId, origin, comment.Environment, _currentUser.IsQuickAccess))
            return Result<ReplyResponse>.Forbidden(MessageKeys.Project.OriginNotAllowed);

        // Replies inherit the parent comment's tenant owner.
        var reply = new Reply
        {
            CommentId = commentId,
            AuthorId = authorId,
            Body = body,
            PayloadFlags = PayloadFlagDetector.Detect(body).ToList(),
            HasPayloadFlag = PayloadFlagDetector.Detect(body).Count > 0,
            OwnerId = comment.OwnerId,
            // Same signal ShowsPayloadFlags uses: the widget/dashboard send X-Pointer-Client, the
            // AI apply flow (CLI/pointer.sh/skill.md) does not.
            IsAi = IsNonHumanCaller,
            AiTool = IsNonHumanCaller ? NormalizeAiTool(request.AiTool) : null,
            AiModel = IsNonHumanCaller ? NormalizeAiModel(request.AiModel) : null
        };

        await _unitOfWork.Repository<Reply>().AddAsync(reply);

        if (comment.AuthorId != authorId)
        {
            var excerpt = body.Length > 80 ? body[..80] : body;
            var payload = new NotificationPayloadDto
            {
                ReplyExcerpt = excerpt
            };
            var notification = new Notification
            {
                OwnerId = comment.OwnerId,
                UserId = comment.AuthorId,
                Type = NotificationType.ReplyAdded,
                CommentId = comment.Id,
                ProjectId = comment.ProjectId,
                ActorId = authorId,
                Payload = JsonSerializer.Serialize(payload),
                CreatedAt = DateTime.UtcNow
            };
            await _notificationService.EnqueueAsync(notification);
        }

        await _unitOfWork.SaveChangesAsync();

        var names = await ResolveNamesAsync(new[] { reply.AuthorId });
        return Result<ReplyResponse>.Success(MapReplyToResponse(reply, names));
    }

    public async Task<Result<ReplyResponse>> EditReplyAsync(int replyId, UpdateReplyRequest request, Guid editorId)
    {
        var reply = await _unitOfWork.Repository<Reply>()
            .Query()
            .Where(r => r.Id == replyId)
            .FirstOrDefaultAsync();

        if (reply == null)
            return Result<ReplyResponse>.NotFound(MessageKeys.Comment.NotFound);

        // Read-only forever, regardless of caller — see Reply.IsAi's doc comment.
        if (reply.IsAi)
            return Result<ReplyResponse>.Failure("Automated replies can't be edited.");

        // Own replies only — not even admins edit someone else's content (mirrors comment EditAsync).
        if (reply.AuthorId != editorId)
            return Result<ReplyResponse>.Failure("You can only edit your own replies.");

        var body = (request.Body ?? string.Empty).Trim();
        if (body.Length == 0)
            return Result<ReplyResponse>.Failure(MessageKeys.Comment.BodyRequired);
        if (body.Length > 4000)
            return Result<ReplyResponse>.Failure(MessageKeys.Comment.BodyRequired);

        reply.Body = body;
        // Recomputed, not left alone — same reasoning as comment EditAsync.
        reply.PayloadFlags = PayloadFlagDetector.Detect(body).ToList();
        reply.HasPayloadFlag = reply.PayloadFlags.Count > 0;
        reply.UpdatedAt = DateTime.UtcNow;
        reply.UpdatedBy = editorId;

        _unitOfWork.Repository<Reply>().Update(reply);
        await _unitOfWork.SaveChangesAsync();

        var names = await ResolveNamesAsync(new[] { reply.AuthorId });
        return Result<ReplyResponse>.Success(MapReplyToResponse(reply, names), "Reply updated.");
    }

    public async Task<Result> DeleteReplyAsync(int replyId, Guid actorId, bool isAdmin)
    {
        var reply = await _unitOfWork.Repository<Reply>()
            .Query()
            .Where(r => r.Id == replyId)
            .FirstOrDefaultAsync();

        if (reply == null)
            return Result.NotFound(MessageKeys.Comment.NotFound);

        // Read-only forever, regardless of caller (author or admin) — see Reply.IsAi's doc comment.
        if (reply.IsAi)
            return Result.Failure("Automated replies can't be deleted.");

        if (actorId != reply.AuthorId && !isAdmin)
            return Result.Failure("You do not have permission to delete this reply.");

        // Hard delete (unlike the parent comment's soft delete) — Reply has no
        // DeletedAt-aware query filter anywhere Replies get mapped, and there is no
        // undo/restore flow for a single reply the way there is for a whole comment.
        _unitOfWork.Repository<Reply>().Remove(reply);
        await _unitOfWork.SaveChangesAsync();

        return Result.Success();
    }

    public async Task<Result> DeleteAsync(int id, Guid actorId, bool isAdmin)
    {
        var comment = await _unitOfWork.Repository<Comment>()
            .Query()
            .Where(c => c.Id == id && c.DeletedAt == null)
            .FirstOrDefaultAsync();

        if (comment == null)
            return Result.NotFound(MessageKeys.Comment.NotFound);

        if (actorId != comment.AuthorId && !isAdmin)
            return Result.Failure("You do not have permission to delete this comment.");

        comment.DeletedAt = DateTime.UtcNow;
        _unitOfWork.Repository<Comment>().Update(comment);
        await _unitOfWork.SaveChangesAsync();

        return Result.Success();
    }

    private static ElementCapture MapToEntity(ElementCaptureDto dto) => new()
    {
        Selector = dto.Selector,
        Snapshot = dto.Snapshot,
        Classes = dto.Classes,
        ComputedStyles = dto.ComputedStyles,
        AppliedCssRules = dto.AppliedCssRules,
        SourcePath = dto.SourcePath,
        ParentInfo = dto.ParentInfo,
        ScreenshotUrl = dto.ScreenshotUrl,
        PageUrl = dto.PageUrl,
        Route = dto.Route,
        PageTitle = dto.PageTitle,
        ViewportWidth = dto.ViewportWidth,
        ViewportHeight = dto.ViewportHeight,
        DeviceType = dto.DeviceType,
        DevicePixelRatio = dto.DevicePixelRatio,
        UserAgent = dto.UserAgent
    };

    private ElementCaptureDto MapElementToDto(ElementCapture entity) => new()
    {
        Selector = entity.Selector,
        Snapshot = entity.Snapshot,
        Classes = entity.Classes,
        ComputedStyles = entity.ComputedStyles,
        AppliedCssRules = entity.AppliedCssRules,
        SourcePath = entity.SourcePath,
        ParentInfo = entity.ParentInfo,
        // Re-sign at every read so the returned URL is always fresh (never a stale/leaked permanent path).
        ScreenshotUrl = string.IsNullOrEmpty(entity.ScreenshotUrl)
            ? entity.ScreenshotUrl
            : _uploadSigner.SignedUrl(_uploadSigner.ExtractRelPath(entity.ScreenshotUrl)),
        PageUrl = entity.PageUrl,
        Route = entity.Route,
        PageTitle = entity.PageTitle,
        ViewportWidth = entity.ViewportWidth,
        ViewportHeight = entity.ViewportHeight,
        DeviceType = entity.DeviceType,
        DevicePixelRatio = entity.DevicePixelRatio,
        UserAgent = entity.UserAgent
    };

    // Resolve display names for a set of author ids (User.PublicId == Comment.AuthorId).
    // One batched query; missing ids simply have no name (component falls back gracefully).
    private Task<Dictionary<Guid, string>> ResolveNamesAsync(IEnumerable<Guid> ids) =>
        UserNameResolver.ResolveAsync(_unitOfWork, ids);

    /// <summary>
    /// Whether this caller is a surface a human is looking at, and so may see the advisory payload
    /// flags. Defaults to FALSE when no client accessor is wired: hiding an advisory badge is a
    /// cosmetic loss, leaking it into an AI payload is an injection surface.
    /// </summary>
    private bool ShowsPayloadFlags => _currentClient?.IsHumanSurface ?? false;

    /// <summary>Whether this caller may attribute a reply to an AI tool — the inverse of
    /// <see cref="ShowsPayloadFlags"/>'s human-surface check. A human typing into the widget or
    /// dashboard can't claim to be an AI, so AiTool/AiModel are dropped (stored null) for them
    /// regardless of what the request body carries — see NormalizeAiTool/NormalizeAiModel.</summary>
    private bool IsNonHumanCaller => !(_currentClient?.IsHumanSurface ?? false);

    /// <summary>Trims and lowercases a tool identifier (e.g. "Claude-Code" -&gt; "claude-code"),
    /// null/empty when absent. Format is enforced upstream by AddReplyValidator/
    /// UpdateCommentStatusValidator; this only normalises casing/whitespace for storage.</summary>
    private static string? NormalizeAiTool(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    /// <summary>Trims a model identifier, preserving case (model ids like "GPT-5.2" or
    /// "Claude-Sonnet-5" are conventionally cased) — null/empty when absent.</summary>
    private static string? NormalizeAiModel(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ReplyResponse MapReplyToResponse(Reply reply, IReadOnlyDictionary<Guid, string> names, bool includeFlags = false) => new()
    {
        Id = reply.Id,
        AuthorId = reply.AuthorId,
        AuthorName = names.GetValueOrDefault(reply.AuthorId),
        Body = reply.Body,
        CreatedAt = reply.CreatedAt,
        IsAi = reply.IsAi,
        AiTool = reply.AiTool,
        AiModel = reply.AiModel,
        HasPayloadFlag = includeFlags ? reply.HasPayloadFlag : null,
        PayloadFlags = includeFlags ? reply.PayloadFlags : null
    };

    private static readonly List<CommentFieldDefinition> NoFieldDefinitions = new();

    // R4-01: the workspace definitions behind a project's comments — loaded once per request and
    // handed to the mappers, so a page of N comments costs one definitions query, not N.
    private async Task<List<CommentFieldDefinition>> LoadFieldDefinitionsAsync(int projectId)
    {
        var ownerId = await _unitOfWork.Repository<Project>()
            .Query()
            .Where(p => p.Id == projectId)
            .Select(p => p.OwnerId)
            .FirstAsync();
        return await _commentFields.GetDefinitionsForOwnerAsync(ownerId, enabledOnly: false);
    }

    private CommentListItemDto MapToListItem(Comment comment, IReadOnlyDictionary<Guid, string> names, IReadOnlyList<CommentFieldDefinition>? fieldDefs = null) => new()
    {
        Id = comment.Id,
        Status = comment.Status,
        Environment = comment.Environment,
        Body = comment.Body,
        IsPrivate = comment.IsPrivate,
        AuthorId = comment.AuthorId,
        AuthorName = names.GetValueOrDefault(comment.AuthorId),
        CreatedAt = comment.CreatedAt,
        AppliedAt = comment.AppliedAt,
        AppliedBy = comment.AppliedBy,
        AppliedByLabel = comment.AppliedByLabel,
        CommitUrl = comment.CommitUrl,
        CommitSha = comment.CommitSha,
        DeployedAt = comment.DeployedAt,
        DeployedSha = comment.DeployedSha,
        VerifiedAt = comment.VerifiedAt,
        EditedAt = comment.EditedAt,
        // Labels only — the prompts are intentionally never exposed here.
        PickedActionTexts = comment.PickedActions.Select(a => a.Text).ToList(),
        Element = MapElementToDto(comment.Element),
        Replies = comment.Replies.Select(r => MapReplyToResponse(r, names, ShowsPayloadFlags)).ToList(),
        IsBugReport = comment.IsBugReport,
        PageContextId = comment.PageContextSnapshotId,
        HasPayloadFlag = ShowsPayloadFlags ? comment.HasPayloadFlag : null,
        PayloadFlags = ShowsPayloadFlags ? comment.PayloadFlags : null,
        Language = comment.Language,
        CustomFields = _commentFields.Resolve(fieldDefs ?? NoFieldDefinitions, comment.CustomFields)
    };

    private CommentResponse MapToResponse(Comment comment, IReadOnlyDictionary<Guid, string> names, List<AiRuleApplyDto>? rules = null, IReadOnlyList<CommentFieldDefinition>? fieldDefs = null) => new()
    {
        Id = comment.Id,
        Status = comment.Status,
        Environment = comment.Environment,
        Body = comment.Body,
        IsPrivate = comment.IsPrivate,
        AuthorId = comment.AuthorId,
        AuthorName = names.GetValueOrDefault(comment.AuthorId),
        CreatedAt = comment.CreatedAt,
        AppliedAt = comment.AppliedAt,
        AppliedBy = comment.AppliedBy,
        AppliedByLabel = comment.AppliedByLabel,
        CommitUrl = comment.CommitUrl,
        CommitSha = comment.CommitSha,
        DeployedAt = comment.DeployedAt,
        DeployedSha = comment.DeployedSha,
        VerifiedAt = comment.VerifiedAt,
        EditedAt = comment.EditedAt,
        // Labels only — the prompts are intentionally never exposed here.
        PickedActionTexts = comment.PickedActions.Select(a => a.Text).ToList(),
        Element = MapElementToDto(comment.Element),
        Replies = comment.Replies.Select(r => MapReplyToResponse(r, names, ShowsPayloadFlags)).ToList(),
        IsBugReport = comment.IsBugReport,
        PageContext = MapPageContextToDto(comment.PageContextSnapshot),
        AiRules = rules ?? new List<AiRuleApplyDto>(),
        HasPayloadFlag = ShowsPayloadFlags ? comment.HasPayloadFlag : null,
        PayloadFlags = ShowsPayloadFlags ? comment.PayloadFlags : null,
        Language = comment.Language,
        CustomFields = _commentFields.Resolve(fieldDefs ?? NoFieldDefinitions, comment.CustomFields)
    };

    // Apply-queue export mapper — the ONLY mapper that carries PickedActionPrompt (admin/AI path).
    private ApplyElementDto MapToApplyElement(Comment comment, string? pageRef) => new()
    {
        PageRef = pageRef,
        Selector = comment.Element.Selector,
        Snapshot = comment.Element.Snapshot,
        SourcePath = comment.Element.SourcePath,
        // Re-sign at every read so the returned URL is always fresh (never a stale/leaked permanent path).
        ScreenshotUrl = string.IsNullOrEmpty(comment.Element.ScreenshotUrl)
            ? comment.Element.ScreenshotUrl
            : _uploadSigner.SignedUrl(_uploadSigner.ExtractRelPath(comment.Element.ScreenshotUrl)),
        Classes = ParseJsonOrRaw(comment.Element.Classes),
        ComputedStyles = ParseJsonOrRaw(comment.Element.ComputedStyles),
        AppliedCssRules = ParseJsonOrRaw(comment.Element.AppliedCssRules),
        Parent = ParseJsonOrRaw(comment.Element.ParentInfo)
    };

    // Old/malformed rows never break the endpoint: a JSON parse failure falls back to the raw
    // string re-wrapped as a JSON string element, rather than a 500 or a silently dropped field.
    private static JsonElement? ParseJsonOrRaw(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(raw);
        }
    }

    // Builds the apply-queue's page/UA compaction dictionaries in one pass over the page's items.
    // Keyed by `route + deviceType` (not route alone) so two comments on the same route captured
    // from different devices/viewports each keep their own real metadata instead of colliding.
    private static (
        Dictionary<string, ApplyPageDto> pages,
        Dictionary<string, string> userAgents,
        Dictionary<int, string?> pageRefByCommentId)
        BuildApplyPageMaps(IReadOnlyList<Comment> items)
    {
        var pages = new Dictionary<string, ApplyPageDto>();
        var pageRefByKey = new Dictionary<string, string>();
        var userAgents = new Dictionary<string, string>();
        var uaRefByValue = new Dictionary<string, string>();
        var pageRefByCommentId = new Dictionary<int, string?>();

        foreach (var comment in items)
        {
            var element = comment.Element;
            var route = NormalizeRoute(element.Route);
            var device = element.DeviceType ?? "unknown";
            var pageKey = $"{route}␟{device}"; // unit-separator — not expected in a route/device string

            if (!pageRefByKey.TryGetValue(pageKey, out var pageRef))
            {
                string? uaRef = null;
                if (!string.IsNullOrEmpty(element.UserAgent))
                {
                    if (!uaRefByValue.TryGetValue(element.UserAgent, out uaRef))
                    {
                        uaRef = $"u{userAgents.Count + 1}";
                        userAgents[uaRef] = element.UserAgent;
                        uaRefByValue[element.UserAgent] = uaRef;
                    }
                }

                pageRef = $"p{pages.Count + 1}";
                pageRefByKey[pageKey] = pageRef;
                pages[pageRef] = new ApplyPageDto
                {
                    Url = element.PageUrl,
                    Route = route,
                    Title = element.PageTitle,
                    Viewport = element.ViewportWidth.HasValue && element.ViewportHeight.HasValue
                        ? $"{element.ViewportWidth}x{element.ViewportHeight}"
                        : null,
                    Device = element.DeviceType,
                    Dpr = element.DevicePixelRatio,
                    UaRef = uaRef
                };
            }

            pageRefByCommentId[comment.Id] = pageRef;
        }

        return (pages, userAgents, pageRefByCommentId);
    }

    private CommentApplyItemDto MapToApplyItem(Comment comment, IReadOnlyDictionary<Guid, string> names, string? pageRef, List<AiRuleApplyDto>? rules = null, IReadOnlyList<CommentFieldDefinition>? fieldDefs = null) => new()
    {
        Id = comment.Id,
        Status = comment.Status,
        Environment = comment.Environment,
        Body = comment.Body,
        AuthorName = names.GetValueOrDefault(comment.AuthorId),
        CreatedAt = comment.CreatedAt,
        Element = MapToApplyElement(comment, pageRef),
        // Built field-by-field into ApplyReplyDto rather than reusing MapReplyToResponse: the
        // AI-facing queue must not inherit whatever the human DTO grows next.
        Replies = comment.Replies
            .Select(r => new ApplyReplyDto
            {
                AuthorName = names.GetValueOrDefault(r.AuthorId) ?? string.Empty,
                Body = r.Body,
                CreatedAt = r.CreatedAt,
            })
            .ToList(),
        // Apply/AI path: carries both label + prompt for each picked action.
        PickedActions = comment.PickedActions
            .Select(a => new PickedActionDto { Text = a.Text, Prompt = a.Prompt }).ToList(),
        AiRules = rules ?? new List<AiRuleApplyDto>(),
        IsBugReport = comment.IsBugReport,
        PageContextId = comment.PageContextSnapshotId,
        Language = comment.Language,
        // R4-01: resolved label/type/suggested-tool per stored value, for the CLI's Fields fence.
        CustomFields = _commentFields.Resolve(fieldDefs ?? NoFieldDefinitions, comment.CustomFields)
    };

    // Lowercase/trim the client-detected tag; "unknown" (the detector's not-confident sentinel)
    // and empty both collapse to null rather than being stored as a literal string.
    // Defense in depth behind CreateCommentValidator's charset rule: strip anything outside
    // [a-z0-9-] so a stored tag can never carry newlines or markup into the apply prompt.
    private static string? NormalizeLanguage(string? language)
    {
        var trimmed = language?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(trimmed) || trimmed == "unknown") return null;
        var safe = System.Text.RegularExpressions.Regex.Replace(trimmed, "[^a-z0-9-]", "");
        return string.IsNullOrEmpty(safe) ? null : safe;
    }

    // Path only — no query/hash — so /checkout?step=1 and ?step=2 share one PageContextSnapshot.
    private static string NormalizeRoute(string? route)
    {
        if (string.IsNullOrEmpty(route)) return string.Empty;
        var cut = route.IndexOfAny(['?', '#']);
        return cut >= 0 ? route[..cut] : route;
    }

    // Appends this request's buffered entries onto the shared page snapshot, capped so a long-lived
    // SPA session with many bug reports on the same page can't grow the row unbounded.
    private const int MaxPageContextEntries = 40;

    private static void MergePageContext(PageContextSnapshot snapshot, PageContextCaptureDto capture)
    {
        foreach (var c in capture.ConsoleEntries)
        {
            snapshot.ConsoleEntries.Add(new ConsoleLogEntry
            {
                Level = c.Level,
                Message = c.Message,
                Stack = c.Stack,
                Count = c.Count < 1 ? 1 : c.Count,
                OccurredAt = c.OccurredAt ?? DateTime.UtcNow
            });
        }
        if (snapshot.ConsoleEntries.Count > MaxPageContextEntries)
            snapshot.ConsoleEntries.RemoveRange(0, snapshot.ConsoleEntries.Count - MaxPageContextEntries);

        foreach (var n in capture.NetworkEntries)
        {
            snapshot.NetworkEntries.Add(new NetworkFailureEntry
            {
                Method = n.Method,
                Url = n.Url,
                StatusCode = n.StatusCode,
                DurationMs = n.DurationMs,
                OccurredAt = n.OccurredAt ?? DateTime.UtcNow
            });
        }
        if (snapshot.NetworkEntries.Count > MaxPageContextEntries)
            snapshot.NetworkEntries.RemoveRange(0, snapshot.NetworkEntries.Count - MaxPageContextEntries);
    }

    // Batch-loads distinct PageContextSnapshots referenced by a page of comments, so N comments
    // sharing a page context cost one dictionary entry (and one query), not N copies.
    private async Task<IReadOnlyDictionary<int, PageContextDto>?> LoadPageContextsAsync(IEnumerable<int?> ids)
    {
        var distinct = ids.Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        if (distinct.Count == 0) return null;

        var snapshots = await _unitOfWork.Repository<PageContextSnapshot>()
            .Query()
            .AsNoTracking()
            .Where(s => distinct.Contains(s.Id))
            .ToListAsync();

        return snapshots.ToDictionary(s => s.Id, s => MapPageContextToDto(s)!);
    }

    private static PageContextDto? MapPageContextToDto(PageContextSnapshot? snapshot)
    {
        if (snapshot == null) return null;
        return new PageContextDto
        {
            Id = snapshot.Id,
            Route = snapshot.Route,
            Environment = snapshot.Environment,
            LastEventAt = snapshot.LastEventAt,
            ConsoleEntries = snapshot.ConsoleEntries.Select(e => new ConsoleEntryDto
            {
                Level = e.Level,
                Message = e.Message,
                Stack = e.Stack,
                Count = e.Count,
                OccurredAt = e.OccurredAt
            }).ToList(),
            NetworkEntries = snapshot.NetworkEntries.Select(e => new NetworkEntryDto
            {
                Method = e.Method,
                Url = e.Url,
                StatusCode = e.StatusCode,
                DurationMs = e.DurationMs,
                OccurredAt = e.OccurredAt
            }).ToList()
        };
    }

    private static IEnumerable<Guid> AuthorIds(Comment c) =>
        new[] { c.AuthorId }.Concat(c.Replies.Select(r => r.AuthorId));
}
