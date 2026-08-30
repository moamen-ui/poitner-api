namespace Pointer.Application.DTOs.Project;

public class ProjectAppUrlResponse
{
    public int AppEnvironmentId { get; set; }
    public string EnvironmentName { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}
