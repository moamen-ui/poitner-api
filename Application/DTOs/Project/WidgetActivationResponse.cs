namespace Pointer.Application.DTOs.Project;

/// <summary>
/// Answers "should the widget render on this page at all" for a given project + page origin.
/// Anonymous, called by the widget itself before it renders anything (see ProjectService.
/// CheckWidgetActiveAsync for the exact gating rules).
/// </summary>
public class WidgetActivationResponse
{
    public bool Active { get; set; }
}
