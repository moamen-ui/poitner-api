using Pointer.Domain.Enums;
using Pointer.Domain.ValueObjects;

namespace Pointer.Application.DTOs.Workspace;

/// <summary>
/// One admin-defined comment field definition, as sent/returned by
/// <c>GET|PUT /api/admin/workspace/comment-fields</c> and exposed (enabled entries only) on the
/// widget's capture-config. Rules are enforced by CommentFieldService.ValidateDefinitions (single
/// source of truth) and mirrored field-by-field by UpdateCommentFieldDefinitionsValidator.
/// </summary>
public class CommentFieldDefinitionDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public CommentFieldType Type { get; set; }
    public List<string> Options { get; set; } = new();
    public List<string> AllowedHosts { get; set; } = new();
    public string? SuggestedTool { get; set; }
    public string? Hint { get; set; }
    public bool Enabled { get; set; }
    public int SortOrder { get; set; }

    public static CommentFieldDefinitionDto FromDomain(CommentFieldDefinition d) => new()
    {
        Key = d.Key,
        Label = d.Label,
        Type = d.Type,
        Options = d.Options,
        AllowedHosts = d.AllowedHosts,
        SuggestedTool = d.SuggestedTool,
        Hint = d.Hint,
        Enabled = d.Enabled,
        SortOrder = d.SortOrder
    };
}

/// <summary>
/// A stored field value joined with its definition for the read DTOs (comment list/detail/apply
/// item). A value whose definition no longer exists is still returned, with Label = Key,
/// Type = Text and no suggested tool — never hide data the stakeholder typed.
/// </summary>
public class CommentFieldValueDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public CommentFieldType Type { get; set; }
    public string Value { get; set; } = string.Empty;
    public string? SuggestedTool { get; set; }
}
