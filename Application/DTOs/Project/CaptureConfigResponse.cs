namespace Pointer.Application.DTOs.Project;

/// <summary>
/// Widget-facing read of a project's capture toggle plus its display name — this is the only
/// call the widget makes at boot that resolves the project by key, so it also carries the name
/// the toolbar shows next to the environment indicator (helps a visitor confirm which project a
/// given install is actually bound to, since project keys aren't unique across a workspace).
/// </summary>
public class CaptureConfigResponse
{
    public bool PageContextCaptureEnabled { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Whether the CURRENT authenticated caller should see the widget's environment
    /// switcher for this project — see ProjectService.ShowEnvironmentSelectorFor. The widget
    /// itself defaults to showing the switcher until this resolves post-login (this endpoint is
    /// [Authorize]-only, so there's no anonymous case here), then hides it if this is false.</summary>
    public bool ShowEnvironmentSelector { get; set; }
}
