using System.Text.Json.Serialization;

namespace Pointer.Application.DTOs.Export;

/// <summary>
/// A reply as serialized in the versioned export schema (snake_case keys).
/// Author is a display name (Guids are meaningless across workspaces).
/// </summary>
public class ReplyExportDto
{
    [JsonPropertyName("export_id")]
    public string? ExportId { get; set; }

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("author_display_name")]
    public string? AuthorDisplayName { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }
}
