namespace Pointer.Application.DTOs.Comment;

/// <summary>Author-only edit of a comment: change the body and/or remove its uploaded image.</summary>
public class EditCommentRequest
{
    public string Body { get; set; } = string.Empty;

    /// <summary>When true, the comment's uploaded screenshot is cleared and the file deleted.</summary>
    public bool RemoveScreenshot { get; set; }
}
