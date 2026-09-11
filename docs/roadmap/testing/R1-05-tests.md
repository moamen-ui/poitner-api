# R1-05-tests — Allowed origins + per-user comment rate limit

Harness: [`00-HARNESS.md`](00-HARNESS.md). Execution doc: [`../execution/R1-05-allowed-origins-ratelimit.md`](../execution/R1-05-allowed-origins-ratelimit.md).

## Covers

- AC-1 (default projects behave exactly as before) → existing phases stay green + R1-05-02 step 3
  (alpha, enforcement off, foreign origin accepted).
- AC-2 matrix: foreign origin 403 + exact-row 200 → **R1-05-01**; `Environment=Local` +
  `http://localhost:5173` → 200 → **R1-05-02**; staff key without `Origin` → 200 vs quick-access
  without `Origin` → 403 → **R1-05-04**.
- AC-3 wildcard rows (`myapp-pr-12` matches, `other` doesn't; `https://*.vercel.app` and
  `https://*.acme.com` → 400; `https://*.staging.acme.com` saves and matches) → **R1-05-03**.
- AC-4 + AC-8 (dashboard origins — `Urls.App` and `app-angular.pointer.moamen.work` — succeed with
  enforcement on and no matching row) → **R1-05-07**.
- AC-5 (31st comment 429 + `Retry-After`, second user unaffected) → **R1-05-05**.
- AC-7 (widget shows the not-allowed toast on 403) → **R1-05-06** — a **real** 403 from the stack,
  strictly stronger than the AC's mocked-403 framing.
- AC-6 (`Tests/AuthRateLimitingTests.Login_IsNotRateLimited` green; policy attributes present) —
  unit-level, see Not covered here.

## Preconditions

- Seed complete **plus the R1-05 fixture** (00-HARNESS §3): as `wsAdmin` on `e2e-beta` —
  1. `GET /api/admin/environments` → note the `default` environment's `id`
  (every tenant has it out of the box, `ProjectService.cs:87-90`); `POST /api/admin/environments
  {name:'e2e-preview'}` → note its `id`.
  2. `PUT /api/admin/projects/{betaId}/app-urls/{defaultEnvId} {url:'https://app.example.com'}` and
  `PUT /api/admin/projects/{betaId}/app-urls/{previewEnvId}
  {url:'https://myapp-*.vercel.app'}`. **Decision:** one row per environment —
  `SetAppUrlAsync` *upserts the single* row per `(projectId, environmentId)`
  (`ProjectService.cs:280-332`), so the harness's two fixture URLs must sit on two different
  environment rows. Row-environment never affects matching (R1-05 Design A — the comment's
  `environment` only feeds the localhost exemption), so scenarios may comment with any
  `environment`.
  3. `PATCH /api/admin/projects/{betaId} {enforceAllowedOrigins:true}`.
- `lib/api.mjs` supports per-call `headers` (00-HARNESS §4) — node fetch sends no `Origin`/
  `Referer` by default, which is exactly the no-origin case; explicit `Origin`/`Referer` are passed
  via `{ token, headers }`.
- FLOOD persona exists; `e2e/fixture-app/beta/` (embeds
  `<pointer-feedback project="e2e-beta" environment="production">`, `e2e/fixture-app/beta/index.html:17-21`).

## Scenarios

| id | intent | tier | layer | role | steps | expected | evidence |
|---|---|---|---|---|---|---|---|
| R1-05-01 | `origin-enforced-blocks-foreign-origin` | PR | api | tester | 1. `POST /api/auth/login` as tester. 2. `POST /api/projects/e2e-beta/comments {body:'origin matrix 1', environment:3, element:{selector:'.sidebar', route:'/'}}` with header `Origin: https://evil.example`. 3. Same body with `Origin: https://app.example.com` → note the returned `id`. 4. Referer fallback: no `Origin`, header `Referer: https://app.example.com/settings`. 5. Replies share the gate: `POST /api/comments/{id}/replies {body:'reply foreign'}` with `Origin: https://evil.example`, then with `Origin: https://app.example.com`. 6. WA: `GET /api/admin/events/summary?projectId=<betaId>` (audit signal, Design D). 7. **Decision:** assert HTTP status only, never message text — `MessageKeys` are English literals, not localisation keys (R1-05 Design A). | 2 → **403**, envelope `isSuccess === false`. 3 → **200** (exact-row match). 4 → **200**. 5 → **403** then **200** (the *parent comment's* environment governs the exemption check; row matching is project-wide). 6 → **conditional**: R1-05 Design D makes `comment_rejected` a `UsageEvent` *"if present; otherwise a structured `ILogger` warning"*, so assert `counts.comment_rejected >= 2` **only when the implementer chose the `UsageEvent` branch** (detect: the summary response has a `comment_rejected` key at all) — otherwise record the sub-step SKIP with reason `Design D log-line branch`. Note the exec docs disagree on ownership: `comment_rejected` is in neither R1-02's client whitelist nor its server-emitted set (`../execution/R1-02-cli-init.md` §I), so making it mandatory requires an R1-05 change to Design D **and** an R1-02 change to the emitted-type list. | report row; each status pair in `detail` |
| R1-05-02 | `origin-enforced-allows-localhost-local` | PR | api | tester | Comment body as R1-05-01 on `e2e-beta`, all with `environment:1` (Local — no row registered for it): 1. `Origin: http://localhost:5173`. 2. `Origin: http://127.0.0.1:4173`. 3. `Origin: http://myapp.localhost:3000` (`.localhost` suffix). 4. `Origin: http://[::1]:5173` (bracketed IPv6 host). 5. Non-local env gets no exemption: same comment with `environment:2` and `Origin: http://localhost:5173`. 6. Enforcement-off default: comment on `e2e-alpha` (`environment:3`) with `Origin: https://evil.example`. | 1–4 → **200** (localhost hosts exempt for Local regardless of rows). 5 → **403**. 6 → **200** (AC-1 — alpha keeps today's behaviour). | report row |
| R1-05-03 | wildcard app-url matrix | PR | api | wsAdmin (saves) + tester (posts) | Save-side, as WA, on a scratch environment created by the spec (`POST /api/admin/environments {name:'e2e-wildcard-lab'}` → `labEnvId`; `PUT /api/admin/projects/{betaId}/app-urls/{labEnvId} {url}`): 1. `https://*.vercel.app` → 400 (bare `*` on a shared-hosting suffix). 2. `https://*.acme.com` → 400 (bare `*`, remaining host < 3 labels). 3. `https://*.foo.github.io` → 400 (suffix list, ends-with semantics). 4. `https://myapp-*.vercel.app` → 200 (non-empty literal part). 5. `https://*.staging.acme.com` → 200 (≥ 3 labels, not shared) — left saved for the match-side. Match-side, as tester on `e2e-beta`, `environment:3`: 6. `Origin: https://myapp-pr-12.vercel.app` → 200. 7. `Origin: https://other.vercel.app` → 403. 8. `Origin: https://x.myapp-pr-12.vercel.app` → 403 (label count differs). 9. `Origin: http://myapp-pr-12.vercel.app` → 403 (scheme differs). 10. `Origin: https://myapp-pr-12.vercel.app:8443` → 403 (**Decision:** a pattern without an explicit port matches only the scheme's default port). 11. `*.staging.acme.com` row: `Origin: https://pr-7.staging.acme.com` → 200; `Origin: https://a.b.staging.acme.com` → 403. Cleanup 12: `DELETE /api/admin/projects/{betaId}/app-urls/{labEnvId}` then `DELETE /api/admin/environments/{labEnvId}` — beta's fixture state returns to exactly the two seed rows. | codes as listed (AC-3 complete). | report row; save-side 400 bodies in `detail` |
| R1-05-04 | no-origin: staff vs quick-access | PR | api | developer + client | 1. WA: `PATCH /api/admin/projects/{alphaId} {enforceAllowedOrigins:true}` — temporary, restored in `finally`. 2. DEV token: `POST /api/projects/e2e-alpha/comments {body:'no-origin staff', environment:3, element:{…}}` — node fetch sends **no** `Origin`/`Referer`. **Note:** AC-2 words this as "staff *key* via curl"; the gate reads the `is_quick_access` JWT claim, and a `login-with-key` token carries the same claim as a password token, so a password-login JWT is equivalent here. To match the AC literally, obtain the token via `POST /api/auth/login-with-key {apiKey: keys.json.developer.apiKey}` instead — same assertion either way. 3. CL token (`client`, invited to `e2e-alpha`): same POST without `Origin`. 4. `finally`: WA `PATCH … {enforceAllowedOrigins:false}` + re-`GET /api/admin/projects` asserting `enforceAllowedOrigins === false`. **Decision:** toggle alpha in-scenario rather than seeding a second quick-access client on `e2e-beta` — personas stay exactly as 00-HARNESS §3 defines them. | 2 → **200** (bypass by design for non-quick-access callers). 3 → **403** (quick-access token without origin). 4 → restored (a leftover would 403 every later widget comment on alpha loudly, never silently). | report row |
| R1-05-05 ⛓ | `comment-burst-429` | nightly | api | flood | Runs in the **isolated last phase** (`run-e2e.sh --429`), after mail/fresh-app/white-label, with the FLOOD persona as sole author (H-07 guarantees zero flood comments before this point): 1. `POST /api/auth/login {email:'flood@example.com', …}`. 2. i = 1…31 sequential `POST /api/projects/e2e-alpha/comments {body:'burst <i>', environment:1, element:{selector:'#decoy', route:'/'}}`. 3. Flood: `POST /api/comments/{anySeedCommentId}/replies {body:'r'}`. 4. Second user unaffected: tester posts one comment on `e2e-alpha`. 5. **Decision:** main stack, not `docker compose -p e2e-429` — the `comments` partition is per-user (`sub`), and nothing after this phase comments; the isolated project stays reserved for IP-partitioned burns (R1-04-05 note, R2-05-06). | 2: i ≤ 30 → 200; i = 31 → **429** with header `retry-after` matching `/^\d+$/` ≥ 1 (sliding window 30/60 s per user). 3 → **429** (replies share the `comments` policy). 4 → **200**. | report row; boundary index + `Retry-After` value in `detail` |
| R1-05-06 | origin-403 widget toast | nightly | widget | tester | 1. `node e2e/fixture-app/serve.mjs beta 4182` — **port 4182, never 4173** (4181 is the `alpha` fixture, R2-04/R2-06): `run-e2e.sh:23-28` holds 4173 with `serve.mjs smoke` for the whole Playwright phase and it is `playwright.config.ts:11`'s default `baseURL`. Pass the URL explicitly (`page.goto('http://localhost:4182/')`), do not rely on `baseURL`. The fixture posts cross-origin to `:8090`, so the browser sends `Origin: http://localhost:4182`; its widget is pinned to `environment="production"`. 2. `preAuthWidget(page, tester.token, tester.user)` (`widget.spec.ts:41-50`); `const cc = page.waitForResponse(r => r.url().includes('/capture-config'))`; `page.goto('http://localhost:4182/')`; `expect(widget.locator('#pf-add')).toBeVisible()`; `await cc` (harness §9). 3. `#pf-add` click → `page.locator('.sidebar').click({ force: true })` → fill `#pf-comment-text` `origin toast probe` → `#pf-submit`. 4. Positive control: `#pf-env` `selectOption('local')` (the select renders — the fixture sets only `environment`, not `fixed-environment`), fill + submit again. 5. Staff API check: `GET /api/projects/e2e-beta/comments?pageSize=5` — newest comment. | 3 → real **403** on the POST (production env, non-localhost origin, no matching row): `expect.poll` → shadow-root `.pf-toast.error` with exact text `Comments are not allowed from this address` (R1-05 Design B); popover stays open (`#pf-comment-text` still visible). 4 → toast `Comment added`; 5 → newest comment `environment === 1`. | report row; toast screenshot + the 403 network entry on failure |
| R1-05-07 | dashboard-origin exemption | PR | api | wsAdmin | On `e2e-beta` (enforce on; rows exclude both dashboard origins), replies to the R1-05-01 comment (parent env Production): 1. `POST /api/comments/{id}/replies {body:'from dashboard app'}` with `Origin: https://app.pointer.moamen.work` (branding `BrandUrlApp` default). 2. Same with `Origin: https://app-angular.pointer.moamen.work` (`Security:TrustedDashboardOrigins` default). 3. Look-alike negative: `Origin: https://app-angular.pointer.moamen.work.evil.example`. | 1 → **200** (AC-4: reply from `Urls.App` succeeds with no matching row). 2 → **200** (AC-8). 3 → **403** (normalised exact compare — no suffix tricks). | report row |

## Spec files

- `e2e/api/origins.spec.mjs` — R1-05-01/02/03/04/07 (needs the `headers` option in `lib/api.mjs`).
- `e2e/api/rate-limits.spec.mjs` — R1-05-05 (+ R1-04-05), guarded by the `--429` flag.
- `e2e/widget/origins.spec.ts` — R1-05-06 (reuses `preAuthWidget` + the `capture-config` wait from
  `widget/widget.spec.ts`).
- Seed additions per Preconditions (in `scripts/seed.mjs`, after the existing beta PATCH).

## Not covered here

- `OriginNormalizer` unit matrix (suffix equals/ends-with, ports, schemes, `Uri.Host` extraction,
  IPv6 `::1` unbracketed) — `Tests/OriginNormalizerTests.cs`; `IsOriginAllowedAsync` branch matrix —
  `Tests/ProjectOriginEnforcementTests.cs`; pattern validation —
  `Tests/SetProjectAppUrlValidatorTests.cs`.
- Reflection assertions on the rate-limit policies (30/60 s sliding window partitioned by `sub`;
  attributes on `CommentsController.Create`/`RepliesController.Add`/`login-with-key`; absent on
  `Login`) — `Tests/CommentRateLimitingTests.cs` + `Tests/AuthRateLimitingTests.cs` (no
  `WebApplicationFactory` exists; live 429s are exactly the two scenarios above).
- A live `login-with-key` 429 burn — deliberately not E2E: it would 429 the suite's own IP for a
  minute (the isolated-phase analogue is R2-05-06).
- `RateLimits:CommentsPerMinute` self-hoster knob, `ExportImport` path unaffected — unit/review.
- Dashboard toggle UI ("Only accept comments from the configured app URLs") — dashboard repo;
  the API surface it writes is `ProjectResponse/UpdateProjectRequest.enforceAllowedOrigins`, proven
  by every toggle step above.

## Flake notes

- 403 plumbing depends on the `IsForbidden → StatusCode(403)` branch being added to
  `CommentsController.Create` and `RepliesController.Add` (R1-05 task 4b) — before it lands, these
  surface as 400; the spec's failure detail should print the actual status to make that visible.
- R1-05-04 mutates `e2e-alpha` mid-phase; the `finally` restore is mandatory. If a restore ever
  fails, subsequent widget specs fail loudly on every comment (never a silent green) — acceptable,
  and the report row for step 4 records the restore assert.
- R1-05-05 must be the last API-touching phase of the run; H-07 (flood has zero comments) runs in
  the api phase *before* it. Comment POSTs are sequential (the limiter is per-user; parallelism
  would only blur the boundary index).
- R1-05-06's toast is fire-and-forget DOM **that removes itself after 2200 ms**
  (`web-component/src/element.ts:1523-1529`, `setTimeout(() => t.remove(), 2200)`). Use
  `expect.poll(..., { intervalMs: 250, timeout: 10_000 })` — **the interval must be ≤ 250 ms** or the
  assertion can step straight over the toast's whole lifetime and fail on a working build. Never a fixed
  sleep; the positive control (step 4) is what distinguishes the 403 toast from a generic failure toast.
- R1-05-06 runs on **port 4182** (4173 is held by `serve.mjs smoke` for the entire Playwright phase).
- The beta fixture must keep `environment="production"` (a `local` default would trip the localhost
  exemption and turn step 3 into a 200).

## State coupling
```
R1-05-05 <- reset           # burns the `comments` bucket for the flood user; must be the run's last API-touching scenario
```
