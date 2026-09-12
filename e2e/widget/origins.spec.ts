// Real Playwright browser automation for R1-05: Origin 403 widget toast.
// Proves that when the widget receives a real 403 Forbidden due to an unallowed origin,
// it displays the specific error toast "Comments are not allowed from this address"
// without closing the popover, and that switching to an allowed environment (Local)
// succeeds with "Comment added".
// Contract: docs/roadmap/testing/R1-05-tests.md
// Tier: nightly
import { test, expect } from '@playwright/test';
import { spawn, type ChildProcess } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { get, login } from '../scripts/lib/api.mjs';
import { preAuthWidget } from './lib/auth';

const here = dirname(fileURLToPath(import.meta.url));
const STATE_DIR = join(here, '..', 'state');
const credentials = JSON.parse(readFileSync(join(STATE_DIR, 'credentials.json'), 'utf8'));

// The beta fixture page is served on port 4182 (port 4173 is held by smoke, 4181 by alpha).
const BETA_FIXTURE_PORT = 4182;
const BETA_FIXTURE_URL = `http://localhost:${BETA_FIXTURE_PORT}/`;

let fixtureServer: ChildProcess | null = null;

test.beforeAll(async () => {
  // Ensure the beta fixture server is up on port 4182.
  // When run within the full suite runner, it may already be running;
  // when dispatched alone, spawn it and clean up in afterAll.
  const isUp = await fetch(BETA_FIXTURE_URL).then((r) => r.ok).catch(() => false);
  if (!isUp) {
    const serveScript = join(here, '..', 'fixture-app', 'serve.mjs');
    fixtureServer = spawn('node', [serveScript, 'beta', String(BETA_FIXTURE_PORT)], {
      stdio: 'ignore',
    });

    const deadline = Date.now() + 10_000;
    while (Date.now() < deadline) {
      const ready = await fetch(BETA_FIXTURE_URL).then((r) => r.ok).catch(() => false);
      if (ready) break;
      await new Promise((r) => setTimeout(r, 200));
    }
  }
});

test.afterAll(() => {
  if (fixtureServer) {
    fixtureServer.kill();
    fixtureServer = null;
  }
});

test('R1-05-06 — origin-403 widget toast', async ({ page }) => {
  const tester = await login(credentials.tester.email, credentials.tester.password);

  // 1-2. Pre-authenticate tester and navigate to beta fixture on port 4182.
  // preAuthWidget sets localStorage pointer_token/pointer_user and sessionStorage pointer_visible.
  // Must wait for capture-config before interacting to avoid race conditions.
  await preAuthWidget(page, tester.token, tester.user);
  const captureConfigLoaded = page.waitForResponse((r) => r.url().includes('/capture-config'));
  await page.goto(BETA_FIXTURE_URL);

  const widget = page.locator('pointer-feedback');
  await expect(widget.locator('#pf-add')).toBeVisible({ timeout: 10_000 });
  await captureConfigLoaded;

  // 3. Initiate element pick on .sidebar and submit comment in default pinned environment (Production).
  // Clicking #pf-add flips it to active and attaches the document pick listener.
  await widget.locator('#pf-add').click();
  await expect(widget.locator('#pf-add')).toHaveClass(/active/);
  await page.locator('.sidebar').click({ force: true });

  const popover = page.locator('#pf-popover-host');
  await expect(popover.locator('#pf-comment-text')).toBeVisible({ timeout: 10_000 });
  await popover.locator('#pf-comment-text').fill('origin toast probe');
  await popover.locator('#pf-submit').click();

  // Expected 3: The POST receives a real 403 Forbidden because e2e-beta enforces allowed origins,
  // the page origin is http://localhost:4182, environment is Production, and no matching row exists.
  // The widget displays the shadow-root toast: "Comments are not allowed from this address".
  // The toast removes itself after 2200ms, so polling interval must be <= 250ms.
  await expect
    .poll(
      async () => {
        return await widget.locator('.pf-toast.error').textContent().catch(() => null);
      },
      {
        message: 'Expected 403 error toast "Comments are not allowed from this address"',
        intervals: [250],
        timeout: 10_000,
      },
    )
    .toBe('Comments are not allowed from this address');

  // Popover stays open on 403 rejection so the commenter does not lose their typed text.
  await expect(popover.locator('#pf-comment-text')).toBeVisible();

  // 4. Positive control: switch environment dropdown to 'local' (#pf-env is rendered because
  // the beta fixture sets environment="production", not fixed-environment).
  await widget.locator('#pf-env').selectOption('local');
  await popover.locator('#pf-comment-text').fill('origin toast probe local allowed');
  await popover.locator('#pf-submit').click();

  // Expected 4: Allowed under localhost exemption for Local env -> success toast "Comment added",
  // and popover closes (host is emptied).
  await expect
    .poll(
      async () => {
        return await widget.locator('.pf-toast.success').textContent().catch(() => null);
      },
      {
        message: 'Expected success toast "Comment added"',
        intervals: [250],
        timeout: 10_000,
      },
    )
    .toBe('Comment added');

  await expect(popover).toBeEmpty({ timeout: 10_000 });

  // 5. Staff API check: verify via API that the newest comment on e2e-beta was created with environment === 1 (Local).
  const wsAdmin = await login(credentials.wsAdmin.email, credentials.wsAdmin.password);
  const commentsRes = await get('/api/projects/e2e-beta/comments?pageSize=5', { token: wsAdmin.token });
  const items = commentsRes.items as Array<Record<string, any>>;
  const newestComment = items?.[0];
  expect(newestComment, 'Newest comment on e2e-beta must exist').toBeTruthy();
  expect(newestComment.environment, 'Newest comment environment must be Local (1)').toBe(1);
});
