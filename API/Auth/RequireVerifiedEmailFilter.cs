using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pointer.Application.Abstractions;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Infrastructure;

namespace Pointer.API.Auth;

/// <summary>
/// DB-14 §3.4 — the admin-write gate. Applies when ALL hold: the request is authenticated; the
/// method is not GET/HEAD/OPTIONS; the controller type's namespace starts with
/// <c>Pointer.API.Controllers.Admin</c>; the action does not carry <see cref="AllowUnverifiedAttribute"/>.
/// Global filter, registered like <c>AuditCoverageFilter</c> in <c>Program.cs</c>.
///
/// Namespace invariant: a new admin mutation MUST live under <c>API/Controllers/Admin/</c> or this
/// filter will not gate it (silently un-gated); a stakeholder-facing POST accidentally placed there
/// is gated instead (fail-closed, safe). The DB-12 coverage test (<c>AuditCoverageTests</c>, same
/// <c>Pointer.API.Controllers.Admin</c> namespace rule) is the twin check — a controller that trips
/// one will trip the other.
/// </summary>
public class RequireVerifiedEmailFilter(IMemoryCache cache, ILogger<RequireVerifiedEmailFilter> logger)
    : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {
        var http = ctx.HttpContext;
        if (
            http.User?.Identity?.IsAuthenticated != true
            || HttpMethods.IsGet(http.Request.Method)
            || HttpMethods.IsHead(http.Request.Method)
            || HttpMethods.IsOptions(http.Request.Method)
        )
        {
            await next();
            return;
        }

        var cad = ctx.ActionDescriptor as ControllerActionDescriptor;
        if (
            cad is null
            || cad.ControllerTypeInfo.Namespace?.StartsWith(
                "Pointer.API.Controllers.Admin",
                StringComparison.Ordinal
            ) != true
            || cad.MethodInfo.GetCustomAttribute<AllowUnverifiedAttribute>() is not null
        )
        {
            await next();
            return;
        }

        var current = http.RequestServices.GetRequiredService<ICurrentUser>(); // Guid? Id — parses sub/NameIdentifier once, the codebase's one precedent
        if (current.IsSuperAdmin)
        {
            await next();
            return;
        }
        if (current.Id is not Guid publicId)
        {
            // An authenticated principal without a parsable sub cannot come from this API's own
            // tokens (JwtTokenService always writes sub). Fail OPEN like the lookup-exception path
            // below: the filter is not an authentication layer. Log once per request.
            logger.LogWarning(
                "RequireVerifiedEmailFilter: authenticated request without a parsable sub on {Method} {Path}; not gated",
                http.Request.Method,
                http.Request.Path
            );
            await next();
            return;
        }

        bool verified;
        try
        {
            verified = await cache.GetOrCreateAsync(
                $"emailverified:{publicId}",
                async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
                    var db = http.RequestServices.GetRequiredService<AppDbContext>();
                    var row = await db
                        .Users.IgnoreQueryFilters()
                        .AsNoTracking()
                        .Where(u => u.PublicId == publicId && u.DeletedAt == null)
                        .Select(u => new { u.EmailVerifiedAt, u.IsDemo })
                        .FirstOrDefaultAsync();
                    return row is null || row.EmailVerifiedAt != null || row.IsDemo; // missing row → not gated (stamp validator already rejected deleted identities)
                }
            );
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "RequireVerifiedEmailFilter: lookup failed; allowing request (fail-open).");
            await next();
            return;
        }

        if (verified)
        {
            await next();
            return;
        }

        http.Response.Headers["X-Email-Verification-Required"] = "true";
        ctx.Result = new ObjectResult(Result.Forbidden(MessageKeys.Auth.EmailNotVerified))
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };
    }
}
