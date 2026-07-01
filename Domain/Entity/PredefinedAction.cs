namespace Pointer.Domain.Entity;

/// <summary>
/// An admin-defined comment action a stakeholder can pick when leaving a comment.
/// The visible <see cref="Text"/> is shown in the widget picker; the <see cref="Prompt"/>
/// is what the apply-time LLM receives and is NEVER exposed to the browser.
///
/// Scope is derived from which of <see cref="ProjectId"/> / <see cref="UserId"/> is set:
///   - Tenant-wide: ProjectId == null AND UserId == null
///   - Project:     ProjectId set
///   - User (FUTURE): UserId set
///
/// <see cref="OwnerId"/> is the tenant isolation boundary. It equals the owning PROJECT's OwnerId
/// (project-scoped) or the creating tenant (tenant-wide), and is NULLABLE — a null owner means a
/// global project/action (e.g. the marketing landing, or projects created before ownership existed),
/// mirroring how <c>Project.OwnerId</c> and the own-plus-global query filter already work.
/// </summary>
public class PredefinedAction : BaseEntity
{
    /// <summary>Tenant / owning-project owner. Null = global (matches a null-owner project).</summary>
    public Guid? OwnerId { get; set; }

    /// <summary>null = not project-specific (tenant-wide).</summary>
    public int? ProjectId { get; set; }

    /// <summary>null = not user-specific. FUTURE: user-defined configs.</summary>
    public Guid? UserId { get; set; }

    /// <summary>Visible label the stakeholder picks (bounded ≤256).</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Sent to the apply-time LLM. Postgres <c>text</c> — multi-paragraph, never exposed to the browser.</summary>
    public string Prompt { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }
}
