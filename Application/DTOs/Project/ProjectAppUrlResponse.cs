namespace Pointer.Application.DTOs.Project;

public class ProjectAppUrlResponse
{
    public int AppEnvironmentId { get; set; }
    public string EnvironmentName { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;

    /// <summary>Independent of Project's Local/Staging/Production activation — whether THIS
    /// specific environment+URL mapping is enabled.</summary>
    public bool IsActive { get; set; }
}
