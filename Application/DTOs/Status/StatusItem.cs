namespace Pointer.Application.DTOs.Status;

public class StatusItem
{
    public int Value { get; set; }
    public string Name { get; set; } = string.Empty;   // canonical enum name, e.g. "ReadyToApply"
    public string Label { get; set; } = string.Empty;  // display label, e.g. "Ready"
    public string Color { get; set; } = string.Empty;  // hex
    public int Order { get; set; }
}
