using Pointer.Application.DTOs.PredefinedAction;

namespace Pointer.Application.DTOs.Project;

public class UpdateProjectRequest
{
    public string? Name { get; set; }
    public bool? IsActive { get; set; }

    /// <summary>
    /// Full desired set of project-scoped predefined actions. Reconcile (last-write-wins):
    /// id present → update; id absent → add; existing row absent from this list → soft-delete.
    /// null (property omitted) → leave actions untouched.
    /// </summary>
    public List<PredefinedActionInput>? PredefinedActions { get; set; }
}
