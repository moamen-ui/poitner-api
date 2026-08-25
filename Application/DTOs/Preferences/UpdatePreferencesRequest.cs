namespace Pointer.Application.DTOs.Preferences;

public class UpdatePreferencesRequest
{
    public string? Language { get; set; }
    public string? Theme { get; set; }

    /// <summary>Per-user "add comment" widget shortcut, e.g. "alt+shift+c". Send an empty string
    /// (not null) to reset to the widget's default; null (property omitted) leaves it untouched.</summary>
    public string? AddCommentShortcut { get; set; }
}
