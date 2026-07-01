using FluentValidation.AspNetCore;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Pointer.API.Extensions;
using Pointer.API.Hosted;
using Pointer.API.Seed;
using Pointer.Application;
using Pointer.Infrastructure;

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
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddJwtAuth(builder.Configuration);
builder.Services.AddAuthorization();
builder.Services.AddHostedService<DemoCleanupService>();

builder.Services.AddRateLimiter(o =>
{
    // Per-IP fixed-window limiters (partition by client IP, honoring X-Forwarded-For
    // via ForwardedHeaders) so one abuser can't exhaust the limit for everyone.
    static string ClientIp(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    o.AddPolicy("signup", ctx =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            ClientIp(ctx),
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromHours(1),
                QueueLimit = 0
            }));

    o.AddPolicy("demo", ctx =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            ClientIp(ctx),
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 3,
                Window = TimeSpan.FromHours(1),
                QueueLimit = 0
            }));
});

// CORS is split by audience. The WIDGET is embedded on arbitrary customer sites and calls the
// public/widget endpoints (comments, replies, uploads, statuses, roles, register, me,
// predefined-actions) cross-origin from those unknown origins — so the DEFAULT policy stays
// open-origin (bearer API, no cookies → no AllowCredentials, so this is not CSRF-exploitable).
// The DASHBOARD-only surface (/api/admin/* and the sensitive auth endpoints: login / me /
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

var app = builder.Build();

if (builder.Configuration.GetValue<bool>("DBMigrationEnabled"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    await AdminSeeder.SeedAsync(app.Services);
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

// Map an UnauthorizedAccessException (e.g. an authenticated request whose token carries no valid
// subject claim — see ClaimsPrincipalExtensions.GetId) to a 401 instead of a leaked 500.
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
            var origin = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            var text = (await File.ReadAllTextAsync(file)).Replace("<POINTER_SERVER>", origin);
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

app.UseStaticFiles();

// Route-based CORS: lock the dashboard-only surface (/api/admin/* + sensitive auth endpoints) to
// the allow-list, and leave the open default policy for the widget/public endpoints. Selecting a
// per-request policy requires calling UseCors with an explicit policy inside a branch; the branch
// predicate matches the privileged routes only, so the widget's cross-origin calls are unaffected.
static bool IsDashboardOnly(HttpContext ctx)
{
    var path = ctx.Request.Path;
    if (path.StartsWithSegments("/api/admin", StringComparison.OrdinalIgnoreCase))
        return true;
    // Sensitive auth endpoints only — NOT register/register-admin/register-invite/signup-enabled,
    // which are part of the public/widget signup flow and must stay open-origin.
    return path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/auth/me", StringComparison.OrdinalIgnoreCase)
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
    var origin = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
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
    el.setAttribute('source-attr', 'data-component-source');
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

app.MapControllers();

app.Run();
