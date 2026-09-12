// Playwright node-layer E2E spec for mock-domain rebrand rehearsal:
// - R2-00-09 ⛓ — rebrand rehearsal: served surfaces advertise the mock domain
// - R2-00-11 ⛓ — rebrand rehearsal: e-mails carry the new name and domain
// - R2-00-12 ⛓ — rebrand rehearsal: teardown restores brand and origin
// Contract: docs/roadmap/testing/R2-00-tests.md, docs/roadmap/testing/00-HARNESS.md §13
import { test, expect } from '@playwright/test';
import { readFileSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { raw, getRaw, postRaw, login, BASE_URL } from '../scripts/lib/api.mjs';
import { SUPER_ADMIN, TENANT_OWNER, USERS } from '../scripts/lib/constants.mjs';
import { record, recordMailEvidence } from '../scripts/lib/report.mjs';
import { restartApi } from '../scripts/restart-api.mjs';
import { setBranding } from '../scripts/set-branding.mjs';
import { resetBranding } from '../scripts/reset-branding.mjs';
import { assertOriginDefault } from '../scripts/assert-origin-default.mjs';
import { captureSettings, applySettings, restoreSettings } from '../scripts/lib/settings.mjs';
import { clear, awaitMessage, extractLink } from '../scripts/lib/mail.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const STATE_DIR = join(here, '..', 'state');
const CRED_PATH = join(STATE_DIR, 'credentials.json');

function getCredentials() {
  if (existsSync(CRED_PATH)) {
    try {
      return JSON.parse(readFileSync(CRED_PATH, 'utf8'));
    } catch {}
  }
  return {
    wsAdmin: TENANT_OWNER,
    developer: USERS.developer,
    superAdmin: SUPER_ADMIN,
  };
}

const MOCK_DOMAIN = 'pick-it.test';
const MOCK_ORIGIN = `http://${MOCK_DOMAIN}:8090`;
const LEAK_REGEX = /(?<![-\w])Pointer(?![-\w])/g;

test('R2-00-09 ⛓ — rebrand rehearsal: served surfaces advertise the mock domain', async () => {
  test.skip(process.env.TIER === 'pr', 'nightly tier only — skipped during PR tier');
  test.setTimeout(180_000);

  const start = Date.now();
  const creds = getCredentials();
  const saCreds = creds.superAdmin || SUPER_ADMIN;
  const saAuth = await login(saCreds.email, saCreds.password);

  try {
    // 1. Restart API with Pointer__PublicUrl override
    console.log(`[R2-00-09] restarting API with Pointer__PublicUrl=${MOCK_ORIGIN}...`);
    await restartApi({
      env: { Pointer__PublicUrl: MOCK_ORIGIN },
    });

    // 2. Super admin PUT /api/admin/branding
    const mockBrandingPayload = {
      productName: 'PickIt',
      tagline: 'Point it, pick it, ship it',
      urls: {
        app: MOCK_ORIGIN,
        landing: MOCK_ORIGIN,
        demo: `${MOCK_ORIGIN}/demo`,
        docs: `${MOCK_ORIGIN}/docs`,
      },
    };

    const putRes = await raw('PUT', '/api/admin/branding', {
      token: saAuth.token,
      body: mockBrandingPayload,
    });
    expect(putRes.status).toBe(200);

    // 3. getRaw('/api/branding') against http://127.0.0.1:8090 with Host: pick-it.test
    const brandingRes = await getRaw('/api/branding', {
      headers: { Host: MOCK_DOMAIN },
    });
    expect(brandingRes.status).toBe(200);
    expect(brandingRes.data?.productName).toBe('PickIt');
    expect(brandingRes.data?.urls?.app).toBe(MOCK_ORIGIN);

    // Every non-null data.assets.* starts with mock origin publicBase
    const assets = brandingRes.data?.assets || {};
    for (const [kind, assetUrl] of Object.entries(assets)) {
      if (assetUrl) {
        expect(
          String(assetUrl).startsWith(`${MOCK_ORIGIN}/api/branding/asset/`),
          `Asset ${kind} must start with mock origin`,
        ).toBe(true);
      }
    }

    // 4. GET /embed.js?project=e2e-alpha&environment=local with Host: pick-it.test
    const embedRes = await raw('GET', '/embed.js?project=e2e-alpha&environment=local', {
      headers: { Host: MOCK_DOMAIN },
    });
    expect(embedRes.status).toBe(200);
    const embedText = embedRes.text;
    expect(embedText).toContain(`var server = '${MOCK_ORIGIN}'`);
    // Frozen contract name pointer-feedback still present (§13.3)
    expect(embedText).toContain('pointer-feedback');

    // 5. GET /skill.md and GET /pointer-init.md
    const skillRes = await raw('GET', '/skill.md', {
      headers: { Host: MOCK_DOMAIN },
    });
    expect(skillRes.status).toBe(200);
    const skillText = skillRes.text;
    expect(skillText).toContain(MOCK_ORIGIN);
    expect(skillText).not.toContain('<POINTER_SERVER>');

    const initRes = await raw('GET', '/pointer-init.md', {
      headers: { Host: MOCK_DOMAIN },
    });
    expect(initRes.status).toBe(200);
    const initText = initRes.text;
    expect(initText).toContain(MOCK_ORIGIN);
    expect(initText).not.toContain('<POINTER_SERVER>');

    // 6. Assert none of steps 3-5 contain localhost:8090
    expect(JSON.stringify(brandingRes.data)).not.toContain('localhost:8090');
    expect(embedText).not.toContain('localhost:8090');
    expect(skillText).not.toContain('localhost:8090');
    expect(initText).not.toContain('localhost:8090');

    const durationMs = Date.now() - start;
    record({
      id: 'R2-00-09',
      tier: 'nightly',
      layer: 'api',
      role: 'SA',
      result: 'PASS',
      ms: durationMs,
      detail: `mockOrigin=${MOCK_ORIGIN}`,
    });
  } catch (err) {
    const durationMs = Date.now() - start;
    record({
      id: 'R2-00-09',
      tier: 'nightly',
      layer: 'api',
      role: 'SA',
      result: 'FAIL',
      ms: durationMs,
      detail: err.message,
    });
    throw err;
  }
});

test('R2-00-11 ⛓ — rebrand rehearsal: e-mails carry the new name and domain', async () => {
  test.skip(process.env.TIER === 'pr', 'nightly tier only — skipped during PR tier');
  test.setTimeout(180_000);

  const start = Date.now();
  const runId = Math.random().toString(36).substring(2, 7);
  const creds = getCredentials();
  const saCreds = creds.superAdmin || SUPER_ADMIN;
  const saAuth = await login(saCreds.email, saCreds.password);
  const wsAdminAuth = await login(creds.wsAdmin.email, creds.wsAdmin.password);

  let originalSettings = null;

  try {
    // Harness §3: Enable email via super-admin read-modify-write PUT /api/admin/settings
    originalSettings = await captureSettings(saAuth.token);
    await applySettings({ emailEnabled: true }, saAuth.token);

    // 1. Clear mailpit inbox
    await clear();

    // 2. Forgot-password request (spends 1 signup token)
    const forgotRes = await postRaw('/api/auth/forgot-password', {
      email: creds.developer.email,
    });
    expect(forgotRes.status).toBe(200);

    const forgotMail = await awaitMessage({
      to: creds.developer.email,
      subjectIncludes: 'PickIt',
      timeoutMs: 15_000,
    });
    expect(forgotMail.subject).toContain('PickIt');
    const resetSubjectLeaks = (forgotMail.subject.match(LEAK_REGEX) || []).filter((m) => m === 'Pointer');
    expect(resetSubjectLeaks.length, 'Forgot password subject must not leak Pointer').toBe(0);

    recordMailEvidence({
      to: creds.developer.email,
      subject: forgotMail.subject,
      scenarioId: 'R2-00-11',
    });

    // 3. WA invites developer (spends 1 signup token)
    const rolesRes = await raw('GET', '/api/admin/roles', { token: wsAdminAuth.token });
    expect(rolesRes.status).toBe(200);
    const roles = rolesRes.data || [];
    const devRole = roles.find((r) => r.name === 'Developer') || roles[0];
    expect(devRole?.id, 'Developer role must exist').toBeTruthy();

    const inviteEmail = `rebrand-${runId}@example.com`;
    const inviteRes = await postRaw(
      '/api/admin/invites',
      {
        email: inviteEmail,
        roleId: devRole.id,
      },
      { token: wsAdminAuth.token },
    );
    expect(inviteRes.status).toBe(200);

    const inviteMail = await awaitMessage({
      to: inviteEmail,
      timeoutMs: 15_000,
    });
    expect(inviteMail.html).toContain('PickIt');
    const joinLink = extractLink(inviteMail.html, '/join');
    // Verifies app_base_url -> brand_url_app fallback chain (42e534e)
    expect(joinLink.startsWith(`${MOCK_ORIGIN}/join?code=`)).toBe(true);

    const inviteLeaks = ((inviteMail.subject + inviteMail.text + inviteMail.html).match(LEAK_REGEX) || []).filter(
      (m) => m === 'Pointer',
    );
    expect(inviteLeaks.length, 'Staff invite email must not leak Pointer').toBe(0);

    recordMailEvidence({
      to: inviteEmail,
      subject: inviteMail.subject,
      scenarioId: 'R2-00-11',
    });

    // 4. (once R1-08 ships) SA tenant invite
    const wsInviteEmail = `ws-${runId}@example.com`;
    const wsInviteRes = await postRaw(
      '/api/admin/tenants/invites',
      {
        email: wsInviteEmail,
        displayName: 'Rehearsal WS',
      },
      { token: saAuth.token },
    );

    if (wsInviteRes.status === 200) {
      const wsMail = await awaitMessage({
        to: wsInviteEmail,
        timeoutMs: 15_000,
      });
      const wsJoinLink = extractLink(wsMail.html, '/join');
      expect(wsJoinLink.startsWith(`${MOCK_ORIGIN}/join?code=`)).toBe(true);
      expect(wsMail.text).not.toContain('Password:');
      const wsLeaks = ((wsMail.subject + wsMail.text).match(LEAK_REGEX) || []).filter((m) => m === 'Pointer');
      expect(wsLeaks.length).toBe(0);

      recordMailEvidence({
        to: wsInviteEmail,
        subject: wsMail.subject,
        scenarioId: 'R2-00-11',
      });
    }

    const durationMs = Date.now() - start;
    record({
      id: 'R2-00-11',
      tier: 'nightly',
      layer: 'mail',
      role: 'SA, DEV, WA',
      result: 'PASS',
      ms: durationMs,
      detail: `inviteLink=${joinLink}`,
    });
  } catch (err) {
    const durationMs = Date.now() - start;
    record({
      id: 'R2-00-11',
      tier: 'nightly',
      layer: 'mail',
      role: 'SA, DEV, WA',
      result: 'FAIL',
      ms: durationMs,
      detail: err.message,
    });
    throw err;
  } finally {
    if (originalSettings) {
      await restoreSettings(originalSettings, saAuth.token).catch(() => {});
    }
  }
});

test('R2-00-12 ⛓ — rebrand rehearsal: teardown restores brand and origin', async () => {
  test.skip(process.env.TIER === 'pr', 'nightly tier only — skipped during PR tier');

  const creds = getCredentials();
  const saCreds = creds.superAdmin || SUPER_ADMIN;
  const saAuth = await login(saCreds.email, saCreds.password);

  // 1. Reset branding
  await resetBranding({ token: saAuth.token });

  // 2. Restart API without override
  await restartApi({ env: {} });

  // 3. Assert origin default
  const res = await assertOriginDefault();
  expect(res.branding.productName).toBe('Pointer');
  expect(res.branding.urls.app).toBe('https://app.pointer.moamen.work');
});
