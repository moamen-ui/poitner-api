using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Pointer.Application.Common;

namespace Pointer.API.Extensions;

/// <summary>
/// Static-file caching and version-routing pipeline for widget.js/widget.css (R3-03).
/// `/pointer.js`/`/pointer.css` are the pre-rename names, served byte-identical (see build.mjs)
/// and kept working as permanent aliases per docs/ON-DISK-CONTRACT.md — every already-integrated
/// site that hardcodes them must never break.
/// </summary>
public static class WidgetStaticPipeline
{
    private static bool IsWidgetBundlePath(PathString path) =>
        path.Equals("/widget.js", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/widget.css", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/pointer.js", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/pointer.css", StringComparison.OrdinalIgnoreCase);

    public static async Task HandleWidgetVersioningAsync(
        HttpContext ctx,
        Func<Task> next,
        WidgetVersionInfo widgetInfo)
    {
        var path = ctx.Request.Path;
        if (IsWidgetBundlePath(path))
        {
            if (ctx.Request.Query.TryGetValue("v", out var vVal))
            {
                var v = vVal.ToString();

                // If version descriptor was missing or corrupt at startup
                if (string.IsNullOrEmpty(widgetInfo.CurrentHash))
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    ctx.Response.Headers["X-Pointer-Widget-Version-Mismatch"] = "unknown";
                    AllowCrossOriginRead(ctx);
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
                AllowCrossOriginRead(ctx);
                return;
            }
        }
        await next();
    }


    /// <summary>
    /// CORS + header exposure for a widget 404.
    /// </summary>
    /// <remarks>
    /// A pinned install loads the widget with `crossorigin="anonymous"` (SRI requires it). Without
    /// Access-Control-Allow-Origin on the 404, the browser does not deliver a readable 404 to the
    /// page at all — it reports an opaque network failure, and X-Pointer-Widget-Version-Mismatch,
    /// which exists precisely so a pinned client can discover the build it should move to, is
    /// unreadable by the only kind of client that needs it.
    ///
    /// Expose-Headers is the other half: a cross-origin response only surfaces the safelisted
    /// headers to script unless it names the rest.
    /// </remarks>
    private static void AllowCrossOriginRead(HttpContext ctx)
    {
        ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
        ctx.Response.Headers["Access-Control-Expose-Headers"] = "X-Pointer-Widget-Version-Mismatch";
    }

    public static void PrepareStaticResponse(StaticFileResponseContext ctx)
    {
        var name = ctx.File.Name;

        // The widget's own assets are fetched cross-origin from every customer site that embeds it,
        // and this static-file middleware runs BEFORE UseCors — so the response is written and sent
        // without the CORS middleware ever seeing it. Without this header the browser blocks
        // widget.css on every install and the widget renders unstyled.
        //
        // `*` is correct rather than permissive: these are public, unauthenticated, read-only build
        // artifacts served to arbitrary unknown origins by design. That is the same reasoning
        // behind the open DEFAULT CORS policy for the widget surface.
        var isWidgetAsset =
            name.Equals("widget.js", StringComparison.OrdinalIgnoreCase)
            || name.Equals("widget.css", StringComparison.OrdinalIgnoreCase)
            || name.Equals("pointer.js", StringComparison.OrdinalIgnoreCase)
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
        else if (name.Equals("widget.js", StringComparison.OrdinalIgnoreCase)
            || name.Equals("widget.css", StringComparison.OrdinalIgnoreCase)
            || name.Equals("pointer.js", StringComparison.OrdinalIgnoreCase)
            || name.Equals("pointer.css", StringComparison.OrdinalIgnoreCase)
            || name.Equals("pointer.version.json", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache";
        }
    }
}
