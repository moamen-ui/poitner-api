namespace Pointer.Application.DTOs.Comment;

/// <summary>
/// PATCH /api/comments/{id}/fields body — the REPLACEMENT map of admin-defined field values
/// (author or workspace admin; see CommentService.UpdateFieldsAsync). Validated against the
/// workspace's definitions exactly like create; an empty/absent map clears all fields.
/// </summary>
public class UpdateCommentFieldsRequest
{
    public Dictionary<string, string> CustomFields { get; set; } = new();
}
