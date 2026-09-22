// R3-03 widget layer: pinning, integrity, and surviving a strict CSP.
//
// These cover the ways a widget install can be made durable against the server changing underneath
// it, and the ways it can fail. All four questions are about the BROWSER's behaviour — whether it
// accepts a pinned script, rejects a tampered one, and renders under a policy that forbids inline
// styles — so none of them can be answered by asking the API.
import { execFileSync, spawn, type ChildProcess } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, test } from '@playwright/test';
import { BASE_URL } from '../scripts/lib/api.mjs';
import { PORTS } from '../scripts/lib/constants.mjs';
import { record } from '../scripts/lib/report.mjs';
import { servePinnedPage } from './lib/pinned-page.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const e2eRoot = resolve(here, '..');

test.describe.configure({ timeout: 180_000 });

// A real server is started per scenario on this port. It must not collide with the other fixture
// ports in constants.mjs.
const PINNED_PORT = 4190;

interface Manifest {
  hash: string;
  files: Record<string, { integrity: string }>;
  retained: Array<{ hash: string }>;
}

let manifest: Manifest;

test.beforeAll(async () => {
  const res = await fetch(`${BASE_URL}/pointer.version.json`);
  expect(res.status, '/pointer.version.json must be served').toBe(200);
  manifest = (await res.json()) as Manifest;
});

test('R3-03-01 — widget-pinned-sri-loads', async ({ page }) => {
  test.skip(process.env.TIER === 'pr', 'nightly tier only');
  const start = Date.now();

  const H = manifest.hash;
  expect(H, 'the manifest must name a 12-hex build').toMatch(/^[0-9a-f]{12}$/);
  const integrity = manifest.files['widget.js'].integrity;
  expect(integrity, 'the manifest must publish an SRI hash for widget.js').toMatch(/^sha384-/);

  // Membership, never position: `retained` is ordered by the server and asserting retained[0]
  // would break on a reorder that changes nothing about what is retained.
  expect(manifest.retained.some((r) => r.hash === H), 'the current build must be retained').toBe(true);

  const consoleErrors: string[] = [];
  page.on('console', (m) => {
    if (m.type() === 'error') consoleErrors.push(m.text());
  });

  const host = await servePinnedPage(PINNED_PORT, { v: H, integrity });
  let js;
  try {
    const jsResponse = page.waitForResponse((r) => r.url().includes('/widget.js?v='));
    await page.goto(host.url);
    js = await jsResponse;

  expect(js.status(), 'the pinned bundle must be served').toBe(200);
  expect(js.headers()['cache-control']).toBe('public, max-age=31536000, immutable');

  // The real proof: the browser accepted the bytes against the published integrity hash and ran
  // them. A shadow root only exists if the script executed.
  const hasShadow = await page.evaluate(
    () => !!document.querySelector('pointer-feedback')?.shadowRoot,
  );
  expect(hasShadow, 'the pinned widget must have booted').toBe(true);

  // Nothing about integrity may appear in the console — a browser that blocked the script logs
  // there, and a silently-blocked script is exactly the failure this scenario exists to catch.
  expect(
    consoleErrors.filter((t) => /integrity/i.test(t)),
    'a correctly pinned script must raise no integrity error',
  ).toHaveLength(0);

    record({
      id: 'R3-03-01', tier: 'nightly', layer: 'widget', role: '—', result: 'PASS', ms: Date.now() - start,
      detail: `pinned v=${H} with ${integrity.slice(0, 20)}… loaded and booted`,
    });
  } finally {
    await host.stop();
  }
});

test('R3-03-02 ⛓ — widget-pinned-older-build', async ({ page }) => {
  test.skip(process.env.TIER === 'pr', 'nightly tier only');
  const start = Date.now();

  // A build that is retained but no longer current — the case a pin exists FOR. A page pinned
  // before the last deploy must keep working, byte for byte, rather than silently drifting onto
  // the new build.
  const older = manifest.retained.find((r) => r.hash !== manifest.hash);
  test.skip(!older, 'the server retains only one build — nothing older to pin to');

  const res = await fetch(`${BASE_URL}/pointer.version.json`);
  const current = (await res.json()) as Manifest;
  const olderEntry = (current.retained as Array<{ hash: string; files?: Record<string, { integrity: string }> }>)
    .find((r) => r.hash === older!.hash);
  const olderIntegrity = olderEntry?.files?.['widget.js']?.integrity;
  expect(olderIntegrity, 'a retained build must publish its own integrity hash').toMatch(/^sha384-/);

  const consoleErrors: string[] = [];
  page.on('console', (m) => {
    if (m.type() === 'error') consoleErrors.push(m.text());
  });

  const host = await servePinnedPage(PINNED_PORT, { v: older!.hash, integrity: olderIntegrity });
  let js;
  try {
    const jsResponse = page.waitForResponse((r) => r.url().includes('/widget.js?v='));
    await page.goto(host.url);
    js = await jsResponse;

  expect(js.status(), 'a retained build must still be served').toBe(200);
  expect(js.headers()['cache-control']).toBe('public, max-age=31536000, immutable');

  const hasShadow = await page.evaluate(
    () => !!document.querySelector('pointer-feedback')?.shadowRoot,
  );
  expect(hasShadow, 'the older pinned build must still boot').toBe(true);
  expect(consoleErrors.filter((t) => /integrity/i.test(t))).toHaveLength(0);

    record({
      id: 'R3-03-02', tier: 'nightly', layer: 'widget', role: '—', result: 'PASS', ms: Date.now() - start,
      detail: `retained build ${older!.hash} still loads and boots under its own SRI`,
    });
  } finally {
    await host.stop();
  }
});

test('R3-03-03 ⛓ — widget-pinned-unknown-404', async ({ page }) => {
  test.skip(process.env.TIER === 'pr', 'nightly tier only');
  const start = Date.now();

  const H = manifest.hash;
  const consoleMessages: string[] = [];
  page.on('console', (m) => consoleMessages.push(m.text()));

  // Integrity is irrelevant here: the request dies at 404 long before the browser checks it. It is
  // passed anyway so the page is otherwise identical to the working one.
  const host = await servePinnedPage(PINNED_PORT, {
    v: '000000000000',
    integrity: manifest.files['widget.js'].integrity,
  });
  let js;
  try {
    const jsResponse = page.waitForResponse((r) => r.url().includes('/widget.js?v=000000000000'));
    await page.goto(host.url);
    js = await jsResponse;

  expect(js.status(), 'an unknown pin must 404, never fall back to the current build').toBe(404);
  expect(
    js.headers()['x-pointer-widget-version-mismatch'],
    'the 404 must name the build the server actually has, so a client can recover',
  ).toBe(H);

  // Nothing booted, and the page said why. `!!` because returning the ShadowRoot itself would be
  // serialised across the boundary as an empty object — truthy, and the assertion would pass for a
  // widget that never loaded.
  const hasShadow = await page.evaluate(
    () => !!document.querySelector('pointer-feedback')?.shadowRoot,
  );
  expect(hasShadow, 'a 404 pin must leave the widget unbooted').toBe(false);
  expect(await page.locator('pointer-feedback #fbk-add').count()).toBe(0);

  // The failure must read as "that build does not exist", not as a tampered file — an integrity
  // message here would send someone hunting a supply-chain problem they do not have.
  expect(
    consoleMessages.filter((t) => /integrity/i.test(t)),
    'a 404 must not surface as an integrity error',
  ).toHaveLength(0);

    record({
      id: 'R3-03-03', tier: 'nightly', layer: 'widget', role: '—', result: 'PASS', ms: Date.now() - start,
      detail: `unknown pin → 404 + mismatch=${H}; widget did not boot; no integrity noise`,
    });
  } finally {
    await host.stop();
  }
});

test('R3-03-04 — widget-nonce-csp-styles', async ({ page }) => {
  test.skip(process.env.TIER === 'pr', 'nightly tier only');
  const start = Date.now();

  let server: ChildProcess | null = null;
  try {
    const port = PORTS.cspNonce ?? 4176;
    const url = `http://localhost:${port}/`;

    const isUp = await fetch(url).then((r) => r.ok).catch(() => false);
    if (!isUp) {
      server = spawn(
        'node',
        [join(e2eRoot, 'fixture-app', 'csp-nonce', 'serve.mjs'), String(port), BASE_URL],
        { stdio: 'ignore' },
      );
      const deadline = Date.now() + 10_000;
      let ready = false;
      while (Date.now() < deadline) {
        ready = await fetch(url).then((r) => r.ok).catch(() => false);
        if (ready) break;
        await new Promise((r) => setTimeout(r, 200));
      }
      expect(ready, `csp-nonce fixture never came up on :${port}`).toBe(true);
    }

    // The stylesheet, NOT capture-config. fetchCaptureConfig runs only from init(), which _boot()
    // calls only when there is a token — this page is a deliberately unauthenticated boot, so a
    // capture-config waiter would hang until timeout and look like a CSP failure.
    const cssResponse = page.waitForResponse((r) => r.url().includes('/widget.css'));
    await page.goto(url);
    await cssResponse;

    const widget = page.locator('pointer-feedback');

    // Wait for the widget to settle into EITHER state, then reveal if it is collapsed.
    //
    // `count()` alone is a point-in-time read that does not wait: called before the widget has
    // rendered it returns 0, the click is skipped, and the assertion below then fails on a
    // toolbar nobody ever opened. Waiting for whichever element appears first removes the race
    // without assuming which state this page produces.
    const launcher = widget.locator('#fbk-launcher');
    const addBtn = widget.locator('#fbk-add');
    await expect(launcher.or(addBtn).first()).toBeVisible({ timeout: 15_000 });
    if (await launcher.isVisible().catch(() => false)) await launcher.click();

    await expect(widget.locator('#fbk-add')).toBeVisible({ timeout: 15_000 });

    // Styles must have actually APPLIED. Under a nonce CSP with no 'unsafe-inline', a widget that
    // injected a <style> would render unstyled while every element still existed — so presence
    // proves nothing and the computed value is the only honest check.
    const styling = await page.evaluate(() => {
      const root = document.querySelector('pointer-feedback')?.shadowRoot;
      if (!root) return null;
      const link = root.querySelector('link[href*="widget.css"]') as HTMLLinkElement | null;
      const add = root.querySelector('#fbk-add') as HTMLElement | null;
      return {
        linkPresent: !!link,
        // A cross-origin stylesheet exposes no cssRules, so `sheet` being non-null is as far as
        // this can go from script — the computed style below is what proves it took effect.
        sheetAttached: !!link?.sheet,
        adopted: root.adoptedStyleSheets?.length ?? 0,
        addCursor: add ? getComputedStyle(add).cursor : null,
      };
    });

    expect(styling, 'the widget must have a shadow root under a strict CSP').not.toBeNull();
    expect(styling!.linkPresent || styling!.adopted > 0, 'the widget must load its CSS as a real stylesheet').toBe(true);
    expect(styling!.addCursor, 'widget styles must have applied, not merely loaded').toBe('pointer');

    record({
      id: 'R3-03-04', tier: 'nightly', layer: 'widget', role: '—', result: 'PASS', ms: Date.now() - start,
      detail: `booted under nonce CSP; stylesheet ${styling!.linkPresent ? 'linked' : 'adopted'}; styles applied`,
    });
  } finally {
    if (server) server.kill();
  }
});

test('R3-03-07 — perf-init long-task metric (warn-only)', async () => {
  test.skip(process.env.TIER === 'pr', 'nightly tier only');
  const start = Date.now();

  const repoRoot = resolve(e2eRoot, '..');
  const outPath = join(e2eRoot, 'state', 'perf-init.json');
  const fixtureUrl = `http://localhost:${PORTS.smoke ?? 4173}/`;

  // The smoke fixture is the phase standard and is already serving during the widget phase; start
  // one only if it is not.
  let server: ChildProcess | null = null;
  try {
    const isUp = await fetch(fixtureUrl).then((r) => r.ok).catch(() => false);
    if (!isUp) {
      server = spawn('node', [join(e2eRoot, 'fixture-app', 'serve.mjs'), 'smoke', String(PORTS.smoke ?? 4173)], {
        stdio: 'ignore',
      });
      const deadline = Date.now() + 10_000;
      let ready = false;
      while (Date.now() < deadline) {
        ready = await fetch(fixtureUrl).then((r) => r.ok).catch(() => false);
        if (ready) break;
        await new Promise((r) => setTimeout(r, 200));
      }
      expect(ready, `smoke fixture never came up on ${fixtureUrl}`).toBe(true);
    }

    execFileSync(
      'node',
      [join('scripts', 'perf-init.mjs'), '--fixture', fixtureUrl, '--runs', '5', '--out', outPath],
      { cwd: repoRoot, encoding: 'utf8', timeout: 180_000, stdio: 'pipe' },
    );

    const report = JSON.parse(readFileSync(outPath, 'utf8'));
    expect(Number.isFinite(report.metricMs), 'perf-init must produce a finite metric').toBe(true);
    expect(Array.isArray(report.runs) && report.runs.length > 0, 'it must record its samples').toBe(true);
    expect(report.gate).toMatchObject({ warnMs: 50, failMs: 60 });

    const { metricMs } = report;
    const hardFail = report.gate.hardFail === true;

    // Warn-only by default, and that is a decision rather than an oversight: this measures a
    // browser on whatever machine happens to run it, so a hard gate would fail for reasons that
    // have nothing to do with the widget and would train everyone to ignore the number. The
    // threshold is recorded on every run; PERF_HARD_FAIL arms it once a baseline has settled.
    let detail: string;
    if (metricMs <= report.gate.warnMs) {
      detail = `metric ${metricMs} ms`;
    } else if (metricMs <= report.gate.failMs) {
      detail = `warn: ${metricMs} ms`;
    } else if (hardFail) {
      expect(metricMs, `widget boot took ${metricMs} ms, over the ${report.gate.failMs} ms gate`)
        .toBeLessThanOrEqual(report.gate.failMs);
      detail = `fail: ${metricMs} ms`;
    } else {
      detail = `warn: ${metricMs} ms (hard-fail disabled until the PERF_HARD_FAIL flip)`;
    }

    record({
      id: 'R3-03-07', tier: 'nightly', layer: 'widget', role: '—', result: 'PASS',
      ms: Date.now() - start, detail,
    });
  } finally {
    if (server) server.kill();
  }
});
