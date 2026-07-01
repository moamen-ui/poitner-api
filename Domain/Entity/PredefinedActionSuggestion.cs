using Pointer.Domain.Enums;

namespace Pointer.Domain.Entity;

/// <summary>
/// A stakeholder-proposed predefined action on a project the suggester cannot edit. An admin
/// reviews it: approve mints a real <see cref="PredefinedAction"/> on the project; reject records
/// the decision. <c>SuggestedBy</c> = <see cref="BaseEntity.CreatedBy"/> (reused, auto-stamped).
///
/// <see cref="OwnerId"/> is the tenant isolation boundary and equals the target project's OwnerId.
/// Unlike <see cref="PredefinedAction"/> (own-plus-global), suggestions use a STRICT-OWN query
/// filter — a null-owner suggestion is never written.
/// </summary>
public class PredefinedActionSuggestion : BaseEntity
{
    /// <summary>Tenant owner (= the target project's OwnerId). Nullable to match the owner model.</summary>
    public Guid? OwnerId { get; set; }

    /// <summary>The target project (required — suggestions are always project-scoped).</summary>
    public int ProjectId { get; set; }

    /// <summary>Proposed visible label (bounded ≤256).</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Proposed LLM prompt (Postgres <c>text</c>).</summary>
    public string Prompt { get; set; } = string.Empty;

    public SuggestionStatus Status { get; set; } = SuggestionStatus.Pending;

    /// <summary>Admin who approved/rejected.</summary>
    public Guid? ReviewedBy { get; set; }

    public DateTime? ReviewedAt { get; set; }
}
