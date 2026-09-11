namespace Pointer.Application.DTOs.Profile;

public class ApiKeyResponse
{
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>First 12 chars — lets a UI show which key this is without revealing it.</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>Last time this key was exchanged for a token. Null until first use.</summary>
    public DateTime? LastUsedAt { get; set; }
}
