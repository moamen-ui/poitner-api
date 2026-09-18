using Pointer.Domain.Enums;

namespace Pointer.Application.DTOs.Notification;

public class NotificationDto
{
    public int Id { get; set; }
    public NotificationType Type { get; set; }
    public int? CommentId { get; set; }
    public int? SuggestionId { get; set; }
    public string ProjectKey { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public string CommentBodyExcerpt { get; set; } = string.Empty;
    public NotificationPayloadDto? Payload { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ReadAt { get; set; }
}
