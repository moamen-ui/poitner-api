using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Services.Implementation;
using Pointer.Domain.Entity;
using Pointer.Infrastructure;

namespace Pointer.API.Startup;

/// <summary>
/// Moves every pre-hardening plaintext <c>User.ApiKey</c> into the hashed+encrypted <c>api_keys</c>
/// table, once, at startup, after migrations have run.
///
/// It exists as code rather than as SQL inside the migration because the rows need AES-GCM
/// encryption, which SQL cannot do. It is idempotent: a user who already has a key row is skipped,
/// so a restart — or a rollback and re-deploy — costs nothing.
///
/// The user's raw key is preserved exactly, so every key already written into a developer's
/// <c>.pointer/credentials.env</c> keeps working across the upgrade; only its storage changes.
/// </summary>
public static class ApiKeyBackfill
{
    /// <summary>Runs inline after MigrateAsync/SeedAsync so the ordering is explicit, not implicit.</summary>
    public static async Task RunAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var log = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("ApiKeyBackfill");
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<IApiKeyProtector>();

        // No tenant context exists at startup, so the strict-own filters would hide every row.
        var legacy = await db
            .Users.IgnoreQueryFilters()
            .Where(u => u.DeletedAt == null && u.ApiKey != null && u.ApiKey != "")
            .ToListAsync(ct);

        if (legacy.Count == 0)
            return;

        var existing = await db
            .ApiKeys.IgnoreQueryFilters()
            .Where(k => k.RevokedAt == null && k.DeletedAt == null)
            .Select(k => k.UserId)
            .ToListAsync(ct);

        var alreadyMigrated = existing.ToHashSet();
        var migrated = 0;

        foreach (var user in legacy)
        {
            if (!alreadyMigrated.Contains(user.Id))
            {
                var raw = user.ApiKey!;
                db.ApiKeys.Add(
                    new ApiKey
                    {
                        UserId = user.Id,
                        OwnerId = user.OwnerId,
                        Prefix = raw.Length >= 12 ? raw[..12] : raw,
                        Hash = protector.Hash(raw),
                        Encrypted = protector.Encrypt(raw),
                        Scopes = (int)ApiKeyScopes.Full,
                        CreatedAt = DateTime.UtcNow,
                    });
                migrated++;
            }

            // Clear the plaintext either way: a user who already had a row does not need the old
            // column, and leaving it behind would defeat the point of the migration.
            user.ApiKey = null;
        }

        await db.SaveChangesAsync(ct);

        log.LogInformation(
            "API-key backfill: migrated {Migrated} key(s) into api_keys and cleared {Cleared} plaintext column(s).",
            migrated,
            legacy.Count);
    }
}
