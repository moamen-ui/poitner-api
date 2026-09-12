// E2E spec for R1-02-05: installed + first_comment events exactly once.
// Proves usage events ingestion, client type whitelisting, and race-safe first_comment emission.
// Contract: docs/roadmap/testing/R1-02-tests.md
import { test, expect } from '@playwright/test';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { raw, postRaw, login } from '../scripts/lib/api.mjs';
import { TENANT_OWNER, USERS, TENANT_B_OWNER } from '../scripts/lib/constants.mjs';
import { record } from '../scripts/lib/report.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const STATE_DIR = join(here, '..', 'state');
const credPath = join(STATE_DIR, 'credentials.json');
const credentials = existsSync(credPath)
  ? JSON.parse(readFileSync(credPath, 'utf8'))
  : {
      wsAdmin: TENANT_OWNER,
      tester: USERS.tester,
      tenantBOwner: TENANT_B_OWNER,
    };

test('R1-02-05 — installed + first_comment events exactly once', async () => {
  const start = Date.now();

  const wsAdmin = await login(credentials.wsAdmin.email, credentials.wsAdmin.password);
  const tester = await login(credentials.tester.email, credentials.tester.password);
  const tenantBOwner = await login(
    credentials.tenantBOwner?.email || TENANT_B_OWNER.email,
    credentials.tenantBOwner?.password || TENANT_B_OWNER.password,
  );

  let projectId = null;

  try {
    // 1. WA: Create project 'e2e-events'
    const projectRes = await postRaw(
      '/api/admin/projects',
      { key: 'e2e-events', name: 'E2E Events' },
      { token: wsAdmin.token },
    );

    if (projectRes.status === 409) {
      // If leftover from a previous aborted run, find its id
      const listRes = await raw('GET', '/api/admin/projects', { token: wsAdmin.token });
      const found = (listRes.data || []).find((p) => p.key === 'e2e-events');
      projectId = found?.id;
    } else {
      expect(projectRes.status).toBe(200);
      projectId = projectRes.data?.id;
    }
    expect(projectId, 'e2e-events project ID must be present').toBeTruthy();

    // 2. QA: POST /api/events with type: 'installed' -> 204
    // postRaw is required because Result<T> unwrap drops status on 204.
    const installRes = await postRaw(
      '/api/events',
      {
        type: 'installed',
        projectKey: 'e2e-events',
        meta: {
          stack: { kind: 'static' },
          aiTool: 'other',
          injected: true,
          cliVersion: '0.1.0',
        },
      },
      { token: tester.token },
    );
    expect(installRes.status).toBe(204);

    // 3. QA: Post two comments -> 200, 200
    const comment1 = await postRaw(
      '/api/projects/e2e-events/comments',
      {
        body: 'events c1',
        environment: 1,
        element: { selector: '#x', route: '/' },
      },
      { token: tester.token },
    );
    expect(comment1.status).toBe(200);

    const comment2 = await postRaw(
      '/api/projects/e2e-events/comments',
      {
        body: 'events c2',
        environment: 1,
        element: { selector: '#x', route: '/' },
      },
      { token: tester.token },
    );
    expect(comment2.status).toBe(200);

    // 4. WA: Read summary -> counts.installed === 1, counts.first_comment === 1
    const summary1 = await raw('GET', `/api/admin/events/summary?projectId=${projectId}`, {
      token: wsAdmin.token,
    });
    expect(summary1.status).toBe(200);
    expect(summary1.data?.counts?.installed).toBe(1);
    expect(summary1.data?.counts?.first_comment).toBe(1);
    expect(summary1.data?.firstAt?.first_comment).toBeTruthy();
    const firstCommentTs = new Date(summary1.data.firstAt.first_comment).getTime();
    expect(Number.isNaN(firstCommentTs)).toBe(false);

    // 5. QA: Post a third comment -> 200. first_comment count must remain 1.
    const comment3 = await postRaw(
      '/api/projects/e2e-events/comments',
      {
        body: 'events c3',
        environment: 1,
        element: { selector: '#x', route: '/' },
      },
      { token: tester.token },
    );
    expect(comment3.status).toBe(200);

    const summary2 = await raw('GET', `/api/admin/events/summary?projectId=${projectId}`, {
      token: wsAdmin.token,
    });
    expect(summary2.status).toBe(200);
    expect(summary2.data?.counts?.first_comment).toBe(1);

    // 6. Negatives as QA:
    // (a) Client cannot emit 'first_comment' (server-emitted only) -> 400
    const neg1 = await postRaw(
      '/api/events',
      { type: 'first_comment', projectKey: 'e2e-events' },
      { token: tester.token },
    );
    expect(neg1.status).toBe(400);

    // (b) Unknown projectKey -> 404
    const neg2 = await postRaw(
      '/api/events',
      { type: 'installed', projectKey: 'nope' },
      { token: tester.token },
    );
    expect(neg2.status).toBe(404);

    // (c) Meta blob > 2000 characters -> 400
    const neg3 = await postRaw(
      '/api/events',
      {
        type: 'installed',
        projectKey: 'e2e-events',
        meta: { blob: 'x'.repeat(2001) },
      },
      { token: tester.token },
    );
    expect(neg3.status).toBe(400);

    // 7. Cross-tenant isolation: Tenant B reading Tenant A's project events summary
    // Query filters yield 0 rows for foreign tenant's project.
    const crossRes = await raw('GET', `/api/admin/events/summary?projectId=${projectId}`, {
      token: tenantBOwner.token,
    });
    expect(crossRes.status).toBe(200);
    const tbCounts = crossRes.data?.counts || {};
    expect(tbCounts.first_comment || 0).toBe(0);
    expect(tbCounts.installed || 0).toBe(0);

    const durationMs = Date.now() - start;
    record({
      id: 'R1-02-05',
      tier: 'PR',
      layer: 'api',
      role: 'wsAdmin',
      result: 'PASS',
      ms: durationMs,
      detail: JSON.stringify(summary2.data || {}),
    });
  } finally {
    // Teardown: delete project e2e-events to restore clean database state
    if (projectId) {
      await raw('DELETE', `/api/admin/projects/${projectId}`, { token: wsAdmin.token });
    }
  }
});
