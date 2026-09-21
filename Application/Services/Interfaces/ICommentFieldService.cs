using Pointer.Application.DTOs.Workspace;
using Pointer.Application.Response;
using Pointer.Domain.ValueObjects;

namespace Pointer.Application.Services.Interfaces;

/// <summary>
/// Admin-defined comment fields (R4-01) — the single source of truth for definition and value
/// validation. Definition schemas live on the workspace's one WorkspaceSetting row; values live
/// in comments.custom_fields; both are joined for read DTOs by <see cref="Resolve"/>.
/// </summary>
public interface ICommentFieldService
{
    /// <summary>
    /// Loads a workspace's definitions, sorted by SortOrder then Key. <paramref name="ownerId"/>
    /// MUST be resolved server-side (the project's/comment's OwnerId, or the current user) — never
    /// from request data. Null owner (a super-admin-created project) yields an empty list. This is
    /// the one read allowed to IgnoreQueryFilters: the caller already resolved the owner, and the
    /// widget user may be a quick-access user of that workspace.
    /// </summary>
    Task<List<CommentFieldDefinition>> GetDefinitionsForOwnerAsync(Guid? ownerId, bool enabledOnly, CancellationToken ct = default);

    /// <summary>
    /// Pure validation of submitted values against definitions. Null/empty input → empty map.
    /// Values are trimmed; a value that trims to empty removes the key (unset). Unknown keys,
    /// disabled definitions and rule violations (text length, url scheme/host/userinfo/length,
    /// select options) reject the whole map, as does a post-trim serialized size over 4000 chars.
    /// </summary>
    Result<Dictionary<string, string>> ValidateValues(IReadOnlyList<CommentFieldDefinition> defs, Dictionary<string, string>? input);

    /// <summary>
    /// Pure validation of a whole PUT list (the A1 rules) plus: at most 10 definitions, distinct
    /// keys, SortOrder re-normalised to 0..n-1 in the given order.
    /// </summary>
    Result<List<CommentFieldDefinition>> ValidateDefinitions(List<CommentFieldDefinitionDto> dtos);

    /// <summary>
    /// Joins stored values with definitions for the read DTOs. Orphan values (definition no
    /// longer exists) are still returned with Label = Key, Type = Text, SuggestedTool = null;
    /// disabled definitions with a stored value are returned too. Order = definition SortOrder,
    /// orphans last alphabetically.
    /// </summary>
    List<CommentFieldValueDto> Resolve(IReadOnlyList<CommentFieldDefinition> defs, Dictionary<string, string>? values);

    /// <summary>GET /api/admin/workspace/comment-fields — all definitions incl. disabled, sorted.</summary>
    Task<Result<CommentFieldDefinitionsResponse>> GetDefinitionsAsync();

    /// <summary>
    /// PUT /api/admin/workspace/comment-fields — replaces the whole list, upserting the
    /// workspace's WorkspaceSetting row. Mirrors AiRuleService.CreateAsync's guards: Forbidden
    /// for super admins and quick-access users; the owner is always derived server-side.
    /// </summary>
    Task<Result<CommentFieldDefinitionsResponse>> UpdateDefinitionsAsync(UpdateCommentFieldDefinitionsRequest request);
}
