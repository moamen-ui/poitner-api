using Pointer.Domain.Entity;

namespace Pointer.Application.Abstractions;

public interface IRepository<T> where T : BaseEntity
{
    IQueryable<T> Query();
    Task AddAsync(T e);
    void Update(T e);
    Task<T?> GetByIdAsync(int id);
}
