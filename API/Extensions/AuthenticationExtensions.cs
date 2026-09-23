using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Pointer.API.Auth;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Auth;

namespace Pointer.API.Extensions;

public static class AuthenticationExtensions
{
    public static IServiceCollection AddJwtAuth(
        this IServiceCollection services,
        IConfiguration config
    )
    {
        // Fail fast at startup: a missing signing key silently produces forgeable tokens. Refuse to
        // boot rather than run insecurely. The rotation runbook (DEPLOY.md) never unsets this value,
        // only repoints it, so this guard stays independent of the >= 32-byte check below (which now
        // applies per-key, via ResolveAllKeys, to cover JWT:Keys entries too).
        var signingKey = config["JWT:SigningKey"];
        if (string.IsNullOrEmpty(signingKey))
            throw new InvalidOperationException(
                "JWT:SigningKey is not configured. Set a random secret of at least 32 bytes."
            );

        // R5-62: current + previous signing keys, each validated (>= 32 bytes) and selected by `kid`.
        var allKeys = ResolveAllKeys(config);
        var activeKeyId = config["JWT:ActiveKeyId"];
        if (string.IsNullOrEmpty(activeKeyId))
            activeKeyId = "k0";
        if (!allKeys.Any(k => k.Id == activeKeyId))
            throw new InvalidOperationException(
                $"JWT:ActiveKeyId '{activeKeyId}' is not present in JWT:Keys."
            );
        // Log only the key ids on boot (never the secrets) so an operator can confirm a rotation took effect.
        Console.WriteLine(
            $"[JWT] active kid={activeKeyId}; configured kids=[{string.Join(", ", allKeys.Select(k => k.Id))}]"
        );

        // H1 / DB-11a / DB-RULES R16: session-invalidation stamp check. Default OFF → behavior identical
        // to before (stateless JWT). When on, checks the token's identity stamp and, when tenant is present,
        // membership stamp against live state (cached ~60s to bound the DB cost), so disable/reject/removal/password
        // change revoke live tokens within the cache TTL (docs/db/DB-RULES.md R16).
        var validateStamp = config.GetValue("Auth:ValidateSecurityStamp", false);
        services.AddMemoryCache();

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = config["JWT:Issuer"],
                    ValidateAudience = true,
                    ValidAudience = config["JWT:Issuer"],
                    ValidateLifetime = true,
                    // R5-62: current + previous key(s), selected by `kid`. When the incoming token
                    // has no `kid` (pre-rotation tokens issued before this deploy), the token library
                    // falls back to trying every key in this list until one validates the signature.
                    IssuerSigningKeys = allKeys
                        .Select(k => new SymmetricSecurityKey(
                            System.Text.Encoding.UTF8.GetBytes(k.Secret)
                        )
                        {
                            KeyId = k.Id,
                        })
                        .ToList(),
                    ValidateIssuerSigningKey = true,
                    NameClaimType = JwtRegisteredClaimNames.Sub,
                    RoleClaimType = "role",
                };

                // DB-11b / DB-RULES R16: the scope fence must run for EVERY request, independent of
                // Auth:ValidateSecurityStamp — a bare [Authorize] endpoint would otherwise accept a
                // 5-minute selection token anywhere. Only the stamp lookup below stays gated.
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async ctx =>
                    {
                        var principal = ctx.Principal;

                        // DB-11b (GLM A6): a scope=select_workspace token is honoured ONLY on the
                        // exact switch-workspace path — never a prefix/sub-route match.
                        if (
                            principal?.FindFirst("scope")?.Value == "select_workspace"
                            && !SelectionScopeFence.Allows(ctx.HttpContext.Request.Path)
                        )
                        {
                            ctx.Fail("Selection token.");
                            return;
                        }

                        if (!validateStamp)
                            return;

                        var sub =
                            principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                            ?? principal?.FindFirst("sub")?.Value;
                        var stampClaim = principal?.FindFirst("stamp")?.Value;
                        if (
                            !Guid.TryParse(sub, out var publicId)
                            || !Guid.TryParse(stampClaim, out var tokenStamp)
                        )
                        {
                            ctx.Fail("Invalid token.");
                            return;
                        }

                        var tenantClaim = principal?.FindFirst("tenant")?.Value;
                        Guid? tenantId = null;
                        if (tenantClaim is not null)
                        {
                            if (!Guid.TryParse(tenantClaim, out var parsedTenant))
                            {
                                ctx.Fail("Invalid token.");
                                return;
                            }
                            tenantId = parsedTenant;
                        }

                        var mstampClaim = principal?.FindFirst("mstamp")?.Value;
                        Guid? tokenMstamp = null;
                        if (
                            mstampClaim is not null
                            && Guid.TryParse(mstampClaim, out var parsedMstamp)
                        )
                        {
                            tokenMstamp = parsedMstamp;
                        }

                        var sp = ctx.HttpContext.RequestServices;
                        var cache = sp.GetRequiredService<IMemoryCache>();
                        StampValidationState? state;
                        try
                        {
                            // DB-11a / DB-RULES R16: cache key includes tenant (or "-" for super admin).
                            state = await cache.GetOrCreateAsync(
                                $"secstamp:{publicId}:{tenantClaim ?? "-"}",
                                async entry =>
                                {
                                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(
                                        60
                                    );
                                    var db = sp.GetRequiredService<AppDbContext>();

                                    var user = await db
                                        .Users.IgnoreQueryFilters()
                                        .AsNoTracking()
                                        .Where(u => u.PublicId == publicId && u.DeletedAt == null)
                                        .Select(u => new { u.Id, u.SecurityStamp })
                                        .FirstOrDefaultAsync();

                                    if (user is null)
                                        return new StampValidationState(null, null, false);

                                    // Super-admin tokens (no tenant) skip membership check (DB-RULES R16).
                                    if (tenantId is null)
                                        return new StampValidationState(
                                            user.SecurityStamp,
                                            null,
                                            false
                                        );

                                    var membership = await db
                                        .WorkspaceMemberships.IgnoreQueryFilters()
                                        .AsNoTracking()
                                        .Where(m =>
                                            m.UserId == user.Id
                                            && m.OwnerId == tenantId.Value
                                            && m.DeletedAt == null
                                            && m.LeftAt == null
                                        )
                                        .Select(m => new
                                        {
                                            m.SecurityStamp,
                                            Live = m.IsActive
                                                && m.ApprovalStatus == ApprovalStatus.Approved,
                                        })
                                        .FirstOrDefaultAsync();

                                    return new StampValidationState(
                                        user.SecurityStamp,
                                        membership?.SecurityStamp,
                                        membership?.Live ?? false
                                    );
                                }
                            );
                        }
                        catch (Exception ex)
                        {
                            // Fail OPEN on a transient lookup error: the JWT signature+expiry already
                            // authenticated the caller, so a DB blip must not 500 every authenticated
                            // request. Revocation is best-effort (≤60s window); allow + log this one.
                            sp.GetService<ILoggerFactory>()
                                ?.CreateLogger("SecurityStampValidation")
                                .LogWarning(
                                    ex,
                                    "Security-stamp lookup failed; allowing request (fail-open)."
                                );
                            return;
                        }

                        // DB-RULES R16: reject when identity stamp mismatches, or when a tenant token's
                        // membership is missing, inactive, unapproved, or membership stamp mismatches.
                        if (
                            !StampValidator.Validate(
                                tokenStamp,
                                tokenMstamp,
                                tenantId is not null,
                                state
                            )
                        )
                            ctx.Fail("Token has been revoked.");
                    },
                };
            });

        // Admin access is capability-based (the user's role grants admin), not tied to a role
        // NAME — so renaming/adding roles never weakens authorization.
        services
            .AddAuthorizationBuilder()
            .AddPolicy(Policies.Admin, p => p.RequireClaim("is_admin", "true"))
            .AddPolicy(Policies.SuperAdmin, p => p.RequireClaim("is_super_admin", "true"));

        return services;
    }

    // R5-62: resolves the full validation key ring from config. Legacy config (no JWT:Keys section)
    // returns a single synthetic "k0" entry wrapping JWT:SigningKey — existing .env.prod files need
    // no changes. A JWT:Keys entry with an empty/missing Secret (an unset rotation slot, e.g. k1
    // before it is filled in) is skipped so it never trips the >= 32-byte check below. Every
    // resolved key IS checked (HS256 requires >= 256-bit / 32-byte keys) — refuse to boot rather
    // than run any key insecurely.
    public static List<JwtKeyEntry> ResolveAllKeys(IConfiguration config)
    {
        var configuredKeys =
            config.GetSection("JWT:Keys").Get<List<JwtKeyEntry>>() ?? new List<JwtKeyEntry>();
        var keys = configuredKeys.Where(k => !string.IsNullOrEmpty(k.Secret)).ToList();

        if (keys.Count == 0)
        {
            keys = new List<JwtKeyEntry>
            {
                new JwtKeyEntry { Id = "k0", Secret = config["JWT:SigningKey"] ?? "" },
            };
        }

        foreach (var k in keys)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(k.Secret);
            if (bytes.Length < 32)
                throw new InvalidOperationException(
                    $"JWT key '{k.Id}' is too short ({bytes.Length} bytes). HS256 requires at least 32 bytes."
                );
        }

        return keys;
    }
}
