using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;

namespace Pointer.Infrastructure.Audit;

/// <summary>
/// DB-12 §3.3 — the single <see cref="IAuditWriter"/> implementation (scoped; registered by hand
/// next to ResetTokenService in Infrastructure DI). Rows are append-only; the Postgres trigger is
/// the authority, the <c>AppDbContext.SaveChangesAsync</c> guard is the early error.
/// </summary>
public class AuditWriter(
    AppDbContext db,
    ICurrentUser currentUser,
    IHttpContextAccessor http,
    IConfiguration config,
    ILogger<AuditWriter> log
) : IAuditWriter
{
    /// <summary>Set on <c>HttpContext.Items</c> after a successful write; read by the coverage filter (§3.8).</summary>
    public const string WrittenItemKey = "audit.written";

    // RequestIdMiddleware.ItemKey (API layer). Infrastructure cannot reference the API assembly,
    // so the key string is repeated here — the two constants MUST stay equal (asserted by
    // Tests/RequestIdMiddlewareTests.cs). Public so the test can reference it instead of a literal.
    public const string RequestIdItemKey = "RequestId";

    private const int MaxUserAgentLength = 256;
    private const int MaxTargetTypeLength = 64;
    private const int MaxTargetIdLength = 128;

    public async Task WriteAsync(AuditEntry entry, CancellationToken ct = default)
    {
        // A typo'd action is a bug, not an audit row — validated (and thrown) before anything else,
        // never swallowed by the best-effort catch below.
        if (!AuditActions.All.Contains(entry.Action))
            throw new ArgumentException(
                $"Unknown audit action '{entry.Action}' — use an AuditActions constant (DB-12).",
                nameof(entry)
            );

        // Declared outside the try so a failed insert can be detached from the ChangeTracker in
        // the catch below — otherwise the still-Added entity is resubmitted (and fails again) on
        // every subsequent SaveChangesAsync on this (request-scoped) DbContext, poisoning the rest
        // of the request.
        AuditEvent? ev = null;
        try
        {
            // DB-13: an impersonating super admin becomes Impersonation here (and
            // ImpersonationSessionId is set from ICurrentUser) when DB-13 lands.
            var actorKind =
                entry.ActorKindOverride
                ?? (
                    currentUser.Id is null ? AuditActorKind.System
                    : currentUser.IsSuperAdmin ? AuditActorKind.SuperAdmin
                    : AuditActorKind.User
                );
            var actorUserId = entry.ActorUserIdOverride ?? currentUser.Id;

            // actor_membership_id: the acting membership when a workspace user acted inside a
            // workspace — resolved from (sub, tenant); no such claim exists on the JWT (§3.4).
            int? actorMembershipId = null;
            if (
                actorUserId is Guid actor
                && entry.OwnerId is Guid workspace
                && !currentUser.IsSuperAdmin
            )
            {
                actorMembershipId = await db
                    .WorkspaceMemberships.IgnoreQueryFilters()
                    .Where(m =>
                        m.User.PublicId == actor && m.OwnerId == workspace && m.DeletedAt == null
                    )
                    .OrderByDescending(m => m.LeftAt == null)
                    .ThenByDescending(m => m.JoinedAt)
                    .Select(m => (int?)m.Id)
                    .FirstOrDefaultAsync(ct);
            }

            var ctx = http.HttpContext;
            ev = new AuditEvent
            {
                OccurredAt = DateTime.UtcNow,
                OwnerId = entry.OwnerId,
                ActorUserId = actorUserId,
                ActorMembershipId = actorMembershipId,
                ActorKind = actorKind,
                Action = entry.Action,
                TargetType = Truncate(entry.TargetType, MaxTargetTypeLength) ?? entry.TargetType,
                TargetId = Truncate(entry.TargetId, MaxTargetIdLength),
                Before = AuditFields.Sanitize(entry.Before),
                After = AuditFields.Sanitize(entry.After),
                RequestId = ctx?.Items[RequestIdItemKey] as string,
                IpHash = Hash(ctx?.Connection.RemoteIpAddress?.ToString()),
                UserAgent = Truncate(ctx?.Request.Headers.UserAgent.ToString(), MaxUserAgentLength),
                // DB-13: written from ICurrentUser.ImpersonationSessionId once DB-13 exists.
                ImpersonationSessionId = null,
            };

            db.AuditEvents.Add(ev);
            await db.SaveChangesAsync(ct);

            // Read by AuditCoverageFilter: an [Audited] action that completes without this key is
            // an AUDIT GAP.
            if (ctx is not null)
                ctx.Items[WrittenItemKey] = true;
        }
        catch (Exception ex)
        {
            // The failed row must not stay tracked as Added — detach it before anything else so a
            // later, unrelated SaveChangesAsync on this DbContext isn't dragged down resubmitting
            // (and re-failing on) the same broken insert. Best-effort itself: if the context is
            // entirely unusable (e.g. already disposed — the cause of the original failure in that
            // case), there is nothing left to detach from, and that must not escape and override
            // the swallow below.
            if (ev is not null)
            {
                try
                {
                    db.Entry(ev).State = EntityState.Detached;
                }
                catch
                {
                    // Nothing left to clean up on an unusable context.
                }
            }

            // D12.1: best-effort by default — the mutation already happened, the audit write is
            // logged at Error and swallowed; Audit:FailClosed=true rethrows instead.
            log.LogError(
                ex,
                "AUDIT WRITE FAILED {Action} {TargetType}/{TargetId}",
                entry.Action,
                entry.TargetType,
                entry.TargetId
            );
            if (config.GetValue("Audit:FailClosed", false))
                throw;
        }
    }

    /// <summary>Keyed HMAC-SHA256 of the remote IP (hex) — pseudonymous, not reversible without the
    /// key. Key: Audit:HashKey, falling back to the JWT signing key; when NEITHER is configured
    /// (blank/whitespace counts as unconfigured) there is no unkeyed hash — ip_hash is stored NULL
    /// rather than HMAC'd with an empty key.</summary>
    private string? Hash(string? ip)
    {
        if (string.IsNullOrEmpty(ip))
            return null;

        var configuredKey = config["Audit:HashKey"];
        var signingKeyFallback = config["JWT:SigningKey"];
        var key =
            !string.IsNullOrWhiteSpace(configuredKey) ? configuredKey
            : !string.IsNullOrWhiteSpace(signingKeyFallback) ? signingKeyFallback
            : null;
        if (key is null)
            return null;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(ip))).ToLowerInvariant();
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? value
        : value.Length <= max ? value
        : value[..max];
}
