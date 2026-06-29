namespace Pointer.Application.DTOs.Status;

public class StatusAdminItem
{
    public int Value { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DefaultLabel { get; set; } = string.Empty;
    public string DefaultColor { get; set; } = string.Empty;
    public int DefaultOrder { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public int Order { get; set; }
    public bool IsOverridden { get; set; }
}
