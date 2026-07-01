using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
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
    }
}
