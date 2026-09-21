using Pointer.Domain.ValueObjects;

namespace Pointer.Domain.Entity;

/// <summary>
/// One row per workspace (tenant) holding workspace-level settings (R4-01). There is no Tenant
/// entity — the workspace IS its admin User row's <see cref="OwnerId"/> — so this table is keyed
/// by that owner id (unique among live rows). Created lazily on the first PUT of comment-field
/// definitions; the service never writes a null OwnerId (super admins are refused upstream).
/// Future workspace-level settings (e.g. the §32 webhook URL) go on this same row.
/// </summary>
public class WorkspaceSetting : BaseEntity
{
    public Guid? OwnerId { get; set; }

    /// <summary>Admin-defined comment fields, stored as one jsonb value (converter, not an owned type).</summary>
    public List<CommentFieldDefinition> CommentFieldDefinitions { get; set; } = new();
}
