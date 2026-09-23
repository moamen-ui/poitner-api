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
        // Review fix (finding 3): normalisation lives in ONE shared helper — see ActiveKeyIdResolver.
        var activeKeyId = ActiveKeyIdResolver.Normalize(config["JWT:ActiveKeyId"]);
        if (!allKeys.Any(k => k.Id == activeKeyId))
            throw new InvalidOperationException(
                $"JWT:ActiveKeyId '{activeKeyId}' is not present in JWT:Keys."
            );
        // Review fix (finding 6): the boot line is logged via ILogger from Program.cs, after the host
        // is built (DescribeKeyRing below) — no ILoggerFactory exists yet at this ConfigureServices
        // stage, and Console.WriteLine bypasses the app's structured logging/log level entirely.

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
                    // Review fix (finding 4): pin the accepted algorithm. Without this, a
                    // `SymmetricSecurityKey` accepts whatever HMAC variant the token header claims
                    // (or, combined with a misconfigured signer, an algorithm-confusion attack) —
                    // this API only ever signs with HS256, so only HS256 is ever valid.
                    ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
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
                    // R5-61 review fix #8: the default 5-minute ClockSkew tolerance is fine for a
                    // 12h session token, but it lets a 5-minute scope=mfa_pending token (or the
                    // similarly short-lived selection token) actually stay valid for up to ~10
                    // minutes past mint — nearly doubling the window an intercepted pending token
                    // works in. 30s is generous for real clock drift between this process and the
                    // token's own `exp` (both computed from the same server's clock — the same
                    // process, in fact) without materially weakening the 12h token either.
                    ClockSkew = TimeSpan.FromSeconds(30),
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
                        var scope = principal?.FindFirst("scope")?.Value;

                        if (
                            scope == "select_workspace"
                            && !SelectionScopeFence.Allows(ctx.HttpContext.Request.Path)
                        )
                        {
                            ctx.Fail("Selection token.");
                            return;
                        }

                        // R5-61: a scope=mfa_pending token is honoured ONLY on the exact
                        // POST /api/auth/mfa/verify path — never a prefix/sub-route match (same
                        // exact-path fence as the selection token above).
                        if (
                            scope == "mfa_pending"
                            && !MfaPendingScopeFence.Allows(ctx.HttpContext.Request.Path)
                        )
                        {
                            ctx.Fail("MFA-pending token.");
                            return;
                        }

                        // DB-13: the impersonation fence + liveness check must run for EVERY
                        // request, independent of Auth:ValidateSecurityStamp — same reasoning as
                        // the selection fence above. Placed after it, before the stamp/membership
                        // block below (§3.5).
                        if (scope == "impersonate")
                        {
                            if (
                                !ImpersonationScopeFence.Allows(
                                    ctx.HttpContext.Request.Method,
                                    ctx.HttpContext.Request.Path
                                )
                            )
                            {
                                ctx.Fail("Impersonation tokens are read-only.");
                                return;
                            }

                            var operatorSub =
                                principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                                ?? principal?.FindFirst("sub")?.Value;
                            if (
                                !long.TryParse(principal?.FindFirst("imp")?.Value, out var imp)
                                || !Guid.TryParse(
                                    principal?.FindFirst("tenant")?.Value,
                                    out var target
                                )
                                || !Guid.TryParse(operatorSub, out var operatorPublicId)
                                || principal?.FindFirst("is_super_admin")?.Value != "true"
                            )
                            {
                                ctx.Fail("Invalid impersonation token.");
                                return;
                            }

                            // Fails CLOSED (unlike the stamp lookup below, which fails open) — the
                            // whole point is that the session is provably live. No cache: one PK
                            // read per request from a single operator; `end` therefore takes effect
                            // on the next request.
                            bool live;
                            try
                            {
                                var impDb =
                                    ctx.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
                                live = await ImpersonationLiveness.IsLiveAsync(
                                    impDb,
                                    imp,
                                    target,
                                    operatorPublicId
                                );
                            }
                            catch (Exception)
                            {
                                ctx.Fail("Impersonation session has ended.");
                                return;
                            }

                            if (!live)
                            {
                                ctx.Fail("Impersonation session has ended.");
                                return;
                            }
                            // fall through to the identity-stamp check below; the membership/mstamp
                            // check is skipped there for scope == "impersonate" (operators have no
                            // membership).
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

                                    // Super-admin tokens (no tenant) skip membership check
                                    // (DB-RULES R16). DB-13: an impersonation token's `tenant` is
                                    // the target workspace, not a membership — operators have none,
                                    // so it skips the membership check too (already proven live by
                                    // the ImpersonationLiveness check above).
                                    if (tenantId is null || scope == "impersonate")
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
                        // DB-13: an impersonation token has a `tenant` claim but no `mstamp`/membership
                        // — its liveness was already proven above, so it is validated as hasTenant=false
                        // here (identity stamp only).
                        if (
                            !StampValidator.Validate(
                                tokenStamp,
                                tokenMstamp,
                                tenantId is not null && scope != "impersonate",
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

        // Review fix (finding 5): ResolveAllKeys previously validated only Secret length — an entry
        // with a Secret but a blank Id (or two entries sharing an Id) silently produced a broken or
        // ambiguous key ring (an unselectable key, or `kid`-based lookup picking whichever duplicate
        // happens to match first). Fail fast at startup instead.
        var blankId = keys.FirstOrDefault(k => string.IsNullOrWhiteSpace(k.Id));
        if (blankId is not null)
            throw new InvalidOperationException(
                "A JWT:Keys entry has a Secret configured but no Id (or a blank Id)."
            );

        var duplicateIds = keys.GroupBy(k => k.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicateIds.Count > 0)
            throw new InvalidOperationException(
                $"JWT:Keys has duplicate id(s): {string.Join(", ", duplicateIds)}."
            );

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

    // Review fix (finding 6): the boot-time key-ring summary, logged from Program.cs via ILogger
    // after the host is built (never Console.WriteLine, never the secrets — ids only).
    public static string DescribeKeyRing(IConfiguration config)
    {
        var allKeys = ResolveAllKeys(config);
        var activeKeyId = ActiveKeyIdResolver.Normalize(config["JWT:ActiveKeyId"]);
        return $"active kid={activeKeyId}; configured kids=[{string.Join(", ", allKeys.Select(k => k.Id))}]";
    }
}
