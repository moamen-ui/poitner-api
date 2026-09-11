namespace Pointer.Application.DTOs.Meta;

public sealed class MetaResponse
{
    public string Version { get; set; } = string.Empty;
    public int ApiVersion { get; set; }
    public string MinCliVersion { get; set; } = string.Empty;
    public string? SkillVersion { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public DateTime ServerTime { get; set; }
}
