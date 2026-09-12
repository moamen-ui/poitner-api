// Mail-layer specs. Contract homes here: H-04 (docs/roadmap/testing/H-tests.md — password-reset
// email) and R2-05-07 (docs/roadmap/testing/R2-05-tests.md — quick-access invite email). This
// file is created by the R2-05 task with its row; H-04 lands alongside when the harness-tests
// contract is implemented (kept first in the mail phase once it exists — H-tests Flake notes).
import { test, expect } from '@playwright/test';
import { raw, login } from '../scripts/lib/api.mjs';
import { credentials as loadCredentials } from '../scripts/lib/state.mjs';
import * as mail from '../scripts/lib/mail.mjs';
import { applySettings } from '../scripts/lib/settings.mjs';
import { record, recordMailEvidence } from '../scripts/lib/report.mjs';
import { ensureQaFixture, clientRoleId, inviteQaClient } from '../scripts/lib/quick-access.mjs';

// Read lazily: Playwright evaluates this file to DISCOVER tests, so an eager read on an
// unseeded workspace made `playwright test --list` report 0 tests in 0 files.
const credentials = () => loadCredentials();

// Unique per run (Flake notes): mail assertions must never match a previous run's message, and
// re-inviting a used address would 409.
const RUN_ID = Math.random().toString(36).substring(2, 7);

test('R2-05-07 — invite email (setting on): link, no password', async () => {
  test.skip(process.env.TIER === 'pr', 'nightly tier only — skipped during PR tier');
  const start = Date.now();

  const superAdmin = await login(credentials().superAdmin.email, credentials().superAdmin.password);
  const wa = await login(credentials().wsAdmin.email, credentials().wsAdmin.password);
  const email = `qa-cl-${RUN_ID}-mail@example.com`;

  try {
    // 1. SA turns the setting ON — read-modify-write through applySettings, because
    // PUT /api/admin/settings is a replace-all writer and a partial body blanks the demo and
    // extension settings. The key is exposed additively on UpdateSettingsRequest/SettingsResponse
    // (R2-05 task 4, super-admin-gated).
    await applySettings({ quickAccessInviteEmailEnabled: true }, superAdmin.token);
    const verify = await raw('GET', '/api/admin/settings', { token: superAdmin.token });
    expect(verify.status).toBe(200);
    expect(verify.data?.quickAccessInviteEmailEnabled, 'GET /api/admin/settings must reflect the toggle').toBe(true);

    // 2. Clear, then invite with the setting on.
    await mail.clear();
    const qaProject = await ensureQaFixture(wa.token);
    const roleId = await clientRoleId(wa.token);
    const invite = await inviteQaClient(wa.token, { roleId, email, expiresInDays: 14, projectId: qaProject.id });
    expect(invite.status).toBe(200);
    expect(invite.data?.emailSent, 'with the setting on, the invite email must be sent').toBe(true);

    // 3. Message within 10 s; body carries the MAGIC LINK and no password anywhere.
    const message = await mail.awaitMessage({ to: email, subjectIncludes: 'invite', timeoutMs: 10_000 });
    expect(message.html).toMatch(/pointer_invite=[A-Za-z0-9_-]{43}/);
    const link = mail.extractLink(message.html, '');
    expect(link).toContain('pointer_invite=');
    // Case-sensitive substring check, per the contract: a password must never appear in the mail.
    for (const body of [message.html, message.text]) {
      expect(body, 'the invite email must not contain a Password: line').not.toContain('Password:');
    }

    recordMailEvidence({ to: email, subject: message.subject, scenarioId: 'R2-05-07' });
    record({
      id: 'R2-05-07', tier: 'nightly', layer: 'mail', role: 'SA, WA', result: 'PASS',
      ms: Date.now() - start, detail: 'magic link in body; no password in body',
    });
  } finally {
    // 4. Setting back to false — a leftover toggle breaks every later invite scenario (they all
    // assert the default-off behaviour). Wrapped so a restore failure cannot mask the assertion
    // error that explains the real failure.
    try {
      await applySettings({ quickAccessInviteEmailEnabled: false }, superAdmin.token);
    } catch (err) {
      console.error(`could not restore quickAccessInviteEmailEnabled=false: ${err?.message ?? err}`);
    }
  }
});
