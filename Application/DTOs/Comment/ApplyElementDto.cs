using System.Text.Json;

namespace Pointer.Application.DTOs.Comment;

/// <summary>
/// Compact, apply-queue-only element shape. Unlike <see cref="ElementCaptureDto"/> (used by the
/// widget/dashboard-facing DTOs), <c>Classes</c>/<c>ComputedStyles</c>/<c>AppliedCssRules</c>/
/// <c>Parent</c> are parsed server-side from their stored JSON-string columns into real JSON —
/// no more JSON-strings-inside-JSON for the AI apply-tool to double-parse. <c>PageRef</c>/
/// <c>UaRef</c> replace the per-item page/viewport/UA fields — see <see cref="ApplyPageDto"/> on
/// the sibling <c>PagedData.Pages</c> dictionary.
/// </summary>
public class ApplyElementDto
{
    public string? PageRef { get; set; }
    public string? Selector { get; set; }
    public string? Snapshot { get; set; }
    public string? SourcePath { get; set; }
    public string? ScreenshotUrl { get; set; }

    /// <summary>Parsed from Element.Classes (stored as a JSON-string array). Null on failure only
    /// if the stored value itself was null/empty — a malformed non-empty string still falls back
    /// to a JSON string element (see CommentService.ParseJsonOrRaw) rather than disappearing.</summary>
    public JsonElement? Classes { get; set; }
    public JsonElement? ComputedStyles { get; set; }
    public JsonElement? AppliedCssRules { get; set; }
    public JsonElement? Parent { get; set; }
}

/// <summary>One entry in PagedData.Pages — keyed by `route + deviceType` (not route alone, which
/// would collide two comments on the same route captured from different devices/viewports).</summary>
public class ApplyPageDto
{
    public string? Url { get; set; }
    public string? Route { get; set; }
    public string? Title { get; set; }

    /// <summary>"{width}x{height}" in CSS px, e.g. "390x844" — compact form of ViewportWidth/Height.</summary>
    public string? Viewport { get; set; }
    public string? Device { get; set; }
    public double? Dpr { get; set; }

    /// <summary>Reference into the sibling PagedData.UserAgents dictionary.</summary>
    public string? UaRef { get; set; }
}
