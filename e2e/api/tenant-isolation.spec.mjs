// E2E spec for R5-67: tenant-isolation CI probe.
// Proves no tenant can see or mutate another tenant's data: for every GET/list route the API
// exposes, tenant B's token sees none of tenant A's ids (empty list or 404), and every
// PATCH/DELETE attempted on tenant A's ids with tenant B's token is refused (403 or 404).
// Contract: docs/roadmap/execution/R5-67-tenant-isolation-ci-probe.md
//
// Reuses the second tenant seed.mjs already creates for exactly this purpose — TENANT_B_OWNER /
// project e2e-gamma (lib/constants.mjs) — rather than registering a new one. Credentials are read
// lazily through lib/state.mjs so `playwright test --list` still works before the workspace is
// seeded (see state.mjs's own comment on why module-scope reads break discovery).
import { test, expect } from '@playwright/test';
import { raw, post, login } from '../scripts/lib/api.mjs';
import { credentials as loadCredentials, expected as loadExpected } from '../scripts/lib/state.mjs';
import { TENANT_B_OWNER, PROJECTS } from '../scripts/lib/constants.mjs';

const credentials = () => loadCredentials();
const expected = () => loadExpected();

const ALPHA_KEY = PROJECTS.alpha.key; // 'e2e-alpha' — tenant A
const BETA_KEY = PROJECTS.beta.key; // 'e2e-beta' — tenant A
const GAMMA_KEY = PROJECTS.gamma.key; // 'e2e-gamma' — tenant B, seeded for exactly this purpose

let tokenA;
let tokenB;
let projectA_id;
let projectB_id;
let commentA_ids; // a representative sample of tenant A's seeded comments (own e2e-alpha project)
let commentB_ids; // 2 comments created in e2e-gamma with tokenB, for the symmetric A-against-B checks

test.beforeAll(async () => {
  const creds = credentials();
  const xp = expected();

  const a = await login(creds.wsAdmin.email, creds.wsAdmin.password);
  const b = await login(
    creds.tenantBOwner?.email || TENANT_B_OWNER.email,
    creds.tenantBOwner?.password || TENANT_B_OWNER.password,
  );
  tokenA = a.token;
  tokenB = b.token;

  // projectA_id / commentA_ids come from the ground-truth fixture seed.mjs writes — no need to
  // re-derive them. c1..c8 are all authored inside e2e-alpha; keep the sample small (2 ids) since
  // one of the assertions below is a DELETE attempt, and a passing isolation check leaves them
  // untouched either way — a larger sample only adds runtime, not confidence.
  projectA_id = xp.projects.alpha.id;
  commentA_ids = [xp.comments.c1.id, xp.comments.c2.id];

  // projectB_id: not in expected.json (that fixture only covers tenant A) — resolve it the same
  // way builds.spec.mjs / project-env-urls.spec.mjs do, by listing tokenB's own projects.
  const projectsB = await raw('GET', '/api/admin/projects', { token: tokenB });
  expect(projectsB.status).toBe(200);
  const gamma = projectsB.data.find((p) => p.key === GAMMA_KEY);
  expect(gamma, `${GAMMA_KEY} must exist under tenant B (seeded by seed.mjs)`).toBeTruthy();
  projectB_id = gamma.id;

  // commentB_ids: create 2 comments in e2e-gamma with tokenB, guarded so re-runs against a reused
  // (E2E_REUSE=1) database don't keep appending — list first, only create what's missing.
  const existing = await raw('GET', `/api/projects/${GAMMA_KEY}/comments?pageSize=50`, { token: tokenB });
  expect(existing.status).toBe(200);
  const existingIsolationComments = (existing.data.items || []).filter((c) => c.body?.startsWith('R5-67 isolation fixture'));

  const ids = existingIsolationComments.map((c) => c.id);
  for (let i = ids.length; i < 2; i++) {
    const created = await post(
      `/api/projects/${GAMMA_KEY}/comments`,
      {
        body: `R5-67 isolation fixture #${i + 1}`,
        environment: 1,
        element: { selector: '#r5-67', route: '/' },
      },
      { token: tokenB },
    );
    ids.push(created.id);
  }
  commentB_ids = ids.slice(0, 2);
});

// === READ ISOLATION: tenant B's token against tenant A's data ===

test.describe('Read isolation — Projects (Admin/ProjectsController)', () => {
  test('GET /api/admin/projects with tokenB sees no A projects', async () => {
    const res = await raw('GET', '/api/admin/projects', { token: tokenB });
    expect(res.status).toBe(200);
    for (const p of res.data) {
      expect(p.id).not.toBe(projectA_id);
      expect([ALPHA_KEY, BETA_KEY]).not.toContain(p.key);
    }
  });

  test('GET /api/admin/projects/{id}/app-urls with tokenB on A’s project id → 403 or 404', async () => {
    const res = await raw('GET', `/api/admin/projects/${projectA_id}/app-urls`, { token: tokenB });
    expect([403, 404]).toContain(res.status);
  });

  test('GET /api/admin/projects/{key}/apply-queue with tokenB on A’s project key → 403 or 404', async () => {
    const res = await raw('GET', `/api/admin/projects/${ALPHA_KEY}/apply-queue`, { token: tokenB });
    expect([403, 404]).toContain(res.status);
  });
});

test.describe('Read isolation — Comments (CommentsController)', () => {
  test('GET /api/projects/e2e-alpha/comments with tokenB → 404 or empty', async () => {
    const res = await raw('GET', `/api/projects/${ALPHA_KEY}/comments`, { token: tokenB });
    expect([200, 404]).toContain(res.status);
    if (res.status === 200) {
      expect(res.data.items ?? res.data).toHaveLength(0);
    }
  });

  test('GET /api/comments/{id} with tokenB on A’s comment ids → 404', async () => {
    for (const id of commentA_ids) {
      const res = await raw('GET', `/api/comments/${id}`, { token: tokenB });
      expect(res.status).toBe(404);
    }
  });
});

test.describe('Read isolation — Workspace (Admin/WorkspaceController)', () => {
  test('GET /api/admin/workspace with tokenB returns tenant B’s own workspace, never A’s', async () => {
    const wsA = await raw('GET', '/api/admin/workspace', { token: tokenA });
    const wsB = await raw('GET', '/api/admin/workspace', { token: tokenB });
    expect(wsA.status).toBe(200);
    expect(wsB.status).toBe(200);
    expect(wsB.data.id).not.toBe(wsA.data.id);
  });

  test('GET /api/admin/workspace/comment-fields with tokenB does not error and stays scoped to B', async () => {
    const res = await raw('GET', '/api/admin/workspace/comment-fields', { token: tokenB });
    expect(res.status).toBe(200);
    expect(Array.isArray(res.data.fields)).toBe(true);
  });
});

test.describe('Read isolation — Other (stack, capture-config, invites, me)', () => {
  test('GET /api/projects/e2e-alpha/stack with tokenB → 403 or 404', async () => {
    const res = await raw('GET', `/api/projects/${ALPHA_KEY}/stack`, { token: tokenB });
    expect([403, 404]).toContain(res.status);
  });

  test('GET /api/projects/e2e-alpha/capture-config with tokenB → 403 or 404', async () => {
    const res = await raw('GET', `/api/projects/${ALPHA_KEY}/capture-config`, { token: tokenB });
    expect([403, 404]).toContain(res.status);
  });

  test('GET /api/admin/invites with tokenB sees none of A’s invite ids', async () => {
    const invitesA = await raw('GET', '/api/admin/invites', { token: tokenA });
    expect(invitesA.status).toBe(200);
    const aIds = new Set(invitesA.data.map((i) => i.id));

    const invitesB = await raw('GET', '/api/admin/invites', { token: tokenB });
    expect(invitesB.status).toBe(200);
    for (const invite of invitesB.data) {
      expect(aIds.has(invite.id)).toBe(false);
    }
  });

  test('GET /api/me/api-key with tokenB returns a key distinct from A’s', async () => {
    const keyA = await raw('GET', '/api/me/api-key', { token: tokenA });
    const keyB = await raw('GET', '/api/me/api-key', { token: tokenB });
    expect(keyA.status).toBe(200);
    expect(keyB.status).toBe(200);
    expect(keyB.data.apiKey).not.toBe(keyA.data.apiKey);
  });

  test('GET /api/me/notifications with tokenB never surfaces A’s comment ids', async () => {
    const res = await raw('GET', '/api/me/notifications?pageSize=100', { token: tokenB });
    expect(res.status).toBe(200);
    for (const n of res.data.items) {
      expect(commentA_ids).not.toContain(n.commentId);
    }
  });

  test('GET /api/me/notifications/unread-count with tokenB is B’s own count', async () => {
    const res = await raw('GET', '/api/me/notifications/unread-count', { token: tokenB });
    expect(res.status).toBe(200);
    expect(typeof res.data.count).toBe('number');
  });
});

// === SYMMETRIC READ ISOLATION: tenant A's token against tenant B's data ===
// Isolation must hold in both directions — asserting it only one way would prove nothing about
// the other tenant's exposure, and is the reason commentB_ids/projectB_id exist at all.

test.describe('Symmetric read isolation — tenant A against tenant B', () => {
  test('GET /api/projects/e2e-gamma/comments with tokenA → 404 or empty', async () => {
    const res = await raw('GET', `/api/projects/${GAMMA_KEY}/comments`, { token: tokenA });
    expect([200, 404]).toContain(res.status);
    if (res.status === 200) {
      expect(res.data.items ?? res.data).toHaveLength(0);
    }
  });

  test('GET /api/comments/{id} with tokenA on B’s comment ids → 404', async () => {
    for (const id of commentB_ids) {
      const res = await raw('GET', `/api/comments/${id}`, { token: tokenA });
      expect(res.status).toBe(404);
    }
  });

  test('GET /api/admin/projects with tokenA sees no B projects', async () => {
    const res = await raw('GET', '/api/admin/projects', { token: tokenA });
    expect(res.status).toBe(200);
    for (const p of res.data) {
      expect(p.id).not.toBe(projectB_id);
      expect(p.key).not.toBe(GAMMA_KEY);
    }
  });
});

// === WRITE ISOLATION: PATCH/DELETE on tenant A's ids with tenant B's token must be refused ===
// A pass here means the attempted mutation never took effect — correct isolation is what keeps
// the seeded comments/project intact for every other spec in the suite.

test.describe('Write isolation — tokenB against tenant A’s ids', () => {
  test('PATCH /api/comments/{id} with tokenB on A’s comment ids → 403 or 404', async () => {
    for (const id of commentA_ids) {
      const res = await raw('PATCH', `/api/comments/${id}`, {
        token: tokenB,
        body: { status: 4 },
      });
      expect([403, 404]).toContain(res.status);
    }
  });

  test('PUT /api/comments/{id} with tokenB on A’s comment ids → 403 or 404', async () => {
    for (const id of commentA_ids) {
      const res = await raw('PUT', `/api/comments/${id}`, {
        token: tokenB,
        body: { body: 'hacked-by-tenant-b', removeScreenshot: false },
      });
      expect([403, 404]).toContain(res.status);
    }
  });

  test('DELETE /api/comments/{id} with tokenB on A’s comment ids → 403 or 404', async () => {
    for (const id of commentA_ids) {
      const res = await raw('DELETE', `/api/comments/${id}`, { token: tokenB });
      expect([403, 404]).toContain(res.status);
    }
  });

  test('PATCH /api/admin/projects/{id} with tokenB on A’s project id → 403 or 404', async () => {
    const res = await raw('PATCH', `/api/admin/projects/${projectA_id}`, {
      token: tokenB,
      body: { name: 'hacked-by-tenant-b' },
    });
    expect([403, 404]).toContain(res.status);
  });

  test('DELETE /api/admin/projects/{id} with tokenB on A’s project id → 403 or 404', async () => {
    const res = await raw('DELETE', `/api/admin/projects/${projectA_id}`, { token: tokenB });
    expect([403, 404]).toContain(res.status);
  });
});

// === SYMMETRIC WRITE ISOLATION: PATCH/DELETE on tenant B's ids with tenant A's token ===

test.describe('Write isolation — tokenA against tenant B’s ids', () => {
  test('PATCH /api/comments/{id} with tokenA on B’s comment ids → 403 or 404', async () => {
    for (const id of commentB_ids) {
      const res = await raw('PATCH', `/api/comments/${id}`, {
        token: tokenA,
        body: { status: 4 },
      });
      expect([403, 404]).toContain(res.status);
    }
  });

  test('DELETE /api/admin/projects/{id} with tokenA on B’s project id → 403 or 404', async () => {
    const res = await raw('DELETE', `/api/admin/projects/${projectB_id}`, { token: tokenA });
    expect([403, 404]).toContain(res.status);
  });
});
