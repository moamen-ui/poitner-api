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
import { expect, test, type Page } from '@playwright/test';
import { BASE_URL } from '../scripts/lib/api.mjs';
import { PORTS } from '../scripts/lib/constants.mjs';
import { record } from '../scripts/lib/report.mjs';
import { servePinnedPage } from './lib/pinned-page.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const e2eRoot = resolve(here, '..');

// Diagnostics for the two specs that fail deterministically in CI but not on an isolated local
// stack (R3-03-04, R3-03-07): neither captured any browser-side signal, so a CI failure says only
// "never became visible" / "no boot marks" and nothing about why. These helpers collect what the
// browser saw and print a compact block to stdout ON FAILURE ONLY — Playwright's line reporter
// prints a failed test's stdout in the CI log, so this is enough to explain the next run without
// changing what passes or fails today.
interface PageDiagnostics {
  consoleMessages: { type: string; text: string }[];
  pageErrors: string[];
  failedRequests: { url: string; failure: string }[];
  badResponses: { url: string; status: number }[];
  getDocCspHeader: () => string | null;
  getWidgetScriptStatus: () => { url: string; status: number } | null;
}

function attachPageDiagnostics(page: Page): PageDiagnostics {
  const consoleMessages: { type: string; text: string }[] = [];
  const pageErrors: string[] = [];
  const failedRequests: { url: string; failure: string }[] = [];
  const badResponses: { url: string; status: number }[] = [];
  let docCspHeader: string | null = null;
  let widgetScriptStatus: { url: string; status: number } | null = null;

  page.on('console', (m) => consoleMessages.push({ type: m.type(), text: m.text() }));
  page.on('pageerror', (err) => pageErrors.push(err?.message ?? String(err)));
  page.on('requestfailed', (req) => {
    failedRequests.push({ url: req.url(), failure: req.failure()?.errorText ?? 'unknown' });
  });
  page.on('response', (res) => {
    const status = res.status();
    if (status >= 400) badResponses.push({ url: res.url(), status });
    // The document's own response carries the CSP header a meta tag would never show for a
    // header-only policy (as the csp-nonce fixture uses) — capture the first one seen.
    if (docCspHeader === null && res.request().resourceType() === 'document') {
      docCspHeader = res.headers()['content-security-policy'] ?? null;
    }
    if (widgetScriptStatus === null && /\/(widget|embed)\.js(\?|$)/.test(res.url())) {
      widgetScriptStatus = { url: res.url(), status };
    }
  });

  return {
    consoleMessages,
    pageErrors,
    failedRequests,
    badResponses,
    getDocCspHeader: () => docCspHeader,
    getWidgetScriptStatus: () => widgetScriptStatus,
  };
}

async function dumpWidgetDiagnostics(page: Page, diag: PageDiagnostics, label: string): Promise<void> {
  const hostState = await page
    .evaluate(() => {
      const host = document.querySelector('pointer-feedback');
      const metaCsp =
        document.querySelector('meta[http-equiv="Content-Security-Policy"]')?.getAttribute('content') ?? null;
      return {
        metaCsp,
        hostExists: !!host,
        project: host?.getAttribute('project') ?? null,
        server: host?.getAttribute('server') ?? null,
        shadowRootChildCount: host?.shadowRoot?.childNodes.length ?? null,
        shadowRootInnerHTMLLength: host?.shadowRoot?.innerHTML.length ?? null,
      };
    })
    .catch((err) => ({ evalError: String(err) }));

  const consoleErrorsAndWarnings = diag.consoleMessages
    .filter((m) => m.type === 'error' || m.type === 'warning')
    .slice(0, 40);

  console.log(
    [
      `---- ${label} diagnostics (failure) ----`,
      `page.url(): ${page.url()}`,
      `CSP meta tag content: ${JSON.stringify((hostState as { metaCsp?: string | null }).metaCsp ?? null)}`,
      `CSP header (document response): ${JSON.stringify(diag.getDocCspHeader())}`,
      `widget/loader script response: ${JSON.stringify(diag.getWidgetScriptStatus())}`,
      `pointer-feedback host: ${JSON.stringify(hostState)}`,
      `failed requests (${diag.failedRequests.length}): ${JSON.stringify(diag.failedRequests)}`,
      `>=400 responses (${diag.badResponses.length}): ${JSON.stringify(diag.badResponses)}`,
      `console errors/warnings (${consoleErrorsAndWarnings.length} of ${diag.consoleMessages.length} total, capped at 40): ${JSON.stringify(consoleErrorsAndWarnings)}`,
      `page errors (${diag.pageErrors.length}): ${JSON.stringify(diag.pageErrors)}`,
      `---- end ${label} diagnostics ----`,
    ].join('\n'),
  );
}

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

    // Attached before any navigation so nothing that happens during boot is missed. Cheap when the
    // test passes (nothing reads these arrays); on failure they are the whole point.
    const diag = attachPageDiagnostics(page);

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
    // 30s, not 15s: the widget's boot here is gated on a real round trip
    // (_checkWidgetActive()'s GET .../widget-status, then Promise.all([stylesReady, loadBranding]))
    // against the SAME api container every earlier nightly phase (reset/seed/probe/api/docs/cli)
    // has been hammering for tens of minutes by the time this phase runs — 15s had no margin left
    // on a loaded CI runner even though this reproduces clean, every time, on an idle isolated
    // stack (verified on an isolated local stack: 7/7 passes with the shorter timeout too, so this
    // is headroom for CI load, not evidence the wait itself was ever the wrong mechanism).
    try {
      await expect(launcher.or(addBtn).first()).toBeVisible({ timeout: 30_000 });
      if (await launcher.isVisible().catch(() => false)) await launcher.click();

      await expect(widget.locator('#fbk-add')).toBeVisible({ timeout: 30_000 });
    } catch (err) {
      await dumpWidgetDiagnostics(page, diag, 'R3-03-04');
      throw err;
    }

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

    try {
      execFileSync(
        'node',
        [join('scripts', 'perf-init.mjs'), '--fixture', fixtureUrl, '--runs', '5', '--out', outPath],
        { cwd: repoRoot, encoding: 'utf8', timeout: 180_000, stdio: 'pipe' },
      );
    } catch (err) {
      // perf-init.mjs prints its own diagnostics (console errors, failed requests, >=400
      // responses, whether the host element/marks exist) to stderr right before it exits
      // non-zero. execFileSync captures that into the thrown error rather than letting it reach
      // the CI log on its own — surface it here so the failure explains itself.
      const e = err as { stdout?: string; stderr?: string; status?: number | null };
      console.log(
        [
          '---- R3-03-07 perf-init.mjs diagnostics (failure) ----',
          `exit status: ${e.status ?? 'unknown'}`,
          `stdout:\n${e.stdout ?? '(none)'}`,
          `stderr:\n${e.stderr ?? '(none)'}`,
          '---- end R3-03-07 perf-init.mjs diagnostics ----',
        ].join('\n'),
      );
      throw err;
    }

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
