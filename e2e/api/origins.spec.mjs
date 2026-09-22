// E2E spec for R1-05: Allowed origins enforcement (API layer).
// Proves the origin allowlist gate on comment creation and replies, localhost exemption for Local env,
// wildcard app-url patterns, staff vs quick-access bypass on absent Origin, and dashboard origins.
// Contract: docs/roadmap/testing/R1-05-tests.md
import { test, expect } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { raw, get, patch, post, del, login } from '../scripts/lib/api.mjs';
import { Environment } from '../scripts/lib/constants.mjs';
import { credentials as loadCredentials, loginClient } from '../scripts/lib/state.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const STATE_DIR = join(here, '..', 'state');
// Read lazily: Playwright evaluates this file to DISCOVER tests, so an eager read on an
// unseeded workspace made `playwright test --list` report 0 tests in 0 files.
const credentials = () => loadCredentials();

/**
 * Resolves a project's database ID from its key via the admin API.
 */
async function getProjectId(key, token) {
  const all = await get('/api/admin/projects', { token });
  const project = all.find((p) => p.key === key);
  if (!project) throw new Error(`Project ${key} not found in admin listing`);
  return project.id;
}

test('R1-05-01 — origin-enforced-blocks-foreign-origin', async () => {
  // 1. Authenticate as tester and wsAdmin.
  const tester = await login(credentials().tester.email, credentials().tester.password);
  const wsAdmin = await login(credentials().wsAdmin.email, credentials().wsAdmin.password);

  const commentPayload = {
    body: 'origin matrix 1',
    environment: Environment.Production,
    element: { selector: '.sidebar', route: '/' },
  };

  // 2. Foreign origin -> 403, envelope isSuccess === false.
  // The origin/permission matrix is a status matrix: raw() returns { status, ok, isSuccess, data }
  // without throwing so the 403 status code is directly assertable.
  const foreignRes = await raw('POST', '/api/projects/e2e-beta/comments', {
    token: tester.token,
    body: commentPayload,
    headers: { Origin: 'https://evil.example' },
  });
  expect(foreignRes.status).toBe(403);
  expect(foreignRes.isSuccess).toBe(false);

  // 3. Allowed exact-row origin (https://app.example.com) -> 200.
  const allowedRes = await raw('POST', '/api/projects/e2e-beta/comments', {
    token: tester.token,
    body: commentPayload,
    headers: { Origin: 'https://app.example.com' },
  });
  expect(allowedRes.status).toBe(200);
  expect(allowedRes.isSuccess).toBe(true);
  const parentCommentId = allowedRes.data?.id;
  expect(parentCommentId).toBeTruthy();

  // 4. Referer fallback: no Origin header, header Referer: https://app.example.com/settings -> 200.
  const refererRes = await raw('POST', '/api/projects/e2e-beta/comments', {
    token: tester.token,
    body: { ...commentPayload, body: 'origin matrix 1 referer' },
    headers: { Referer: 'https://app.example.com/settings' },
  });
  expect(refererRes.status).toBe(200);
  expect(refererRes.isSuccess).toBe(true);

  // 5. Replies share the gate: parent comment's environment governs the exemption check;
  // row matching is project-wide.
  const replyForeign = await raw('POST', `/api/comments/${parentCommentId}/replies`, {
    token: tester.token,
    body: { body: 'reply foreign' },
    headers: { Origin: 'https://evil.example' },
  });
  expect(replyForeign.status).toBe(403);

  const replyAllowed = await raw('POST', `/api/comments/${parentCommentId}/replies`, {
    token: tester.token,
    body: { body: 'reply allowed' },
    headers: { Origin: 'https://app.example.com' },
  });
  expect(replyAllowed.status).toBe(200);

  // 6. WA audit signal check (conditional per Design D):
  // R1-05 Design D makes comment_rejected a UsageEvent "if present; otherwise a structured ILogger warning".
  // Assert counts.comment_rejected >= 2 only when the summary response has a comment_rejected key.
  const betaId = await getProjectId('e2e-beta', wsAdmin.token);
  const summaryRes = await raw('GET', `/api/admin/events/summary?projectId=${betaId}`, {
    token: wsAdmin.token,
  });
  if (summaryRes.ok && summaryRes.data?.counts && 'comment_rejected' in summaryRes.data.counts) {
    expect(summaryRes.data.counts.comment_rejected).toBeGreaterThanOrEqual(2);
  }
});

test('R1-05-02 — origin-enforced-allows-localhost-local', async () => {
  const tester = await login(credentials().tester.email, credentials().tester.password);

  const localPayload = {
    body: 'origin localhost local probe',
    environment: Environment.Local,
    element: { selector: '.sidebar', route: '/' },
  };

  // 1-4: Loopback origins are unconditionally allowed for Environment.Local on an enforced project.
  // 1. Origin: http://localhost:5173 -> 200
  const r1 = await raw('POST', '/api/projects/e2e-beta/comments', {
    token: tester.token,
    body: localPayload,
    headers: { Origin: 'http://localhost:5173' },
  });
  expect(r1.status).toBe(200);

  // 2. Origin: http://127.0.0.1:4173 -> 200
  const r2 = await raw('POST', '/api/projects/e2e-beta/comments', {
    token: tester.token,
    body: localPayload,
    headers: { Origin: 'http://127.0.0.1:4173' },
  });
  expect(r2.status).toBe(200);

  // 3. Origin: http://myapp.localhost:3000 (.localhost suffix) -> 200
  const r3 = await raw('POST', '/api/projects/e2e-beta/comments', {
    token: tester.token,
    body: localPayload,
    headers: { Origin: 'http://myapp.localhost:3000' },
  });
  expect(r3.status).toBe(200);

  // 4. Origin: http://[::1]:5173 (bracketed IPv6 host) -> 200
  const r4 = await raw('POST', '/api/projects/e2e-beta/comments', {
    token: tester.token,
    body: localPayload,
    headers: { Origin: 'http://[::1]:5173' },
  });
  expect(r4.status).toBe(200);

  // 5. Non-local environment gets no localhost exemption: environment: 2 (Staging) -> 403
  const r5 = await raw('POST', '/api/projects/e2e-beta/comments', {
    token: tester.token,
    body: { ...localPayload, environment: Environment.Staging },
    headers: { Origin: 'http://localhost:5173' },
  });
  expect(r5.status).toBe(403);

  // 6. Enforcement-off default: e2e-alpha keeps default behavior, accepting foreign origin -> 200
  const r6 = await raw('POST', '/api/projects/e2e-alpha/comments', {
    token: tester.token,
    body: { ...localPayload, environment: Environment.Production },
    headers: { Origin: 'https://evil.example' },
  });
  expect(r6.status).toBe(200);
});

test('R1-05-03 — wildcard app-url matrix', async () => {
  const wsAdmin = await login(credentials().wsAdmin.email, credentials().wsAdmin.password);
  const tester = await login(credentials().tester.email, credentials().tester.password);
  const betaId = await getProjectId('e2e-beta', wsAdmin.token);

  // Create scratch environment on e2e-beta for wildcard pattern testing.
  const envRes = await raw('POST', '/api/admin/environments', {
    token: wsAdmin.token,
    body: { name: 'e2e-wildcard-lab' },
  });
  const labEnvId = envRes.data?.id;
  expect(labEnvId, 'Scratch environment e2e-wildcard-lab must be created').toBeTruthy();

  try {
    // Save-side validation rules enforced by OriginNormalizer.ValidatePattern:
    // 1. Bare '*' on a shared-hosting suffix -> 400
    const s1 = await raw('PUT', `/api/admin/projects/${betaId}/app-urls/${labEnvId}`, {
      token: wsAdmin.token,
      body: { url: 'https://*.vercel.app' },
    });
    expect(s1.status).toBe(400);

    // 2. Bare '*' with remaining host < 3 labels -> 400
    const s2 = await raw('PUT', `/api/admin/projects/${betaId}/app-urls/${labEnvId}`, {
      token: wsAdmin.token,
      body: { url: 'https://*.acme.com' },
    });
    expect(s2.status).toBe(400);

    // 3. Shared suffix list ends-with semantics (*.foo.github.io) -> 400
    const s3 = await raw('PUT', `/api/admin/projects/${betaId}/app-urls/${labEnvId}`, {
      token: wsAdmin.token,
      body: { url: 'https://*.foo.github.io' },
    });
    expect(s3.status).toBe(400);

    // 4. Non-empty literal part on shared host (myapp-*.vercel.app) -> 200
    const s4 = await raw('PUT', `/api/admin/projects/${betaId}/app-urls/${labEnvId}`, {
      token: wsAdmin.token,
      body: { url: 'https://myapp-*.vercel.app' },
    });
    expect(s4.status).toBe(200);

    // 5. Pattern with >= 3 labels not on shared host (*.staging.acme.com) -> 200
    // Overwrites labEnvId's URL to remain saved for match-side testing.
    const s5 = await raw('PUT', `/api/admin/projects/${betaId}/app-urls/${labEnvId}`, {
      token: wsAdmin.token,
      body: { url: 'https://*.staging.acme.com' },
    });
    expect(s5.status).toBe(200);

    // Match-side: as tester on e2e-beta, environment: 3 (Production)
    // Beta has:
    // - previewEnv row from seed: https://myapp-*.vercel.app
    // - labEnv row from step 5: https://*.staging.acme.com
    const postWithOrigin = (origin) =>
      raw('POST', '/api/projects/e2e-beta/comments', {
        token: tester.token,
        body: {
          body: `wildcard probe ${origin}`,
          environment: Environment.Production,
          element: { selector: '.sidebar', route: '/' },
        },
        headers: { Origin: origin },
      });

    // 6. Origin matching pattern literal prefix -> 200
    const m6 = await postWithOrigin('https://myapp-pr-12.vercel.app');
    expect(m6.status).toBe(200);

    // 7. Non-matching subdomain on same domain -> 403
    const m7 = await postWithOrigin('https://other.vercel.app');
    expect(m7.status).toBe(403);

    // 8. Differing label count -> 403
    const m8 = await postWithOrigin('https://x.myapp-pr-12.vercel.app');
    expect(m8.status).toBe(403);

    // 9. Differing scheme -> 403
    const m9 = await postWithOrigin('http://myapp-pr-12.vercel.app');
    expect(m9.status).toBe(403);

    // 10. Explicit non-default port -> 403 (pattern without explicit port matches default port only)
    const m10 = await postWithOrigin('https://myapp-pr-12.vercel.app:8443');
    expect(m10.status).toBe(403);

    // 11. *.staging.acme.com row match checks
    const m11a = await postWithOrigin('https://pr-7.staging.acme.com');
    expect(m11a.status).toBe(200);

    const m11b = await postWithOrigin('https://a.b.staging.acme.com');
    expect(m11b.status).toBe(403);
  } finally {
    // 12. Teardown: delete the scratch app-url and environment so beta fixture state is cleanly restored.
    if (labEnvId) {
      await raw('DELETE', `/api/admin/projects/${betaId}/app-urls/${labEnvId}`, { token: wsAdmin.token });
      await raw('DELETE', `/api/admin/environments/${labEnvId}`, { token: wsAdmin.token });
    }
  }
});

test('R1-05-04 — no-origin: staff vs quick-access', async () => {
  const wsAdmin = await login(credentials().wsAdmin.email, credentials().wsAdmin.password);
  const dev = await login(credentials().developer.email, credentials().developer.password);
  const client = await loginClient({ post, login });
  const alphaId = await getProjectId('e2e-alpha', wsAdmin.token);

  // 1. WA: Temporarily enable origin enforcement on e2e-alpha.
  // Must be restored in finally to avoid breaking subsequent alpha tests.
  await patch(`/api/admin/projects/${alphaId}`, { enforceAllowedOrigins: true }, { token: wsAdmin.token });

  try {
    // 2. Staff developer token without Origin/Referer (node fetch default) -> 200.
    // Allowed by design for non-quick-access callers (CLI, curl, AI agents).
    const staffRes = await raw('POST', '/api/projects/e2e-alpha/comments', {
      token: dev.token,
      body: {
        body: 'no-origin staff comment',
        environment: Environment.Production,
        element: { selector: '.sidebar', route: '/' },
      },
    });
    expect(staffRes.status).toBe(200);

    // 3. Client (QuickAccess) token without Origin/Referer -> 403.
    // Quick-access callers only ever legitimately interact through the browser widget.
    const clientRes = await raw('POST', '/api/projects/e2e-alpha/comments', {
      token: client.token,
      body: {
        body: 'no-origin client comment',
        environment: Environment.Production,
        element: { selector: '.sidebar', route: '/' },
      },
    });
    expect(clientRes.status).toBe(403);
  } finally {
    // 4. Restore alpha's enforceAllowedOrigins to false and verify restoration.
    await patch(`/api/admin/projects/${alphaId}`, { enforceAllowedOrigins: false }, { token: wsAdmin.token });
    const all = await get('/api/admin/projects', { token: wsAdmin.token });
    const restoredAlpha = all.find((p) => p.id === alphaId);
    expect(restoredAlpha?.enforceAllowedOrigins).toBe(false);
  }
});

test('R1-05-07 — dashboard-origin exemption', async () => {
  const wsAdmin = await login(credentials().wsAdmin.email, credentials().wsAdmin.password);

  // Find or create a parent comment on e2e-beta with Environment.Production.
  // The test stands alone: if seed comment exists, use it; otherwise create one via an allowed origin.
  const commentsRes = await get('/api/projects/e2e-beta/comments?pageSize=10', { token: wsAdmin.token });
  let parent = commentsRes.items?.find((c) => c.environment === Environment.Production);
  if (!parent) {
    const createRes = await raw('POST', '/api/projects/e2e-beta/comments', {
      token: wsAdmin.token,
      body: {
        body: 'parent comment for dashboard exemption',
        environment: Environment.Production,
        element: { selector: '.sidebar', route: '/' },
      },
      headers: { Origin: 'https://app.example.com' },
    });
    parent = createRes.data;
  }
  const parentId = parent?.id;
  expect(parentId, 'Parent comment with Production environment must exist on e2e-beta').toBeTruthy();

  // 1. Reply with Origin: https://app.pointer.moamen.work (BrandUrlApp default, also the sole
  //    Security:TrustedDashboardOrigins default entry) -> 200 (AC-4). AC-8's original second entry,
  //    https://app-angular.pointer.moamen.work, was removed from that default when the Angular
  //    dashboard (and its DNS host) was retired 2026-09-15, so that sub-case no longer applies.
  const r1 = await raw('POST', `/api/comments/${parentId}/replies`, {
    token: wsAdmin.token,
    body: { body: 'reply from default dashboard app' },
    headers: { Origin: 'https://app.pointer.moamen.work' },
  });
  expect(r1.status).toBe(200);

  // 2. Look-alike negative: https://app.pointer.moamen.work.evil.example -> 403
  // Normalized exact comparison prevents suffix bypasses.
  const r2 = await raw('POST', `/api/comments/${parentId}/replies`, {
    token: wsAdmin.token,
    body: { body: 'reply from evil lookalike' },
    headers: { Origin: 'https://app.pointer.moamen.work.evil.example' },
  });
  expect(r2.status).toBe(403);
});
