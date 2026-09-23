namespace Pointer.Application.DTOs.Stats;

/// <summary>
/// DB-15: the caller's own workspace "getting started" checklist — which activation steps this
/// workspace has reached and which one is next. Backs <c>GET /api/admin/stats/activation</c>
/// (class-level Admin policy). Steps are one-shot facts from usage_events; a step with no row yet
/// is simply not done.
/// </summary>
public class WorkspaceActivationResponse
{
    /// <summary>Funnel order: demo_started / workspace_converted only when this workspace actually
    /// started as a demo; then always widget_installed, first_comment, first_apply.</summary>
    public List<ActivationStepStatus> Steps { get; set; } = new();

    /// <summary>Key of the first not-yet-done step of the core three (widget_installed →
    /// first_comment → first_apply); null when all three are done.</summary>
    public string? NextStepKey { get; set; }

    /// <summary>True when a demo_started row exists for this workspace (it entered via the demo path).</summary>
    public bool IsDemo { get; set; }
}

public class ActivationStepStatus
{
    /// <summary>A <c>UsageEventTypes</c> one-shot fact key — the dashboard maps keys to i18n.</summary>
    public string Key { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;
    public bool Done { get; set; }

    /// <summary>When this workspace's earliest row of the type was written; null when not done.</summary>
    public DateTime? ReachedAt { get; set; }

    /// <summary>The project key the step was reached on, when the fact is project-scoped.</summary>
    public string? ProjectKey { get; set; }
}
