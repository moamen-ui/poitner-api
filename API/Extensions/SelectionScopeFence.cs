using Microsoft.AspNetCore.Http;

namespace Pointer.API.Extensions;

/// <summary>
/// DB-11b: which route a <c>scope=select_workspace</c> token may be used against. Static so tests can
/// call <see cref="Allows"/> directly.
/// </summary>
public static class SelectionScopeFence
{
    public static readonly PathString SwitchWorkspacePath = new("/api/auth/switch-workspace");

    /// <summary>GLM A6 (DB-11b): EXACT path match, case-insensitive, no trailing slash, no sub-routes —
    /// a future /api/auth/switch-workspace/anything must NOT accept a selection token.</summary>
    public static bool Allows(PathString path) =>
        path.HasValue && path.Equals(SwitchWorkspacePath, StringComparison.OrdinalIgnoreCase);
}
