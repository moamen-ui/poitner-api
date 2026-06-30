namespace Pointer.Domain.ValueObjects;

public class ElementCapture
{
    public string? Selector { get; set; }
    public string? Snapshot { get; set; }
    public string? Classes { get; set; }
    public string? ComputedStyles { get; set; }
    public string? AppliedCssRules { get; set; }
    public string? SourcePath { get; set; }
    public string? ParentInfo { get; set; }
    public string? ScreenshotUrl { get; set; }
    /// <summary>Full URL of the page the element was on (includes route/query/hash).</summary>
    public string? PageUrl { get; set; }
    /// <summary>Active route relative to the origin: path + query params (+ hash).</summary>
    public string? Route { get; set; }
    /// <summary>document.title of that page, for human context.</summary>
    public string? PageTitle { get; set; }
}
