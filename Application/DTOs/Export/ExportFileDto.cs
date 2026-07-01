using System.Text.Json.Serialization;

namespace Pointer.Application.DTOs.Export;

/// <summary>
/// Root object of the versioned export file. The schema version follows
/// <c>&lt;major&gt;.&lt;minor&gt;</c>: minor bumps are additive (importer ignores unknown
/// fields); major bumps are rejected by the importer.
/// </summary>
public class ExportFileDto
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = "1.0";

    [JsonPropertyName("exported_at")]
    public DateTime ExportedAt { get; set; }

    [JsonPropertyName("source_project")]
    public string? SourceProject { get; set; }

    [JsonPropertyName("source_server")]
    public string? SourceServer { get; set; }

    [JsonPropertyName("comments")]
    public List<CommentExportDto> Comments { get; set; } = new();
}
