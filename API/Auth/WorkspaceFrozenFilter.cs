using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pointer.Application.Abstractions;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Auth;

/// <summary>
/// DB-18 §3.5 — the workspace-freeze gate. Copies the shape of <see cref="RequireVerifiedEmailFilter"/>.
/// Global filter, registered after it in <c>Program.cs</c>. Decision order:
/// <list type="number">
/// <item>not authenticated, or the endpoint carries <see cref="IAllowAnonymous"/> metadata → next.</item>
/// <item><see cref="ICurrentUser.IsSuperAdmin"/> (plain or impersonating — impersonation writes are
/// fenced already) → next.</item>
/// <item>no tenant claim → next.</item>
/// <item>not a key session: GET/HEAD/OPTIONS, or <see cref="AllowWhenWorkspacePausedAttribute"/>
/// present (method wins over class) → next. A key session additionally requires
/// <see cref="AllowWhenWorkspacePausedAttribute.AllowKeySessions"/> — every other method is checked,
/// reads included (D18.4).</item>
/// <item>not frozen (or the lookup throws — fail-open like <see cref="RequireVerifiedEmailFilter"/>) → next.</item>
/// <item>otherwise 423 Locked, header <c>X-Workspace-Paused: true</c>.</item>
/// </list>
/// </summary>
public class WorkspaceFrozenFilter(ILogger<WorkspaceFrozenFilter> logger) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        var http = ctx.HttpContext;

        if (http.User?.Identity?.IsAuthenticated != true)
        {
            await next();
            return;
        }

        if (ctx.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any())
        {
            await next();
            return;
        }

        var current = http.RequestServices.GetRequiredService<ICurrentUser>();

        // Impersonation writes are already fenced by the impersonation/content-boundary machinery
        // (R18) — a plain super admin owns nothing to freeze either.
        if (current.IsSuperAdmin)
        {
            await next();
            return;
        }

        if (current.TenantId is not Guid tenantId)
        {
            await next();
            return;
        }

        var cad = ctx.ActionDescriptor as ControllerActionDescriptor;
        var attr =
            cad?.MethodInfo.GetCustomAttributes(typeof(AllowWhenWorkspacePausedAttribute), false)
                .Cast<AllowWhenWorkspacePausedAttribute>()
                .FirstOrDefault()
            ?? cad?.ControllerTypeInfo.GetCustomAttributes(
                    typeof(AllowWhenWorkspacePausedAttribute),
                    false
                )
                .Cast<AllowWhenWorkspacePausedAttribute>()
                .FirstOrDefault();

        var isKeySession = current.KeyScopes != null;

        if (isKeySession)
        {
            if (attr?.AllowKeySessions == true)
            {
                await next();
                return;
            }
            // D18.4: every other key-session call is checked, reads included.
        }
        else
        {
            var isRead =
                HttpMethods.IsGet(http.Request.Method)
                || HttpMethods.IsHead(http.Request.Method)
                || HttpMethods.IsOptions(http.Request.Method);
            if (isRead || attr != null)
            {
                await next();
                return;
            }
        }

        WorkspaceFreeze state;
        try
        {
            var workspaceState = http.RequestServices.GetRequiredService<IWorkspaceStateService>();
            state = await workspaceState.GetAsync(tenantId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "WorkspaceFrozenFilter: lookup failed; allowing request (fail-open)."
            );
            await next();
            return;
        }

        if (!state.IsFrozen)
        {
            await next();
            return;
        }

        var msg =
            state.DeletionScheduledFor != null ? MessageKeys.Workspace.DeletionScheduledReadOnly
            : state.PausedByOperator ? MessageKeys.Workspace.PausedByOperator
            : MessageKeys.Workspace.Paused;

        http.Response.Headers["X-Workspace-Paused"] = "true";
        ctx.Result = new ObjectResult(Result.Failure(msg)) { StatusCode = 423 };
    }
}
