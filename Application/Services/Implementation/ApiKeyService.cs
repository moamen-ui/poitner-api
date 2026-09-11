using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;

namespace Pointer.Application.Services.Implementation;

/// <summary>
/// Personal API keys, stored hashed for lookup and encrypted for display.
///
/// Every query here uses <c>IgnoreQueryFilters()</c>: login resolves a key before any tenant context
/// exists, and the backfill/last-used paths run outside a request entirely, so the strict-own filter
/// would return zero rows. Tenant scoping is preserved by stamping <see cref="ApiKey.OwnerId"/> from
/// the owning user and by only ever reaching a row through that user's own id or the raw key itself.
/// </summary>
public class ApiKeyService(IUnitOfWork unitOfWork, IApiKeyProtector protector) : IApiKeyService
{
    /// <summary>How stale LastUsedAt may get before we write again. Keeps a busy agent off the write path.</summary>
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    public async Task<ApiKeyResult> GetOrCreateAsync(Guid publicId)
    {
        var user = await FindUserAsync(publicId);
        if (user is null)
            return ApiKeyResult.NotFound();

        var existing = await ActiveKeyQuery(user.Id).FirstOrDefaultAsync();
        if (existing is not null)
        {
            var raw = protector.Decrypt(existing.Encrypted);
            // Undecryptable means "cannot display", never "missing" — minting here would silently
            // rotate every user's key the first time someone viewed it after a key change.
            return raw is null ? ApiKeyResult.Undecryptable(existing) : ApiKeyResult.Ok(existing, raw);
        }

        return await MintAsync(user);
    }

    public async Task<ApiKeyResult> RegenerateAsync(Guid publicId)
    {
        var user = await FindUserAsync(publicId);
        if (user is null)
            return ApiKeyResult.NotFound();

        var existing = await ActiveKeyQuery(user.Id).FirstOrDefaultAsync();
        if (existing is not null)
        {
            // Revoke rather than delete: the row is the record that this key once existed, and
            // LastUsedAt on it is the only evidence of where a leaked key had been used.
            existing.RevokedAt = DateTime.UtcNow;
            unitOfWork.Repository<ApiKey>().Update(existing);
            await unitOfWork.SaveChangesAsync();
        }

        return await MintAsync(user);
    }

    public async Task<ApiKey?> ResolveAsync(string rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
            return null;

        var hash = protector.Hash(rawKey.Trim());

        return await unitOfWork
            .Repository<ApiKey>()
            .Query()
            .IgnoreQueryFilters()
            .Include(k => k.User)
            .ThenInclude(u => u.Role)
            .FirstOrDefaultAsync(k => k.Hash == hash && k.RevokedAt == null && k.DeletedAt == null);
    }

    public async Task TouchLastUsedAsync(int apiKeyId)
    {
        var key = await unitOfWork
            .Repository<ApiKey>()
            .Query()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(k => k.Id == apiKeyId);

        if (key is null)
            return;

        var now = DateTime.UtcNow;
        if (key.LastUsedAt is not null && now - key.LastUsedAt.Value < TouchInterval)
            return;

        key.LastUsedAt = now;
        unitOfWork.Repository<ApiKey>().Update(key);
        await unitOfWork.SaveChangesAsync();
    }

    private async Task<User?> FindUserAsync(Guid publicId) =>
        await unitOfWork
            .Repository<User>()
            .Query()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.PublicId == publicId && u.DeletedAt == null);

    private IQueryable<ApiKey> ActiveKeyQuery(int userId) =>
        unitOfWork
            .Repository<ApiKey>()
            .Query()
            .IgnoreQueryFilters()
            .Where(k => k.UserId == userId && k.RevokedAt == null && k.DeletedAt == null);

    private async Task<ApiKeyResult> MintAsync(User user)
    {
        var raw = await GenerateUniqueAsync();
        var key = BuildRow(user, raw);

        await unitOfWork.Repository<ApiKey>().AddAsync(key);
        await unitOfWork.SaveChangesAsync();

        return ApiKeyResult.Ok(key, raw);
    }

    /// <summary>
    /// Builds a row from a raw key without persisting it — shared with the backfill so migrated keys
    /// are stored byte-identically to newly minted ones.
    /// </summary>
    public ApiKey BuildRow(User user, string raw) =>
        new()
        {
            UserId = user.Id,
            OwnerId = user.OwnerId,
            Prefix = raw.Length >= 12 ? raw[..12] : raw,
            Hash = protector.Hash(raw),
            Encrypted = protector.Encrypt(raw),
            Scopes = (int)ApiKeyScopes.Full,
        };

    // ptr_ + 40 hex, unchanged from the pre-hardening format so every key already written into a
    // customer's .pointer/credentials.env keeps working. Collision-checked on the hash.
    private async Task<string> GenerateUniqueAsync()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var candidate = "ptr_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();
            var hash = protector.Hash(candidate);

            var exists = await unitOfWork
                .Repository<ApiKey>()
                .Query()
                .AsNoTracking()
                .IgnoreQueryFilters()
                .AnyAsync(k => k.Hash == hash);

            if (!exists)
                return candidate;
        }

        throw new InvalidOperationException("Could not generate a unique API key after 5 attempts.");
    }
}
