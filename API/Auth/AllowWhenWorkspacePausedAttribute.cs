namespace Pointer.API.Auth;

/// <summary>
/// DB-18 §3.5. Marks a controller or action as reachable while its workspace is frozen (paused or a
/// deletion is scheduled) — the exact opposite of the default (every non-GET action 423s while
/// frozen). <see cref="AllowKeySessions"/> additionally lets an API-key/CLI session through (only a
/// handful of actions do — <c>AuthController.Me</c>, <c>EventsController.RecordEvent</c>); every
/// other key-session call gets 423 even for reads (D18.4). The exact placement list is pinned by
/// <c>Tests/WorkspaceFreezeCoverageTests</c> — DB-18 §3.5/§6 test 7.
/// </summary>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Method,
    Inherited = false,
    AllowMultiple = false
)]
public sealed class AllowWhenWorkspacePausedAttribute : Attribute
{
    public bool AllowKeySessions { get; init; }
}
