using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Pointer.Application.Abstractions;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure;

public class AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUser currentUser, IConfiguration configuration) : DbContext(options)
{
    // C1 hardening lever (default OFF → behavior identical to before). When a non-super-admin
    // principal has a null TenantId (no `tenant` claim → owner_id is null), the strict-own filter
    // `e.OwnerId == currentUser.TenantId` collapses to `owner_id IS NULL`, exposing the whole
    // legacy/global bucket. With this flag ON, a null-tenant non-super principal matches NOTHING on
    // strict-own entities. Enable it ONLY after back-filling owner_id and giving global projects a
    // real owner (see docs/reviews/fable-fix-plan.md, T4) — otherwise legitimate null-owner-project
    // stakeholders lose access. Config: "Tenancy:StrictNullTenantIsolation": true.
    // Process-static (read once from config); safe to bake into the cached EF model.
    private readonly bool _strictNullTenant =
        configuration.GetValue("Tenancy:StrictNullTenantIsolation", false);

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // The strict-null-tenant flag is baked into the compiled query filters, so the model cache
        // key must vary by it — otherwise a context with the flag ON would reuse a model built with
        // it OFF (or vice-versa). Harmless in production (flag is process-static) but required so a
        // process that constructs contexts with different flag values (tests) gets distinct models.
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, TenancyModelCacheKeyFactory>();
    }

    private sealed class TenancyModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) =>
            (context.GetType(), (context as AppDbContext)?._strictNullTenant ?? false, designTime);

        public object Create(DbContext context) => Create(context, false);
    }

    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RoleTenantOverride> RoleTenantOverrides => Set<RoleTenantOverride>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<Reply> Replies => Set<Reply>();
    public DbSet<StatusPresentation> StatusPresentations => Set<StatusPresentation>();
    public DbSet<PredefinedAction> PredefinedActions => Set<PredefinedAction>();
    public DbSet<PredefinedActionSuggestion> PredefinedActionSuggestions => Set<PredefinedActionSuggestion>();
    public DbSet<Invite> Invites => Set<Invite>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<ExtensionSite> ExtensionSites => Set<ExtensionSite>();
    public DbSet<PageContextSnapshot> PageContextSnapshots => Set<PageContextSnapshot>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Captured for the strict-own filters below. Process-static config value.
        var strict = _strictNullTenant;

        // Tenant isolation: every query is scoped to the current user's tenant by default.
        // Super-admin/system code paths (cascade delete, background jobs) must call
        // .IgnoreQueryFilters() explicitly on the query to bypass these filters.

        // Strict-own: visible only to the owning tenant or super-admin.
        b.Entity<Project>().HasQueryFilter(e => currentUser.IsSuperAdmin || (currentUser.TenantId != null && e.OwnerId == currentUser.TenantId) || (currentUser.TenantId == null && !strict && e.OwnerId == null));
        b.Entity<User>().HasQueryFilter(e => currentUser.IsSuperAdmin || (currentUser.TenantId != null && e.OwnerId == currentUser.TenantId) || (currentUser.TenantId == null && !strict && e.OwnerId == null));
        b.Entity<Comment>().HasQueryFilter(e => currentUser.IsSuperAdmin || (currentUser.TenantId != null && e.OwnerId == currentUser.TenantId) || (currentUser.TenantId == null && !strict && e.OwnerId == null));
        b.Entity<Reply>().HasQueryFilter(e => currentUser.IsSuperAdmin || (currentUser.TenantId != null && e.OwnerId == currentUser.TenantId) || (currentUser.TenantId == null && !strict && e.OwnerId == null));
        // PageContextSnapshot carries browser-captured console/network data for a tenant's project —
        // strict-own like Comment, so it can never leak across tenants through a future admin listing.
        b.Entity<PageContextSnapshot>().HasQueryFilter(e => currentUser.IsSuperAdmin || (currentUser.TenantId != null && e.OwnerId == currentUser.TenantId) || (currentUser.TenantId == null && !strict && e.OwnerId == null));
        // Invites are strict-own (no "sees the global bucket" branch). OwnerId is non-null for
        // every invite that joins an existing tenant; the one exception (a super-admin "new
        // workspace" invite) is only ever read by a super admin (bypasses via IsSuperAdmin above)
        // or the anonymous accept/preview path, which always uses IgnoreQueryFilters() explicitly.
        b.Entity<Invite>().HasQueryFilter(e => currentUser.IsSuperAdmin || (currentUser.TenantId != null && e.OwnerId == currentUser.TenantId) || (currentUser.TenantId == null && !strict && e.OwnerId == null));
        // Own-plus-global: a tenant sees its own actions plus null-owner (global) ones — needed so
        // actions on a global/null-owner project (e.g. the marketing landing) resolve for that
        // project's null-owner stakeholders. Cross-project leakage is prevented separately by the
        // ProjectId scope in the widget-read query. A REAL tenant always sees the global bucket
        // (unaffected by `strict` — that was never the leak); only a null-tenant caller's access to
        // the global bucket is gated by `strict`, matching the strict-own group's C1 fix. (Naively
        // reusing `e.OwnerId == currentUser.TenantId` for the "own" branch would silently collapse to
        // `e.OwnerId == null` for a null-tenant caller too, defeating the strict gate below — hence
        // the explicit `TenantId != null` guard on the own branch.)
        b.Entity<PredefinedAction>().HasQueryFilter(e => currentUser.IsSuperAdmin || (currentUser.TenantId != null && (e.OwnerId == currentUser.TenantId || e.OwnerId == null)) || (currentUser.TenantId == null && !strict && e.OwnerId == null));
        // STRICT-OWN (BINDING #5): suggestions are visible only to the owning tenant or super-admin —
        // NEVER own-plus-global. A null-owner suggestion is never written, and the strict filter keeps
        // one tenant from ever loading another tenant's (or a null-owner) pending suggestion by id.
        b.Entity<PredefinedActionSuggestion>().HasQueryFilter(e => currentUser.IsSuperAdmin || (currentUser.TenantId != null && e.OwnerId == currentUser.TenantId) || (currentUser.TenantId == null && !strict && e.OwnerId == null));

        // Own-plus-global: tenants also see rows with OwnerId == null (super-admin/global defaults).
        // Same split as PredefinedAction above: a real tenant always sees the global bucket
        // regardless of `strict`; only a null-tenant caller's access to it is `strict`-gated.
        b.Entity<Role>().HasQueryFilter(e => currentUser.IsSuperAdmin || (currentUser.TenantId != null && (e.OwnerId == currentUser.TenantId || e.OwnerId == null)) || (currentUser.TenantId == null && !strict && e.OwnerId == null));
        b.Entity<StatusPresentation>().HasQueryFilter(e => currentUser.IsSuperAdmin || (currentUser.TenantId != null && (e.OwnerId == currentUser.TenantId || e.OwnerId == null)) || (currentUser.TenantId == null && !strict && e.OwnerId == null));

        // Strict-own (no global bucket — OwnerId is never null here, unlike Role/StatusPresentation
        // above): a tenant's override of a GLOBAL role's active status only ever exists for exactly
        // one tenant, so there is nothing to share.
        b.Entity<RoleTenantOverride>().HasQueryFilter(e => currentUser.IsSuperAdmin || (currentUser.TenantId != null && e.OwnerId == currentUser.TenantId));

        // AppSetting: no filter — not tenant data; guarded by endpoint authorization.

        // Plan: no filter — GLOBAL catalog, not tenant data; guarded by endpoint authorization
        // (super-admin CRUD; anonymous marketing read). Exactly like AppSetting.

        // Subscription + ExtensionSite: strict-own (OwnerId non-null) — like Invite.
        b.Entity<Subscription>().HasQueryFilter(e => currentUser.IsSuperAdmin || (currentUser.TenantId != null && e.OwnerId == currentUser.TenantId) || (currentUser.TenantId == null && !strict && e.OwnerId == null));
        b.Entity<ExtensionSite>().HasQueryFilter(e => currentUser.IsSuperAdmin || (currentUser.TenantId != null && e.OwnerId == currentUser.TenantId) || (currentUser.TenantId == null && !strict && e.OwnerId == null));
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
