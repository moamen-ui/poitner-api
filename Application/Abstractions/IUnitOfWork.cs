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
}
