namespace Pointer.Application.Services.Interfaces;

/// <summary>DB-18 §3.3. One source of truth for "is this workspace frozen".</summary>
public sealed record WorkspaceFreeze(
    bool IsFrozen,
    bool IsPaused,
    bool PausedByOperator,
    DateTime? DeletionScheduledFor
);

/// <summary>
/// DB-18 §3.3. Single source of truth for "frozen" — read by <c>WorkspaceFrozenFilter</c>,
/// <c>ProjectService.CheckWidgetActiveAsync</c>, <c>InviteService.AcceptJoinExistingWorkspaceAsync</c>
/// and <c>AuthService.RegisterAsync</c>. Single API instance on one VM → in-process cache
/// invalidation is sufficient; worst case after a crash is ≤ 30 s staleness.
/// </summary>
public interface IWorkspaceStateService
{
    /// <summary>Cached 30 s per workspace (IMemoryCache key "wsfrozen:{id:N}"); IgnoreQueryFilters +
    /// AsNoTracking + explicit <c>w.Id == workspaceId &amp;&amp; w.DeletedAt == null</c> (Gemini LOW
    /// #2); a missing or soft-deleted row → not frozen.</summary>
    Task<WorkspaceFreeze> GetAsync(Guid workspaceId);

    /// <summary>Called by every write in §3.4 right after its SaveChangesAsync.</summary>
    void Invalidate(Guid workspaceId);
}
