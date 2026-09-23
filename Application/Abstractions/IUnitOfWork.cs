using Microsoft.EntityFrameworkCore;
using Pointer.Domain.Entity;

namespace Pointer.Application.Abstractions;

public interface IUnitOfWork
{
    IRepository<T> Repository<T>()
        where T : BaseEntity;
    DbSet<UsageEvent> UsageEvents { get; }
    DbSet<UsageDaily> UsageDaily { get; }
    DbSet<Workspace> Workspaces { get; }
    DbSet<UserAlias> UserAliases { get; }
    DbSet<AuditEvent> AuditEvents { get; }
    DbSet<ImpersonationSession> ImpersonationSessions { get; }
    Task<int> SaveChangesAsync();

    /// <summary>
    /// Executes the supplied action inside a DB transaction using the configured execution strategy
    /// (compatible with Npgsql retry strategies). Commits on success; rolls back on exception.
    /// </summary>
    Task ExecuteInTransactionAsync(Func<Task> action);

    /// <summary>
    /// Marks <paramref name="entity"/> so the next <see cref="SaveChangesAsync"/> preserves its
    /// current <see cref="BaseEntity.CreatedAt"/> value instead of stamping it with UtcNow.
    /// <para>
    /// Used by the comment-import path to restore original timestamps from an export file
    /// (Open Decision #2 — Option A of docs/superpowers/plans/2026-07-01-comment-export-import.md).
    /// <see cref="BaseEntity.CreatedBy"/> is still stamped to the importing user.
    /// </para>
    /// </summary>
    void PreserveCreatedAtOnInsert(BaseEntity entity);

    /// <summary>
    /// Detaches all tracked entities (EF <c>ChangeTracker.Clear()</c>). Used by batched bulk inserts
    /// (comment import) to bound change-tracker memory: flush a batch with <see cref="SaveChangesAsync"/>,
    /// then clear so the tracker doesn't accumulate the whole import graph.
    /// </summary>
    void ClearChangeTracker();

    /// <summary>
    /// H1 (TOCTOU fix): atomically increments <see cref="Domain.Entity.Invite.Uses"/> by 1 only
    /// when the invite is still valid (not deleted/revoked/expired and Uses &lt; MaxUses or unlimited).
    /// Returns the number of rows updated (1 = slot claimed; 0 = exhausted or revoked concurrently).
    /// The caller must NOT create the user when the return value is 0.
    /// </summary>
    Task<int> AtomicClaimInviteSlotAsync(int inviteId, DateTime now);

    /// <summary>
    /// DB-11c review finding #5: runs a raw, parameterised SQL statement against the underlying
    /// connection (no result rows expected) — used to <c>SELECT … FOR UPDATE</c>-lock a workspace's
    /// membership rows inside the caller's transaction, closing the race between two concurrent
    /// sole-admin-guard checks (S-13) over the same workspace. <c>{0}</c>-style placeholders are
    /// parameterised the same way as <c>ExecuteSqlRawAsync</c> everywhere else in EF Core. No-ops on
    /// a non-relational provider (<c>Database.IsRelational() == false</c> — e.g. the EF Core
    /// InMemory provider used by the test suite): callers must not depend on it for correctness
    /// there (those tests are single-threaded, so the race it closes cannot occur).
    /// </summary>
    Task ExecuteSqlRawAsync(string sql, params object[] parameters);
}
