using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Repository;

public sealed class UnitOfWork(AppDbContext db) : IUnitOfWork
{
    private readonly Dictionary<Type, object> _repos = new();

    public IRepository<T> Repository<T>() where T : BaseEntity
    {
        if (!_repos.TryGetValue(typeof(T), out var r))
        {
            r = new Repository<T>(db);
            _repos[typeof(T)] = r;
        }
        return (IRepository<T>)r;
    }

    public Task<int> SaveChangesAsync() => db.SaveChangesAsync();

    /// <inheritdoc />
    public async Task<int> AtomicClaimInviteSlotAsync(int inviteId, DateTime now)
    {
        // For relational providers (Postgres in production): issue a single atomic UPDATE … WHERE
        // that returns rows-affected — this is the H1 TOCTOU fix. The condition on MaxUses in the
        // WHERE clause ensures the increment only fires if the slot is still available, so two
        // concurrent callers cannot both succeed for a MaxUses=1 invite.
        if (db.Database.ProviderName != "Microsoft.EntityFrameworkCore.InMemory")
        {
            return await db.Invites
                .IgnoreQueryFilters()
                .Where(i => i.Id == inviteId
                            && i.DeletedAt == null
                            && i.RevokedAt == null
                            && i.ExpiresAt > now
                            && (i.MaxUses == null || i.Uses < i.MaxUses))
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.Uses, i => i.Uses + 1));
        }

        // InMemory provider does not support ExecuteUpdateAsync (relational-only). For tests: load
        // the tracked row and apply the same condition + increment manually. Not concurrency-safe
        // (tests are single-threaded) but behaviourally equivalent for all service-level tests.
        var invite = await db.Invites
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == inviteId
                                      && i.DeletedAt == null
                                      && i.RevokedAt == null
                                      && i.ExpiresAt > now
                                      && (i.MaxUses == null || i.Uses < i.MaxUses));
        if (invite == null) return 0;
        invite.Uses += 1;
        return await db.SaveChangesAsync();
    }

    public void PreserveCreatedAtOnInsert(BaseEntity entity) => db.PreserveCreatedAtOnInsert(entity);

    public async Task ExecuteInTransactionAsync(Func<Task> action)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(
            state: action,
            operation: async (_, state, ct) =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await state();
                await tx.CommitAsync(ct);
                return true;
            },
            verifySucceeded: null);
    }
}
