using Pointer.Domain.Enums;

namespace Pointer.Application.DTOs.Comment;

public class CreateCommentRequest
{
    public string Body { get; set; } = string.Empty;
    public EnvironmentTag Environment { get; set; }
    public ElementCaptureDto Element { get; set; } = new();
}
