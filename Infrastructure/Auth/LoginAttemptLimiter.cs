using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Pointer.Application.Abstractions;

namespace Pointer.Infrastructure.Auth;

/// <summary>
/// In-memory sliding-window limiter for failed login attempts (R5-59 §12).
/// Keyed by <c>login-fail:&lt;normalised e-mail&gt;</c>. Reaching the failure threshold
/// locks the account for the window duration. Successful logins reset the entry.
/// </summary>
public class LoginAttemptLimiter : ILoginAttemptLimiter
{
    private readonly IMemoryCache _cache;
    private readonly int _threshold;
    private readonly TimeSpan _window;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();

    public LoginAttemptLimiter(
        IMemoryCache cache,
        IOptions<LoginLockoutOptions>? options = null,
        TimeProvider? timeProvider = null
    )
    {
        _cache = cache;
        _timeProvider = timeProvider ?? TimeProvider.System;
        var opts = options?.Value ?? new LoginLockoutOptions();
        _threshold = opts.Threshold > 0 ? opts.Threshold : 10;
        _window = TimeSpan.FromMinutes(opts.WindowMinutes > 0 ? opts.WindowMinutes : 15);
    }

    public Task<bool> IsLockedAsync(string email)
    {
        var key = CacheKey(email);
        var now = _timeProvider.GetUtcNow();

        lock (_sync)
        {
            if (!_cache.TryGetValue(key, out LockoutEntry? entry) || entry == null)
                return Task.FromResult(false);

            if (entry.LockedUntil.HasValue)
            {
                if (now < entry.LockedUntil.Value)
                    return Task.FromResult(true);

                // Lockout window expired; clear the key
                _cache.Remove(key);
                return Task.FromResult(false);
            }

            return Task.FromResult(false);
        }
    }

    public Task<int> GetRetryAfterSecondsAsync(string email)
    {
        var key = CacheKey(email);
        var now = _timeProvider.GetUtcNow();

        lock (_sync)
        {
            if (
                _cache.TryGetValue(key, out LockoutEntry? entry)
                && entry?.LockedUntil.HasValue == true
            )
            {
                var remaining = (entry.LockedUntil.Value - now).TotalSeconds;
                if (remaining > 0)
                    return Task.FromResult((int)Math.Max(1, Math.Ceiling(remaining)));
            }

            return Task.FromResult(0);
        }
    }

    public Task RecordFailureAsync(string email)
    {
        var key = CacheKey(email);
        var now = _timeProvider.GetUtcNow();

        lock (_sync)
        {
            if (
                !_cache.TryGetValue(key, out LockoutEntry? entry)
                || entry == null
                || (entry.LockedUntil.HasValue && now >= entry.LockedUntil.Value)
            )
            {
                entry = new LockoutEntry();
            }

            entry.Failures++;
            if (entry.Failures >= _threshold && !entry.LockedUntil.HasValue)
            {
                entry.LockedUntil = now.Add(_window);
            }

            // Relative, not absolute: MemoryCache evaluates expiry against the real system clock, while
            // `now` comes from the injectable TimeProvider. An absolute DateTimeOffset from a test clock set
            // in the past evicted the entry immediately (the WindowExpiry test broke on 2026-09-23 after
            // 12:15 UTC). The lockout decision itself still uses LockedUntil against TimeProvider.
            var expiresAt = entry.LockedUntil ?? now.Add(_window);
            var ttl = expiresAt - now;
            _cache.Set(key, entry, ttl > TimeSpan.Zero ? ttl : TimeSpan.FromSeconds(1));
        }

        return Task.CompletedTask;
    }

    public Task ResetAsync(string email)
    {
        var key = CacheKey(email);
        lock (_sync)
        {
            _cache.Remove(key);
        }
        return Task.CompletedTask;
    }

    private static string NormalizeEmail(string? email) =>
        (email ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// Fixed-size (SHA-256 hex, 64 chars) cache key so per-entry memory cannot grow with the
    /// length of an attacker-supplied e-mail (GLM review M1 — see LoginValidator.MaximumLength
    /// for the companion input-size cap).
    /// </summary>
    private static string CacheKey(string? email)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeEmail(email)));
        return $"login-fail:{Convert.ToHexString(hash)}";
    }

    private sealed class LockoutEntry
    {
        public int Failures { get; set; }
        public DateTimeOffset? LockedUntil { get; set; }
    }
}
