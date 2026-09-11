// E2E spec for R1-05: Comment rate limiting (per-user sliding window 30/60s).
// Proves the 31st sequential comment POST within 60s from one user receives 429 + Retry-After,
// that replies share the policy, and that other users remain completely unaffected.
// Contract: docs/roadmap/testing/R1-05-tests.md
// Tier: nightly (runs in the isolated last phase: run-e2e.sh --429)
import { test, expect } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { raw, get, login } from '../scripts/lib/api.mjs';
import { Environment } from '../scripts/lib/constants.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const STATE_DIR = join(here, '..', 'state');
const credentials = JSON.parse(readFileSync(join(STATE_DIR, 'credentials.json'), 'utf8'));

// Guard: R1-05-05 burns the comments rate-limit bucket for the flood user and must run only
// in the dedicated 429 phase (run-e2e.sh --429) or nightly run, never during the PR tier.
// When dispatched directly by scenario id (npx playwright test -g R1-05-05), the guard permits execution.
const is429Run =
  process.env.TIER === 'nightly' ||
  process.env.E2E_429 === '1' ||
  process.argv.some((arg) => arg.includes('429') || arg.includes('R1-05-05'));

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
