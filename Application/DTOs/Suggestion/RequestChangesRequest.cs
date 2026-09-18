namespace Pointer.Application.DTOs.Suggestion;

/// <summary>Body for an admin asking the submitter to revise a Pending suggestion.</summary>
public class RequestChangesRequest
{
    /// <summary>Admin's feedback explaining what to change.</summary>
    public string Feedback { get; set; } = string.Empty;
}
