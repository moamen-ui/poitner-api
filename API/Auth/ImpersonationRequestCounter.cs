using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pointer.Application.Abstractions;
using Pointer.Infrastructure;

namespace Pointer.API.Auth;

/// <summary>
/// DB-13 §3.5 — request counting for a live impersonation session. Global filter (registered next
/// to <see cref="AuditCoverageFilter"/> in Program.cs): when the caller's token carries `imp`,
/// bumps that session's <c>request_count</c>/<c>last_request_at</c> after the action runs, in a
/// best-effort <c>try/catch</c> — a failure here must never fail the request itself. Relational
/// only: <c>ExecuteUpdateAsync</c> is not supported against the InMemory provider used by most
/// tests, so it is skipped there (Sqlite-backed tests exercise it for real).
///
/// This runs after <c>next()</c> regardless of the resulting status code, so a 4xx/5xx response
/// still counts as a request against the session's budget — deliberate: the token was used either
/// way, and that is what the count is for.
/// </summary>
public class ImpersonationRequestCounter(
    ICurrentUser currentUser,
    AppDbContext db,
    ILogger<ImpersonationRequestCounter> logger
) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        await next();

        if (currentUser.ImpersonationSessionId is not long imp)
            return;

        if (!db.Database.IsRelational())
            return;

        try
        {
            // DB-13 review fix #9 (NIT): don't resurrect a session's counters after it has ended —
            // the row could still be matched by id alone (e.g. a request that raced the sweep/manual
            // end past its own OnActionExecutionAsync).
            await db
                .ImpersonationSessions.IgnoreQueryFilters()
                .Where(s => s.Id == imp && s.EndedAt == null)
                .ExecuteUpdateAsync(s =>
                    s.SetProperty(x => x.RequestCount, x => x.RequestCount + 1)
                        .SetProperty(x => x.LastRequestAt, DateTime.UtcNow)
                );
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "ImpersonationRequestCounter: failed to bump session {SessionId}",
                imp
            );
        }
    }
}
