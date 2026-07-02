using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Domain.ValueObjects;
using Pointer.Infrastructure;

namespace Pointer.API.Seed;

public static class AdminSeeder
{
    // Default roles seeded on first boot. "Admin" is a protected system role (grants dashboard
    // access, cannot be renamed/disabled). The rest are ordinary labels admins can manage.
    private static readonly (string Name, bool GrantsAdmin, bool IsSystem, bool IsSuperAdmin)[] DefaultRoles =
    {
        ("Admin", true, true, true),
        ("Workspace Admin", true, true, false),
        ("Developer", false, false, false),
        ("PM", false, false, false),
        ("Tester", false, false, false),
        ("Client", false, false, false),
    };

    public static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        // 1) Seed any missing default roles.
        var existingRoleNames = await db.Roles.Select(r => r.Name).ToListAsync();
        foreach (var (name, grantsAdmin, isSystem, isSuperAdmin) in DefaultRoles)
        {
            if (!existingRoleNames.Contains(name))
                db.Roles.Add(new Role { Name = name, GrantsAdmin = grantsAdmin, IsSystem = isSystem, IsSuperAdmin = isSuperAdmin });
        }
        await db.SaveChangesAsync();

        // Idempotently upgrade the existing "Admin" role to IsSuperAdmin (for databases seeded
        // before this flag existed). New databases already get it from the tuple above.
        var adminRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == "Admin");
        if (adminRole is not null && !adminRole.IsSuperAdmin)
        {
            adminRole.IsSuperAdmin = true;
            await db.SaveChangesAsync();
        }

        // 2) Reconcile the super-admin (operator) account FROM CONFIG. ADMIN__EMAIL / ADMIN__PASSWORD
        //    (env) are the source of truth: on every boot we create the super-admin if absent, or
        //    update its email/password to match if they changed — so rotating the operator credentials
        //    is just editing .env.prod and restarting the api. (The old behavior only created it once
        //    and ignored later env changes.)
        var adminEmail = config["ADMIN:EMAIL"];
        var adminPassword = config["ADMIN:PASSWORD"];
        if (string.IsNullOrEmpty(adminEmail) || string.IsNullOrEmpty(adminPassword))
            return;
        adminEmail = adminEmail.Trim().ToLower();

        try
        {
            var adminRoleId = await db.Roles.Where(r => r.IsSuperAdmin).Select(r => r.Id).FirstAsync();

            // Match the operator BY EMAIL (never rename an existing row into a duplicate email — that
            // would violate the unique-email index and crash startup). Create if absent, else ensure it
            // is an active, approved super-admin with the configured password.
            var user = await db.Users
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.DeletedAt == null && u.Email.ToLower() == adminEmail);

            if (user == null)
            {
                db.Users.Add(new User
                {
                    Email = adminEmail,
                    PasswordHash = hasher.Hash(adminPassword),
                    DisplayName = "Administrator",
                    RoleId = adminRoleId,
                    IsActive = true,
                    ApprovalStatus = ApprovalStatus.Approved,
                    PublicId = Guid.NewGuid(),
                });
            }
            else
            {
                if (user.RoleId != adminRoleId) user.RoleId = adminRoleId;
                if (!user.IsActive) user.IsActive = true;
                if (user.ApprovalStatus != ApprovalStatus.Approved) user.ApprovalStatus = ApprovalStatus.Approved;
                if (!hasher.Verify(adminPassword, user.PasswordHash)) user.PasswordHash = hasher.Hash(adminPassword);
                db.Users.Update(user);
            }

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Never let operator-account reconciliation crash API startup.
            Console.Error.WriteLine($"[AdminSeeder] super-admin reconcile skipped: {ex.Message}");
        }

        // 3) Seed monetization plans (Free + hidden Legacy) and backfill existing tenants. Idempotent;
        //    wrapped in try/catch so a plan-seed hiccup never blocks API startup.
        try
        {
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            await SeedPlansAsync(db, settings);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AdminSeeder] plan seed skipped: {ex.Message}");
        }
    }

    /// <summary>
    /// Seeds the Free plan (from catalog defaults, with the ONE real AppSetting map
    /// emailsPerMonth←EmailDailyCap) and a hidden internal Legacy plan (all entitlements -1), then
    /// backfills a Subscription(Legacy, Active) for every EXISTING tenant so they are never
    /// retroactively limited. Idempotent: never duplicates or overwrites an edited Free plan.
    /// </summary>
    private static async Task SeedPlansAsync(AppDbContext db, ISettingsService settings)
    {
        // ── Free plan (Slug=free, price 0, Visible) ──
        var free = await db.Plans.FirstOrDefaultAsync(p => p.Slug == "free");
        if (free == null)
        {
            // Build entitlements from the catalog defaults; the only real AppSetting map today is
            // emailsPerMonth ← EmailDailyCap. Everything else comes from FreePlanDefaults (catalog).
            var entitlements = BuildFreeEntitlements();
            var emailDailyCap = await settings.GetIntAsync(ISettingsService.EmailDailyCap, 250);
            entitlements.EmailsPerMonth = emailDailyCap;

            db.Plans.Add(new Plan
            {
                Name = "Free",
                Slug = "free",
                PriceMonthly = 0m,
                Currency = "USD",
                Interval = BillingInterval.Monthly,
                SortOrder = 0,
                IsActive = true,
                DisplayState = PlanDisplayState.Visible,
                FeatureBullets = new List<string>
                {
                    "3 projects",
                    "5 seats",
                    "100 comments / month",
                    "Community support"
                },
                Entitlements = entitlements
            });
            await db.SaveChangesAsync();
        }
        // If Free already exists, leave its entitlements untouched (admin may have edited them).

        // ── Hidden internal Legacy plan (Slug=legacy, inactive, all entitlements unlimited) ──
        var legacy = await db.Plans.FirstOrDefaultAsync(p => p.Slug == "legacy");
        if (legacy == null)
        {
            legacy = new Plan
            {
                Name = "Legacy",
                Slug = "legacy",
                PriceMonthly = 0m,
                Currency = "USD",
                Interval = BillingInterval.Monthly,
                SortOrder = 1000,
                IsActive = false,
                DisplayState = PlanDisplayState.Hidden,
                FeatureBullets = new List<string>(),
                Entitlements = BuildUnlimitedEntitlements()
            };
            db.Plans.Add(legacy);
            await db.SaveChangesAsync();
        }

        // ── ONE-TIME backfill of Subscription(Legacy, Active) for PRE-MONETIZATION tenants ──
        // Runs exactly once (guarded by LegacyBackfillCompleted). Without this guard the seeder would
        // re-run every boot and retroactively grant Legacy-unlimited to any NEW subless (Free) signup —
        // an enforcement bypass. After the one-time run, new signups correctly default to Free
        // (missing sub ⇒ Free). A tenant = a self-owning workspace-admin (GrantsAdmin && !IsSuperAdmin).
        if (await settings.GetBoolAsync(ISettingsService.LegacyBackfillCompleted, fallback: false))
            return;

        var tenantPublicIds = await db.Users
            .IgnoreQueryFilters()
            .Include(u => u.Role)
            .Where(u => u.DeletedAt == null
                        && u.Role.GrantsAdmin
                        && !u.Role.IsSuperAdmin
                        && u.OwnerId == u.PublicId)
            .Select(u => u.PublicId)
            .ToListAsync();

        if (tenantPublicIds.Count == 0)
        {
            // Fresh DB (no pre-existing tenants): mark done so future signups never get Legacy.
            await settings.SetBoolAsync(ISettingsService.LegacyBackfillCompleted, true);
            return;
        }

        var alreadySubscribed = await db.Subscriptions
            .IgnoreQueryFilters()
            .Where(s => tenantPublicIds.Contains(s.OwnerId))
            .Select(s => s.OwnerId)
            .ToListAsync();
        var subscribedSet = alreadySubscribed.ToHashSet();

        var now = DateTime.UtcNow;
        var added = false;
        foreach (var tenantId in tenantPublicIds)
        {
            if (subscribedSet.Contains(tenantId))
                continue;
            db.Subscriptions.Add(new Subscription
            {
                OwnerId = tenantId,
                PlanId = legacy.Id,
                Status = SubscriptionStatus.Active
            });
            added = true;
        }

        if (added)
            await db.SaveChangesAsync();

        // Mark the one-time backfill complete — later boots skip it, so new signups stay on Free.
        await settings.SetBoolAsync(ISettingsService.LegacyBackfillCompleted, true);
    }

    /// <summary>Free-plan entitlements = catalog defaults (int + bool). Callers override emailsPerMonth.</summary>
    private static PlanEntitlements BuildFreeEntitlements()
    {
        var e = new PlanEntitlements();
        foreach (var spec in EntitlementCatalog.All.Values)
        {
            var prop = typeof(PlanEntitlements).GetProperty(spec.Key)!;
            if (spec.Kind == EntitlementKind.Int)
                prop.SetValue(e, spec.DefaultInt);
            else
                prop.SetValue(e, spec.DefaultBool);
        }
        return e;
    }

    /// <summary>Legacy-plan entitlements = unlimited: -1 for every int, true for every bool.</summary>
    private static PlanEntitlements BuildUnlimitedEntitlements()
    {
        var e = new PlanEntitlements();
        foreach (var spec in EntitlementCatalog.All.Values)
        {
            var prop = typeof(PlanEntitlements).GetProperty(spec.Key)!;
            if (spec.Kind == EntitlementKind.Int)
                prop.SetValue(e, -1);
            else
                prop.SetValue(e, true);
        }
        return e;
    }
}
