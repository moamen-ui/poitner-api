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
