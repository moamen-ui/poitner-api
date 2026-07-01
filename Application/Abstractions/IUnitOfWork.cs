using Pointer.Domain.Entity;

namespace Pointer.Application.Abstractions;

public interface IUnitOfWork
{
    IRepository<T> Repository<T>() where T : BaseEntity;
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
    /// H1 (TOCTOU fix): atomically increments <see cref="Domain.Entity.Invite.Uses"/> by 1 only
    /// when the invite is still valid (not deleted/revoked/expired and Uses &lt; MaxUses or unlimited).
    /// Returns the number of rows updated (1 = slot claimed; 0 = exhausted or revoked concurrently).
    /// The caller must NOT create the user when the return value is 0.
    /// </summary>
    Task<int> AtomicClaimInviteSlotAsync(int inviteId, DateTime now);
}
