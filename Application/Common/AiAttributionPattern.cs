namespace Pointer.Application.Common;

/// <summary>
/// Shared shape for the AI attribution fields on a reply (<c>AiTool</c>/<c>AiModel</c>) — plain
/// identifiers like "claude-code" or "gpt-5.2", never free text. Used by AddReplyValidator and
/// UpdateCommentStatusValidator, and mirrored by CommentService.Normalize which lowercases/trims
/// before this ever runs against the stored value.
/// </summary>
public static class AiAttributionPattern
{
    public const string Regex = "^[A-Za-z0-9][A-Za-z0-9._:/+-]*$";
}
