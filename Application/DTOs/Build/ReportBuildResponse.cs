namespace Pointer.Application.DTOs.Build;

public class ReportBuildResponse
{
    public string Sha { get; set; } = string.Empty;

    /// <summary>False when this sha had already been reported — the report is idempotent.</summary>
    public bool FirstSeen { get; set; }

    /// <summary>
    /// Only the comments THIS call moved to deployed. A repeat report returns an empty list rather
    /// than re-listing everything the sha ever carried, so a caller can print "N newly live"
    /// without double-counting.
    /// </summary>
    public List<int> DeployedCommentIds { get; set; } = new();
}
