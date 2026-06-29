namespace Pointer.Application.DTOs.Status;

public class UpdateStatusPresentationRequest
{
    public string? Label { get; set; }
    public string? Color { get; set; }
    public int? Order { get; set; }
}
