# R5-58 — Observability baseline (§58 · Release 5 · 2–3 d)

**Status (2026-09-23):** shipped `b3160c2`; live — `/health` returns `{"status":"Healthy",…}`,
`X-Request-Id` is echoed, JSON logs carry a request scope. `SENTRY_DSN` and `UPTIME_PING_URL` are
**empty in production** until the owner supplies them; `docs/runbooks/ALERTING.md` exists.
Implementation finding: claims are read lazily at log-write time because the request-id
middleware runs before authentication (§3.2 design note).

## 1. Goal

Ship the minimum observability stack before any customer data hits the system: structured JSON logs
with a request id and user/tenant context on every line, an `X-Request-Id` response header, a
`/health` endpoint that pings the DB, an error tracker (Sentry), and a hook for an external uptime
monitor. The system has **none** of these today — `Program.cs` uses the default console formatter
(unstructured), no health check endpoint exists (`00-API-INVENTORY.md:75` "No `/api/meta`, `/api/health`, `/api/version` exists today"), and grep for `Sentry`, `Serilog`, `HealthCheck` across every `.csproj` and `Program.cs` returns zero hits.

Effort: 2–3 d.

## 2. Prerequisites (verified facts)

- **Logging today**: `API/Program.cs:20` `var builder = WebApplication.CreateBuilder(args);` — the
  default `builder` has no explicit logging configuration; ASP.NET's default console formatter writes
  plain text. No `Serilog` or `Microsoft.Extensions.Logging.Console.JsonConsoleFormatter` is
  referenced anywhere (`grep -rn 'Serilog\|JsonConsole' *.csproj API/ → 0 hits`).
- **No Sentry**: `grep -rn 'Sentry' *.csproj API/Program.cs → 0 hits`.
- **No health check**: `grep -rn 'Health' API/Program.cs API/Extensions/ → 0 hits`; `00-API-INVENTORY.md:75`.
- **Exception handler**: `API/Program.cs:196-216` — catches unhandled exceptions, logs via
  `ILogger<Program>.LogError`, returns `Result.Failure`. No request id in the log.
- **EF/Postgres**: `Infrastructure/Pointer.Infrastructure.csproj:10` `Microsoft.EntityFrameworkCore.Design`
  8.0.11; `:14` `Npgsql.EntityFrameworkCore.PostgreSQL` (same 8.0.11); `docker-compose.prod.yml:6`
  `image: postgres:15`; connection string at `:23`.
- **Rate limiting policies**: `RateLimitingExtensions.cs:45-139` — `signup`, `login`, `demo`,
  `plans`, `meta`, `events`, `comments`, `builds`, `device-start`, `device-poll`. We will reuse
  `meta` (120/min/IP, `:99-107`) for `/health`.
- **Auth pipeline**: `Program.cs:343-345` `UseAuthentication(); UseAuthorization(); UseRateLimiter();`.
- **Compose prod**: `docker-compose.prod.yml:14-61` (api service), `:63-76` (caddy).
  `.env.prod.example:1-41`.
- **Deploy**: `scripts/deploy-api.sh` (106 lines); smoke checks at `:98-102`.
- **On-disk contract**: `docs/ON-DISK-CONTRACT.md:21` served URLs list — `/health` is NOT listed;
  it is an infra endpoint, not customer-facing, so it does **not** need to be added to the frozen
  contract. No new `data-*`, storage key, or window global.
- **Tests**: `Tests/` xUnit; `justfile:8` `just test` = `dotnet test`.
- **Caddy access log**: `Caddyfile` currently has no explicit `log` directive — Caddy 2 logs to
  stdout by default in JSON format when run as a container (`docker compose logs caddy` shows them).
- **Dependencies**: independent of DB-11a/b/c/d, DB-12 (`docs/db/execution/DB-12-audit-log.md`,
  status "written; not implemented") and DB-13 (`docs/db/execution/DB-13-operator-impersonation.md`,
  same status) — this doc adds request-id/log/health infra only, no identity or audit-log coupling.

## 3. Design

### 3.1 Structured JSON logging (built-in formatter)

**Decision: use the built-in `JsonConsoleFormatter`**, not Serilog.

*Justification*: the API is a single .NET 8 process behind Caddy; the built-in JSON console
formatter (`Microsoft.Extensions.Logging` → `Console` → `FormatterName = "json"`) produces
Newline-delimited JSON to stdout, which Docker captures, and `docker compose logs` already
presents. Adding Serilog would introduce 4 new NuGet packages for a single-process API with no
need for multi-sink structured logging, file rotation, or seq/elastic ingestion. The built-in
formatter is zero-dependency and sufficient.

`API/Extensions/ObservabilityExtensions.cs` (new):

```csharp
namespace Pointer.API.Extensions;

public static class ObservabilityExtensions
{
    public static IServiceCollection AddObservability(this IServiceCollection services,
        IConfiguration config, ILoggingBuilder logging)
    {
        // 3.1 — structured JSON console logging (replaces the default plain-text formatter).
        logging.AddConsole(o => o.FormatterName = "json");
        logging.AddJsonConsole(o =>
        {
            o.IncludeScopes = true;
            o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
            o.UseUtcTimestamp = true;
        });
        return services;
    }
}
```

### 3.2 Request-id middleware

New `API/Middleware/RequestIdMiddleware.cs`:

- On every request: read `X-Request-Id` from the incoming headers (Caddy or a load balancer may set
  it). If absent, generate `Guid.NewGuid().ToString("N")` (32 hex chars, no dashes).
- Set the response header `X-Request-Id` to that value.
- Push a logging scope `{ "RequestId": id, "UserId": sub, "TenantId": tenant }` around `next()`.
  `sub` is extracted the same way `RateLimitingExtensions.PartitionKeyFor` (`:191-199`) reads the
  user id (`ClaimTypes.NameIdentifier` → JWT `sub` claim fallback); `tenant` is a separate claim
  (`ctx.User.FindFirst("tenant")?.Value` — the `tenant` claim minted by `JwtTokenService`, holding
  `User.OwnerId`, per `00-API-INVENTORY.md` §1) that `PartitionKeyFor` does not itself read. Both
  are `null` for anonymous requests.
- Register early in the pipeline — `Program.cs`, immediately after `app.UseForwardedHeaders(fwd);`
  (`:189`), before the exception handler (`:196`).

### 3.3 `/health` endpoint

`Microsoft.Extensions.Diagnostics.HealthChecks` (the base health-check API + `AddHealthChecks()`/
`MapHealthChecks()`) ships in the ASP.NET Core shared framework — no NuGet needed. **`AddDbContextCheck<T>`
does not** — it lives in the separate `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore`
NuGet package, which is not referenced anywhere in this repo today (`API/Pointer.API.csproj`,
`Infrastructure/Pointer.Infrastructure.csproj`) — add
`<PackageReference Include="Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore" Version="8.0.*" />`
to `API/Pointer.API.csproj` (see File-level tasks).

In `ObservabilityExtensions`:
```csharp
services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("db", tags: new[] { "ready" });
```

In `Program.cs`, after `app.MapControllers();` (`:418`):
```csharp
app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = async (ctx, report) =>
    {
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsJsonAsync(new { status = report.Status.ToString(), checks = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString(), ms = e.Value.Duration.TotalMilliseconds }) });
    }
}).AllowAnonymous().RequireRateLimiting("meta");
```

This endpoint is:
- **Anonymous** (no JWT needed — uptime monitors and load balancers cannot authenticate).
- **Rate-limited** under `meta` (120/min/IP, `RateLimitingExtensions.cs:99-107`).
- **Excluded from auth** by `AllowAnonymous()` on the endpoint, plus it sits after `MapControllers`
  so the global auth pipeline applies but AllowAnonymous overrides it.

### 3.4 Sentry error tracking

Add `Sentry.AspNetCore` NuGet to `API/Pointer.API.csproj`. Configure:

```csharp
var sentryDsn = config["SENTRY_DSN"];
if (!string.IsNullOrWhiteSpace(sentryDsn))
{
    builder.WebHost.UseSentry(o =>
    {
        o.Dsn = sentryDsn;
        o.TracesSampleRate = 0.1;
        o.SendDefaultPii = false;
        o.Environment = builder.Environment.EnvironmentName;
    });
}
```

**Disabled when `SENTRY_DSN` is empty/unset** — local dev and test never send events. Add
`SENTRY_DSN=` (empty) to `.env.prod.example` and `docker-compose.prod.yml` api environment.

### 3.5 External uptime monitor

Pattern: the existing offsite-backup already uses a dead-man's-switch URL
(`OFFSITE_HEALTHCHECK_URL`, `scripts/offsite-backup.sh:13`). Follow the same pattern:

- Env var `UPTIME_PING_URL` in `.env.prod.example` and `docker-compose.prod.yml`.
- A `HostedService` (`API/Hosted/UptimePingService.cs`) that every 60 s GETs `/health` locally
  (localhost:8080), and if healthy, pings `UPTIME_PING_URL` via `HttpClient`. If `/health` is
  unhealthy or `UPTIME_PING_URL` is empty, does nothing.
- The monitoring service (healthchecks.io — the existing `OFFSITE_HEALTHCHECK_URL` implies the
  account exists) alerts when pings stop.

### 3.6 Alert rule

Document only (no code): `docs/runbooks/ALERTING.md` — when the healthchecks.io check for
`UPTIME_PING_URL` goes down, the operator gets an email. Additionally, Caddy's JSON access logs
(stdout) can be tailed with a simple script or log-based alert: `5xx > 5/min` in a 5-minute window.
Provide the `jq` one-liner to count 5xx lines from `docker compose logs caddy`. This is
sufficient pre-launch; a real alerting stack (Grafana/Loki or Datadog) is out of scope.

## 4. Safety / impact

**Additive** — code, two NuGet packages (`Sentry.AspNetCore`, none for health checks — built-in),
new middleware, new hosted service, new config lines. No migration, no data change, no existing
behaviour modified. The JSON log formatter changes log output format from plain text to JSON, which
is a visual change in `docker compose logs` output but does not affect any consumer (no log parser
exists today).

## 5. File-level tasks

1. **`API/Pointer.API.csproj`** — add `<PackageReference Include="Sentry.AspNetCore" Version="4.*" />`
   and `<PackageReference Include="Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore" Version="8.0.*" />`
   (§3.3 — `AddDbContextCheck<T>` is not in the shared framework).
2. **`API/Extensions/ObservabilityExtensions.cs`** (new) — §3.1 + §3.3 + §3.4.
3. **`API/Middleware/RequestIdMiddleware.cs`** (new) — §3.2.
4. **`API/Program.cs`**:
   - After `:20` (`var builder = …`), before the skill version block (`:22`): call
     `builder.Services.AddObservability(builder.Configuration, builder.Logging);`.
   - After `:189` (`app.UseForwardedHeaders(fwd);`), before `:196` (exception handler):
     `app.UseMiddleware<Pointer.API.Middleware.RequestIdMiddleware>();`.
   - After `:418` (`app.MapControllers();`): the `app.MapHealthChecks(...)` block from §3.3.
5. **`API/Hosted/UptimePingService.cs`** (new) — §3.5.
6. **`API/Program.cs`** — register: `builder.Services.AddHostedService<UptimePingService>();` after
   `:81` (next to the other hosted services).
7. **`docker-compose.prod.yml`** — add to `api.environment` (after `:59`, `Email__FromName`):
   ```yaml
   SENTRY_DSN: "${SENTRY_DSN:-}"
   UPTIME_PING_URL: "${UPTIME_PING_URL:-}"
   ```
8. **`.env.prod.example`** — append:
   ```
   # Error tracking (Sentry). Leave empty to disable.
   SENTRY_DSN=
   # Dead-man's-switch uptime ping URL (healthchecks.io). Pinged every 60s when /health is healthy.
   UPTIME_PING_URL=
   ```
9. **`docs/runbooks/ALERTING.md`** (new) — §3.6.
10. **`scripts/deploy-api.sh`** — add `/health` as a third path in the smoke-check loop at line 98:
    ```bash
    for path in /api/branding /swagger/v1/swagger.json /health; do
    ```
    so the verify step (lines 98-102) confirms the health endpoint is live.

## 6. Tests

New `Tests/ObservabilityTests.cs` (xUnit):

1. **`RequestIdMiddleware_EchoesIncomingId`** — construct a `DefaultHttpContext`, set
   `Request.Headers["X-Request-Id"]` to `"test-123"`, invoke the middleware, assert
   `Response.Headers["X-Request-Id"]` == `"test-123"`.
2. **`RequestIdMiddleware_GeneratesIdWhenMissing`** — no incoming header → response header is a
   32-char hex string (no dashes).
3. **`HealthEndpoint_Returns200_WithDbCheck`** — integration: `WebApplicationFactory<Program>` with
   an in-memory `AppDbContext`, GET `/health` → 200, body contains `"status":"Healthy"` and
   `"name":"db"`. (If `WebApplicationFactory` is not currently used in the test suite, use the
   simpler pure-unit approach: just test the middleware and the extension method registration; the
   `/health` endpoint is tested by the deploy smoke check.)
4. **`Sentry_NotConfigured_WhenDsnEmpty`** — unit: call the Sentry config block with an empty DSN,
   verify `UseSentry` is not called (or that the services collection does not contain the Sentry
   middleware). Alternatively, this is a smoke-check: the API boots without `SENTRY_DSN` set and
   does not throw.

## 7. Acceptance criteria

1. `docker compose logs api 2>/dev/null | head -5` → each line is valid JSON containing
   `"Timestamp"`, `"EventId"`, `"LogLevel"`, `"Category"`, `"Message"`.
2. `curl -sI https://api.pointer.moamen.work/api/branding | grep -i x-request-id` → header
   present, 32 hex chars.
3. `curl -s https://api.pointer.moamen.work/health | jq .status` → `"Healthy"`.
4. `curl -s -o /dev/null -w '%{http_code}' https://api.pointer.moamen.work/health` → `200`.
5. Unauthenticated: `curl -s https://api.pointer.moamen.work/health` → 200 (no Bearer token).
6. `dotnet test --filter ObservabilityTests` → green.
7. `grep -c SENTRY_DSN docker-compose.prod.yml .env.prod.example` → 1 each.
8. `grep -c UPTIME_PING_URL docker-compose.prod.yml .env.prod.example` → 1 each.
9. `bash scripts/deploy-api.sh` smoke includes `/health 200`.
10. With `SENTRY_DSN` unset, the API boots without error.

## 8. Rollback

`git revert` the commit; redeploy. No migration, no data. The only visible change is the log format
reverting to plain text and the `/health` endpoint disappearing.

## 9. Release steps

1. Merge PR. `unit` CI job covers the new tests.
2. On the VM: set `SENTRY_DSN` and `UPTIME_PING_URL` in `.env.prod` (create a Sentry project first;
   create a healthchecks.io check with a 2-minute grace period).
3. `bash scripts/deploy-api.sh` — watch for `/health 200` in the smoke output.
4. Verify criteria 1–5 from the VM.
5. Confirm the healthchecks.io dashboard shows the check as UP.

## 10. Out of scope

Serilog, OpenTelemetry, Grafana, Loki, Datadog, Prometheus metrics, distributed tracing, log
shipping, Caddy access-log parsing automation, dashboard health widget, `/api/meta` or
`/api/version` endpoints, request-id forwarding to downstream services (there are none).
Custom Sentry breadcrumbs, performance monitoring beyond `TracesSampleRate`, PII scrubbing
(already `SendDefaultPii = false`).
