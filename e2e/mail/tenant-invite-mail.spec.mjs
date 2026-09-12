// Playwright Mail layer specs for R1-08 (Tenant invitation by email).
// Scenarios implemented:
// - R1-08-02 — invitation email carries a link and no password
// - R1-08-08 — resend keeps the link working; rotate invalidates it
// - R1-08-10 ⛓ — mail disabled → link-copy fallback
// - R1-08-16 ⛓ — resend while mail is disabled
import { test, expect } from '@playwright/test';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { get, login, postRaw } from '../scripts/lib/api.mjs';
import * as mail from '../scripts/lib/mail.mjs';
import { captureSettings, applySettings, restoreSettings } from '../scripts/lib/settings.mjs';
import { deleteTenantByEmail } from '../scripts/lib/tenants.mjs';
import { recordMailEvidence } from '../scripts/lib/report.mjs';
import { SUPER_ADMIN, TENANT_OWNER } from '../scripts/lib/constants.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const STATE_DIR = join(here, '..', 'state');
const credPath = join(STATE_DIR, 'credentials.json');
const credentials = existsSync(credPath)
  ? JSON.parse(readFileSync(credPath, 'utf8'))
  : { superAdmin: SUPER_ADMIN, wsAdmin: TENANT_OWNER };

const RUN_ID = Math.random().toString(36).substring(2, 7);

test.beforeAll(async () => {
  // Ensure email is enabled before starting the mail phase
  const superAdmin = await login(credentials.superAdmin.email, credentials.superAdmin.password);
  await applySettings({ emailEnabled: true, emailFromEmail: 'noreply@e2e.local' }, superAdmin.token);
});

test('R1-08-02 — invitation email carries a link and no password', async () => {
  const superAdmin = await login(credentials.superAdmin.email, credentials.superAdmin.password);
  const email = `inv-${RUN_ID}-2@example.com`;

  // 1. Clear mail before test
  await mail.clear();

  // 2. Create invite as superAdmin
  const createRes = await postRaw(
    '/api/admin/tenants/invites',
    { email, displayName: 'Mail Co', expiresInDays: 7 },
    { token: superAdmin.token },
  );
  expect(createRes.status).toBe(200);
  expect(createRes.data.emailSent).toBe(true);
  const url = createRes.data.url;
  expect(url).toBeTruthy();

  // 3. Await email in Mailpit
  const message = await mail.awaitMessage({
    to: email,
    subjectIncludes: 'invited',
    timeoutMs: 10_000,
  });
  expect(message).toBeTruthy();

  // Verify subject contains product name from branding
  const branding = await get('/api/branding');
  expect(message.subject.toLowerCase()).toContain((branding.productName || 'pointer').toLowerCase());

  // 4. Assert HTML carries the link and no password
  expect(message.html).toContain(url);
  expect(message.html).not.toMatch(/password\s*:/i);

  // Record mail evidence
  recordMailEvidence({ to: email, subject: message.subject, scenarioId: 'R1-08-02' });
});

test('R1-08-08 — resend keeps the link working; rotate invalidates it', async () => {
  const superAdmin = await login(credentials.superAdmin.email, credentials.superAdmin.password);
  const email = `inv-${RUN_ID}-8@example.com`;

  try {
    // 1. Create invite with 7 days lifetime
    const createRes = await postRaw(
      '/api/admin/tenants/invites',
      { email, displayName: 'Resend Co', expiresInDays: 7 },
      { token: superAdmin.token },
    );
    expect(createRes.status).toBe(200);
    const { id, url: url1, expiresAt: expiresAt1 } = createRes.data;
    const code1 = new URL(url1).searchParams.get('code');
    expect(code1).toBeTruthy();

    // 2. Resend without rotation: re-sends same link and extends expiry
    const resend1 = await postRaw(
      `/api/admin/tenants/invites/${id}/resend`,
      {},
      { token: superAdmin.token },
    );
    expect(resend1.status).toBe(200);
    expect(resend1.data.url).toContain(code1);
    const expiresAtResent = new Date(resend1.data.expiresAt).getTime();
    expect(expiresAtResent).toBeGreaterThanOrEqual(new Date(expiresAt1).getTime() - 60_000);

    // 3. Clear mail, then resend with rotate=true
    await mail.clear();
    const resend2 = await postRaw(
      `/api/admin/tenants/invites/${id}/resend?rotate=true`,
      {},
      { token: superAdmin.token },
    );
    expect(resend2.status).toBe(200);
    const code2 = new URL(resend2.data.url).searchParams.get('code');
    expect(code2).toBeTruthy();
    expect(code2).not.toBe(code1);

    // 4. Inspect newest mail in Mailpit: contains code2 and NOT code1
    const message = await mail.awaitMessage({ to: email, timeoutMs: 10_000 });
    expect(message.html).toContain(code2);
    expect(message.html).not.toContain(code1);
    recordMailEvidence({ to: email, subject: message.subject, scenarioId: 'R1-08-08' });

    // 5. Accept with code2: succeeds (200)
    const accept2 = await postRaw('/api/auth/register-invite', {
      code: code2,
      email,
      password: 'RotatedPass123!',
      displayName: 'Resend Co',
    });
    expect(accept2.status).toBe(200);

    // Accept with code1: fails (404, rotated out)
    const accept1 = await postRaw('/api/auth/register-invite', {
      code: code1,
      email,
      password: 'OldCodePass123!',
      displayName: 'Resend Co',
    });
    expect(accept1.status).toBe(404);
  } finally {
    await deleteTenantByEmail(email, superAdmin.token);
  }
});

test('R1-08-10 ⛓ — mail disabled → link-copy fallback', async () => {
  const superAdmin = await login(credentials.superAdmin.email, credentials.superAdmin.password);
  const email = `inv-${RUN_ID}-10@example.com`;
  const origSettings = await captureSettings(superAdmin.token);

  try {
    // 1. Disable email in DB settings
    await applySettings({ emailEnabled: false }, superAdmin.token);

    // 2. Clear mail
    await mail.clear();

    // 3. Create invite: succeeds with emailSent = false
    const createRes = await postRaw(
      '/api/admin/tenants/invites',
      { email, displayName: 'No Mail Co', expiresInDays: 7 },
      { token: superAdmin.token },
    );
    expect(createRes.status).toBe(200);
    expect(createRes.data.emailSent).toBe(false);
    expect(createRes.data.url).toBeTruthy();
    const code = new URL(createRes.data.url).searchParams.get('code');

    // 4. Assert no mail was sent
    await mail.assertNoMail({ to: email, withinMs: 3000 });
    recordMailEvidence({ to: email, subject: '(no mail - disabled)', scenarioId: 'R1-08-10' });

    // 5. Accept with code: succeeds (200)
    const acceptRes = await postRaw('/api/auth/register-invite', {
      code,
      email,
      password: 'FallbackPass1!',
      displayName: 'No Mail Co',
    });
    expect(acceptRes.status).toBe(200);
  } finally {
    // 6. Restore original settings
    await restoreSettings(origSettings, superAdmin.token);
    await deleteTenantByEmail(email, superAdmin.token);
  }
});

test('R1-08-16 ⛓ — resend while mail is disabled', async () => {
  const superAdmin = await login(credentials.superAdmin.email, credentials.superAdmin.password);
  const email = `inv-${RUN_ID}-16@example.com`;
  const origSettings = await captureSettings(superAdmin.token);

  try {
    // 1. Create invite while mail is on
    const createRes = await postRaw(
      '/api/admin/tenants/invites',
      { email, displayName: 'Resend Off Co', expiresInDays: 7 },
      { token: superAdmin.token },
    );
    expect(createRes.status).toBe(200);
    const { id } = createRes.data;

    // 2. Disable email in DB settings
    await applySettings({ emailEnabled: false }, superAdmin.token);

    // 3. Clear mail
    await mail.clear();

    // 4. Resend with email disabled: degrades gracefully (200, emailSent=false, non-empty url)
    const resendRes = await postRaw(
      `/api/admin/tenants/invites/${id}/resend`,
      {},
      { token: superAdmin.token },
    );
    expect(resendRes.status).toBe(200);
    expect(resendRes.data.emailSent).toBe(false);
    expect(resendRes.data.url).toBeTruthy();

    // 5. Assert no message sent
    await mail.assertNoMail({ to: email, withinMs: 3000 });
    recordMailEvidence({ to: email, subject: '(no mail - disabled)', scenarioId: 'R1-08-16' });
  } finally {
    // 6. Restore original settings
    await restoreSettings(origSettings, superAdmin.token);
  }
});
