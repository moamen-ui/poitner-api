using Pointer.Application.DTOs.PredefinedAction;

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

    /// <summary>
    /// Full desired set of project-scoped predefined actions. Reconcile (last-write-wins):
    /// id present → update; id absent → add; existing row absent from this list → soft-delete.
    /// null (property omitted) → leave actions untouched.
    /// </summary>
    public List<PredefinedActionInput>? PredefinedActions { get; set; }
}
