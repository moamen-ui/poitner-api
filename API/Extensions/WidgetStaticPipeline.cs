using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Pointer.Application.Common;

namespace Pointer.API.Extensions;

/// <summary>
/// Static-file caching and version-routing pipeline for pointer.js/pointer.css (R3-03).
/// </summary>
public static class WidgetStaticPipeline
{
    public static async Task HandleWidgetVersioningAsync(
        HttpContext ctx,
        Func<Task> next,
        WidgetVersionInfo widgetInfo)
    {
        var path = ctx.Request.Path;
        if (path.Equals("/pointer.js", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/pointer.css", StringComparison.OrdinalIgnoreCase))
        {
            if (ctx.Request.Query.TryGetValue("v", out var vVal))
            {
                var v = vVal.ToString();

                // If version descriptor was missing or corrupt at startup
                if (string.IsNullOrEmpty(widgetInfo.CurrentHash))
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    ctx.Response.Headers["X-Pointer-Widget-Version-Mismatch"] = "unknown";
                    return;
                }

                // Stable channel: current bytes with 1 hour cache
                if (string.Equals(v, "stable", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Items["WidgetCacheControl"] = "public, max-age=3600";
                    await next();
                    return;
                }

                // Pinned, immutable release from retained set
                if (!string.IsNullOrWhiteSpace(v) && widgetInfo.IsRetained(v))
                {
                    var fileName = path.Value!.TrimStart('/');
                    ctx.Request.Path = $"/widget/{v}/{fileName}";
                    ctx.Items["WidgetCacheControl"] = "public, max-age=31536000, immutable";
                    await next();
                    return;
                }

                // Unknown / pruned / malformed v
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                ctx.Response.Headers["X-Pointer-Widget-Version-Mismatch"] = widgetInfo.CurrentHash;
                return;
            }
        }
        await next();
    }

    public static void PrepareStaticResponse(StaticFileResponseContext ctx)
    {
        var name = ctx.File.Name;

        // The widget's own assets are fetched cross-origin from every customer site that embeds it,
        // and this static-file middleware runs BEFORE UseCors — so the response is written and sent
        // without the CORS middleware ever seeing it. Without this header the browser blocks
        // pointer.css on every install and the widget renders unstyled.
        //
        // `*` is correct rather than permissive: these are public, unauthenticated, read-only build
        // artifacts served to arbitrary unknown origins by design. That is the same reasoning
        // behind the open DEFAULT CORS policy for the widget surface.
        var isWidgetAsset =
            name.Equals("pointer.js", StringComparison.OrdinalIgnoreCase)
            || name.Equals("pointer.css", StringComparison.OrdinalIgnoreCase)
            || name.Equals("pointer.version.json", StringComparison.OrdinalIgnoreCase)
            || ctx.Context.Request.Path.StartsWithSegments("/widget", StringComparison.OrdinalIgnoreCase);

        if (isWidgetAsset)
        {
            ctx.Context.Response.Headers["Access-Control-Allow-Origin"] = "*";
        }
        if (ctx.Context.Items.TryGetValue("WidgetCacheControl", out var cc) && cc != null)
        {
            ctx.Context.Response.Headers.CacheControl = cc.ToString();
        }
        else if (ctx.Context.Request.Path.StartsWithSegments("/widget", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        }
        else if (name.Equals("pointer.js", StringComparison.OrdinalIgnoreCase)
            || name.Equals("pointer.css", StringComparison.OrdinalIgnoreCase)
            || name.Equals("pointer.version.json", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache";
        }
    }
}
