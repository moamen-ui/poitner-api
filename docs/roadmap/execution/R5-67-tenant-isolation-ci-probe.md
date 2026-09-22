# R5-67 — Tenant-isolation CI probe (§67 · Release 5 · 1 d)

## 1. Goal

Add an e2e job that proves no tenant can see or mutate another tenant’s data. The probe seeds two
workspaces, obtains tokens for both, and for every GET/list route asserts that tenant B’s token
sees none of tenant A’s IDs, plus negative writes (PATCH/DELETE on A’s IDs with B’s token →
403 or 404). This runs in CI on every push to `main` and on PRs touching API/Domain/Infrastructure.

Effort: 1 d.

## 2. Prerequisites (verified facts)

- **Tenant query filters**: `Infrastructure/AppDbContext.cs:73-292` — `OnModelCreating`; the
  individual `HasQueryFilter(...)` calls (strict-own and own-plus-global) sit at lines 86, 92, 100,
  106, 114, 119, 127, 137, 145, 159, 171, 181, 190, 203, 212, 223, 236, 242, 248, 258, 264, 270, 289
  (one per tenanted entity — `Project`, `User`, `ApiKey`, `Comment`, `ProjectBuild`, `Reply`, …,
  `Workspace`). See `docs/db/execution/DB-13-operator-impersonation.md` §2 for the authoritative
  per-entity line list (that doc's F2 impersonation work reads the same filters).
- **Existing e2e**: `e2e/run-e2e.sh:1-269` — phases in order: `reset`, `seed`, `probe`, `api`,
  `docs`, `cli`, `widget`, `mail`, … (`run_phase "api" "bash scripts/pw.sh api"` at line 179).
  `e2e/scripts/seed.mjs` creates: super admin, tenant (workspace) owner `e2e-owner@example.com`,
  projects `e2e-alpha` + `e2e-beta`, comments C1–C8.
- **A second tenant already exists in the seeder** — do not reinvent it. `e2e/scripts/lib/constants.mjs`
  exports `TENANT_B_OWNER` (`email: 'e2e-b-owner@example.com'`, `password: 'E2eBOwnerPass1!'`) and a
  `PROJECTS.gamma` fixture (`key: 'e2e-gamma'`), explicitly commented "Tenant B's project — the target
  of cross-tenant reads that must fail." `seed.mjs` (~lines 113–121) creates tenant B via
  `POST /api/admin/tenants` (super-admin token) and its `e2e-gamma` project via
  `POST /api/admin/projects` with the tenant-B owner's own token, then stores the owner's
  email/password under `credentials.json.tenantBOwner` (`seed.mjs` ~line 359) — no token is
  persisted, so a consumer logs in fresh. Three existing specs already reuse this exact fixture for
  cross-tenant assertions: `e2e/api/builds.spec.mjs`, `e2e/api/project-env-urls.spec.mjs`,
  `e2e/api/events.spec.mjs` — all three follow the pattern
  `credentials().tenantBOwner?.email || TENANT_B_OWNER.email` (falling back to the constant if the
  credentials file predates this field). The new isolation spec should follow the same pattern rather
  than registering a fresh workspace.
- **State readers**: `e2e/scripts/lib/state.mjs` exports lazy `credentials()` / `keys()` readers
  over `e2e/state/credentials.json` / `keys.json` — module-scope `readFileSync` in a spec file
  breaks Playwright's test-discovery pass on an unseeded workspace (`state.mjs:1-13` explains why);
  every existing spec reads through `state.mjs`, not by hand.
- **API test pattern**: Playwright `test()`; `raw(method, path, opts)` / `get`/`post`/`patch`/`put`/`delRaw`
  helpers from `e2e/scripts/lib/api.mjs:17-73`. Assertions on HTTP status codes and envelope payload.
- **CI workflow**: `.github/workflows/e2e.yml:1-228` — the `e2e` job runs `bash run-e2e.sh --pr --ci`
  (line 54) or `--nightly --ci` (line 59).
- **Probe script**: `e2e/scripts/probe-visibility.mjs` — existing role-visibility / cross-project
  (not cross-tenant) isolation probe, run as its own `probe` phase. The new spec is a Playwright spec
  in `e2e/api/`, not an addition to this script.

### GET/list routes by controller (verified against the controllers directly, line numbers as of this doc)

**Auth** (no list routes to test cross-tenant — `me` returns the caller's own data):
- `GET /api/auth/me` (`API/Controllers/AuthController.cs:68-76`) — returns own user.

**Projects** (`API/Controllers/Admin/ProjectsController.cs`, `[Route("api/admin/projects")]`):
- `GET /api/admin/projects` (`:20-22`) — list projects.
- `GET /api/admin/projects/{id}/app-urls` (`:64-66`) — project app URLs.
- `GET /api/admin/projects/{key}/apply-queue` (`:102-105`, `[Authorize(Policy="Admin")]`) — apply queue.

**Comments** (`API/Controllers/CommentsController.cs`):
- `GET /api/projects/{key}/comments` (`:29-31`) — list comments.
- `GET /api/comments/{id}` (`:50-52`) — single comment.

**Workspace** (`API/Controllers/Admin/WorkspaceController.cs`, `[Route("api/admin/workspace")]`):
- `GET /api/admin/workspace` (`:23-25`) — own workspace.
- `GET /api/admin/workspace/comment-fields` (`:45-47`) — comment field definitions.

**Other**:
- `GET /api/projects/{key}/stack` (`API/Controllers/ProjectStackController.cs:22-24`) — project stack.
- `GET /api/projects/{key}/capture-config` (`API/Controllers/CaptureConfigController.cs:21-23`) — capture config.
- `GET /api/branding` (`API/Controllers/BrandingController.cs:34-36`) — global (not tenanted).
- `GET /api/plans` (`API/Controllers/PlansPublicController.cs:22-24`) — global.
- `GET /api/public/stacks-summary` (`API/Controllers/StacksPublicController.cs:19-21`) — global.

Also:
- `GET /api/me/api-key` (`API/Controllers/MeController.cs:49-51`) — own key.
- `GET /api/me/notifications` and `GET /api/me/notifications/unread-count` (`MeController.cs:69-71`, `:77-79`) — these exist; own notifications only.
- `GET /api/invites/{code}` (`API/Controllers/InvitesController.cs:22`, anonymous invite-preview) and `GET /api/admin/invites` (`API/Controllers/Admin/InvitesController.cs:21`, tenant-scoped list).

**Write routes for negative tests** (`CommentsController.cs` / `Admin/ProjectsController.cs`):
- `PATCH /api/comments/{id}` (`:60-62`) — update comment status.
- `PUT /api/comments/{id}` (`:83-85`) — edit comment.
- `DELETE /api/comments/{id}` (`:118-120`) — delete comment.
- `PATCH /api/admin/projects/{id}` (`:42-44`) — update project.
- `DELETE /api/admin/projects/{id}` (`:53-55`) — delete project.

**Dependencies**: none. This probe only needs the tenancy model that exists today
(`User.OwnerId` / `currentUser.TenantId`, no membership table). It is independent of
`docs/db/execution/DB-11a-identity-and-workspace-memberships.md` (not yet merged) and of DB-12/DB-13
(both written, not implemented) — when DB-11a's membership model lands, this spec's assertions
still hold (workspace scoping still keys off the same tenant/workspace boundary), but the
implementer should re-run it after DB-11a merges to confirm nothing regressed.

## 3. Design

### 3.1 Seeding

The probe reuses the existing e2e seeder (`e2e/scripts/seed.mjs`), which already creates **both**
tenants — no new tenant needs to be registered:
- Workspace A (`e2e-owner@example.com`, `credentials().wsAdmin`) with projects `e2e-alpha`,
  `e2e-beta`, comments C1–C8.
- Workspace B (`TENANT_B_OWNER` = `e2e-b-owner@example.com`, `credentials().tenantBOwner`) with
  project `e2e-gamma`, created for exactly this purpose ("Tenant B's project — the target of
  cross-tenant reads that must fail" — `e2e/scripts/lib/constants.mjs`).

The spec logs in as both personas (`login(credentials().wsAdmin.email, ...)` and
`login(credentials().tenantBOwner?.email || TENANT_B_OWNER.email, ...)`, the same fallback used by
`builds.spec.mjs`/`project-env-urls.spec.mjs`/`events.spec.mjs`) to get `tokenA`/`tokenB`, then
creates 2 comments in `e2e-gamma` with `tokenB` if the fixture doesn't already have enough for the
negative-write assertions (check first — idempotent re-runs must not pile up comments).

### 3.2 Spec file: `e2e/api/tenant-isolation.spec.mjs`

Structure:

```javascript
import { test, expect } from '@playwright/test';
import { raw, login, post } from '../scripts/lib/api.mjs';
import { credentials as loadCredentials } from '../scripts/lib/state.mjs';
import { TENANT_B_OWNER } from '../scripts/lib/constants.mjs';

// Setup: log in as both already-seeded tenants, create 2 comments in B's project, store IDs.
// Lazy credentials() read (via state.mjs, not module-scope readFileSync) so `playwright test
// --list` still works before the workspace is seeded — see state.mjs's own comment.
let tokenA, tokenB;
let projectA_id, projectB_id;
let commentA_ids, commentB_ids;

test.beforeAll(async () => {
  const credentials = loadCredentials();
  const a = await login(credentials.wsAdmin.email, credentials.wsAdmin.password);
  const b = await login(
    credentials.tenantBOwner?.email || TENANT_B_OWNER.email,
    credentials.tenantBOwner?.password || TENANT_B_OWNER.password,
  );
  tokenA = a.token;
  tokenB = b.token;
  // projectA_id / commentA_ids: read from e2e/state/expected.json (written by seed.mjs) rather
  // than re-deriving — c.id fields are already there for e2e-alpha.
  // projectB_id: GET /api/admin/projects with tokenB, find key === 'e2e-gamma'.
  // commentB_ids: create 2 comments on e2e-gamma with tokenB (skip if 2 already exist, for
  // idempotent re-runs).
});

// === READ ISOLATION ===

test('GET /api/admin/projects with tokenB sees no A projects', async () => {
  const res = await raw('GET', '/api/admin/projects', { token: tokenB });
  expect(res.status).toBe(200);
  const projects = res.body.data;
  for (const p of projects) {
    expect(p.id).not.toBe(projectA_id);
    expect(p.key).not.toMatch(/^e2e-alpha|e2e-beta$/);
  }
});

test('GET /api/projects/e2e-alpha/comments with tokenB → 404 or empty', async () => {
  const res = await raw('GET', '/api/projects/e2e-alpha/comments', { token: tokenB });
  // Either 404 (project not found for this tenant) or 200 with empty list
  expect([200, 404]).toContain(res.status);
  if (res.status === 200) {
    expect(res.body.data.items ?? res.body.data).toHaveLength(0);
  }
});

test('GET /api/comments/<A_comment_id> with tokenB → 404', async () => {
  for (const id of commentA_ids) {
    const res = await raw('GET', `/api/comments/${id}`, { token: tokenB });
    expect(res.status).toBe(404);
  }
});

// … repeat for every GET/list route from the inventory

// === WRITE ISOLATION ===

test('PATCH /api/comments/<A_id> with tokenB → 403 or 404', async () => {
  for (const id of commentA_ids) {
    const res = await raw('PATCH', `/api/comments/${id}`, {
      token: tokenB,
      body: { status: 4 },
    });
    expect([403, 404]).toContain(res.status);
  }
});

test('DELETE /api/comments/<A_id> with tokenB → 403 or 404', async () => {
  for (const id of commentA_ids) {
    const res = await raw('DELETE', `/api/comments/${id}`, { token: tokenB });
    expect([403, 404]).toContain(res.status);
  }
});

test('PATCH /api/admin/projects/<A_id> with tokenB → 403 or 404', async () => {
  const res = await raw('PATCH', `/api/admin/projects/${projectA_id}`, {
    token: tokenB,
    body: { name: 'hacked' },
  });
  expect([403, 404]).toContain(res.status);
});

test('DELETE /api/admin/projects/<A_id> with tokenB → 403 or 404', async () => {
  const res = await raw('DELETE', `/api/admin/projects/${projectA_id}`, { token: tokenB });
  expect([403, 404]).toContain(res.status);
});
```

### 3.3 CI wiring

In `.github/workflows/e2e.yml`, the `e2e` job already runs `bash run-e2e.sh --pr --ci` which
includes the `api` phase (`run_phase "api" "bash scripts/pw.sh api"`). The `api` phase runs all
specs in `e2e/api/`, so `tenant-isolation.spec.mjs` will be picked up automatically.

No new workflow job needed — the spec runs as part of the existing `api` phase.

### 3.4 Integration with `run-e2e.sh`

No seeder change needed: `seed.mjs` already creates workspace B (`e2e-b-owner@example.com` /
`e2e-gamma`) as part of the normal `seed` phase (`run_phase "seed" "node scripts/seed.mjs"`,
`run-e2e.sh:177`), before the `api` phase runs. The spec's `beforeAll` only needs to (a) log in as
both personas via `state.mjs`/`constants.mjs` as shown in §3.2, and (b) create the 2 comments it
needs in `e2e-gamma` with `tokenB`, guarded so a second run doesn't keep appending (e.g. list
existing comments on `e2e-gamma` first and only create what's missing). It tears down nothing —
soft-delete means the data stays, and comment creation is the only idempotency concern.

## 4. Safety / impact

**Test-only** — one new spec file in `e2e/api/`. No API, migration, or config change. The spec
creates 2 comments in the already-seeded second tenant's project (`e2e-gamma`) in the test
environment; it registers no new tenant.

## 5. File-level tasks

1. **`e2e/api/tenant-isolation.spec.mjs`** (new) — per §3.2.
2. No change to `.github/workflows/e2e.yml` (auto-picked-up by the `api` phase).
3. No change to `e2e/run-e2e.sh`.

## 6. Tests

The spec itself IS the test. Expected count: at least 10 `test()` blocks:
- 5+ read-isolation tests (one per GET/list route group)
- 4+ write-isolation tests (PATCH/DELETE on comments and projects)
- 1 workspace metadata test

## 7. Acceptance criteria

1. `npx playwright test e2e/api/tenant-isolation.spec.mjs` → all green.
2. The spec covers at least: `/api/admin/projects`, `/api/projects/{key}/comments`,
   `/api/comments/{id}`, `PATCH /api/comments/{id}`, `DELETE /api/comments/{id}`,
   `PATCH /api/admin/projects/{id}`, `DELETE /api/admin/projects/{id}`.
3. The spec runs as part of `bash run-e2e.sh --pr --ci` (no new CI job needed).
4. Workspace B (`e2e-b-owner@example.com` / `e2e-gamma`) comes from the existing `seed.mjs` fixture;
   the spec only creates the 2 comments it needs in `e2e-gamma` and does not register a new tenant.
5. `grep -c 'tokenB' e2e/api/tenant-isolation.spec.mjs` → ≥5.
6. No spec asserts 200 when receiving cross-tenant data.

## 8. Rollback

Delete `e2e/api/tenant-isolation.spec.mjs`. No other change.

## 9. Release steps

1. Merge PR. CI runs the spec as part of the `e2e` job.
2. Verify green in the GitHub Actions run.

## 10. Out of scope

Data-level tenant isolation tests at the DB layer (EF query filter unit tests exist in
`Tests/TenantQueryFilterTests.cs`), performance/load testing of tenant isolation, row-level
security in Postgres (the app uses EF query filters, not RLS), super-admin cross-tenant access
tests (super-admin intentionally sees all).
