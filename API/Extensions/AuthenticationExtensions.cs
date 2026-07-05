using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Pointer.API.Auth;
using Pointer.Infrastructure;
using System;
using System.IdentityModel.Tokens.Jwt;

namespace Pointer.API.Extensions;

public static class AuthenticationExtensions
{
    public static IServiceCollection AddJwtAuth(
        this IServiceCollection services,
        IConfiguration config
    )
    {
        // Fail fast at startup: a missing/short signing key silently produces forgeable tokens
        // (HS256 requires ≥ 256-bit / 32-byte keys). Refuse to boot rather than run insecurely.
        var signingKey = config["JWT:SigningKey"];
        if (string.IsNullOrEmpty(signingKey))
            throw new InvalidOperationException("JWT:SigningKey is not configured. Set a random secret of at least 32 bytes.");
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(signingKey);
        if (keyBytes.Length < 32)
            throw new InvalidOperationException($"JWT:SigningKey is too short ({keyBytes.Length} bytes). HS256 requires at least 32 bytes.");

        // H1: session-invalidation stamp check. Default OFF → behavior identical to before (stateless
        // JWT). When on, every authenticated request re-checks the token's `stamp` claim against the
        // user's current SecurityStamp (cached ~60s to bound the DB cost), so disable/reject/password
        // change revoke live tokens within the cache TTL. Enable only after deploying + a smoke test.
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
                    IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
                    ValidateIssuerSigningKey = true,
                    NameClaimType = JwtRegisteredClaimNames.Sub,
                    RoleClaimType = "role",
                };

                if (validateStamp)
                {
                    options.Events = new JwtBearerEvents
                    {
                        OnTokenValidated = async ctx =>
                        {
                            var principal = ctx.Principal;
                            var sub = principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                                      ?? principal?.FindFirst("sub")?.Value;
                            var stampClaim = principal?.FindFirst("stamp")?.Value;
                            if (!Guid.TryParse(sub, out var publicId) || !Guid.TryParse(stampClaim, out var tokenStamp))
                            {
                                ctx.Fail("Invalid token.");
                                return;
                            }

                            var sp = ctx.HttpContext.RequestServices;
                            var cache = sp.GetRequiredService<IMemoryCache>();
                            Guid? currentStamp;
                            try
                            {
                                currentStamp = await cache.GetOrCreateAsync($"secstamp:{publicId}", async entry =>
                                {
                                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
                                    var db = sp.GetRequiredService<AppDbContext>();
                                    return await db.Users
                                        .IgnoreQueryFilters()
                                        .AsNoTracking()
                                        .Where(u => u.PublicId == publicId && u.DeletedAt == null)
                                        .Select(u => (Guid?)u.SecurityStamp)
                                        .FirstOrDefaultAsync();
                                });
                            }
                            catch (Exception ex)
                            {
                                // Fail OPEN on a transient lookup error: the JWT signature+expiry already
                                // authenticated the caller, so a DB blip must not 500 every authenticated
                                // request. Revocation is best-effort (≤60s window); allow + log this one.
                                sp.GetService<ILoggerFactory>()?
                                    .CreateLogger("SecurityStampValidation")
                                    .LogWarning(ex, "Security-stamp lookup failed; allowing request (fail-open).");
                                return;
                            }

                            // Missing user (deleted) or a stamp mismatch (disabled/rejected/password
                            // changed since this token was issued) → reject. Up to ~60s stale via cache.
                            if (currentStamp is null || currentStamp.Value != tokenStamp)
                                ctx.Fail("Token has been revoked.");
                        },
                    };
                }
            });

        // Admin access is capability-based (the user's role grants admin), not tied to a role
        // NAME — so renaming/adding roles never weakens authorization.
        services
            .AddAuthorizationBuilder()
            .AddPolicy(Policies.Admin, p => p.RequireClaim("is_admin", "true"))
            .AddPolicy(Policies.SuperAdmin, p => p.RequireClaim("is_super_admin", "true"));

        return services;
    }
}
