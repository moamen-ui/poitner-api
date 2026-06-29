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

        // 2) Seed the admin user (linked to the Admin role) if none exists yet.
        var adminEmail = config["ADMIN:EMAIL"];
        var adminPassword = config["ADMIN:PASSWORD"];
        if (string.IsNullOrEmpty(adminEmail) || string.IsNullOrEmpty(adminPassword))
            return;

        var adminRoleId = await db.Roles.Where(r => r.Name == "Admin").Select(r => r.Id).FirstAsync();

        var hasAdmin = await db.Users.AnyAsync(u =>
            u.DeletedAt == null && u.Role.GrantsAdmin);
        if (hasAdmin)
            return;

        db.Users.Add(new User
        {
            Email = adminEmail.Trim().ToLower(),
            PasswordHash = hasher.Hash(adminPassword),
            DisplayName = "Administrator",
            RoleId = adminRoleId,
            IsActive = true,
            PublicId = Guid.NewGuid(),
        });

        await db.SaveChangesAsync();
    }
}
