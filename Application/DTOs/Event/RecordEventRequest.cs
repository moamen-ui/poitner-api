namespace Pointer.Application.DTOs.Event;

public class RecordEventRequest
{
    public string Type { get; set; } = string.Empty;
    public string? ProjectKey { get; set; }
    public object? Meta { get; set; }
}
