using Pointer.Domain.Entity;

namespace Pointer.Application.Abstractions;

public interface IUnitOfWork
{
    IRepository<T> Repository<T>() where T : BaseEntity;
    Task<int> SaveChangesAsync();
}
