using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Domain.Entity;
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

        // Target the SUPER-admin role specifically (not any GrantsAdmin role — that includes tenants).
        var adminRoleId = await db.Roles.Where(r => r.IsSuperAdmin).Select(r => r.Id).FirstAsync();

        var superAdmin = await db.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.DeletedAt == null && u.RoleId == adminRoleId);

        if (superAdmin == null)
        {
            db.Users.Add(new User
            {
                Email = adminEmail,
                PasswordHash = hasher.Hash(adminPassword),
                DisplayName = "Administrator",
                RoleId = adminRoleId,
                IsActive = true,
                PublicId = Guid.NewGuid(),
            });
        }
        else
        {
            var changed = false;
            if (!string.Equals(superAdmin.Email, adminEmail, StringComparison.OrdinalIgnoreCase))
            {
                superAdmin.Email = adminEmail;
                changed = true;
            }
            if (!hasher.Verify(adminPassword, superAdmin.PasswordHash))
            {
                superAdmin.PasswordHash = hasher.Hash(adminPassword);
                changed = true;
            }
            if (!superAdmin.IsActive)
            {
                superAdmin.IsActive = true;
                changed = true;
            }
            if (changed)
                db.Users.Update(superAdmin);
        }

        await db.SaveChangesAsync();
    }
}
