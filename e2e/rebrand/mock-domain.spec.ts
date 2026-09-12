// Playwright browser-layer E2E spec for mock-domain rebrand rehearsal:
// - R2-00-10 ⛓ — rebrand rehearsal: widget served from the mock domain shows the new brand
// - R2-00-13 ⛓ — rebrand rehearsal: TLS variant emits https
// Contract: docs/roadmap/testing/R2-00-tests.md, docs/roadmap/testing/00-HARNESS.md §13
import { test, expect } from '@playwright/test';
import { readFileSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { raw, login } from '../scripts/lib/api.mjs';
import { SUPER_ADMIN, TENANT_OWNER, USERS } from '../scripts/lib/constants.mjs';
import { record } from '../scripts/lib/report.mjs';
import { preAuthWidget } from '../widget/lib/pre-auth';
import { setBranding } from '../scripts/set-branding.mjs';
import { resetBranding } from '../scripts/reset-branding.mjs';
import { restartApi } from '../scripts/restart-api.mjs';
import { assertOriginDefault } from '../scripts/assert-origin-default.mjs';

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

test('R2-00-10 ⛓ — rebrand rehearsal: widget served from the mock domain shows the new brand', async ({
  page,
  browser,
}) => {
  test.skip(process.env.TIER === 'pr', 'nightly tier only — skipped during PR tier');
  test.setTimeout(180_000);

  const start = Date.now();
  const creds = getCredentials();
  const saCreds = creds.superAdmin || SUPER_ADMIN;
  const saAuth = await login(saCreds.email, saCreds.password);
  const devAuth = await login(creds.developer.email, creds.developer.password);

  let secondContext = null;

  try {
    // 1. Ensure mock-domain branding is active (PickIt / http://pick-it.test:8090)
    await setBranding(
      {
        productName: 'PickIt',
        tagline: 'Point it, pick it, ship it',
        urls: {
          app: MOCK_ORIGIN,
          landing: MOCK_ORIGIN,
          demo: `${MOCK_ORIGIN}/demo`,
          docs: `${MOCK_ORIGIN}/docs`,
        },
      },
      { token: saAuth.token },
    );

    // 2. Pre-authenticate developer and navigate to /check on mock host
    // Context created after branding PUT (element.ts:263)
    await preAuthWidget(page, devAuth.token, devAuth.user);
    const cfg = page.waitForResponse((r) => r.url().includes('/capture-config'));

    const checkUrl = `${MOCK_ORIGIN}/check?project=e2e-alpha&environment=local`;
    await page.goto(checkUrl);

    // 3. Await capture-config: proves resolver rule routed and widget loaded cross-nothing
    const cfgResponse = await cfg;
    expect(cfgResponse.url()).toContain(MOCK_DOMAIN);

    // 4. Widget shadow root text: contains PickIt, zero leak-regex matches
    const widget = page.locator('pointer-feedback');
    await expect(widget).toBeAttached({ timeout: 15_000 });

    const innerText = await widget.innerText();
    expect(innerText).toContain('PickIt');
    const leakMatches = (innerText.match(LEAK_REGEX) || []).filter((m) => m === 'Pointer');
    expect(leakMatches.length, `Leaked Pointer matches in widget innerText: ${leakMatches.join(', ')}`).toBe(0);

    // 5. Second, non-pre-authed context: collapsed by default
    secondContext = await browser.newContext();
    const page2 = await secondContext.newPage();
    await page2.goto(checkUrl);

    const widget2 = page2.locator('pointer-feedback');
    await expect(widget2).toBeAttached({ timeout: 15_000 });
    const launcher = widget2.locator('#pf-launcher');
    await expect(launcher).toBeVisible({ timeout: 10_000 });

    const titleAttr = await launcher.getAttribute('title');
    const ariaAttr = await launcher.getAttribute('aria-label');
    expect(titleAttr).toBe('Open PickIt feedback');
    expect(ariaAttr).toBe('Open PickIt feedback');

    // 6. Verify server attribute on <pointer-feedback> element
    const serverAttr = await page.evaluate(() =>
      document.querySelector('pointer-feedback')?.getAttribute('server'),
    );
    expect(serverAttr).toBe(MOCK_ORIGIN);

    // Get shadow-root DOM dump for report evidence
    const shadowDump = await page.evaluate(() => {
      const el = document.querySelector('pointer-feedback');
      return el?.shadowRoot?.innerHTML?.slice(0, 500) || '';
    });

    const durationMs = Date.now() - start;
    record({
      id: 'R2-00-10',
      tier: 'nightly',
      layer: 'widget',
      role: 'SA, DEV',
      result: 'PASS',
      ms: durationMs,
      detail: `serverAttr=${serverAttr}, shadowLen=${shadowDump.length}`,
    });
  } catch (err) {
    const durationMs = Date.now() - start;
    record({
      id: 'R2-00-10',
      tier: 'nightly',
      layer: 'widget',
      role: 'SA, DEV',
      result: 'FAIL',
      ms: durationMs,
      detail: err.message,
    });
    throw err;
  } finally {
    if (secondContext) await secondContext.close().catch(() => {});
  }
});

test('R2-00-13 ⛓ — rebrand rehearsal: TLS variant emits https', async ({ page }) => {
  // Manual variant only, gated on process.env.E2E_MOCK_TLS
  test.skip(!process.env.E2E_MOCK_TLS, 'manual --mock-domain-tls only');
  test.setTimeout(180_000);

  const start = Date.now();
  const creds = getCredentials();
  const saCreds = creds.superAdmin || SUPER_ADMIN;
  const saAuth = await login(saCreds.email, saCreds.password);

  const TLS_ORIGIN = `https://${MOCK_DOMAIN}:8443`;

  try {
    // 1. Caddy runs on 8443 with tls internal + reverse_proxy api:8080
    // 2. Restart API with Pointer__PublicUrl=https://pick-it.test:8443
    await restartApi({
      env: { Pointer__PublicUrl: TLS_ORIGIN },
    });

    await setBranding(
      {
        productName: 'PickIt',
        tagline: 'Point it, pick it, ship it',
        urls: {
          app: TLS_ORIGIN,
          landing: TLS_ORIGIN,
          demo: `${TLS_ORIGIN}/demo`,
          docs: `${TLS_ORIGIN}/docs`,
        },
      },
      { token: saAuth.token },
    );

    // 3. Node fetch https://127.0.0.1:8443/embed.js?project=e2e-alpha with Host: pick-it.test
    const embedRes = await raw('GET', 'https://127.0.0.1:8443/embed.js?project=e2e-alpha', {
      headers: { Host: MOCK_DOMAIN },
    });
    expect(embedRes.status).toBe(200);
    expect(embedRes.text).toContain(`var server = '${TLS_ORIGIN}'`);

    // 4. Playwright with resolver rule and ignoreHTTPSErrors: true
    const consoleErrors: string[] = [];
    page.on('console', (msg) => {
      if (msg.type() === 'error') consoleErrors.push(msg.text());
    });

    await page.goto(`${TLS_ORIGIN}/check?project=e2e-alpha`);
    const widget = page.locator('pointer-feedback');
    await expect(widget).toBeAttached({ timeout: 15_000 });

    const innerText = await widget.innerText();
    expect(innerText).toContain('PickIt');

    // Verify no mixed-content errors
    const mixedContentErrors = consoleErrors.filter((e) => /mixed-content|insecure/i.test(e));
    expect(mixedContentErrors.length, `Mixed content errors found: ${mixedContentErrors.join(', ')}`).toBe(0);

    const durationMs = Date.now() - start;
    record({
      id: 'R2-00-13',
      tier: 'manual',
      layer: 'api+widget',
      role: 'SA',
      result: 'PASS',
      ms: durationMs,
      detail: `tlsOrigin=${TLS_ORIGIN}`,
    });
  } catch (err) {
    const durationMs = Date.now() - start;
    record({
      id: 'R2-00-13',
      tier: 'manual',
      layer: 'api+widget',
      role: 'SA',
      result: 'FAIL',
      ms: durationMs,
      detail: err.message,
    });
    throw err;
  } finally {
    await resetBranding({ token: saAuth.token }).catch(() => {});
    await restartApi({ env: {} }).catch(() => {});
    await assertOriginDefault().catch(() => {});
  }
});
