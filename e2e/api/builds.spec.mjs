// E2E spec for R3-01: Vite source-stamp plugin, local manifest, deploy awareness — API layer.
// Covers:
// - R3-01-06: /builds negatives & isolation (AC-2, AC-9)
// Contract: docs/roadmap/testing/R3-01-tests.md
import { test, expect } from '@playwright/test';
import { raw, post, get, patch, login, ApiError } from '../scripts/lib/api.mjs';
import { credentials as loadCredentials, keys as loadKeys, loginClient } from '../scripts/lib/state.mjs';
import { TENANT_B_OWNER } from '../scripts/lib/constants.mjs';
import { record } from '../scripts/lib/report.mjs';

const credentials = () => loadCredentials();
const keys = () => loadKeys();

const PROJECT_KEY = 'e2e-r301';
const PROJECT_GAMMA = 'e2e-gamma';
const VALID_SHA = 'a1b2c3d4e5f6789012345678901234567890abcd';

test.describe('R3-01: /builds API', () => {
  let wa;
  let qa;
  let tb;
  let cl;

  test.beforeAll(async () => {
    wa = await login(credentials().wsAdmin.email, credentials().wsAdmin.password);
    qa = await login(credentials().tester.email, credentials().tester.password);
    tb = await login(
      credentials().tenantBOwner?.email || TENANT_B_OWNER.email,
      credentials().tenantBOwner?.password || TENANT_B_OWNER.password,
    );
    cl = await loginClient({ post, login });

    // Ensure e2e-r301 project exists (idempotent, 409-tolerant)
    let project;
    try {
      project = await post('/api/admin/projects', { key: PROJECT_KEY, name: 'E2E R3-01' }, { token: wa.token });
    } catch (err) {
      if (!(err instanceof ApiError) || err.status !== 409) throw err;
      const all = await get('/api/admin/projects', { token: wa.token });
      project = all.find((p) => p.key === PROJECT_KEY);
      if (!project) throw new Error(`e2e-r301 conflicted but was not found in admin project list`);
    }
  });

  // BLOCKED — not a test defect. POST /api/projects/{key}/builds does not exist (R3-01 deploy-awareness half).
  // Marked fixme rather than left failing so the nightly tier stays a signal; the scenario
  // stays here, and this line is what has to be deleted when the feature lands.
  test('R3-01-06 — /builds negatives & isolation', async () => {
    test.fixme(true, 'POST /api/projects/{key}/builds does not exist (R3-01 deploy-awareness half)');
    const start = Date.now();
    const createdComments = [];

    try {
      // 1. Validation with WA token: POST /api/projects/e2e-r301/builds
      // Uppercase hex normalised then validated -> 200 with sha echoed lowercase
      const upperRes = await raw('POST', `/api/projects/${PROJECT_KEY}/builds`, {
        token: wa.token,
        body: { sha: 'ABCDEF1234567' },
      });
      expect(upperRes.status).toBe(200);
      expect(upperRes.data?.sha).toBe('abcdef1234567');

      // Non-hex -> 400
      const nonHexRes = await raw('POST', `/api/projects/${PROJECT_KEY}/builds`, {
        token: wa.token,
        body: { sha: 'xyz' },
      });
      expect(nonHexRes.status).toBe(400);

      // Too short (< 7 chars) -> 400
      const shortRes = await raw('POST', `/api/projects/${PROJECT_KEY}/builds`, {
        token: wa.token,
        body: { sha: 'abc' },
      });
      expect(shortRes.status).toBe(400);

      // Too long (> 40 chars) -> 400
      const longRes = await raw('POST', `/api/projects/${PROJECT_KEY}/builds`, {
        token: wa.token,
        body: { sha: 'a'.repeat(41) },
      });
      expect(longRes.status).toBe(400);

      // List cap exceeded (> 200 items in containsCommitShas) -> 400
      const capRes = await raw('POST', `/api/projects/${PROJECT_KEY}/builds`, {
        token: wa.token,
        body: {
          sha: VALID_SHA,
          containsCommitShas: Array(201).fill(VALID_SHA),
        },
      });
      expect(capRes.status).toBe(400);

      // 2. CL token (QuickAccess Client), valid body -> 403
      const clRes = await raw('POST', `/api/projects/${PROJECT_KEY}/builds`, {
        token: cl.token,
        body: { sha: VALID_SHA },
      });
      expect(clRes.status).toBe(403);

      // 3. TB token (Tenant B Owner) targeting e2e-r301 -> 404
      const tbCrossRes = await raw('POST', `/api/projects/${PROJECT_KEY}/builds`, {
        token: tb.token,
        body: { sha: VALID_SHA },
      });
      expect(tbCrossRes.status).toBe(404);

      // TB on own project e2e-gamma -> 200
      const tbOwnRes = await raw('POST', `/api/projects/${PROJECT_GAMMA}/builds`, {
        token: tb.token,
        body: { sha: VALID_SHA },
      });
      expect(tbOwnRes.status).toBe(200);

      // 4. QA token (staff, non-admin) -> 200
      const qaSha = 'b2c3d4e5f6789012345678901234567890abcdef';
      const qaRes = await raw('POST', `/api/projects/${PROJECT_KEY}/builds`, {
        token: qa.token,
        body: { sha: qaSha },
      });
      expect(qaRes.status).toBe(200);

      // 5. Repeat identical report -> firstSeen: false, deployedCommentIds: []
      const repeatRes = await raw('POST', `/api/projects/${PROJECT_KEY}/builds`, {
        token: qa.token,
        body: { sha: qaSha },
      });
      expect(repeatRes.status).toBe(200);
      expect(repeatRes.data?.firstSeen).toBe(false);
      expect(repeatRes.data?.deployedCommentIds).toEqual([]);

      // 6. Tenancy isolation:
      // Create Applied comment in e2e-gamma with commitSha = isolationSha
      const isolationSha = 'c3d4e5f6789012345678901234567890abcdef01';
      const gammaComment = await raw('POST', `/api/projects/${PROJECT_GAMMA}/comments`, {
        token: tb.token,
        body: {
          body: 'Tenant B isolation comment',
          element: { selector: '#test' },
        },
      });
      expect(gammaComment.status).toBe(200);
      const gammaCommentId = gammaComment.data?.id;
      createdComments.push({ project: PROJECT_GAMMA, id: gammaCommentId, token: tb.token });

      const patchGamma = await raw('PATCH', `/api/comments/${gammaCommentId}`, {
        token: tb.token,
        body: {
          status: 3,
          commitSha: isolationSha,
        },
      });
      expect(patchGamma.status).toBe(200);

      // WA reports isolationSha on e2e-r301
      const waReportRes = await raw('POST', `/api/projects/${PROJECT_KEY}/builds`, {
        token: wa.token,
        body: { sha: isolationSha },
      });
      expect(waReportRes.status).toBe(200);

      // TB reads e2e-gamma comment -> deployedAt stays null
      const checkGamma = await raw('GET', `/api/comments/${gammaCommentId}`, {
        token: tb.token,
      });
      expect(checkGamma.status).toBe(200);
      expect(checkGamma.data?.deployedAt).toBeNull();

      // 7. Old comment (no commitSha) on e2e-r301 -> never in deployedCommentIds
      const oldSha = 'd4e5f6789012345678901234567890abcdef0123';
      const oldComment = await raw('POST', `/api/projects/${PROJECT_KEY}/comments`, {
        token: wa.token,
        body: {
          body: 'Old comment without commitSha',
          element: { selector: '#test' },
        },
      });
      expect(oldComment.status).toBe(200);
      const oldCommentId = oldComment.data?.id;
      createdComments.push({ project: PROJECT_KEY, id: oldCommentId, token: wa.token });

      const patchOld = await raw('PATCH', `/api/comments/${oldCommentId}`, {
        token: wa.token,
        body: { status: 3 },
      });
      expect(patchOld.status).toBe(200);

      const oldReportRes = await raw('POST', `/api/projects/${PROJECT_KEY}/builds`, {
        token: wa.token,
        body: { sha: oldSha },
      });
      expect(oldReportRes.status).toBe(200);
      expect(oldReportRes.data?.deployedCommentIds ?? []).not.toContain(oldCommentId);

      record({
        id: 'R3-01-06',
        tier: 'PR',
        layer: 'api',
        role: 'WA',
        result: 'PASS',
        ms: Date.now() - start,
        detail: 'builds validation, CL 403, TB 404, repeat firstSeen=false, cross-tenant isolation verified',
      });
    } finally {
      // Restore state: archive any comments created in the run
      for (const item of createdComments) {
        try {
          await raw('PATCH', `/api/comments/${item.id}`, {
            token: item.token,
            body: { status: 4 }, // Archived
          });
        } catch {}
      }
    }
  });
});
