namespace Pointer.Application.DTOs.Event;

public class RecordEventRequest
{
    public string Type { get; set; } = string.Empty;

    /// <summary>Optional caller-declared source (e.g. "web-component"). Defaults to "cli" — every
    /// caller before the widget started using this endpoint was the CLI, and existing integrations
    /// don't send this field.</summary>
    public string? Source { get; set; }
    public string? ProjectKey { get; set; }
    public object? Meta { get; set; }
}
