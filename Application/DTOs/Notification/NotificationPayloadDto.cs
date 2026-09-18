namespace Pointer.Application.DTOs.Notification;

public class NotificationPayloadDto
{
    public string? CommitUrl { get; set; }
    public string? AppliedByLabel { get; set; }
    public string? ReplyExcerpt { get; set; }
    public string? SuggestionText { get; set; }
    public string? AdminFeedback { get; set; }
}
