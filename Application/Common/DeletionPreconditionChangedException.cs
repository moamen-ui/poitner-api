namespace Pointer.Application.Common;

/// <summary>
/// DB-18 code review (Opus MEDIUM): thrown ONLY by <c>TenantService.HardDeleteAsync</c>'s locked
/// re-check when a guarded reason ("demo_expired" or "owner_requested") is no longer due — a
/// conversion/extension or a cancel/operator-pause committed between the pre-check and the
/// <c>FOR UPDATE</c> lock. Deliberately a DEDICATED subtype of <see cref="InvalidOperationException"/>
/// (rather than throwing the base type directly) so the hosted sweep loops
/// (<c>WorkspaceDeletionService</c>, <c>DemoCleanupService</c>) can catch exactly this — and only
/// this — as a benign "someone else already handled it" skip that resets the per-id failure
/// counter. Catching plain <see cref="InvalidOperationException"/> for that purpose would also
/// swallow a genuine bug elsewhere in the call graph that happens to throw the same base type,
/// letting it retry forever without ever reaching the Critical log line.
/// </summary>
public sealed class DeletionPreconditionChangedException(string message)
    : InvalidOperationException(message);
