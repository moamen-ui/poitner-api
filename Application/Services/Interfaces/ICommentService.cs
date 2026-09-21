using Pointer.Application.DTOs.Comment;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface ICommentService
{
    Task<Result<CommentResponse>> CreateAsync(string projectKey, CreateCommentRequest request, Guid authorId, string? origin = null);
    Task<Result<PagedData<CommentListItemDto>>> ListAsync(string projectKey, CommentFilter filter, Guid callerId);

    /// <summary>Lean ?view=summary projection of the same query ListAsync runs (same status/
    /// environment/quick-access/private-comment rules) — see CommentSummaryDto.</summary>
    Task<Result<PagedData<CommentSummaryDto>>> ListSummaryAsync(string projectKey, CommentFilter filter, Guid callerId);

    /// <summary>
    /// Admin-gated apply-queue export (the .NET analogue of pending.json). Returns self-contained
    /// items INCLUDING the snapshotted <c>PickedActionPrompt</c> so the apply-time LLM receives it.
    /// This is the ONLY path that surfaces the prompt — kept off the widget-facing DTOs.
    /// Tenant isolation is via the EF query filter (the endpoint is [Authorize(Admin)]).
    /// </summary>
    Task<Result<PagedData<CommentApplyItemDto>>> ListApplyQueueAsync(string projectKey, CommentFilter filter);
    Task<Result<CommentResponse>> GetByIdAsync(int id, Guid callerId);
    Task<Result<CommentResponse>> UpdateStatusAsync(int id, UpdateCommentStatusRequest request, Guid actorId);
    Task<Result<CommentResponse>> VerifyAsync(int id, VerifyCommentRequest request, Guid actorId);
    Task<Result<CommentResponse>> EditAsync(int id, EditCommentRequest request, Guid editorId);

    /// <summary>
    /// R4-01: replace a comment's admin-defined field values (PATCH /api/comments/{id}/fields).
    /// Author (incl. quick-access) or workspace admin; the comment is loaded through the FILTERED
    /// query so another workspace's admin gets 404. Values are re-validated against the
    /// workspace's definitions; EditedAt/EditedBy are stamped.
    /// </summary>
    Task<Result<CommentResponse>> UpdateFieldsAsync(int id, UpdateCommentFieldsRequest request, Guid actorId);
    Task<Result<CommentResponse>> SetVisibilityAsync(int id, Guid callerId, bool isPrivate);
    Task<Result<ReplyResponse>> AddReplyAsync(int commentId, AddReplyRequest request, Guid authorId, string? origin = null);
    Task<Result<ReplyResponse>> EditReplyAsync(int replyId, UpdateReplyRequest request, Guid editorId);
    Task<Result> DeleteReplyAsync(int replyId, Guid actorId, bool isAdmin);
    Task<Result> DeleteAsync(int id, Guid actorId, bool isAdmin);
}
