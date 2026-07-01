using Pointer.Application.DTOs.Comment;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface ICommentService
{
    Task<Result<CommentResponse>> CreateAsync(string projectKey, CreateCommentRequest request, Guid authorId);
    Task<Result<PagedData<CommentListItemDto>>> ListAsync(string projectKey, CommentFilter filter, Guid callerId);

    /// <summary>
    /// Admin-gated apply-queue export (the .NET analogue of pending.json). Returns self-contained
    /// items INCLUDING the snapshotted <c>PickedActionPrompt</c> so the apply-time LLM receives it.
    /// This is the ONLY path that surfaces the prompt — kept off the widget-facing DTOs.
    /// Tenant isolation is via the EF query filter (the endpoint is [Authorize(Admin)]).
    /// </summary>
    Task<Result<PagedData<CommentApplyItemDto>>> ListApplyQueueAsync(string projectKey, CommentFilter filter);
    Task<Result<CommentResponse>> GetByIdAsync(int id, Guid callerId);
    Task<Result<CommentResponse>> UpdateStatusAsync(int id, UpdateCommentStatusRequest request, Guid actorId);
    Task<Result<CommentResponse>> EditAsync(int id, EditCommentRequest request, Guid editorId);
    Task<Result<CommentResponse>> SetVisibilityAsync(int id, Guid callerId, bool isPrivate);
    Task<Result<ReplyResponse>> AddReplyAsync(int commentId, AddReplyRequest request, Guid authorId);
    Task<Result> DeleteAsync(int id, Guid actorId, bool isAdmin);
}
