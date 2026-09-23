using Microsoft.Extensions.Configuration;

namespace Pointer.Application.Common;

/// <summary>
/// DB-18 §3.4. Shared config readers for the workspace-deletion grace period and job cadence — used
/// by <c>WorkspaceLifecycleService</c>, <c>WorkspaceService</c> (the response's <c>GraceDays</c>) and
/// <c>WorkspaceDeletionService</c> so all three agree on the same clamp.
/// </summary>
public static class WorkspaceDeletionConfig
{
    public const int DefaultGraceDays = 7;
    public const int DefaultSweepMinutes = 15;

    /// <summary>Clamp 0..14 (Opus NIT — keeps "grace + 30 d backups" ≤ 44 d).</summary>
    public static int GraceDays(IConfiguration? config)
    {
        var raw = config?["WorkspaceDeletion:GraceDays"];
        return int.TryParse(raw, out var parsed) ? Math.Clamp(parsed, 0, 14) : DefaultGraceDays;
    }

    /// <summary>Clamp 1..60; e2e sets 1.</summary>
    public static int SweepMinutes(IConfiguration? config)
    {
        var raw = config?["WorkspaceDeletion:SweepMinutes"];
        return int.TryParse(raw, out var parsed) ? Math.Clamp(parsed, 1, 60) : DefaultSweepMinutes;
    }
}
