# R1-05 — Allowed origins + comment rate limit (§41 · Release 1 · 1 day)

## Goal
Two independent protections. (1) **Origin allowlist — an anti-footgun, not a security control:** a
project owner can restrict which *sites* the widget may post comments from, so a copied project key on
the wrong site (a fork, a stale preview, a competitor's page) cannot fill the queue by accident. It is
**bypassable by design** for non-quick-access callers that send no `Origin` (CLI, curl, AI agents) — see
Design A. (2) **Per-user comment rate limit:** no single authenticated account (or leaked key) can flood
a project. Flooding is handled by the limiter, never by the origin check. Comment creation already requires
a JWT (`API/Controllers/CommentsController.cs:13-21`), so anonymous abuse is out of scope.

## Out of scope
- Anonymous abuse (already impossible). CAPTCHA. IP reputation.
- Per-plan limits (`MaxCommentsPerMonth` is already enforced, `CommentService.cs:89-100`).
- A dashboard UI for app-urls beyond what exists (Project → environments/app-urls already manage `ProjectAppUrl` rows: `Admin/ProjectsController.cs:62-88`).

## Prerequisites
- None (independent). Facts: `ProjectAppUrl { ProjectId, AppEnvironmentId, Url, IsActive, OwnerId }`
  (`Domain/Entity/ProjectAppUrl.cs`); origin matching helper `Application/Common/OriginNormalizer.cs`;
  the widget-status gate already compares origins in `ProjectService.CheckWidgetActiveAsync`
  (`ProjectService.cs:793-840`) with the semantics "origin matching an *inactive* row is blocked; an
  origin matching **no** row is allowed". Rate-limit infrastructure: `API/Extensions/RateLimitingExtensions.cs`
  (per-IP fixed windows `signup`, `demo`, `plans`).

## Design

### A. Origin enforcement mode on the project
- New column `Project.EnforceAllowedOrigins` (bool, default **false**) — additive migration `AddProjectEnforceAllowedOrigins`.
- Semantics when `true`: for **comment creation** (`POST /api/projects/{key}/comments`) and **replies**
  (`POST /api/comments/{id}/replies`) the request's origin — `Origin` header, else the origin of `Referer`
  — must normalise (`OriginNormalizer.Normalize`) to one of the project's `ProjectAppUrl` rows with
  `IsActive == true`. Otherwise `403` with `Result.Failure(MessageKeys.Project.OriginNotAllowed)`.
- **Environment for the check**: comment create → `CreateCommentRequest.Environment`; reply → the
  **parent comment's** `Environment` (`AddReplyRequest` has only `Body`, `Application/DTOs/Comment/AddReplyRequest.cs`;
  `CommentService.AddReplyAsync` already loads the parent).
- **Local environment exemption**: if that environment is `EnvironmentTag.Local` **and** the origin host is
  `localhost`, `127.0.0.1`, `[::1]` or ends with `.localhost`, allow regardless (developers never
  configure their dev ports). Non-local environments get no exemption.
- **Dashboard exemption**: the dashboard itself posts replies/status changes, and in production it is
  served from **more than one host** (`app.pointer.moamen.work` **and** `app-angular.pointer.moamen.work`,
  `DEPLOY.md:10`; more per-framework hosts are planned). Always allow: (a) the branding app URL read via
  `ISettingsService.GetStringAsync(ISettingsService.BrandUrlApp, "https://app.pointer.moamen.work")`
  (`ISettingsService.cs:26,52` — `IBrandingService` exposes no direct `Urls.App` getter, its `GetAsync`
  needs a `publicBase`), **plus** (b) a configurable list `Security:TrustedDashboardOrigins` (string array;
  default in `appsettings.json`: `["https://app-angular.pointer.moamen.work"]`; env form
  `Security__TrustedDashboardOrigins__0`). Compare normalised origins; read once per request.
- No `Origin` and no `Referer` (CLI/curl/AI clients) → **allowed** when the caller's JWT is *not*
  quick-access (`is_quick_access` claim false); quick-access (Client) tokens without an origin → 403.
  Rationale: automation uses staff keys; clients only ever come through the widget.
- When `false` (default): behaviour exactly as today. Widget-status gate (`CheckWidgetActiveAsync`) is
  unchanged — it still blocks inactive matched rows.
- **Wildcards — Decision:** a single `*` is allowed **only inside the leftmost host label** of a
  `ProjectAppUrl.Url`, e.g. `https://myapp-*.vercel.app`, `https://pr-*.preview.acme.com`,
  `https://*.staging.acme.com`. Rules enforced by `OriginNormalizer.ValidatePattern` (400 on save):
  1. at most one `*`, only in the leftmost label; scheme and port must be explicit or default;
  2. if the leftmost label is exactly `*`, the remaining host must have **≥ 3 labels** (`*.staging.acme.com`
     ok; `*.acme.com` rejected — too broad for a comment-ingest allowlist);
  3. the remaining host must not be on the **shared-hosting suffix list** (hard-coded, extend as needed):
     `vercel.app, netlify.app, pages.dev, github.io, web.app, firebaseapp.com, herokuapp.com,
     azurewebsites.net, cloudfront.net, amplifyapp.com, onrender.com, fly.dev, railway.app, surge.sh,
     ngrok.io, ngrok-free.app, loca.lt` — unless the leftmost label has a non-empty literal part
     (`myapp-*` on `vercel.app` is fine; bare `*` on `vercel.app` is rejected: it would authorise every
     other Vercel tenant).
  Suffix-list semantics: reject when the remaining host **equals** a list entry **or ends with `.` +
  entry** (so `*.foo.github.io` is caught too).
  `OriginNormalizer.Matches(pattern, origin)`: same scheme, same port, label-count equal, leftmost label
  glob-matched, all other labels exact. Existing exact-match rows unchanged.
- **Host extraction**: normalised origins are strings; extract the host with `new Uri(origin).Host` *before*
  normalising/comparing. `Uri.Host` yields `[::1]` bracketed for IPv6 — the localhost check accepts both
  `[::1]` and `::1`.
- **403 plumbing**: the service returns `Result<T>.Forbidden(MessageKeys.Project.OriginNotAllowed)`
  (`Application/Response/Result.cs:29,43`). Today neither `CommentsController.Create`
  (`CommentsController.cs:17-20`: NotFound/Conflict/Ok/BadRequest only) nor `RepliesController.Add` has a
  forbidden branch — a service `Forbidden` would surface as **400**. Add
  `if (result.IsForbidden) return StatusCode(403, result);` to both actions before the `BadRequest` fallthrough.
- **`MessageKeys` are English literals, not keys**: `Result` carries only flags + a human `message`
  (`Result.cs:10-32`); `MessageKeys.Project.*` values are plain English (`MessageKeys.cs:39-50`) and nothing
  localises them. Clients must key off **HTTP 403**, never message text.

### B. Exposure
- `ProjectResponse.EnforceAllowedOrigins` (bool) and `UpdateProjectRequest.EnforceAllowedOrigins?` (bool) —
  same admin-or-creator gate as every project setting (`ProjectService.UpdateAsync`).
- `CaptureConfigResponse` unchanged (the widget doesn't need to know).
- Widget: detection is by **HTTP status 403 only** (never message text). In `element.ts` `saveComment`
  (the existing failure toast is inline there, `element.ts:~1182-1184`; `templates.ts` is the `TPL` markup
  object and has no string catalogue), a 403 shows the toast "Comments are not allowed from this address";
  other non-401 errors keep the existing generic toast. Rebuild `pointer.js`.
  Widget DoD for this doc = `npm run typecheck && npm run build` only (no `npm test` exists in
  `web-component/` until R3-03 adds it).

### C. Rate limiting (per user, not per IP)
- **Policy ownership** (each doc adds its own `AddPolicy` in `RateLimitingExtensions.cs`; expect a trivial
  merge conflict in that file when R1 docs land in parallel — resolve by keeping all policies, never by
  duplicating a name, since a duplicate `AddPolicy` throws at startup):
  - `events` (60/min per user) — **owned by R1-02**.
  - `meta` (120/min per IP) — **owned by R1-04**.
  - **This doc adds `comments` and `login`:**
  - `comments`: sliding window **30 requests / 60 s** partitioned by user id (`sub` claim) — fall back to IP for anonymous (never happens on these endpoints, but the partition function must not throw).
  - `login`: 60/min per IP — applied to **`POST /api/auth/login-with-key` only** (CLI/agent surface, today
    unlimited) and later `login-with-invite` (R2-05). **Not** applied to `POST /api/auth/login`:
    `Tests/AuthRateLimitingTests.cs:21-27` `Login_IsNotRateLimited` asserts the password login carries no
    `EnableRateLimiting` (header comment `:13-19` — widget logins from arbitrary origins share NAT budgets),
    and that test stays valid and green.
  - Helper `static string UserOrIp(HttpContext ctx)` (shared by `comments`/`events`).
- Apply `[EnableRateLimiting("comments")]` to `CommentsController.Create` and `RepliesController.Add`.
  Rejections return 429 + `Retry-After` (existing `OnRejected`).
- Configurable: `RateLimits:CommentsPerMinute` (default 30) read at startup, so a self-hoster can raise it.

### D. Audit signal
On each 403 (origin) and 429 (comments) write a `UsageEvent` (`Type: "comment_rejected"`, `Meta: {reason, origin}`) via the R1-02 service if present; otherwise a structured `ILogger` warning. Keeps §41 observable without a new table. `Meta.origin` is attacker-controlled free text: **truncate to 200 chars** and treat it as untrusted data everywhere it is displayed (same rule as comment bodies in `skill.md`).

## Tasks
1. Migration + entity + `ProjectMapping` (`Infrastructure/Mappings/ProjectMapping.cs`) for `EnforceAllowedOrigins`.
2. `OriginNormalizer.ValidatePattern(url)` + `Matches(pattern, origin)` per Design A (leftmost-label glob, ≥3-label rule, shared-suffix list) + tests.
3. `IProjectService.IsOriginAllowedAsync(int projectId, string? origin, EnvironmentTag env, bool isQuickAccess)` implementing A; unit tests for every branch (exact match, wildcard, inactive row, no rows + enforce on → 403, local exemption, dashboard exemption, no-origin staff vs quick-access).
4. Call it from `CommentService.CreateAsync` (request environment) and `CommentService.AddReplyAsync` (`CommentService.cs:589`, parent comment's environment); resolve origin from `IHttpContextAccessor`: `Origin` header → `Referer` origin. Return `Result<T>.Forbidden(MessageKeys.Project.OriginNotAllowed)` (add the English literal to `MessageKeys.cs`; there is no localisation layer).
4b. Add the `IsForbidden → StatusCode(403, result)` branch to `CommentsController.Create` and `RepliesController.Add`.
4c. `Security:TrustedDashboardOrigins` config (appsettings default `["https://app-angular.pointer.moamen.work"]`, `.env.prod.example` comment) + branding `BrandUrlApp` read.
5. DTOs: `ProjectResponse`, `UpdateProjectRequest`, `ProjectService.UpdateAsync` mapping; **create** `Application/Validators/SetProjectAppUrlValidator.cs` (none exists today — `Application/Validators/` has only Create/UpdateProjectValidator) calling `OriginNormalizer.ValidatePattern`; `ProjectService.SetAppUrlAsync` returns 400 on an invalid pattern.
6. Rate-limit policies `comments` + `login`, attributes on `Create`/`Add`/`login-with-key`, config knob; `Tests/CommentRateLimitingTests.cs` in the **same reflection pattern as `Tests/AuthRateLimitingTests.cs:21-40`** — assert the `comments` policy options (30 / 60 s sliding window, partition by `sub`) and `[EnableRateLimiting("comments")]` presence on `CommentsController.Create` and `RepliesController.Add`, `login` on `login-with-key` and **absent** on `Login`. (No `WebApplicationFactory` exists in `Tests/`; live-429 assertions are E2E, see Tests.)
7. Widget: 403 → toast in `element.ts` `saveComment`; `npm run typecheck && npm run build`; commit artifacts.
8. Audit signal (D).
9. Docs: `pointer-init.md` gets a short "Restrict origins" note under the Swagger/production section; `AGENTS.md` mentions the knob.

## Dashboard tasks
- Regenerate services (`ProjectResponse.enforceAllowedOrigins`, `UpdateProjectRequest.enforceAllowedOrigins`).
- Project settings: a toggle "Only accept comments from the configured app URLs" next to the existing app-urls editor, with helper text listing the active URLs; disabled state hint when no URLs are configured.

## Tests
- `Tests/OriginNormalizerTests.cs` (wildcards incl. suffix-list equals/ends-with, ports, schemes, `Uri.Host` extraction, IPv6 `[::1]`/`::1` localhost).
- `Tests/ProjectOriginEnforcementTests.cs` (service branches incl. both dashboard origins and `Result.IsForbidden`).
- `Tests/CommentRateLimitingTests.cs` (attribute/options reflection — see Task 6).
- `Tests/SetProjectAppUrlValidatorTests.cs` (patterns accepted/rejected per Design A).
- E2E (R2-00): `origin-enforced-blocks-foreign-origin`, `origin-enforced-allows-localhost-local`, `comment-burst-429`.

## Acceptance criteria
- [ ] Default projects behave exactly as before (all existing tests green, no new 403s).
- [ ] With `enforceAllowedOrigins=true` and app-url `https://app.example.com`: comment POST with `Origin: https://evil.example` → 403 `OriginNotAllowed`; with `Origin: https://app.example.com` → 200; `Environment=Local` + `Origin: http://localhost:5173` → 200; staff key via curl without Origin → 200; quick-access token without Origin → 403.
- [ ] `https://myapp-*.vercel.app` row matches `https://myapp-pr-12.vercel.app` and not `https://other.vercel.app`; saving `https://*.vercel.app` or `https://*.acme.com` → 400; `https://*.staging.acme.com` saves and matches `https://pr-7.staging.acme.com`.
- [ ] A reply posted from the dashboard origin (`Urls.App`) succeeds with enforcement on and no matching app-url row.
- [ ] 31 comment POSTs from one user inside 60 s → the 31st is 429 with `Retry-After`; a second user is unaffected (E2E `comment-burst-429`, against the running API — not a unit test).
- [ ] `Tests/AuthRateLimitingTests.Login_IsNotRateLimited` still green; `login-with-key` carries `[EnableRateLimiting("login")]`.
- [ ] Widget shows the not-allowed toast on a 403 response (R2-00 spec scenario with a mocked 403; detection is status-based).
- [ ] A reply posted with `Origin: https://app-angular.pointer.moamen.work` succeeds with enforcement on.

## Rollout / compatibility
Additive column, default off — zero behaviour change until an owner enables it. Rate limit of 30/min is far above human speed; document the knob for bulk imports (`ExportImport` uses its own path and is unaffected — verify).

## Report template
Files changed · `just test` line · curl transcript for the five origin cases and the 429 case · widget artifact commit hash.
