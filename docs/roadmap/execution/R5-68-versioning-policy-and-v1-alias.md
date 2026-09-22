# R5-68 — Versioning policy + `/api/v1/*` alias (§68 · Release 5 · 1 d)

**Status (2026-09-23):** shipped `435f688`; live, parity verified in production;
`docs/VERSIONING.md` exists. Implementation finding: an explicit `app.UseRouting()` after the
rewrite middleware was required (not called out in the design in §3.1).

## 1. Goal

Publish `docs/VERSIONING.md` (a single versioning policy page covering served files, CLI npm
semver, client packages, API paths, and skill files), and add a `/api/v1/*` URL rewrite so that
`/api/v1/auth/login` routes to the same controller action as `/api/auth/login`. This lets the
API declare `/api/v1` as the canonical documented prefix for external consumers without changing
any existing client, controller, or route.

Effort: 1 d.

## 2. Prerequisites (verified facts)

- **Current API paths**: all routes are `/api/*` (unversioned). `00-API-INVENTORY.md:10-75` lists
  every endpoint; none uses `/api/v1/`.
- **On-disk contract**: `docs/ON-DISK-CONTRACT.md:21` lists served URLs. `/api/v1/*` is not listed.
  Adding the alias requires a note in the contract doc.
- **OnDiskContractTests**: `Tests/OnDiskContractTests.cs:9-143` — the `FrozenNames` array (`:145-156`
  is a separate `StorageKeys` array immediately after it — do not confuse the two when editing);
  served URLs sit at `:59-70` (`"/widget.js"` … `"/vendor/snapdom.js"`). `FrozenNames_AreDocumented`
  (`:165-176`) asserts every entry in the array is a substring of `docs/ON-DISK-CONTRACT.md` — it
  does not distinguish a statically-served file from a rewritten path, it just greps for the
  string. Per this repo's own convention (every existing served-URL entry is added to **both**
  places in the same PR — see `CLAUDE.md`/`.claude/CLAUDE.md` "new served URL or storage key" rule
  and `docs/ON-DISK-CONTRACT.md`'s own header), `/api/v1/*` — a new customer-facing URL prefix,
  which is this doc's entire point — belongs in `FrozenNames` too, not only in the contract doc.
  The separate `ServedFiles_UseOnlyFrozenNames` test (`:180+`) only scans `data-*` attribute
  literals in `wwwroot`/`Program.cs`/widget source, so it is unaffected either way.
- **Controllers**: routing is a mix of class-level `[Route(...)]` and per-method full-path
  `[HttpGet("api/...")]` attributes — both patterns exist, and the rewrite (a path-string rewrite
  before routing runs) works identically against either:
  - `AuthController.cs:12` `[Route("api/auth")]` (class-level; per-method routes like
    `[HttpGet("signup-enabled")]` at `:102` combine with it → `/api/auth/signup-enabled`).
  - `CommentsController.cs` has **no** class-level `[Route]` — every action carries its own full
    path, e.g. `[HttpGet("api/projects/{key}/comments")]` (`:29`), `[HttpGet("api/comments/{id:int}")]` (`:50`).
  - `ExportImportController.cs` likewise has no class-level `[Route]` — `[HttpGet("api/projects/{key}/export")]` (`:38`), `[HttpGet("api/export")]` (`:53`).
- **Middleware pipeline**: `Program.cs:189-345` — `UseForwardedHeaders` → exception handler →
  Swagger → skill injection → static files → CORS → auth → rate limiter. Controllers at `:418`.
- **Swagger**: `Program.cs:121-155` — `AddSwaggerGen` (service registration) +
  `AddFluentValidationRulesToSwagger`; the middleware calls `UseSwagger()`/`UseSwaggerUI()` are
  separate, at `:218-219` (after the exception handler, not in the 121-155 range). Swagger
  should continue to show `/api/*` routes (not duplicated as `/api/v1/*`).
- **URL rewriting**: ASP.NET Core has `Microsoft.AspNetCore.Rewrite` (built-in) with
  `AddRewriteRule` or a simple inline middleware.
- **CLI**: `pointer-feedback` npm package. Uses semver. CLI calls `/api/auth/login-with-key`,
  `/api/projects/{key}/comments`, etc.
- **Client packages**: `@moamen-ui/pointer-react` (generated via orval from Swagger spec).
  Calls `/api/*` paths.

## 3. Design

### 3.1 URL rewrite middleware

A simple inline middleware in `Program.cs`, placed **early** in the pipeline (before
`UseSwagger`, before any path-based routing), that rewrites `/api/v1/...` to `/api/...`:

```csharp
// R5-68: /api/v1/* → /api/* rewrite. Lets external docs declare /api/v1 as canonical while all
// controllers stay at /api. Swagger is unaffected (it reads from the controller routes, not
// from the request path).
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path;
    if (path.StartsWithSegments("/api/v1", out var rest))
    {
        ctx.Request.Path = "/api" + rest;
    }
    await next();
});
```

**Placement**: after `app.UseForwardedHeaders(fwd);` (`:189`) and after the request-id
middleware (R5-58), before the exception handler (`:196`). This ensures the rewrite happens
before any path-based logic.

**Why not `MapFallback` or `UseRewriter`**: a simple middleware is the most transparent and
testable approach. `UseRewriter` with regex works but is less readable. `MapFallback` doesn’t
apply here (it’s for unmatched routes, not path rewriting).

### 3.2 `docs/VERSIONING.md`

Structure:

1. **API (HTTP)**: all endpoints live at `/api/*`. `/api/v1/*` is a supported alias that maps
   to the same endpoints. No `/api/v2` exists or is planned. Breaking changes (field removal,
   type change, behavioural change) are avoided; if unavoidable, they are documented in the
   release notes and the affected endpoint is deprecated with a sunset header for at least
   one release cycle. Additive changes (new fields, new endpoints) are not breaking.

2. **CLI (`pointer-feedback` npm package)**: follows npm semver. A `Cli__MinVersion` config on
   the server (`docker-compose.prod.yml:54`) can enforce a minimum CLI version; the CLI checks
   this on startup and prints a warning. The deprecation window for a CLI major version is 90 days
   after the next major is published. The old version continues to work against the API (the API
   is backward-compatible) but may miss new features.

3. **Client packages (`@moamen-ui/pointer-react`)**: generated from the live Swagger spec via
   orval. Version follows the API’s own semver tag. Breaking changes are gated by the API’s own
   backward-compatibility promise.

4. **Served files**: `/widget.js`, `/widget.css`, `/embed.js`, `/skill.md`, etc. are unversioned
   (always the latest). The widget supports a `?v=<hash>` pin for reproducible builds (R3-03).
   `/install.sh` and skill files are always the latest.

5. **Skill files**: `SKILL.md` and sub-skills carry a `<POINTER_SKILL_VERSION>` stamp injected
   at serve time (`Program.cs:280-285`). The CLI’s `doctor` command compares installed vs served
   versions.

### 3.3 Contract note

Add a new row to `docs/ON-DISK-CONTRACT.md`'s table (next to the existing "Served URLs" row):

| Surface | Name(s) | Written by | Post-rebrand promise |
|---|---|---|---|
| API alias | `/api/v1/*` (rewrites to `/api/*`) | API middleware | keep; future `/api/v2` if ever needed |

And add `"/api/v1/*"` (or `"/api/v1/"`, matching whatever literal substring the doc row actually
uses) to the `FrozenNames` array in `Tests/OnDiskContractTests.cs` (near the "Served URLs" group,
`:59-70`) — the guard test only checks `doc.Contains(n)`, so the two strings must match exactly.
Skipping this leaves the new URL undocumented-by-the-guard, contrary to this repo's "every new
served URL or storage key" rule.

Also update `00-API-INVENTORY.md` with a note at the top: "`/api/v1/*` is a supported alias for
`/api/*` (R5-68). All examples in this document use the unversioned `/api/*` prefix."

### 3.4 Tests

**Preferred: the e2e approach.** `Tests/*.cs` (102 files) is exclusively xunit unit-style tests
today — none of them spins up a live server via `WebApplicationFactory` (verified: zero matches
for `WebApplicationFactory` under `Tests/`), so a `Tests/ApiV1AliasTests.cs` that needs a running
app would be a new test pattern for this repo with no precedent to follow, while `e2e/api/*.spec.mjs`
already runs Playwright specs against the real running compose stack (`e2e/scripts/lib/api.mjs`'s
`raw`), which is exactly what "hit both paths and diff the bodies" needs. `e2e/api/served-contract.spec.mjs`
exists but covers a different concern (the R1-01 static-file freeze for `/install.sh`/`/skill.md`,
diffing served content against the on-disk file) — do not fold this into it; add a new
`e2e/api/api-v1-alias.spec.mjs`:
```javascript
import { test, expect } from '@playwright/test';
import { raw } from '../scripts/lib/api.mjs';

for (const path of ['/api/v1/branding', '/api/v1/plans', '/api/v1/auth/signup-enabled']) {
  test(`/api/v1 alias: ${path}`, async () => {
    const v1 = await raw('GET', path);
    const plain = await raw('GET', path.replace('/api/v1/', '/api/'));
    expect(v1.status).toBe(plain.status);
    expect(JSON.stringify(v1.body)).toBe(JSON.stringify(plain.body));
  });
}

test('/api/v1 rewrite does not leak into Swagger', async () => {
  const res = await raw('GET', '/swagger/v1/swagger.json');
  expect(JSON.stringify(res.body)).not.toContain('/api/v1/');
});
```

## 4. Safety / impact

**Additive** — one middleware that rewrites `/api/v1/*` → `/api/*`. No existing route or
behaviour is changed. Swagger is unaffected. No migration, no data change.

**Risk**: near zero. The rewrite only fires on paths starting with `/api/v1/`. All other paths
pass through unchanged.

## 5. File-level tasks

1. **`API/Program.cs`** — add the rewrite middleware after `UseForwardedHeaders` (`:189`).
2. **`docs/VERSIONING.md`** (new) — per §3.2.
3. **`docs/ON-DISK-CONTRACT.md`** — add the `/api/v1/*` alias row (§3.3).
4. **`Tests/OnDiskContractTests.cs`** — add `/api/v1/*` to `FrozenNames` (§3.3).
5. **`docs/roadmap/execution/00-API-INVENTORY.md`** — add the alias note at the top (§3.3).
6. **`e2e/api/api-v1-alias.spec.mjs`** (new) — §3.4.

## 6. Tests

Per §3.4: 3 sample routes tested for identical responses via both paths, plus a Swagger
non-duplication check.

## 7. Acceptance criteria

1. `curl -s https://api.pointer.moamen.work/api/v1/branding | jq .data.productName` → same as
   `curl -s https://api.pointer.moamen.work/api/branding | jq .data.productName`.
2. `curl -s https://api.pointer.moamen.work/api/v1/plans | jq '.data | length'` → same as
   the `/api/plans` response.
3. `curl -s https://api.pointer.moamen.work/api/v1/auth/signup-enabled | jq .data.enabled` →
   same as `/api/auth/signup-enabled`.
4. `curl -s https://api.pointer.moamen.work/swagger/v1/swagger.json | grep -c '/api/v1/'` → 0
   (Swagger shows only `/api/*` paths).
5. `npx playwright test e2e/api/api-v1-alias.spec.mjs` → all green.
6. `docs/VERSIONING.md` exists and covers API, CLI, client packages, served files, skill files.
7. `grep -c '/api/v1/' docs/ON-DISK-CONTRACT.md` → ≥1.
8. `grep -c '/api/v1/' docs/roadmap/execution/00-API-INVENTORY.md` → ≥1.
9. `dotnet test --filter FrozenNames_AreDocumented` → green (the new `FrozenNames` entry is a
   substring of `docs/ON-DISK-CONTRACT.md`).

## 8. Rollback

`git revert` the commit; redeploy. The `/api/v1/*` paths stop working (404). No data, no
migration. Any client already using `/api/v1/` must switch back to `/api/`.

## 9. Release steps

1. Merge PR. `unit` CI (`dotnet test`) covers `FrozenNames_AreDocumented`; the `e2e` job's `api`
   phase (`.github/workflows/e2e.yml`) picks up `api-v1-alias.spec.mjs` automatically.
2. Deploy: `bash scripts/deploy-api.sh`.
3. Verify criteria 1–4.
4. Update external documentation (README, landing docs) to mention `/api/v1/` as the canonical
   prefix for new integrations.

## 10. Out of scope

`/api/v2` (no current need), automatic API versioning frameworks (`Asp.Versioning.*`), Swagger
showing both `/api/` and `/api/v1/` paths, versioned Swagger specs, client package path changes
(clients continue to use `/api/`), header-based versioning (`Api-Version` header), query-string
versioning.

## 11. Dashboard / widget / CLI tasks

None. `docs/VERSIONING.md` documents the CLI's npm-semver policy and the `@moamen-ui/pointer-react`
client-package versioning already in effect — it records existing behavior, it does not change the
CLI, the widget, or the generated client. `Cli__MinVersion` (`docker-compose.prod.yml`) and the
orval-generated client both keep calling unversioned `/api/*` paths; neither needs code changes
for this doc.
