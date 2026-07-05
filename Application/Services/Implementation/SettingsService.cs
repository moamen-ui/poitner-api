using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Pointer.Application.Abstractions;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;

namespace Pointer.Application.Services.Implementation;

public class SettingsService(IUnitOfWork unitOfWork, IMemoryCache cache) : ISettingsService
{
    // M11: AppSettings are read on hot anonymous paths (e.g. GET /api/branding reads 8 keys per widget
    // load). They change only via the super-admin settings page, so a short per-key cache with
    // write-through eviction is safe and collapses those reads to ~0 DB round trips on a cache hit.
    private const int TtlSeconds = 30;

    private static string CacheKey(string key) => $"setting:{key}";

    /// <summary>Cached raw stored value for a key (null when absent). Eviction happens on write.</summary>
    private async Task<string?> GetRawAsync(string key) =>
        await cache.GetOrCreateAsync(CacheKey(key), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(TtlSeconds);
            var setting = await unitOfWork.Repository<AppSetting>()
                .Query()
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.DeletedAt == null && s.Key == key);
            return setting?.Value;
        });

    public async Task<bool> GetBoolAsync(string key, bool fallback = false)
    {
        var v = await GetRawAsync(key);
        return v is null ? fallback : v.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> GetStringAsync(string key, string fallback = "")
    {
        var v = await GetRawAsync(key);
        return string.IsNullOrWhiteSpace(v) ? fallback : v;
    }

    public async Task<int> GetIntAsync(string key, int fallback = 0)
    {
        var v = await GetRawAsync(key);
        return int.TryParse(v, out var n) ? n : fallback;
    }

    public Task SetBoolAsync(string key, bool value) => UpsertAsync(key, value ? "true" : "false");

    public Task SetStringAsync(string key, string value) => UpsertAsync(key, value ?? string.Empty);

    public Task SetIntAsync(string key, int value) => UpsertAsync(key, value.ToString());

    private async Task UpsertAsync(string key, string value)
    {
        var setting = await unitOfWork.Repository<AppSetting>()
            .Query()
            .FirstOrDefaultAsync(s => s.DeletedAt == null && s.Key == key);

        if (setting == null)
            await unitOfWork.Repository<AppSetting>().AddAsync(new AppSetting { Key = key, Value = value });
        else
        {
            setting.Value = value;
            unitOfWork.Repository<AppSetting>().Update(setting);
        }

        await unitOfWork.SaveChangesAsync();
        // Write-through eviction: the next read reflects the change immediately (no ≤30s staleness).
        cache.Remove(CacheKey(key));
    }
}
