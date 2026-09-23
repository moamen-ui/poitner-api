using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Pointer.API.Auth;
using Pointer.Application.Response;
using Pointer.Infrastructure.Audit;

namespace Pointer.API.Auth;

/// <summary>
/// DB-12 §3.8 — coverage enforcement. Runs after the ACTION (result filters and result execution
/// still follow), reads the status from <c>executed.Result</c> (the status-carrying action-result
/// interface) —
/// never from the response, whose StatusCode is still the default 200 here. An
/// <c>[Audited]</c> action that completed successfully without writing an audit row (the writer
/// sets <c>Items[AuditWriter.WrittenItemKey]</c>) logs <c>AUDIT GAP</c>; with
/// <c>Audit:StrictCoverage=true</c> the result is replaced with a 500 (legal — the result has not
/// executed yet). Strict defaults OFF until DB-12 part 2 lands the call sites.
/// </summary>
public class AuditCoverageFilter(ILogger<AuditCoverageFilter> logger, IConfiguration config)
    : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        var executed = await next(); // runs the ACTION only; the IActionResult has NOT been executed yet (MVC runs result filters + result execution after every action filter returns) — Response.HasStarted is false here.
        var audited = (
            ctx.ActionDescriptor as ControllerActionDescriptor
        )?.MethodInfo.GetCustomAttribute<AuditedAttribute>();
        if (audited is null || executed.Exception is not null || executed.Canceled)
            return;
        var status =
            (executed.Result as IStatusCodeActionResult)?.StatusCode ?? StatusCodes.Status200OK; // NEVER read the status off the response — it is still the default 200 at this point
        if (status >= 400)
            return;
        if (ctx.HttpContext.Items.ContainsKey(AuditWriter.WrittenItemKey))
            return;
        ctx.HttpContext.Items["audit.gap"] = audited.Action;
        logger.LogError(
            "AUDIT GAP: {Action} completed without an audit row ({Method} {Path})",
            audited.Action,
            ctx.HttpContext.Request.Method,
            ctx.HttpContext.Request.Path
        );
        if (config.GetValue("Audit:StrictCoverage", false))
            executed.Result = new ObjectResult(Result.Failure("Audit gap")) // legal: the result is replaced before it executes
            {
                StatusCode = StatusCodes.Status500InternalServerError,
            };
    }
}
