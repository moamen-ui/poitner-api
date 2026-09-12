// Real Playwright widget automation for R2-06: Secrets / payload advisory flag.
// Scenarios:
// 1. R2-06-01 ⛓ — flag: secret in comment shows badge in widget
// 2. R2-06-04 ⛓ (widget half, steps 3–4) — flag: edit removes secret → pill cleared on reload
// Contract: docs/roadmap/testing/R2-06-tests.md
//
// Fixture: the ALPHA page on 4181 (PORTS.alpha — 4173 is reserved for the smoke fixture R2-05
// needs). It mounts environment="staging", so every test must switch #pf-env to 'local' before
// the list will include comment F (fetchComments filters by environmentInt, element.ts:751).
import { test, expect } from '@playwright/test';
import { spawn, type ChildProcess } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { raw, post, login } from '../scripts/lib/api.mjs';
import { preAuthWidget } from './lib/auth';
import { credentials as loadCredentials } from '../scripts/lib/state.mjs';
import {
  CLEAN_BODY,
  ROTATE_BODY,
  SCRIPT_REPLY,
  ensureFlaggedComment,
} from '../scripts/lib/secrets-flag.mjs';

const here = dirname(fileURLToPath(import.meta.url));
// Read lazily: Playwright evaluates this file to DISCOVER tests, so an eager read on an
// unseeded workspace made `playwright test --list` report 0 tests in 0 files.
const credentials = () => loadCredentials();

const ALPHA_FIXTURE_PORT = 4181; // PORTS.alpha (scripts/lib/constants.mjs)
const ALPHA_FIXTURE_URL = `http://localhost:${ALPHA_FIXTURE_PORT}/`;

// The advisory pill as SHIPPED: class pf-payload-flag (web-component/src/templates.ts:198).
// R2-06-tests.md originally pinned `.pf-pill.pf-flag`; that was a contract-time decision taken
// without checking the widget, and the doc has since been corrected to the shipped name. The
// selector here matches the code.
const FLAG_PILL = '.pf-pill.pf-payload-flag';

const WIDGET_HEADERS = { 'X-Pointer-Client': 'widget' };

let fixtureServer: ChildProcess | null = null;

test.beforeAll(async () => {
  // Ensure the alpha fixture server is up on 4181. When run within the full suite runner another
  // spec may already be serving it; when dispatched alone, spawn it and clean up in afterAll
  // (same pattern as widget/origins.spec.ts for beta on 4182).
  const isUp = await fetch(ALPHA_FIXTURE_URL).then((r) => r.ok).catch(() => false);
  if (!isUp) {
    const serveScript = join(here, '..', 'fixture-app', 'serve.mjs');
    fixtureServer = spawn('node', [serveScript, 'alpha', String(ALPHA_FIXTURE_PORT)], {
      stdio: 'ignore',
    });

    const deadline = Date.now() + 10_000;
    while (Date.now() < deadline) {
      const ready = await fetch(ALPHA_FIXTURE_URL).then((r) => r.ok).catch(() => false);
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

/** Boots the widget on the alpha fixture as `tester`, switched to the Local environment. */
async function openWidgetOnLocal(page: import('@playwright/test').Page, token: string, user: unknown) {
  await preAuthWidget(page, token, user);
  // Registered BEFORE page.goto per the harness widget-boot rule (§9): interacting before
  // capture-config resolves races the console/network patch.
  const captureConfigLoaded = page.waitForResponse((r) => r.url().includes('/capture-config'));
  await page.goto(ALPHA_FIXTURE_URL);

  const widget = page.locator('pointer-feedback');
  await expect(widget.locator('#pf-add')).toBeVisible({ timeout: 10_000 });
  await captureConfigLoaded;

  // F lives on environment 1 (Local) while the fixture mounts environment="staging" — switch
  // first, or the list never contains F.
  await widget.locator('#pf-env').selectOption('local');
  await widget.locator('#pf-toggle').click();
  return widget;
}

test('R2-06-01 — flag: secret in comment shows badge in widget', async ({ page }) => {
  const tester = await login(credentials().tester.email, credentials().tester.password);

  // 1. Comment F exists with the ghp_ canary (created here when the api/cli phases have not run
  // yet — find-or-create keeps this spec correct under the harness phase order).
  const F = await ensureFlaggedComment(tester.token);

  // 2. Widget as QA (pre-auth + reveal), Local environment, sidebar open.
  const widget = await openWidgetOnLocal(page, tester.token, tester.user);

  // 3. The advisory pill on F's card: ⚠ text + the matched pattern in its tooltip.
  const card = widget.locator(`.pf-card[data-id="${F}"]`);
  await expect(card).toBeVisible({ timeout: 10_000 });
  const pill = card.locator(FLAG_PILL);
  await expect(pill).toBeVisible();
  await expect(pill).toContainText('contains a secret/payload?');
  await expect(pill).toHaveAttribute('title', /github_token/);

  // 4. Reply carrying an executable-payload canary → refresh the list via the widget's own
  // control (#pf-refresh).
  await post(`/api/comments/${F}/replies`, { body: SCRIPT_REPLY }, { token: tester.token });
  await widget.locator('#pf-refresh').click();
  await expect(card.locator('.pf-replies')).toContainText('alert(1)');

  // 5. The reply itself is flagged server-side (AC-6 — detector runs on the AddReplyAsync path).
  // R2-06-tests.md expects a reply PILL here too; the shipped widget renders no pill on replies
  // (templates.ts card() draws pf-reply divs with author + text only) — reported as
  // SPEC-CONFLICT, so the reply flag is asserted at the API layer the widget list consumes.
  const detail = await raw('GET', `/api/comments/${F}`, { token: tester.token, headers: WIDGET_HEADERS });
  expect(detail.status).toBe(200);
  const reply = ((detail.data as any)?.replies || []).find((r: any) => String(r.body || '').includes('alert(1)'));
  expect(reply, 'the script-tag reply must be on the comment').toBeTruthy();
  expect(reply.hasPayloadFlag, 'reply must be flagged (detector runs on replies)').toBe(true);
  expect(reply.payloadFlags).toContain('script_tag');
});

test('R2-06-04 — flag: edit removes secret → flag cleared on reload (widget half: steps 3–4)', async ({ page }) => {
  const tester = await login(credentials().tester.email, credentials().tester.password);
  const F = await ensureFlaggedComment(tester.token, { ensureFlagged: false });

  // Step 1's clean edit is repeated here (idempotently) because this half runs in the widget
  // phase — after the api half already did steps 1–2/4 — and step 3 needs F unflagged NOW. Each
  // Playwright test gets a fresh page, so page.goto below is the doc's page.reload() re-boot.
  const clean = await raw('PUT', `/api/comments/${F}`, { token: tester.token, body: { body: CLEAN_BODY } });
  expect(clean.status).toBe(200);

  // 3. Re-boot, re-select local (a reload resets environmentInt to the attribute value), re-open
  // the list → the pill is gone. F is status=2 after R2-06-03 step 4 — the widget list is not
  // status-filtered, so the card is still listed.
  const widget = await openWidgetOnLocal(page, tester.token, tester.user);
  const card = widget.locator(`.pf-card[data-id="${F}"]`);
  await expect(card).toBeVisible({ timeout: 10_000 });
  await expect(card.locator(FLAG_PILL)).toHaveCount(0);

  // 4. Re-add via edit (a NEW canary) → the flag returns: edit recomputes, not create-only. The
  // header-gated GET proves it server-side, and the widget's own refresh re-renders the pill.
  const readd = await raw('PUT', `/api/comments/${F}`, { token: tester.token, body: { body: ROTATE_BODY } });
  expect(readd.status).toBe(200);
  const afterReadd = await raw('GET', `/api/comments/${F}`, { token: tester.token, headers: WIDGET_HEADERS });
  expect(afterReadd.status).toBe(200);
  expect(afterReadd.text).toContain('"hasPayloadFlag":true');

  await widget.locator('#pf-refresh').click();
  await expect(card.locator(FLAG_PILL)).toBeVisible();
});
