using System.Text.Json.Serialization;

namespace Pointer.Application.DTOs.Export;

/// <summary>
/// A comment as serialized in the versioned export schema (snake_case keys).
/// <para>
/// <c>export_id</c> is a synthetic key local to the file (e.g. "c-1") — never the DB id.
/// Enum values are serialized by NAME (not int) for forward compatibility.
/// Authors are display names; Guids are not portable across workspaces.
/// </para>
/// </summary>
public class CommentExportDto
{
    [JsonPropertyName("export_id")]
    public string? ExportId { get; set; }

    [JsonPropertyName("project_key")]
    public string? ProjectKey { get; set; }

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("environment")]
    public string Environment { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("is_private")]
    public bool IsPrivate { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("applied_at")]
    public DateTime? AppliedAt { get; set; }

    [JsonPropertyName("applied_by_label")]
    public string? AppliedByLabel { get; set; }

    [JsonPropertyName("edited_at")]
    public DateTime? EditedAt { get; set; }

    [JsonPropertyName("author_display_name")]
    public string? AuthorDisplayName { get; set; }

    [JsonPropertyName("applied_by_display_name")]
    public string? AppliedByDisplayName { get; set; }

    [JsonPropertyName("edited_by_display_name")]
    public string? EditedByDisplayName { get; set; }

    [JsonPropertyName("element")]
    public ElementCaptureExportDto? Element { get; set; }

    [JsonPropertyName("replies")]
    public List<ReplyExportDto> Replies { get; set; } = new();
}
