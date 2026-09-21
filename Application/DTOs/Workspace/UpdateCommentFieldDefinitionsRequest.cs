namespace Pointer.Application.DTOs.Workspace;

/// <summary>
/// PUT /api/admin/workspace/comment-fields body. Replaces the workspace's whole definition list
/// (empty list = delete all definitions; stored comment values are kept and surface as orphans).
/// </summary>
public class UpdateCommentFieldDefinitionsRequest
{
    public List<CommentFieldDefinitionDto> Fields { get; set; } = new();
}
