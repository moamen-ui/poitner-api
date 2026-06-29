using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure;

public class AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUser currentUser) : DbContext(options)
{
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<Reply> Replies => Set<Reply>();
    public DbSet<StatusPresentation> StatusPresentations => Set<StatusPresentation>();

    protected override void OnModelCreating(ModelBuilder b) =>
        b.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var uid = currentUser.Id ?? Guid.Empty;
        foreach (var e in ChangeTracker.Entries<BaseEntity>())
        {
            if (e.State == EntityState.Added) { e.Entity.CreatedAt = now; e.Entity.CreatedBy = uid; }
            else if (e.State == EntityState.Modified)
            {
                e.Entity.UpdatedAt = now; e.Entity.UpdatedBy = uid;
                if (e.Entity.DeletedAt is not null && e.Property(nameof(BaseEntity.DeletedAt)).IsModified)
                    e.Entity.DeletedBy = uid;
            }
        }
        return base.SaveChangesAsync(ct);
    }
}
