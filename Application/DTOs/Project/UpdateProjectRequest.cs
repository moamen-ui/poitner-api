using Pointer.Application.DTOs.PredefinedAction;
using Pointer.Domain.Enums;

namespace Pointer.Application.DTOs.Project;

public class UpdateProjectRequest
{
    public string? Name { get; set; }

    /// <summary>null (property omitted) → leave untouched, per-environment activation flags.</summary>
    public bool? IsActiveLocal { get; set; }
    public bool? IsActiveStaging { get; set; }
    public bool? IsActiveProduction { get; set; }

    /// <summary>null (property omitted) → leave untouched, matching Name's treatment.</summary>
    public string? AppUrl { get; set; }

    /// <summary>null (property omitted) → leave untouched, matching IsActive's treatment.</summary>
    public bool? PageContextCaptureEnabled { get; set; }

    /// <summary>Whether the AI apply flow bundles applied comments into one commit or commits
    /// each separately (see Comment.CommitUrl). null (property omitted) → leave untouched.</summary>
    public CommitStyle? CommitStyle { get; set; }

    /// <summary>
    /// Which Role.Id values see the widget's environment switcher. null (property omitted) →
    /// leave untouched. An empty list is NOT the same as omitted — it explicitly clears back to
    /// the default (everyone except Client/QuickAccess roles).
    /// </summary>
    public List<int>? EnvironmentSelectorRoleIds { get; set; }

    /// <summary>
    /// Full desired set of project-scoped predefined actions. Reconcile (last-write-wins):
    /// id present → update; id absent → add; existing row absent from this list → soft-delete.
    /// null (property omitted) → leave actions untouched.
    /// </summary>
    public List<PredefinedActionInput>? PredefinedActions { get; set; }
}
