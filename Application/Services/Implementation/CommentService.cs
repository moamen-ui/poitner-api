using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
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
    private readonly IFileStorage _fileStorage;

    public CommentService(IUnitOfWork unitOfWork, IProjectService projectService, IFileStorage fileStorage)
    {
        _unitOfWork = unitOfWork;
        _projectService = projectService;
        _fileStorage = fileStorage;
    }

    public async Task<Result<CommentResponse>> CreateAsync(string projectKey, CreateCommentRequest request, Guid authorId)
    {
        var projectResult = await _projectService.EnsureAsync(projectKey);
        if (!projectResult.IsSuccess)
            return projectResult.IsConflict
                ? Result<CommentResponse>.Conflict(projectResult.Message ?? MessageKeys.Project.Disabled)
                : Result<CommentResponse>.NotFound(projectResult.Message ?? MessageKeys.Project.NotFound);

        var comment = new Comment
        {
            ProjectId = projectResult.Data,
            Environment = request.Environment,
            Status = CommentStatus.Open,
            AuthorId = authorId,
            Body = request.Body.Trim(),
            IsPrivate = request.IsPrivate,
            Element = MapToEntity(request.Element)
        };

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
        return Result<PagedData<CommentListItemDto>>.Success(
            new PagedData<CommentListItemDto>(items.Select(c => MapToListItem(c, names)).ToList(), pagination, hiddenPrivateCount));
    }

    public async Task<Result<CommentResponse>> GetByIdAsync(int id, Guid callerId)
    {
        var comment = await _unitOfWork.Repository<Comment>()
            .Query()
            .AsNoTracking()
            .Include(c => c.Replies)
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
            var reply = new Reply
            {
                CommentId = comment.Id,
                AuthorId = actorId,
                Body = request.Reply.Trim()
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
            .Where(c => c.Id == id && c.DeletedAt == null)
            .FirstOrDefaultAsync();

        if (comment == null)
            return Result<CommentResponse>.NotFound(MessageKeys.Comment.NotFound);

        // Own comments only — not even admins edit someone else's content.
        if (comment.AuthorId != editorId)
            return Result<CommentResponse>.Failure("You can only edit your own comments.");

        var body = (request.Body ?? string.Empty).Trim();
        if (body.Length == 0)
            return Result<CommentResponse>.Failure(MessageKeys.Comment.BodyRequired);
        comment.Body = body;

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
        var comment = await _unitOfWork.Repository<Comment>()
            .Query()
            .AsNoTracking()
            .Where(c => c.Id == commentId && c.DeletedAt == null)
            .FirstOrDefaultAsync();

        if (comment == null)
            return Result<ReplyResponse>.NotFound(MessageKeys.Comment.NotFound);

        var reply = new Reply
        {
            CommentId = commentId,
            AuthorId = authorId,
            Body = request.Body.Trim()
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
        ScreenshotUrl = dto.ScreenshotUrl
    };

    private static ElementCaptureDto MapElementToDto(ElementCapture entity) => new()
    {
        Selector = entity.Selector,
        Snapshot = entity.Snapshot,
        Classes = entity.Classes,
        ComputedStyles = entity.ComputedStyles,
        AppliedCssRules = entity.AppliedCssRules,
        SourcePath = entity.SourcePath,
        ParentInfo = entity.ParentInfo,
        ScreenshotUrl = entity.ScreenshotUrl
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

    private static CommentListItemDto MapToListItem(Comment comment, IReadOnlyDictionary<Guid, string> names) => new()
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
        Element = MapElementToDto(comment.Element),
        Replies = comment.Replies.Select(r => MapReplyToResponse(r, names)).ToList()
    };

    private static CommentResponse MapToResponse(Comment comment, IReadOnlyDictionary<Guid, string> names) => new()
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
        Element = MapElementToDto(comment.Element),
        Replies = comment.Replies.Select(r => MapReplyToResponse(r, names)).ToList()
    };

    private static IEnumerable<Guid> AuthorIds(Comment c) =>
        new[] { c.AuthorId }.Concat(c.Replies.Select(r => r.AuthorId));
}
