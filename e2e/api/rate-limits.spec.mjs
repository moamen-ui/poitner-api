// E2E spec for rate limiting:
// - R1-04-05 ⛓: Meta rate limiting (per-IP fixed window 120/60s).
// - R1-05-05: Comment rate limiting (per-user sliding window 30/60s).
// Contract: docs/roadmap/testing/R1-04-tests.md and docs/roadmap/testing/R1-05-tests.md
// Tier: nightly (runs in the isolated last phase: run-e2e.sh --429)
import { test, expect } from '@playwright/test';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { raw, get, login } from '../scripts/lib/api.mjs';
import { Environment, FLOOD, USERS } from '../scripts/lib/constants.mjs';
import { record } from '../scripts/lib/report.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const STATE_DIR = join(here, '..', 'state');
const credPath = join(STATE_DIR, 'credentials.json');
const credentials = existsSync(credPath)
  ? JSON.parse(readFileSync(credPath, 'utf8'))
  : { flood: FLOOD, tester: USERS.tester };

// Guard: Rate limit tests burn policy buckets and must run only in the dedicated 429 phase
// (run-e2e.sh --429) or nightly run, never during the PR tier.
// When dispatched directly by scenario id (npx playwright test -g R1-04-05), the guard permits execution.
const is429Run =
  process.env.TIER === 'nightly' ||
  process.env.E2E_429 === '1' ||
  process.argv.some(
    (arg) => arg.includes('429') || arg.includes('R1-05-05') || arg.includes('R1-04-05')
  );

test('R1-04-05 ⛓ — meta 121st/min/IP -> 429', async () => {
  test.skip(!is429Run, 'Scenario R1-04-05 is guarded by the --429 flag (nightly phase only)');
  const start = Date.now();

  // 1. Issue 121 anonymous GET /api/meta requests concurrently (Promise.all)
  // Concurrent issuing avoids fixed 60s window resets mid-run.
  const requests = Array.from({ length: 121 }, () => raw('GET', '/api/meta'));
  const responses = await Promise.all(requests);

  const statuses = responses.map((r) => r.status);
  const count200 = statuses.filter((s) => s === 200).length;
  const count429 = statuses.filter((s) => s === 429).length;

  // Counts logged to CI log, NOT in detail (H-02 determinism)
  console.log(`[R1-04-05] 121 concurrent GET /api/meta requests: 200=${count200}, 429=${count429}`);

  // Multiset assertion: under concurrency arrival order is undefined,
  // so assert exactly 120 with status 200 and exactly 1 with status 429.
  expect(count200, 'Exactly 120 requests must succeed with 200').toBe(120);
  expect(count429, 'Exactly 1 request must be throttled with 429').toBe(1);

  const throttledRes = responses.find((r) => r.status === 429);
  expect(throttledRes, 'Throttled 429 response must exist').toBeTruthy();

  const retryAfter = throttledRes.headers.get('retry-after');
  expect(retryAfter, '429 response must carry a numeric Retry-After header').toMatch(/^\d+$/);
  expect(Number(retryAfter), 'Retry-After value must be >= 1').toBeGreaterThanOrEqual(1);

  // 2. Poll (<= 70s) until GET /api/meta returns 200 again (fixed 60s window released)
  await expect
    .poll(
      async () => {
        const res = await raw('GET', '/api/meta');
        return res.status;
      },
      {
        timeout: 70_000,
        intervals: [1_000, 2_000],
      }
    )
    .toBe(200);

  const durationMs = Date.now() - start;
  record({
    id: 'R1-04-05',
    tier: 'nightly',
    layer: 'api',
    role: '—',
    result: 'PASS',
    ms: durationMs,
    detail: 'windowReleased=true',
  });
});

test('R1-05-05 — comment-burst-429', async () => {
  test.skip(!is429Run, 'Scenario R1-05-05 is guarded by the --429 flag (nightly phase only)');

  // 1. Authenticate as the dedicated FLOOD persona (guaranteed zero prior comments by H-07).
  const flood = await login(credentials.flood.email, credentials.flood.password);
  const tester = await login(credentials.tester.email, credentials.tester.password);

  // 2. 31 sequential comment creations on e2e-alpha by the flood user.
  // Sliding window is 30 requests / 60s partitioned by user id (sub claim).
  for (let i = 1; i <= 31; i++) {
    const res = await raw('POST', '/api/projects/e2e-alpha/comments', {
      token: flood.token,
      body: {
        body: `burst ${i}`,
        environment: Environment.Local,
        element: { selector: '#decoy', route: '/' },
      },
    });

    if (i <= 30) {
      expect(res.status, `Comment ${i} of 30 must succeed (200)`).toBe(200);
    } else {
      // The 31st request exceeds the policy limit -> 429 Too Many Requests.
      expect(res.status, 'The 31st comment within 60s must be throttled with 429').toBe(429);

      const retryAfter = res.headers.get('retry-after');
      expect(retryAfter, '429 response must contain a numeric Retry-After header').toMatch(/^\d+$/);
      expect(Number(retryAfter), 'Retry-After seconds must be at least 1').toBeGreaterThanOrEqual(1);
    }
  }

  // 3. Flood user attempts a reply: replies share the "comments" policy and must also be 429 throttled.
  const commentsList = await get('/api/projects/e2e-alpha/comments?pageSize=5', { token: tester.token });
  const targetComment = commentsList.items?.[0];
  expect(targetComment, 'An existing comment must exist on e2e-alpha to reply to').toBeTruthy();

  const replyRes = await raw('POST', `/api/comments/${targetComment.id}/replies`, {
    token: flood.token,
    body: { body: 'r' },
  });
  expect(replyRes.status, 'Reply from throttled flood user must be rate-limited (429)').toBe(429);

  // 4. Second user unaffected: tester posts one comment on e2e-alpha and receives 200.
  // Proves partitioning is per-user (sub), not per-IP or global.
  const testerRes = await raw('POST', '/api/projects/e2e-alpha/comments', {
    token: tester.token,
    body: {
      body: 'unaffected second user comment',
      environment: Environment.Local,
      element: { selector: '#decoy', route: '/' },
    },
  });
  expect(testerRes.status, 'Second authenticated user must not be throttled (200)').toBe(200);
});
