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
    public DbSet<PredefinedAction> PredefinedActions => Set<PredefinedAction>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Tenant isolation: every query is scoped to the current user's tenant by default.
        // Super-admin/system code paths (cascade delete, background jobs) must call
        // .IgnoreQueryFilters() explicitly on the query to bypass these filters.

        // Strict-own: visible only to the owning tenant or super-admin.
        b.Entity<Project>().HasQueryFilter(e => currentUser.IsSuperAdmin || e.OwnerId == currentUser.TenantId);
        b.Entity<User>().HasQueryFilter(e => currentUser.IsSuperAdmin || e.OwnerId == currentUser.TenantId);
        b.Entity<Comment>().HasQueryFilter(e => currentUser.IsSuperAdmin || e.OwnerId == currentUser.TenantId);
        b.Entity<Reply>().HasQueryFilter(e => currentUser.IsSuperAdmin || e.OwnerId == currentUser.TenantId);
        // Own-plus-global: a tenant sees its own actions plus null-owner (global) ones — needed so
        // actions on a global/null-owner project (e.g. the marketing landing) resolve for that
        // project's null-owner stakeholders. Cross-project leakage is prevented separately by the
        // ProjectId scope in the widget-read query.
        b.Entity<PredefinedAction>().HasQueryFilter(e => currentUser.IsSuperAdmin || e.OwnerId == currentUser.TenantId || e.OwnerId == null);

        // Own-plus-global: tenants also see rows with OwnerId == null (super-admin/global defaults).
        b.Entity<Role>().HasQueryFilter(e => currentUser.IsSuperAdmin || e.OwnerId == currentUser.TenantId || e.OwnerId == null);
        b.Entity<StatusPresentation>().HasQueryFilter(e => currentUser.IsSuperAdmin || e.OwnerId == currentUser.TenantId || e.OwnerId == null);

        // AppSetting: no filter — not tenant data; guarded by endpoint authorization.
    }

    // Entities whose CreatedAt must survive the SaveChangesAsync stamping loop (the comment-import
    // path restores original timestamps from an export file). Membership is consumed once on save.
    // Reference equality (BaseEntity has no custom Equals) is correct: the same tracked instance is
    // registered here and encountered in ChangeTracker.Entries.
    private readonly HashSet<BaseEntity> _preserveCreatedAt = new();

    /// <summary>Opts <paramref name="entity"/> out of the UtcNow-Now CreatedAt stamp on its next insert.</summary>
    public void PreserveCreatedAtOnInsert(BaseEntity entity) => _preserveCreatedAt.Add(entity);

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var uid = currentUser.Id ?? Guid.Empty;
        foreach (var e in ChangeTracker.Entries<BaseEntity>())
        {
            if (e.State == EntityState.Added)
            {
                // Only stamp CreatedAt when the caller hasn't explicitly opted out (import path
                // restores the original timestamp from the export file).
                if (_preserveCreatedAt.Remove(e.Entity))
                    e.Entity.CreatedBy = uid;
                else
                {
                    e.Entity.CreatedAt = now;
                    e.Entity.CreatedBy = uid;
                }
            }
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
