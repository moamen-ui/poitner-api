namespace Pointer.Application.DTOs.Workspace;

/// <summary>Response of GET and PUT /api/admin/workspace/comment-fields — the whole list (PUT replaces it).</summary>
public class CommentFieldDefinitionsResponse
{
    public List<CommentFieldDefinitionDto> Fields { get; set; } = new();
}
