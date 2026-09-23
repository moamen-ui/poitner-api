using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Pointer.Application.Abstractions;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure;

public class AppDbContext(
    DbContextOptions<AppDbContext> options,
    ICurrentUser currentUser,
    IConfiguration configuration
) : DbContext(options)
{
    // C1 hardening lever (default OFF → behavior identical to before). When a non-super-admin
    // principal has a null TenantId (no `tenant` claim → owner_id is null), the strict-own filter
    // `e.OwnerId == currentUser.TenantId` collapses to `owner_id IS NULL`, exposing the whole
    // legacy/global bucket. With this flag ON, a null-tenant non-super principal matches NOTHING on
    // strict-own entities. Enable it ONLY after back-filling owner_id and giving global projects a
    // real owner (see docs/reviews/fable-fix-plan.md, T4) — otherwise legitimate null-owner-project
    // stakeholders lose access. Config: "Tenancy:StrictNullTenantIsolation": true.
    // Process-static (read once from config); safe to bake into the cached EF model.
    private readonly bool _strictNullTenant = configuration.GetValue(
        "Tenancy:StrictNullTenantIsolation",
        false
    );

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
    public DbSet<ProjectBuild> ProjectBuilds => Set<ProjectBuild>();
    public DbSet<Reply> Replies => Set<Reply>();
    public DbSet<StatusPresentation> StatusPresentations => Set<StatusPresentation>();
    public DbSet<PredefinedAction> PredefinedActions => Set<PredefinedAction>();
    public DbSet<PredefinedActionSuggestion> PredefinedActionSuggestions =>
        Set<PredefinedActionSuggestion>();
    public DbSet<Invite> Invites => Set<Invite>();
    public DbSet<QuickAccessLink> QuickAccessLinks => Set<QuickAccessLink>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<ExtensionSite> ExtensionSites => Set<ExtensionSite>();
    public DbSet<PageContextSnapshot> PageContextSnapshots => Set<PageContextSnapshot>();
    public DbSet<AppEnvironment> AppEnvironments => Set<AppEnvironment>();
    public DbSet<ProjectAppUrl> ProjectAppUrls => Set<ProjectAppUrl>();
    public DbSet<AiRule> AiRules => Set<AiRule>();
    public DbSet<WorkspaceSetting> WorkspaceSettings => Set<WorkspaceSetting>();
    public DbSet<UsageEvent> UsageEvents => Set<UsageEvent>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<DeviceLogin> DeviceLogins => Set<DeviceLogin>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<WorkspaceMembership> WorkspaceMemberships => Set<WorkspaceMembership>();
    public DbSet<UserAlias> UserAliases => Set<UserAlias>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<ImpersonationSession> ImpersonationSessions => Set<ImpersonationSession>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Captured for the strict-own filters below. Process-static config value.
        var strict = _strictNullTenant;

        // Tenant isolation: every query is scoped to the current user's tenant by default.
        // Super-admin/system code paths (cascade delete, background jobs) must call
        // .IgnoreQueryFilters() explicitly on the query to bypass these filters.

        // Strict-own: visible only to the owning tenant or super-admin.
        b.Entity<Project>()
            .HasQueryFilter(e =>
                currentUser.IsSuperAdmin
                || (currentUser.TenantId != null && e.OwnerId == currentUser.TenantId)
                || (currentUser.TenantId == null && !strict && e.OwnerId == null)
            );
        // DB-11a: a person's presence in a workspace is a workspace_memberships row, never
        // users.owner_id. Any membership counts, INCLUDING ended ones — a person who left still
        // authored comments the workspace can see, and their name must resolve. Listing "current
        // members" always filters LeftAt == null explicitly at the call site (DB-RULES R8.7).
        b.Entity<User>()
            .HasQueryFilter(e =>
                currentUser.IsSuperAdmin
                || (
                    currentUser.TenantId != null
                    && e.Memberships.Any(m => m.OwnerId == currentUser.TenantId)
                )
                || (currentUser.TenantId == null && !strict && e.OwnerId == null)
            );
        // Strict-own like its owning User. Login and the backfill deliberately IgnoreQueryFilters —
        // they run before any tenant context exists — and stamp OwnerId from the user instead.
        b.Entity<ApiKey>()
            .HasQueryFilter(e =>
                currentUser.IsSuperAdmin
                || (currentUser.TenantId != null && e.OwnerId == currentUser.TenantId)
                || (currentUser.TenantId == null && !strict && e.OwnerId == null)
            );
        // DB-13 (F2): content — no unconditional super-admin branch; an operator reads this only under an impersonation token, whose tenant claim is the target workspace.
        b.Entity<Comment>()
            .HasQueryFilter(e =>
                (currentUser.TenantId != null && e.OwnerId == currentUser.TenantId)
                || (currentUser.TenantId == null && !strict && e.OwnerId == null)
            );
        // Strict-own, like QuickAccessLink: a build row says which of a tenant's shas are live, and
        // there is no global-fallback case where another tenant should see one.
        b.Entity<ProjectBuild>()
            .HasQueryFilter(e =>
                currentUser.IsSuperAdmin
                || (currentUser.TenantId != null && e.OwnerId == currentUser.TenantId)
            );
        // DB-13 (F2): content — no unconditional super-admin branch; an operator reads this only under an impersonation token, whose tenant claim is the target workspace.
        b.Entity<Reply>()
            .HasQueryFilter(e =>
                (currentUser.TenantId != null && e.OwnerId == currentUser.TenantId)
                || (currentUser.TenantId == null && !strict && e.OwnerId == null)
            );
        // PageContextSnapshot carries browser-captured console/network data for a tenant's project —
        // strict-own like Comment, so it can never leak across tenants through a future admin listing.
        // DB-13 (F2): content — no unconditional super-admin branch; an operator reads this only under an impersonation token, whose tenant claim is the target workspace.
        b.Entity<PageContextSnapshot>()
            .HasQueryFilter(e =>
                (currentUser.TenantId != null && e.OwnerId == currentUser.TenantId)
                || (currentUser.TenantId == null && !strict && e.OwnerId == null)
            );
        // Invites are strict-own (no "sees the global bucket" branch). OwnerId is non-null for
        // every invite that joins an existing tenant; the one exception (a super-admin "new
        // workspace" invite) is only ever read by a super admin (bypasses via IsSuperAdmin above)
        // or the anonymous accept/preview path, which always uses IgnoreQueryFilters() explicitly.
        b.Entity<Invite>()
            .HasQueryFilter(e =>
                currentUser.IsSuperAdmin
                || (currentUser.TenantId != null && e.OwnerId == currentUser.TenantId)
                || (currentUser.TenantId == null && !strict && e.OwnerId == null)
            );
        // Strict-own: a magic link belongs to exactly one tenant and is never global. Redemption is
        // anonymous and uses IgnoreQueryFilters() explicitly — there is no caller to scope by.
        b.Entity<QuickAccessLink>()
            .HasQueryFilter(e =>
                currentUser.IsSuperAdmin
                || (currentUser.TenantId != null && e.OwnerId == currentUser.TenantId)
            );
        // Own-plus-global: a tenant sees its own actions plus null-owner (global) ones — needed so
        // actions on a global/null-owner project (e.g. the marketing landing) resolve for that
        // project's null-owner stakeholders. Cross-project leakage is prevented separately by the
        // ProjectId scope in the widget-read query. A REAL tenant always sees the global bucket
        // (unaffected by `strict` — that was never the leak); only a null-tenant caller's access to
        // the global bucket is gated by `strict`, matching the strict-own group's C1 fix. (Naively
        // reusing `e.OwnerId == currentUser.TenantId` for the "own" branch would silently collapse to
        // `e.OwnerId == null` for a null-tenant caller too, defeating the strict gate below — hence
        // the explicit `TenantId != null` guard on the own branch.)
        // DB-13 (F2): tenant rows are content (D13.2); a plain operator keeps managing the global
        // (null-owner) bucket only — the first branch is rewritten, not removed, from an
        // unconditional super-admin match to a global-only super-admin match.
        b.Entity<PredefinedAction>()
            .HasQueryFilter(e =>
                (currentUser.IsSuperAdmin && currentUser.TenantId == null && e.OwnerId == null)
                || (
                    currentUser.TenantId != null
                    && (e.OwnerId == currentUser.TenantId || e.OwnerId == null)
                )
                || (currentUser.TenantId == null && !strict && e.OwnerId == null)
            );
        // STRICT-OWN (BINDING #5): suggestions are visible only to the owning tenant or super-admin —
        // NEVER own-plus-global. A null-owner suggestion is never written, and the strict filter keeps
        // one tenant from ever loading another tenant's (or a null-owner) pending suggestion by id.
        // DB-13 (F2): content — no unconditional super-admin branch; an operator reads this only under an impersonation token, whose tenant claim is the target workspace.
        b.Entity<PredefinedActionSuggestion>()
            .HasQueryFilter(e =>
                (currentUser.TenantId != null && e.OwnerId == currentUser.TenantId)
                || (currentUser.TenantId == null && !strict && e.OwnerId == null)
            );

        // Own-plus-global: tenants also see rows with OwnerId == null (super-admin/global defaults).
        // Same split as PredefinedAction above: a real tenant always sees the global bucket
        // regardless of `strict`; only a null-tenant caller's access to it is `strict`-gated.
        b.Entity<Role>()
            .HasQueryFilter(e =>
                currentUser.IsSuperAdmin
                || (
                    currentUser.TenantId != null
                    && (e.OwnerId == currentUser.TenantId || e.OwnerId == null)
                )
                || (currentUser.TenantId == null && !strict && e.OwnerId == null)
            );
        b.Entity<StatusPresentation>()
            .HasQueryFilter(e =>
                currentUser.IsSuperAdmin
                || (
                    currentUser.TenantId != null
                    && (e.OwnerId == currentUser.TenantId || e.OwnerId == null)
                )
                || (currentUser.TenantId == null && !strict && e.OwnerId == null)
            );

        // Strict-own (no global bucket — OwnerId is never null here, unlike Role/StatusPresentation
        // above): a tenant's override of a GLOBAL role's active status only ever exists for exactly
        // one tenant, so there is nothing to share.
        b.Entity<RoleTenantOverride>()
            .HasQueryFilter(e =>
                currentUser.IsSuperAdmin
                || (currentUser.TenantId != null && e.OwnerId == currentUser.TenantId)
            );

        // AppEnvironment: own-plus-global, exactly like Role — a super-admin-seeded catalog
        // ("default", "prod", "staging", "testing") every tenant sees, plus each tenant's own custom
        // environments layered on top.
        b.Entity<AppEnvironment>()
            .HasQueryFilter(e =>
                currentUser.IsSuperAdmin
                || (
                    currentUser.TenantId != null
                    && (e.OwnerId == currentUser.TenantId || e.OwnerId == null)
                )
                || (currentUser.TenantId == null && !strict && e.OwnerId == null)
            );
        // ProjectAppUrl: strict-own like Comment/Invite — a URL always belongs to exactly the
        // project's own tenant, never a shared/global bucket.
        b.Entity<ProjectAppUrl>()
            .HasQueryFilter(e =>
                currentUser.IsSuperAdmin
                || (currentUser.TenantId != null && e.OwnerId == currentUser.TenantId)
                || (currentUser.TenantId == null && !strict && e.OwnerId == null)
            );

        // AppSetting: no filter — not tenant data; guarded by endpoint authorization.

        // Plan: no filter — GLOBAL catalog, not tenant data; guarded by endpoint authorization
        // (super-admin CRUD; anonymous marketing read). Exactly like AppSetting.

        // Subscription + ExtensionSite: strict-own (OwnerId non-null) — like Invite.
        b.Entity<Subscription>()
            .HasQueryFilter(e =>
                currentUser.IsSuperAdmin
                || (currentUser.TenantId != null && e.OwnerId == currentUser.TenantId)
                || (currentUser.TenantId == null && !strict && e.OwnerId == null)
            );
        b.Entity<ExtensionSite>()
            .HasQueryFilter(e =>
                currentUser.IsSuperAdmin
                || (currentUser.TenantId != null && e.OwnerId == currentUser.TenantId)
                || (currentUser.TenantId == null && !strict && e.OwnerId == null)
            );
        // DB-13 (F2): content — no unconditional super-admin branch; an operator reads this only under an impersonation token, whose tenant claim is the target workspace.
        b.Entity<AiRule>()
            .HasQueryFilter(e =>
                (currentUser.TenantId != null && e.OwnerId == currentUser.TenantId)
                || (currentUser.TenantId == null && !strict && e.OwnerId == null)
            );
        // WorkspaceSetting: strict-own, Project-filter shape (R4-01) — one row per workspace. The
        // filter alone is never the only scope on a workspace-level read: a super admin passes it
        // for EVERY row, so services additionally filter .Where(x => x.OwnerId == owner) with an
        // owner resolved server-side (see CommentFieldService / R4-01 A2).
        b.Entity<WorkspaceSetting>()
            .HasQueryFilter(e =>
                currentUser.IsSuperAdmin
                || (currentUser.TenantId != null && e.OwnerId == currentUser.TenantId)
                || (currentUser.TenantId == null && !strict && e.OwnerId == null)
            );
        b.Entity<UsageEvent>()
            .HasQueryFilter(e =>
                currentUser.IsSuperAdmin
                || (currentUser.TenantId != null && e.OwnerId == currentUser.TenantId)
                || (currentUser.TenantId == null && !strict && e.OwnerId == null)
            );
        // DB-12: audit rows are metadata; super admin sees all (DB-13 does not narrow this).
        // Strict-own like UsageEvent — rows with owner_id IS NULL (operator-level, detached
        // workspaces) are visible only to super admins in production (strict=true).
        b.Entity<AuditEvent>()
            .HasQueryFilter(e =>
                currentUser.IsSuperAdmin
                || (currentUser.TenantId != null && e.OwnerId == currentUser.TenantId)
                || (currentUser.TenantId == null && !strict && e.OwnerId == null)
            );
        b.Entity<Notification>()
            .HasQueryFilter(e =>
                currentUser.IsSuperAdmin
                || (currentUser.TenantId != null && e.OwnerId == currentUser.TenantId)
                || (currentUser.TenantId == null && !strict && e.OwnerId == null)
            );

        // DeviceLogin: deliberately NO query filter — exempt from tenant isolation. /device/start
        // and /device/poll are anonymous (no session, let alone a tenant claim) and OwnerId is only
        // known once a signed-in user approves the row, so a strict-own filter would hide every
        // still-Pending row from the very GET/approve/deny endpoints that need to find it before
        // that happens. The code itself (43 random bytes for the device code, an 8-char alphabet
        // for the user code) is the access control; OwnerId is stamped on approval for
        // query-filter-shape parity with every other table only, and is never relied on to scope a
        // read. See DeviceLogin's remarks.

        // Workspace: strict-own on its own Id (DB-03) — a workspace is visible only to itself or a
        // super admin. There is no null-owner/global branch: a workspace's Id IS the tenant id, it
        // is never null.
        b.Entity<Workspace>()
            .HasQueryFilter(e =>
                currentUser.IsSuperAdmin
                || (currentUser.TenantId != null && e.Id == currentUser.TenantId)
            );

        // WorkspaceMembership: strict-own on OwnerId (the workspace), like Project. DB-11a.
        b.Entity<WorkspaceMembership>()
            .HasQueryFilter(e =>
                currentUser.IsSuperAdmin
                || (currentUser.TenantId != null && e.OwnerId == currentUser.TenantId)
            );

        // ImpersonationSession: metadata (§3.1) — strict-own copy of UsageEvent (:273-278 above). A
        // workspace admin may list the sessions that targeted THEIR workspace; the super admin sees
        // all (unchanged — this table is never narrowed, only the six content filters above are).
        b.Entity<ImpersonationSession>()
            .HasQueryFilter(e =>
                currentUser.IsSuperAdmin
                || (currentUser.TenantId != null && e.OwnerId == currentUser.TenantId)
                || (currentUser.TenantId == null && !strict && e.OwnerId == null)
            );

        // UserAlias: no query filter. It is a lookup table keyed by a uuid the caller already
        // holds, joined to `users`, which is filtered — same reasoning as Workspace. DB-11a.
    }

    // Entities whose CreatedAt must survive the SaveChangesAsync stamping loop (the comment-import
    // path restores original timestamps from an export file). Membership is consumed once on save.
    // Reference equality (BaseEntity has no custom Equals) is correct: the same tracked instance is
    // registered here and encountered in ChangeTracker.Entries.
    private readonly HashSet<BaseEntity> _preserveCreatedAt = new();

    /// <summary>Opts <paramref name="entity"/> out of the UtcNow-Now CreatedAt stamp on its next insert.</summary>
    public void PreserveCreatedAtOnInsert(BaseEntity entity) => _preserveCreatedAt.Add(entity);

    /// <summary>DB-12: audit_events is append-only. The Postgres trigger is the authority; this is
    /// the early, provider-agnostic error — shared by every SaveChanges(Async) overload (review
    /// finding #12) so an update/delete of an AuditEvent can't slip through by using a different
    /// overload than SaveChangesAsync(CancellationToken).</summary>
    private void EnforceAuditEventsAppendOnly()
    {
        if (
            ChangeTracker
                .Entries<AuditEvent>()
                .Any(e => e.State is EntityState.Modified or EntityState.Deleted)
        )
            throw new InvalidOperationException(
                "audit_events is append-only (DB-12): update/delete is not allowed."
            );
    }

    // Review finding #12 (NIT): the append-only guard above used to run ONLY on
    // SaveChangesAsync(CancellationToken) — SaveChanges() and SaveChangesAsync(bool,
    // CancellationToken) are separate virtual entry points on DbContext (the sync path does not go
    // through the async override at all) and could bypass the guard entirely. Grepped: no
    // production caller uses any overload but the plain SaveChangesAsync() (only
    // Infrastructure/Repository/UnitOfWork.cs calls db.SaveChangesAsync()); throwing
    // NotSupportedException on the others is NOT the safe option here, because ~200 Tests/ call
    // sites seed data with the synchronous SaveChanges() and would break at RUNTIME. Routing those
    // two overloads through the CreatedAt/UpdatedAt STAMPING as well is *also* not safe — several
    // existing tests deliberately backdate CreatedAt on an entity and then seed it via the
    // synchronous SaveChanges() specifically because that path historically left an explicit
    // CreatedAt untouched (proven by re-running the suite: routing sync SaveChanges through the
    // stamping loop silently overwrote those backdated values with DateTime.UtcNow and broke
    // PublicStatsTests/PlatformInsightsServiceTests' hour-delta assertions). So: every overload gets
    // the guard; stamping stays exactly where it always was, on SaveChangesAsync(CancellationToken).
    public override int SaveChanges() => SaveChanges(acceptAllChangesOnSuccess: true);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceAuditEventsAppendOnly();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken ct = default
    )
    {
        EnforceAuditEventsAppendOnly();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
    }

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        EnforceAuditEventsAppendOnly();

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
                e.Entity.UpdatedAt = now;
                e.Entity.UpdatedBy = uid;
                if (
                    e.Entity.DeletedAt is not null
                    && e.Property(nameof(BaseEntity.DeletedAt)).IsModified
                )
                    e.Entity.DeletedBy = uid;
            }
        }
        return base.SaveChangesAsync(ct);
    }
}
