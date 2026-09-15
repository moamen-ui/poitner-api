using Microsoft.AspNetCore.Mvc;
using Pointer.Application.Services.Interfaces;
using FluentValidation.AspNetCore;
using MicroElements.Swashbuckle.FluentValidation.AspNetCore;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.OpenApi.Models;
using Pointer.API.Extensions;
using Pointer.API.Hosted;
using Pointer.API.Seed;
using Pointer.Application;
using Pointer.Application.Common;
using Pointer.Application.Response;
using Pointer.Infrastructure;

using Pointer.API.Startup;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers(options =>
{
    options.Filters.Add(new Microsoft.AspNetCore.Mvc.ProducesAttribute("application/json"));
});
builder.Services.AddEndpointsApiExplorer();
// Run registered FluentValidation validators automatically on model binding, so write DTOs
// (CreateCommentRequest, CreateProjectRequest, AddReplyRequest, etc.) return 400 on invalid input
// before reaching the controller/service. Validators themselves are registered in AddApplication().
builder.Services.AddFluentValidationAutoValidation();
// Auto-validation's default 400 is ASP.NET's own ValidationProblemDetails shape — nothing like the
// Result envelope every other endpoint returns (isSuccess/message/data), which breaks the
// dashboards' envelope-unwrapping interceptor and error-message extraction. Reshape it to match.
builder.Services.Configure<Microsoft.AspNetCore.Mvc.ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var message = context.ModelState.Values
            .SelectMany(v => v.Errors)
            .Select(e => e.ErrorMessage)
            .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m))
            ?? "Invalid request.";
        return new Microsoft.AspNetCore.Mvc.BadRequestObjectResult(Result.Failure(message));
    };
});
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddJwtAuth(builder.Configuration);
builder.Services.AddAuthorization();
builder.Services.AddHostedService<DemoCleanupService>();

builder.Services.AddApiRateLimiting(builder.Configuration);

// CORS is split by audience. The WIDGET is embedded on arbitrary customer sites and calls the
// public/widget endpoints (comments, replies, uploads, statuses, roles, login, register,
// predefined-actions) cross-origin from those unknown origins — so the DEFAULT policy stays
// open-origin (bearer API, no cookies → no AllowCredentials, so this is not CSRF-exploitable).
// login is a widget endpoint: the widget performs in-page login from whatever origin hosts it,
// so it cannot sit behind an origin allow-list. Login is deliberately NOT rate-limited (a shared
// NAT would exhaust any per-IP budget and lock everyone out); the "signup" limiter covers the
// account-creation/password-email surface only (see RateLimitingExtensions + AuthRateLimitingTests).
// The DASHBOARD-only surface (/api/admin/* and the dashboard-only auth endpoints: me /
// forgot-password / reset-password) is locked to an allow-list of known dashboard origins via the
// "dashboard" policy, applied by route below. This shrinks the origins that can drive privileged
// operations without breaking the widget.
const string DashboardCorsPolicy = "dashboard";
string[] dashboardOrigins =
[
    "https://app.pointer.moamen.work",
    "https://app-react.pointer.moamen.work",
    "https://app-vue.pointer.moamen.work",
    "https://demo.pointer.moamen.work",
    "https://pointer.moamen.work",
];
// Cors__ExtraDashboardOrigins (comma-separated) extends the allow-list per environment —
// local dev sets it to the localhost dev-server origins in .env; prod leaves it unset.
var extraDashboardOrigins = builder
    .Configuration["Cors:ExtraDashboardOrigins"]
    ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (extraDashboardOrigins is { Length: > 0 })
    dashboardOrigins = [.. dashboardOrigins, .. extraDashboardOrigins];
builder.Services.AddCors(o =>
{
    o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
    o.AddPolicy(DashboardCorsPolicy, p => p
        .WithOrigins(dashboardOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod());
});

builder.Services.AddSwaggerGen(c =>
{
    c.AddSecurityDefinition(
        "Bearer",
        new OpenApiSecurityScheme
        {
            In = ParameterLocation.Header,
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
        }
    );

    c.AddSecurityRequirement(
        new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id = "Bearer",
                    },
                },
                Array.Empty<string>()
            },
        }
    );
});
// Reflects every registered FluentValidation rule (NotEmpty → required, MaximumLength → maxLength,
// Matches → pattern, IsInEnum → enum, etc.) into the generated OpenAPI schema, so a consumer reading
// /swagger can see a DTO's real constraints instead of guessing from the 400 body at runtime.
builder.Services.AddFluentValidationRulesToSwagger();

builder.Services.AddSingleton<WidgetVersionInfo>(sp =>
{
    var env = sp.GetRequiredService<IWebHostEnvironment>();
    var logger = sp.GetRequiredService<ILogger<WidgetVersionInfo>>();
    return WidgetVersionInfo.Load(env.ContentRootPath, logger);
});

var app = builder.Build();

if (builder.Configuration.GetValue<bool>("DBMigrationEnabled"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    await AdminSeeder.SeedAsync(app.Services);
    // Moves any pre-hardening plaintext User.ApiKey into the hashed+encrypted api_keys table.
    // Idempotent and inline (not a hosted service) so it is guaranteed to follow the migration.
    await ApiKeyBackfill.RunAsync(app.Services);
}

// Behind a TLS-terminating reverse proxy (Caddy): honor X-Forwarded-Proto/For so
// Request.Scheme is "https" — the self-configuring /embed.js and served skills then
// emit https URLs. KnownProxies/Networks cleared because the proxy runs on the
// docker network (non-loopback) and the API isn't exposed directly.
var fwd = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
fwd.KnownNetworks.Clear();
fwd.KnownProxies.Clear();
app.UseForwardedHeaders(fwd);

// Global exception handler (early in the pipeline): map unhandled exceptions to the Result
// envelope so no error escapes as a raw 500 with a leaky text body. An UnauthorizedAccessException
// (e.g. an authenticated request whose token carries no valid subject — see
// ClaimsPrincipalExtensions.GetId) maps to 401; every other unhandled exception becomes a 500 with
// a generic Result.Failure body (no exception details are leaked).
app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (UnauthorizedAccessException)
    {
        if (!ctx.Response.HasStarted)
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
    }
    catch (Exception ex)
    {
        ctx.RequestServices.GetRequiredService<ILogger<Program>>()
            .LogError(ex, "Unhandled exception on {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
        if (ctx.Response.HasStarted)
            throw;
        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await ctx.Response.WriteAsJsonAsync(Result.Failure("An unexpected error occurred."));
    }
});

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    // Embed the Pointer feedback widget on this API's own Swagger as a consumer.
    // EVERY setting comes from the "Pointer" section of appsettings*.json, so it's
    // toggled/tuned per environment (appsettings.{Environment}.json or env vars):
    //   Enabled      — turn the embed on/off
    //   Server       — absolute Pointer server URL (used for embed.js + pointer.js)
    //   Project      — dashboard project key; blank → this app's name (assembly)
    //   Environment  — comment environment tag (local|staging|production)
    var pointer = app.Configuration.GetSection("Pointer");
    if (pointer.GetValue("Enabled", false))
    {
        var server = (pointer["Server"] ?? "http://localhost:8090").TrimEnd('/');
        var project = pointer["Project"];
        if (string.IsNullOrWhiteSpace(project)) project = app.Environment.ApplicationName;
        var environment = pointer["Environment"];
        if (string.IsNullOrWhiteSpace(environment)) environment = "staging";
        c.InjectJavascript($"{server}/embed.js?project={Uri.EscapeDataString(project)}&environment={Uri.EscapeDataString(environment)}");
    }
});

// Serve the skill markdown + the install script with the request origin injected
// into the <POINTER_SERVER> placeholder, so anything fetched from the deployed URL
// arrives pre-filled with that URL (no localhost, nothing to ask). The one-command
// installer (install.sh) downloads the skills from the same origin.
// Runs before UseStaticFiles so it takes precedence over the raw file.
var injectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "/pointer-init.md",
    "/skill.md",
    "/install.sh",
    "/pointer.sh",
};
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? string.Empty;
    if (HttpMethods.IsGet(ctx.Request.Method) && injectedFiles.Contains(path))
    {
        var webRoot = app.Environment.WebRootPath
            ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
        var file = Path.Combine(webRoot, path.TrimStart('/'));
        if (File.Exists(file))
        {
            var origin = PointerUrlResolver.ResolvePublicUrl(app.Configuration, ctx.Request);
            // These files are installed INTO the customer's repository (.claude/skills/…/SKILL.md,
            // .pointer/pointer.sh). A rebranded install that serves skills saying "Pointer"
            // throughout is the most visible white-label leak there is, so the product name is
            // substituted the same way the server URL already is.
            //
            // Only the prose name is templated. Every on-disk identifier — .pointer/, POINTER_*,
            // pointer.sh, pointer-feedback — is frozen by R1-01 and deliberately untouched.
            var settingsService = ctx.RequestServices.GetRequiredService<ISettingsService>();
            var product = await settingsService.GetStringAsync(
                ISettingsService.BrandProductName, BrandingDefaults.ProductName);

            // The stamp lets an installed copy be compared against the server's, so `doctor` can
            // say "your skill.md is from a different version" instead of the developer wondering
            // why an instruction they read months ago no longer matches the API.
            var skillVersion = Pointer.Application.Common.SkillVersionResolver.Resolve(app.Configuration);

            var text = (await File.ReadAllTextAsync(file))
                .Replace("<POINTER_SERVER>", origin)
                .Replace("<POINTER_PRODUCT>", product)
                .Replace("<POINTER_SKILL_VERSION>", skillVersion);
            ctx.Response.ContentType = path.EndsWith(".sh", StringComparison.OrdinalIgnoreCase)
                ? "text/x-shellscript; charset=utf-8"
                : "text/markdown; charset=utf-8";
            await ctx.Response.WriteAsync(text);
            return;
        }
    }
    await next();
});

app.UseDefaultFiles();

// Block direct static access to /uploads/* — files are only served through the
// HMAC-validated endpoint GET /api/uploads/file?p=...&exp=...&sig=...
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/uploads", StringComparison.OrdinalIgnoreCase))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await next();
});

// Widget versioning pipeline for /pointer.js and /pointer.css (R3-03)
app.Use((ctx, next) =>
{
    var widgetInfo = ctx.RequestServices.GetRequiredService<WidgetVersionInfo>();
    return WidgetStaticPipeline.HandleWidgetVersioningAsync(ctx, next, widgetInfo);
});

app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = WidgetStaticPipeline.PrepareStaticResponse,
});

// Route-based CORS: lock the dashboard-only surface (/api/admin/* + dashboard-only auth
// endpoints) to the allow-list, and leave the open default policy for the widget/public
// endpoints. Selecting a per-request policy requires calling UseCors with an explicit policy
// inside a branch; the branch predicate matches the privileged routes only, so the widget's
// cross-origin calls are unaffected.
static bool IsDashboardOnly(HttpContext ctx)
{
    var path = ctx.Request.Path;
    if (path.StartsWithSegments("/api/admin", StringComparison.OrdinalIgnoreCase))
        return true;
    // Dashboard-only auth endpoints — NOT login/register/register-admin/register-invite/
    // signup-enabled, which the widget calls in-page from arbitrary host origins and must stay
    // open-origin (login is guarded by the per-IP "signup" rate limit instead).
    return path.Equals("/api/auth/me", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/auth/forgot-password", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/auth/reset-password", StringComparison.OrdinalIgnoreCase);
}
app.UseWhen(IsDashboardOnly, branch => branch.UseCors(DashboardCorsPolicy));
app.UseWhen(ctx => !IsDashboardOnly(ctx), branch => branch.UseCors());

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Self-configuring embed loader: any page (e.g. another API's Swagger UI) can add
//   <script src="https://<pointer-server>/embed.js?project=<key>"></script>
// and it injects pointer.js + a configured <pointer-feedback>, with server = this
// origin. Reusable across projects; the project key comes from the query string.
app.MapGet("/embed.js", (HttpContext ctx) =>
{
    var origin = PointerUrlResolver.ResolvePublicUrl(app.Configuration, ctx.Request);
    static bool Safe(string s) => s.Length > 0 && s.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-');
    var project = ctx.Request.Query["project"].ToString();
    var environment = ctx.Request.Query["environment"].ToString();
    var safeProject = Safe(project) ? project : "";
    var safeEnv = Safe(environment) ? environment : "staging";
    var js =
$$"""
(function () {
  if (window.__pointerEmbedded) return;
  window.__pointerEmbedded = true;
  var server = '{{origin}}';
  function mount() {
    var s = document.createElement('script');
    s.src = server + '/pointer.js';
    s.defer = true;
    document.head.appendChild(s);
    var el = document.createElement('pointer-feedback');
    el.setAttribute('project', '{{safeProject}}');
    el.setAttribute('server', server);
    el.setAttribute('environment', '{{safeEnv}}');
    document.body.appendChild(el);
  }
  // embed.js may run in <head> before <body> exists — wait for the DOM.
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', mount);
  else mount();
})();
""";
    ctx.Response.ContentType = "application/javascript; charset=utf-8";
    return ctx.Response.WriteAsync(js);
});

app.MapGet("/check", async (HttpContext ctx, [FromServices] ISettingsService settings) =>
{
    var origin = PointerUrlResolver.ResolvePublicUrl(app.Configuration, ctx.Request);
    static bool Safe(string s) => s.Length > 0 && s.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-');
    var project = ctx.Request.Query["project"].ToString();
    var environment = ctx.Request.Query["environment"].ToString();
    var safeProject = Safe(project) ? project : "";
    var safeEnv = Safe(environment) ? environment : "staging";
    var productName = await settings.GetStringAsync(ISettingsService.BrandProductName, "Pointer");

    var html =
$"""
<!DOCTYPE html>
<html>
<head>
  <meta charset="utf-8">
  <title>{productName} check</title>
</head>
<body>
  <p>If you can see the {productName} button in the corner, the widget is served correctly. Sign in to test a comment.</p>
  <script src="{origin}/embed.js?project={Uri.EscapeDataString(safeProject)}&environment={Uri.EscapeDataString(safeEnv)}"></script>
</body>
</html>
""";
    ctx.Response.ContentType = "text/html; charset=utf-8";
    // await, not return. This lambda is `async`, so returning the Task makes its own return type
    // Task<Task>: the framework completes the response when the OUTER task finishes, which is
    // before WriteAsync has actually written. The body still usually arrives, but the terminating
    // zero-length chunk races it — clients see "transfer closed with outstanding read data
    // remaining" on a 200. On the one page a developer opens to confirm their install works.
    await ctx.Response.WriteAsync(html);
});

app.MapControllers();

app.Run();
