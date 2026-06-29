using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Repository;

public class Repository<T>(AppDbContext db) : IRepository<T> where T : BaseEntity
{
    private readonly DbSet<T> _set = db.Set<T>();

    public IQueryable<T> Query() => _set;

    public async Task AddAsync(T e) => await _set.AddAsync(e);

    public void Update(T e) => _set.Update(e);

    public async Task<T?> GetByIdAsync(int id) => await _set.FindAsync(id);

    public void Remove(T e) => _set.Remove(e);

    public void RemoveRange(IEnumerable<T> entities) => _set.RemoveRange(entities);
}
