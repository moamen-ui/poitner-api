using Pointer.Domain.Enums;

namespace Pointer.Application.DTOs.Project;

/// <summary>
/// Widget-facing read of a project's capture toggle plus its display name — this is the only
/// call the widget makes at boot that resolves the project by key, so it also carries the name
/// the toolbar shows next to the environment indicator (helps a visitor confirm which project a
/// given install is actually bound to, since project keys aren't unique across a workspace).
/// </summary>
public class CaptureConfigResponse
{
    /// <summary>Needed so the widget's commit-style control (only shown when CanEditSettings) can
    /// PATCH /api/admin/projects/{id} — the widget otherwise only ever knows the project's `key`,
    /// not its numeric id.</summary>
    public int Id { get; set; }

    public bool PageContextCaptureEnabled { get; set; }
    /// <summary>When false, the widget emits no text content in the DOM snapshot for any element and
    /// sets pageTitle to •••; default true.</summary>
    public bool CaptureTextContent { get; set; } = true;
    public string Name { get; set; } = string.Empty;

    /// <summary>Whether the CURRENT authenticated caller should see the widget's environment
    /// switcher for this project — see ProjectService.ShowEnvironmentSelectorFor. The widget
    /// itself defaults to showing the switcher until this resolves post-login (this endpoint is
    /// [Authorize]-only, so there's no anonymous case here), then hides it if this is false.</summary>
    public bool ShowEnvironmentSelector { get; set; }

    /// <summary>Whether the AI apply flow bundles applied comments into one commit or commits each
    /// one separately — read by skill.md's Step 1, changeable via the widget's commit-style
    /// control (only rendered when CanEditSettings is true).</summary>
    public CommitStyle CommitStyle { get; set; }

    /// <summary>Whether the CURRENT authenticated caller is authorized to change project settings
    /// at all (admin or the project's creator — same gate as ProjectService.UpdateAsync). The
    /// widget must check this before rendering the commit-style control, rather than rendering it
    /// and letting the PATCH 403.</summary>
    public bool CanEditSettings { get; set; }
}
