using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Comment;
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

    public CommentService(IUnitOfWork unitOfWork, IProjectService projectService, IPredefinedActionService predefinedActions, IFileStorage fileStorage, ICurrentUser currentUser, IUploadSigner uploadSigner, ISettingsService settings, IEntitlementService entitlements)
    {
        _unitOfWork = unitOfWork;
        _projectService = projectService;
        _predefinedActions = predefinedActions;
        _fileStorage = fileStorage;
        _currentUser = currentUser;
        _uploadSigner = uploadSigner;
        _settings = settings;
        _entitlements = entitlements;
    }

    public async Task<Result<CommentResponse>> CreateAsync(string projectKey, CreateCommentRequest request, Guid authorId)
    {
        var projectResult = await _projectService.EnsureAsync(projectKey);
        if (!projectResult.IsSuccess)
            return projectResult.IsConflict
                ? Result<CommentResponse>.Conflict(projectResult.Message ?? MessageKeys.Project.Disabled)
                : Result<CommentResponse>.NotFound(projectResult.Message ?? MessageKeys.Project.NotFound);

        // Stamp OwnerId from the PROJECT's tenant: a comment belongs to whoever owns
        // the project, regardless of who authored it. This is correct even when a super
        // admin comments on a tenant-owned project (OwnerFor(caller) would wrongly be null).
        var projectInfo = await _unitOfWork.Repository<Project>().Query()
            .Where(p => p.Id == projectResult.Data)
            .Select(p => new { p.OwnerId, p.PageContextCaptureEnabled })
            .FirstAsync();
        var projectOwnerId = projectInfo.OwnerId;

        // Enforce the demo comment cap for demo tenants. A per-tenant override wins; otherwise
        // the global super-admin-tunable setting (default 10) applies.
        if (projectOwnerId is Guid owner)
        {
            var demoOwner = await _unitOfWork.Repository<User>()
                .Query()
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(u => u.PublicId == owner && u.IsDemo && u.DeletedAt == null)
                .Select(u => new { u.DemoCommentCapOverride })
                .FirstOrDefaultAsync();

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
            Environment = request.Environment,
            Status = CommentStatus.Open,
            AuthorId = authorId,
            Body = request.Body.Trim(),
            IsPrivate = request.IsPrivate,
            OwnerId = projectOwnerId,
            Element = MapToEntity(request.Element),
            IsBugReport = request.IsBugReport
        };

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
                         && s.Environment == request.Environment
                         && s.SessionId == capture.SessionId
                         && s.DeletedAt == null)
                .FirstOrDefaultAsync();

            if (pageContext == null)
            {
                pageContext = new PageContextSnapshot
                {
                    ProjectId = projectResult.Data,
                    Environment = request.Environment,
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

        var names = await ResolveNamesAsync(AuthorIds(comment));
        return Result<CommentResponse>.Success(MapToResponse(comment, names), MessageKeys.Comment.Created);
    }

    public async Task<Result<PagedData<CommentListItemDto>>> ListAsync(string projectKey, CommentFilter filter, Guid callerId)
    {
        var projectResult = await _projectService.EnsureAsync(projectKey);
        if (!projectResult.IsSuccess)
            return projectResult.IsConflict
                ? Result<PagedData<CommentListItemDto>>.Conflict(projectResult.Message ?? MessageKeys.Project.Disabled)
                : Result<PagedData<CommentListItemDto>>.NotFound(projectResult.Message ?? MessageKeys.Project.NotFound);

        var projectId = projectResult.Data;

        var query = _unitOfWork.Repository<Comment>()
            .Query()
            .AsNoTracking()
            .Include(c => c.Replies)
            .Where(c => c.ProjectId == projectId && c.DeletedAt == null);

        if (filter.Status.HasValue)
            query = query.Where(c => c.Status == filter.Status.Value);

        if (filter.Environment.HasValue)
            query = query.Where(c => c.Environment == filter.Environment.Value);

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
            new PagedData<CommentListItemDto>(items.Select(c => MapToListItem(c, names)).ToList(), pagination, hiddenPrivateCount, pageContexts));
    }

    public async Task<Result<PagedData<CommentApplyItemDto>>> ListApplyQueueAsync(string projectKey, CommentFilter filter)
    {
        var projectResult = await _projectService.EnsureAsync(projectKey);
        if (!projectResult.IsSuccess)
            return projectResult.IsConflict
                ? Result<PagedData<CommentApplyItemDto>>.Conflict(projectResult.Message ?? MessageKeys.Project.Disabled)
                : Result<PagedData<CommentApplyItemDto>>.NotFound(projectResult.Message ?? MessageKeys.Project.NotFound);

        var projectId = projectResult.Data;

        var query = _unitOfWork.Repository<Comment>()
            .Query()
            .AsNoTracking()
            .Include(c => c.Replies)
            .Where(c => c.ProjectId == projectId && c.DeletedAt == null);

        if (filter.Status.HasValue)
            query = query.Where(c => c.Status == filter.Status.Value);

        if (filter.Environment.HasValue)
            query = query.Where(c => c.Environment == filter.Environment.Value);

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
        return Result<PagedData<CommentApplyItemDto>>.Success(
            new PagedData<CommentApplyItemDto>(items.Select(c => MapToApplyItem(c, names)).ToList(), pagination, pageContexts: pageContexts));
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

        var names = await ResolveNamesAsync(AuthorIds(comment));
        return Result<CommentResponse>.Success(MapToResponse(comment, names));
    }

    public async Task<Result<CommentResponse>> UpdateStatusAsync(int id, UpdateCommentStatusRequest request, Guid actorId)
    {
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
        }

        if (!string.IsNullOrWhiteSpace(request.Reply))
        {
            // Replies inherit the parent comment's tenant owner.
            var reply = new Reply
            {
                CommentId = comment.Id,
                AuthorId = actorId,
                Body = request.Reply.Trim(),
                OwnerId = comment.OwnerId
            };
            // comment is tracked (loaded without AsNoTracking); adding to its
            // collection lets EF insert the new reply on save. Do NOT also call
            // AddAsync — that double-adds the reply to the in-memory graph.
            comment.Replies.Add(reply);
        }

        _unitOfWork.Repository<Comment>().Update(comment);
        await _unitOfWork.SaveChangesAsync();

        var names = await ResolveNamesAsync(AuthorIds(comment));
        var message = request.Status == CommentStatus.Applied ? MessageKeys.Comment.Applied : null;
        return Result<CommentResponse>.Success(MapToResponse(comment, names), message);
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
        return Result<CommentResponse>.Success(MapToResponse(comment, editNames), "Comment updated.");
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
        return Result<CommentResponse>.Success(MapToResponse(comment, names));
    }

    public async Task<Result<ReplyResponse>> AddReplyAsync(int commentId, AddReplyRequest request, Guid authorId)
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

        // Replies inherit the parent comment's tenant owner.
        var reply = new Reply
        {
            CommentId = commentId,
            AuthorId = authorId,
            Body = body,
            OwnerId = comment.OwnerId
        };

        await _unitOfWork.Repository<Reply>().AddAsync(reply);
        await _unitOfWork.SaveChangesAsync();

        var names = await ResolveNamesAsync(new[] { reply.AuthorId });
        return Result<ReplyResponse>.Success(MapReplyToResponse(reply, names));
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
    private async Task<Dictionary<Guid, string>> ResolveNamesAsync(IEnumerable<Guid> ids)
    {
        var distinct = ids.Where(g => g != Guid.Empty).Distinct().ToList();
        if (distinct.Count == 0)
            return new Dictionary<Guid, string>();

        return await _unitOfWork.Repository<User>()
            .Query()
            .AsNoTracking()
            .Where(u => distinct.Contains(u.PublicId))
            .ToDictionaryAsync(u => u.PublicId, u => u.DisplayName);
    }

    private static ReplyResponse MapReplyToResponse(Reply reply, IReadOnlyDictionary<Guid, string> names) => new()
    {
        Id = reply.Id,
        AuthorId = reply.AuthorId,
        AuthorName = names.GetValueOrDefault(reply.AuthorId),
        Body = reply.Body,
        CreatedAt = reply.CreatedAt
    };

    private CommentListItemDto MapToListItem(Comment comment, IReadOnlyDictionary<Guid, string> names) => new()
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
        EditedAt = comment.EditedAt,
        // Labels only — the prompts are intentionally never exposed here.
        PickedActionTexts = comment.PickedActions.Select(a => a.Text).ToList(),
        Element = MapElementToDto(comment.Element),
        Replies = comment.Replies.Select(r => MapReplyToResponse(r, names)).ToList(),
        IsBugReport = comment.IsBugReport,
        PageContextId = comment.PageContextSnapshotId
    };

    private CommentResponse MapToResponse(Comment comment, IReadOnlyDictionary<Guid, string> names) => new()
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
        EditedAt = comment.EditedAt,
        // Labels only — the prompts are intentionally never exposed here.
        PickedActionTexts = comment.PickedActions.Select(a => a.Text).ToList(),
        Element = MapElementToDto(comment.Element),
        Replies = comment.Replies.Select(r => MapReplyToResponse(r, names)).ToList(),
        IsBugReport = comment.IsBugReport,
        PageContext = MapPageContextToDto(comment.PageContextSnapshot)
    };

    // Apply-queue export mapper — the ONLY mapper that carries PickedActionPrompt (admin/AI path).
    private CommentApplyItemDto MapToApplyItem(Comment comment, IReadOnlyDictionary<Guid, string> names) => new()
    {
        Id = comment.Id,
        Status = comment.Status,
        Environment = comment.Environment,
        Body = comment.Body,
        AuthorId = comment.AuthorId,
        AuthorName = names.GetValueOrDefault(comment.AuthorId),
        CreatedAt = comment.CreatedAt,
        Element = MapElementToDto(comment.Element),
        Replies = comment.Replies.Select(r => MapReplyToResponse(r, names)).ToList(),
        // Apply/AI path: carries both label + prompt for each picked action.
        PickedActions = comment.PickedActions
            .Select(a => new PickedActionDto { Text = a.Text, Prompt = a.Prompt }).ToList(),
        IsBugReport = comment.IsBugReport,
        PageContextId = comment.PageContextSnapshotId
    };

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
