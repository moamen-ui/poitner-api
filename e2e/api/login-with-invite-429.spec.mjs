// R2-05-06 ⛓ — quick-access: 61st login-with-invite in a minute is 429.
// Contract: docs/roadmap/testing/R2-05-tests.md (nightly, isolated, LAST phase).
//
// Runs only in the dedicated --429 phase (run-e2e.sh): 61 rapid redemptions poison the per-IP
// `login` bucket for a minute, and that bucket is shared with login-with-key — the JWT handshake
// every CLI/MCP/apply scenario uses — so this must be the suite's final scenario. State coupling:
// `R2-05-06 <- reset` (one-shot bucket burn; not re-runnable inside a run).
import { test, expect } from '@playwright/test';
import { raw, login } from '../scripts/lib/api.mjs';
import { credentials as loadCredentials, keys as loadKeys } from '../scripts/lib/state.mjs';
import { record } from '../scripts/lib/report.mjs';
import {
  ensureQaFixture,
  clientRoleId,
  inviteQaClient,
  extractInviteToken,
} from '../scripts/lib/quick-access.mjs';

// Read lazily: Playwright evaluates this file to DISCOVER tests, so an eager read on an
// unseeded workspace made `playwright test --list` report 0 tests in 0 files.
const credentials = () => loadCredentials();
const keys = () => loadKeys();

const RUN_ID = Math.random().toString(36).substring(2, 7);

// Same guard shape as api/rate-limits.spec.mjs: gated on the PHASE flag only, never on TIER, with
// an argv heuristic so a solo dispatch by scenario id (`npx playwright test -g R2-05-06`) runs it.
const is429Run =
  process.env.E2E_429 === '1' ||
  process.argv.some((arg) => arg.includes('429') || arg.includes('R2-05-06'));

test('R2-05-06 ⛓ — quick-access: 61st login-with-invite in a minute is 429', async () => {
  test.skip(!is429Run, 'Scenario R2-05-06 is guarded by the --429 flag (final isolated phase only)');
  const start = Date.now();

  // 1. WA creates a fresh invite on the fixture project → valid token (unlimited uses within TTL).
  const wa = await login(credentials().wsAdmin.email, credentials().wsAdmin.password);
  const qaProject = await ensureQaFixture(wa.token);
  const roleId = await clientRoleId(wa.token);
  const email = `qa-cl-${RUN_ID}-429@example.com`;
  const invite = await inviteQaClient(wa.token, { roleId, email, expiresInDays: 14, projectId: qaProject.id });
  expect(invite.status).toBe(200);
  const token = extractInviteToken(invite.data?.magicLink);

  // 2-3. Loop i = 1…61 — sequential, raw (no throw on non-200); record every status.
  const statuses = [];
  for (let i = 1; i <= 61; i++) {
    const res = await raw('POST', '/api/auth/login-with-invite', { body: { token } });
    statuses.push(res.status);
    if (i <= 60) {
      expect(res.status, `redemption ${i} of 60 must succeed (200)`).toBe(200);
      expect(res.data?.status).toBe('ok');
    } else {
      // The 61st: 429 with a Retry-After header (integer, ≤ 60 — the window is one minute).
      expect(res.status, 'the 61st redemption within the minute must be throttled (429)').toBe(429);
      const retryAfter = res.headers.get('retry-after');
      expect(retryAfter, 'the 429 must carry a numeric Retry-After header').toMatch(/^\d+$/);
      expect(Number(retryAfter), 'Retry-After seconds must be at least 1').toBeGreaterThanOrEqual(1);
      expect(Number(retryAfter), 'Retry-After seconds must not exceed the 60s window').toBeLessThanOrEqual(60);
    }
  }

  // Compressed counts to the CI log, NOT into the report detail (H-02 determinism strips detail
  // anyway, and counters/boundary indices belong in the log — rate-limits.spec.mjs precedent).
  const okCount = statuses.filter((s) => s === 200).length;
  const throttledCount = statuses.filter((s) => s === 429).length;
  console.log(`[R2-05-06] 61 sequential login-with-invite calls: 200=${okCount}, 429=${throttledCount}`);

  // 4. Same-IP collateral: another endpoint under the same per-IP `login` policy must ALSO be
  // throttled — this is what proves the bucket is the `login` policy and not `signup`. Do NOT use
  // POST /api/auth/login here: it deliberately carries no limiter (Tests/AuthRateLimitingTests.cs
  // Login_IsNotRateLimited keeps it that way) and would return 200.
  const collateral = await raw('POST', '/api/auth/login-with-key', {
    body: { apiKey: keys().developer.apiKey },
  });
  expect(collateral.status, 'login-with-key must share the poisoned per-IP login bucket (429)').toBe(429);
  console.log(`[R2-05-06] collateral login-with-key: ${collateral.status}`);

  // 5. Negative control: password login is unaffected — the bucket is scoped to the `login`
  // policy's endpoints, not to all auth.
  const control = await raw('POST', '/api/auth/login', {
    body: { email: credentials().developer.email, password: credentials().developer.password },
  });
  expect(control.status, 'password login must stay unlimited (200)').toBe(200);
  expect(control.data?.status).toBe('ok');
  console.log(`[R2-05-06] negative-control password login: ${control.status}`);

  record({
    id: 'R2-05-06', tier: 'nightly', layer: 'api', role: '—', result: 'PASS',
    ms: Date.now() - start, detail: 'login policy throttles login-with-invite + login-with-key; login unaffected',
  });
});
