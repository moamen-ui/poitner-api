namespace Pointer.Domain.Enums;

public enum NotificationType
{
    CommentApplied = 1,
    CommentReopened = 2,
    ReplyAdded = 3,

    /// <summary>A stakeholder submitted a predefined-action suggestion. Received by the tenant's admins.</summary>
    SuggestionSubmitted = 4,

    /// <summary>An admin asked for changes on a suggestion. Received by the original submitter.</summary>
    SuggestionChangesRequested = 5,

    /// <summary>The submitter edited and resubmitted a suggestion an admin had sent back. Received by
    /// the tenant's admins.</summary>
    SuggestionResubmitted = 6,
}
