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

    public CommentService(IUnitOfWork unitOfWork, IProjectService projectService)
    {
        _unitOfWork = unitOfWork;
        _projectService = projectService;
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
            Element = MapToEntity(request.Element)
        };

        await _unitOfWork.Repository<Comment>().AddAsync(comment);
        await _unitOfWork.SaveChangesAsync();

        return Result<CommentResponse>.Success(MapToResponse(comment), MessageKeys.Comment.Created);
    }

    public async Task<Result<PagedData<CommentListItemDto>>> ListAsync(string projectKey, CommentFilter filter)
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

        return Result<PagedData<CommentListItemDto>>.Success(
            new PagedData<CommentListItemDto>(items.Select(MapToListItem).ToList(), pagination));
    }

    public async Task<Result<CommentResponse>> GetByIdAsync(int id)
    {
        var comment = await _unitOfWork.Repository<Comment>()
            .Query()
            .AsNoTracking()
            .Include(c => c.Replies)
            .Where(c => c.Id == id && c.DeletedAt == null)
            .FirstOrDefaultAsync();

        if (comment == null)
            return Result<CommentResponse>.NotFound(MessageKeys.Comment.NotFound);

        return Result<CommentResponse>.Success(MapToResponse(comment));
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

        var message = request.Status == CommentStatus.Applied ? MessageKeys.Comment.Applied : null;
        return Result<CommentResponse>.Success(MapToResponse(comment), message);
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

        return Result<ReplyResponse>.Success(MapReplyToResponse(reply));
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
        ParentInfo = dto.ParentInfo
    };

    private static ElementCaptureDto MapElementToDto(ElementCapture entity) => new()
    {
        Selector = entity.Selector,
        Snapshot = entity.Snapshot,
        Classes = entity.Classes,
        ComputedStyles = entity.ComputedStyles,
        AppliedCssRules = entity.AppliedCssRules,
        SourcePath = entity.SourcePath,
        ParentInfo = entity.ParentInfo
    };

    private static ReplyResponse MapReplyToResponse(Reply reply) => new()
    {
        Id = reply.Id,
        AuthorId = reply.AuthorId,
        Body = reply.Body,
        CreatedAt = reply.CreatedAt
    };

    private static CommentListItemDto MapToListItem(Comment comment) => new()
    {
        Id = comment.Id,
        Status = comment.Status,
        Environment = comment.Environment,
        Body = comment.Body,
        AuthorId = comment.AuthorId,
        CreatedAt = comment.CreatedAt,
        AppliedAt = comment.AppliedAt,
        AppliedBy = comment.AppliedBy,
        AppliedByLabel = comment.AppliedByLabel,
        Element = MapElementToDto(comment.Element),
        Replies = comment.Replies.Select(MapReplyToResponse).ToList()
    };

    private static CommentResponse MapToResponse(Comment comment) => new()
    {
        Id = comment.Id,
        Status = comment.Status,
        Environment = comment.Environment,
        Body = comment.Body,
        AuthorId = comment.AuthorId,
        CreatedAt = comment.CreatedAt,
        AppliedAt = comment.AppliedAt,
        AppliedBy = comment.AppliedBy,
        AppliedByLabel = comment.AppliedByLabel,
        Element = MapElementToDto(comment.Element),
        Replies = comment.Replies.Select(MapReplyToResponse).ToList()
    };
}
