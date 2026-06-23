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
}
