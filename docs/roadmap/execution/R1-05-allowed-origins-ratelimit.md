# R1-05 — Allowed origins + comment rate limit (§41 · Release 1 · 1 day)

## Goal
A project owner can restrict **where** comments may be posted from (the app's real origins), and no
single authenticated account can flood a project with comments. Threat model (agreed in review): an
approved account or a leaked key — comment creation already requires a JWT
(`API/Controllers/CommentsController.cs:13-21`).

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
- **Local environment exemption**: if the request body's `Environment == EnvironmentTag.Local` **and**
  the origin host is `localhost`, `127.0.0.1`, `[::1]` or ends with `.localhost`, allow regardless
  (developers never configure their dev ports). Non-local environments get no exemption.
- No `Origin` and no `Referer` (CLI/curl/AI clients) → **allowed** when the caller's JWT is *not*
  quick-access (`is_quick_access` claim false); quick-access (Client) tokens without an origin → 403.
  Rationale: automation uses staff keys; clients only ever come through the widget.
- When `false` (default): behaviour exactly as today. Widget-status gate (`CheckWidgetActiveAsync`) is
  unchanged — it still blocks inactive matched rows.
- Wildcards: **Decision:** support a leading `*.` in `ProjectAppUrl.Url` host (e.g. `https://*.vercel.app`):
  `OriginNormalizer.Matches(pattern, origin)` — same scheme, host suffix match on `.vercel.app`, same port.
  Extend `SetProjectAppUrlRequest` validation to accept `*.` hosts. Existing exact-match behaviour unchanged.

### B. Exposure
- `ProjectResponse.EnforceAllowedOrigins` (bool) and `UpdateProjectRequest.EnforceAllowedOrigins?` (bool) —
  same admin-or-creator gate as every project setting (`ProjectService.UpdateAsync`).
- `CaptureConfigResponse` unchanged (the widget doesn't need to know).
- Widget: on `403` from comment create with message key `OriginNotAllowed`, show toast text
  "Comments are not allowed from this address" (add i18n string in `web-component/src/templates.ts`
  strings; rebuild `pointer.js`).

### C. Rate limiting (per user, not per IP)
- New policies in `RateLimitingExtensions.cs`:
  - `comments`: sliding window **30 requests / 60 s** partitioned by user id (`sub` claim) — fall back to IP for anonymous (never happens on these endpoints, but the partition function must not throw).
  - `events`: 60/min per user (used by R1-02's `POST /api/events`).
  - Helper `static string UserOrIp(HttpContext ctx)`.
- Apply `[EnableRateLimiting("comments")]` to `CommentsController.Create` and `RepliesController.Add`.
  Rejections return 429 + `Retry-After` (existing `OnRejected`).
- Configurable: `RateLimits:CommentsPerMinute` (default 30) read at startup, so a self-hoster can raise it.

### D. Audit signal
On each 403 (origin) and 429 (comments) write a `UsageEvent` (`Type: "comment_rejected"`, `Meta: {reason, origin}`) via the R1-02 service if present; otherwise a structured `ILogger` warning. Keeps §41 observable without a new table.

## Tasks
1. Migration + entity + `ProjectMapping` (`Infrastructure/Mappings/ProjectMapping.cs`) for `EnforceAllowedOrigins`.
2. `OriginNormalizer.Matches(pattern, origin)` with wildcard support + tests.
3. `IProjectService.IsOriginAllowedAsync(int projectId, string? origin, EnvironmentTag env, bool isQuickAccess)` implementing A; unit tests for every branch (exact match, wildcard, inactive row, no rows + enforce on → 403, local exemption, no-origin staff vs quick-access).
4. Call it from `CommentService.CreateAsync` and `ReplyService.AddAsync` (resolve origin from `IHttpContextAccessor`: `Origin` header → `Referer` origin). Message key `MessageKeys.Project.OriginNotAllowed` (+ en/ar resources where `MessageKeys` are localised).
5. DTOs: `ProjectResponse`, `UpdateProjectRequest`, `ProjectService.UpdateAsync` mapping; `SetProjectAppUrlRequest` validator accepts `*.` hosts.
6. Rate-limit policies + attributes + config knob; `Tests/CommentRateLimitingTests.cs` mirroring `Tests/AuthRateLimitingTests.cs` (31st request in a minute → 429 with `Retry-After`; different users independent).
7. Widget toast string + rebuild + commit artifacts.
8. Audit signal (D).
9. Docs: `pointer-init.md` gets a short "Restrict origins" note under the Swagger/production section; `AGENTS.md` mentions the knob.

## Dashboard tasks
- Regenerate services (`ProjectResponse.enforceAllowedOrigins`, `UpdateProjectRequest.enforceAllowedOrigins`).
- Project settings: a toggle "Only accept comments from the configured app URLs" next to the existing app-urls editor, with helper text listing the active URLs; disabled state hint when no URLs are configured.

## Tests
- `Tests/OriginNormalizerTests.cs` (wildcards, ports, schemes, IPv6 localhost).
- `Tests/ProjectOriginEnforcementTests.cs` (service branches).
- `Tests/CommentRateLimitingTests.cs`.
- E2E (R2-00): `origin-enforced-blocks-foreign-origin`, `origin-enforced-allows-localhost-local`, `comment-burst-429`.

## Acceptance criteria
- [ ] Default projects behave exactly as before (all existing tests green, no new 403s).
- [ ] With `enforceAllowedOrigins=true` and app-url `https://app.example.com`: comment POST with `Origin: https://evil.example` → 403 `OriginNotAllowed`; with `Origin: https://app.example.com` → 200; `Environment=Local` + `Origin: http://localhost:5173` → 200; staff key via curl without Origin → 200; quick-access token without Origin → 403.
- [ ] `https://*.vercel.app` row matches `https://pr-12-app.vercel.app`.
- [ ] 31 comment POSTs from one user inside 60 s → the 31st is 429 with `Retry-After`; a second user is unaffected.
- [ ] Widget shows the not-allowed toast on 403.

## Rollout / compatibility
Additive column, default off — zero behaviour change until an owner enables it. Rate limit of 30/min is far above human speed; document the knob for bulk imports (`ExportImport` uses its own path and is unaffected — verify).

## Report template
Files changed · `just test` line · curl transcript for the five origin cases and the 429 case · widget artifact commit hash.
