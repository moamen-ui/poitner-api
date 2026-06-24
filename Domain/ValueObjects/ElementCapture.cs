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
}
