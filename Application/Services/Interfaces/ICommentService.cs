using Pointer.Application.DTOs.Comment;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface ICommentService
{
    Task<Result<CommentResponse>> CreateAsync(string projectKey, CreateCommentRequest request, Guid authorId);
    Task<Result<PagedData<CommentListItemDto>>> ListAsync(string projectKey, CommentFilter filter);
    Task<Result<CommentResponse>> GetByIdAsync(int id);
    Task<Result<CommentResponse>> UpdateStatusAsync(int id, UpdateCommentStatusRequest request, Guid actorId);
    Task<Result<ReplyResponse>> AddReplyAsync(int commentId, AddReplyRequest request, Guid authorId);
    Task<Result> DeleteAsync(int id, Guid actorId, bool isAdmin);
}
