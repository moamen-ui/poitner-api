namespace Pointer.Application.DTOs.Project;

public class SetProjectAppUrlRequest
{
    public string Url { get; set; } = string.Empty;

    /// <summary>Defaults true — a newly-added environment URL is active unless explicitly
    /// toggled off.</summary>
    public bool IsActive { get; set; } = true;
}
