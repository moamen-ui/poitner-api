namespace Pointer.Application.DTOs.Build;

/// <summary>
/// "This build is now live." Reported by the CLI after a deploy, or by the widget on boot.
/// </summary>
public class ReportBuildRequest
{
    /// <summary>The deployed build's commit sha (7–40 hex, case-insensitive).</summary>
    public string Sha { get; set; } = string.Empty;

    /// <summary>
    /// Shas this build is known to contain, computed by the CLI with `git merge-base --is-ancestor`.
    ///
    /// The server cannot work this out for itself — it has no clone — so ancestry is answered where
    /// the repository is. When omitted (the widget's path) only comments whose CommitSha equals
    /// <see cref="Sha"/> exactly can be marked, which is why the CLI path is the primary one.
    /// </summary>
    public List<string>? ContainsCommitShas { get; set; }
}
