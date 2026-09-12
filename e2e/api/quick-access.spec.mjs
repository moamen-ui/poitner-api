// R2-05: passwordless quick-access invites — API layer.
// Scenarios implemented (docs/roadmap/testing/R2-05-tests.md):
// - R2-05-05 — quick-access: password login blocked
// - R2-05-08 — invite email default off (mail layer row, PR tier — mailpit runs on every PR run)
//
// Every expected failure is asserted through raw() — a status matrix expressed via thrown errors
// invites exactly the silently-passing try/catch this suite exists to prevent.
import { test, expect } from '@playwright/test';
import { raw, login } from '../scripts/lib/api.mjs';
import { credentials as loadCredentials } from '../scripts/lib/state.mjs';
import * as mail from '../scripts/lib/mail.mjs';
import { record, recordMailEvidence } from '../scripts/lib/report.mjs';
import {
  MAGIC_LINK_RE,
  ensureQaFixture,
  clientRoleId,
  inviteQaClient,
} from '../scripts/lib/quick-access.mjs';

// Read lazily: Playwright evaluates this file to DISCOVER tests, so an eager read on an
// unseeded workspace made `playwright test --list` report 0 tests in 0 files.
const credentials = () => loadCredentials();

// Unique per run (Flake notes): re-inviting a used address would 409.
const RUN_ID = Math.random().toString(36).substring(2, 7);

let wa;
let qaProject;
let roleId;

test.beforeAll(async () => {
  // The api phase runs before the widget phase, so this file usually materialises the fixture;
  // either way ensureQaFixture is create-or-reuse and idempotent.
  wa = await login(credentials().wsAdmin.email, credentials().wsAdmin.password);
  qaProject = await ensureQaFixture(wa.token);
  roleId = await clientRoleId(wa.token);
});

test('R2-05-05 — quick-access: password login blocked', async () => {
  const start = Date.now();
  const email = `qa-cl-${RUN_ID}-1@example.com`;

  // Provision the passwordless client first — this row's own client (one per scenario).
  const invite = await inviteQaClient(wa.token, { roleId, email, expiresInDays: 14, projectId: qaProject.id });
  expect(invite.status).toBe(200);

  // 1. Password login with the seeded-looking password: refused.
  const first = await raw('POST', '/api/auth/login', { body: { email, password: 'ClientPass1!' } });
  expect(first.status, 'password login must fail for a PasswordlessOnly account').toBe(400);
  expect(first.data?.status).not.toBe('ok');

  // 2. A second, different password: same refusal, and the SAME message — no account-state leak
  // (no passwordless/PasswordlessOnly wording; the Auth.InvalidCredentials family).
  const second = await raw('POST', '/api/auth/login', { body: { email, password: 'Guess123!' } });
  expect(second.status).toBe(400);
  expect(second.data?.status).not.toBe('ok');
  expect(second.body?.message, 'both passwords must produce the identical message').toBe(first.body?.message);
  expect(String(first.body?.message), 'the message must not name the account type').not.toMatch(/passwordless/i);

  // 3. Negative control: the endpoint itself is healthy — a real password account logs in.
  // (Two failed attempts stay far under the 60/min/IP login policy — which POST /api/auth/login
  // does not even carry, by design.)
  const control = await raw('POST', '/api/auth/login', {
    body: { email: credentials().developer.email, password: credentials().developer.password },
  });
  expect(control.status, 'password login for a normal account must succeed').toBe(200);
  expect(control.data?.status).toBe('ok');

  record({
    id: 'R2-05-05', tier: 'PR', layer: 'api', role: 'CL', result: 'PASS',
    ms: Date.now() - start, detail: 'identical refusal for both passwords; control login ok',
  });
});

test('R2-05-08 — invite email default off', async () => {
  const start = Date.now();
  const email = `qa-cl-${RUN_ID}-nomail@example.com`;

  // 1. Setting false — the default. Asserted via GET when the response carries the key
  // (super-admin-gated); the contract also permits trusting the default, and R2-05-07's finally
  // restores it. The key is absent from the response until R2-05 task 4's additive DTO lands.
  const superAdmin = await login(credentials().superAdmin.email, credentials().superAdmin.password);
  const settings = await raw('GET', '/api/admin/settings', { token: superAdmin.token });
  expect(settings.status).toBe(200);
  if (settings.data && 'quickAccessInviteEmailEnabled' in settings.data) {
    expect(settings.data.quickAccessInviteEmailEnabled).toBe(false);
  }

  // 2. Clear, then invite — mailbox cleared before every mail scenario.
  await mail.clear();
  const invite = await inviteQaClient(wa.token, { roleId, email, expiresInDays: 14, projectId: qaProject.id });

  // 3. 200, no email, but the magic link is present regardless (link-copy delivery).
  expect(invite.status).toBe(200);
  expect(invite.data?.emailSent, 'no email by default').toBe(false);
  expect(invite.data?.magicLink).toMatch(MAGIC_LINK_RE);

  // 4. Zero messages in the window — SmtpEmailSender never throws, so a failure surfaces only
  // via this poll.
  await mail.assertNoMail({ to: email, withinMs: 3000 });
  recordMailEvidence({ to: email, subject: '(no mail — default off)', scenarioId: 'R2-05-08' });

  record({
    id: 'R2-05-08', tier: 'PR', layer: 'mail', role: 'WA', result: 'PASS',
    ms: Date.now() - start, detail: 'emailSent=false; mailbox stayed empty; magicLink returned',
  });
});
