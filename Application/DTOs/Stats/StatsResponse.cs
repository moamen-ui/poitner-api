namespace Pointer.Application.DTOs.Stats;

public class StatsResponse
{
    public StatsTotals Totals { get; set; } = new();
    public List<ProjectStats> Projects { get; set; } = new();
}

public class StatsTotals
{
    public int Projects { get; set; }
    public int Users { get; set; }
    public int PendingUsers { get; set; }
    public int Comments { get; set; }
    public int Open { get; set; }
    public int Pending { get; set; }
    public int Completed { get; set; }
    public int PrivateComments { get; set; }
}

public class ProjectStats
{
    public int ProjectId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public int Comments { get; set; }
    public int Open { get; set; }
    public int Pending { get; set; }
    public int Completed { get; set; }
    public int PrivateComments { get; set; }
}
