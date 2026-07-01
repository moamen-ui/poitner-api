using System.Text.Json.Serialization;

namespace Pointer.Application.DTOs.Export;

/// <summary>
/// Element capture as serialized in the versioned export schema (snake_case keys).
/// <para>
/// <see cref="ScreenshotUrl"/> is ALWAYS null on export (screenshots are not transferred);
/// <see cref="ScreenshotOmitted"/> flags that a screenshot existed so importers can warn.
/// </para>
/// </summary>
public class ElementCaptureExportDto
{
    [JsonPropertyName("selector")]
    public string? Selector { get; set; }

    [JsonPropertyName("snapshot")]
    public string? Snapshot { get; set; }

    [JsonPropertyName("classes")]
    public string? Classes { get; set; }

    [JsonPropertyName("computed_styles")]
    public string? ComputedStyles { get; set; }

    [JsonPropertyName("applied_css_rules")]
    public string? AppliedCssRules { get; set; }

    [JsonPropertyName("source_path")]
    public string? SourcePath { get; set; }

    [JsonPropertyName("parent_info")]
    public string? ParentInfo { get; set; }

    [JsonPropertyName("screenshot_url")]
    public string? ScreenshotUrl { get; set; }

    [JsonPropertyName("screenshot_omitted")]
    public bool ScreenshotOmitted { get; set; }

    [JsonPropertyName("page_url")]
    public string? PageUrl { get; set; }

    [JsonPropertyName("route")]
    public string? Route { get; set; }

    [JsonPropertyName("page_title")]
    public string? PageTitle { get; set; }

    [JsonPropertyName("viewport_width")]
    public int? ViewportWidth { get; set; }

    [JsonPropertyName("viewport_height")]
    public int? ViewportHeight { get; set; }

    [JsonPropertyName("device_type")]
    public string? DeviceType { get; set; }

    [JsonPropertyName("device_pixel_ratio")]
    public double? DevicePixelRatio { get; set; }
}
