#!/usr/bin/env node
// Measures how long the widget takes to become usable on a host page.
//
// The number that matters to a site owner is not "how big is the bundle" but "how long before the
// page is interactive again". The widget marks `pf:boot:start` and `pf:boot:end` around its own
// boot, so this reads those marks from inside the page rather than timing the whole navigation —
// which would mostly measure the host page and the network.
//
// Reported, never enforced by default. A perf number that fails a build on a shared laptop trains
// people to ignore it; PERF_HARD_FAIL=true turns it into a gate once the baseline has settled.
//
//   node scripts/perf-init.mjs --fixture http://localhost:4173 --runs 5 --out e2e/state/perf-init.json
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';

function arg(name, fallback) {
  const i = process.argv.indexOf(`--${name}`);
  return i !== -1 && process.argv[i + 1] ? process.argv[i + 1] : fallback;
}

const fixture = arg('fixture', 'http://localhost:4173');
const runs = Math.max(1, Number(arg('runs', '5')));
const out = resolve(arg('out', 'e2e/state/perf-init.json'));

const GATE = {
  warnMs: 50,
  failMs: 60,
  hardFail: process.env.PERF_HARD_FAIL === 'true',
};

// Playwright is a dev dependency of e2e/, not of the repo root, and this script lives at the root
// by contract. Resolve it from where it actually is rather than requiring the caller to run from a
// particular directory — a bare import here fails with ERR_MODULE_NOT_FOUND and says nothing about
// why.
const require = createRequire(resolve('e2e', 'package.json'));
let chromium;
try {
  const mod = await import(pathToFileURL(require.resolve('@playwright/test')).href);
  // @playwright/test is CommonJS: imported as ESM its exports may sit on the namespace or on
  // `.default`, depending on how the interop resolves. Taking one and hoping gives
  // "Cannot read properties of undefined", which says nothing about the cause.
  chromium = mod.chromium ?? mod.default?.chromium;
  if (!chromium) throw new Error('@playwright/test exposed no chromium export');
} catch (err) {
  console.error(
    `Could not load @playwright/test from e2e/node_modules (${err?.message ?? err}). ` +
      `Run \`npm install\` in e2e/ first.`,
  );
  process.exit(1);
}

const browser = await chromium.launch();
const samples = [];

try {
  for (let i = 0; i < runs; i++) {
    // A fresh context per run: a warm HTTP cache would measure the second load, and the number
    // that matters is a visitor's first one.
    const context = await browser.newContext();
    const page = await context.newPage();
    try {
      await page.goto(fixture, { waitUntil: 'load', timeout: 30_000 });

      // Wait for the widget to finish booting, then read its own marks.
      //
      // 30s, not 15s: booting here is gated on a real round trip to the api container (widget-
      // status, then styles+branding), and in CI this runs as the last of several nightly phases
      // that have already been loading the same container for tens of minutes — 15s left no
      // margin there even though this script reproduces clean on an idle isolated stack every
      // time (verified on an isolated local stack). A slow boot should still get measured and
      // reported (that's the whole point of this script being warn-only), not discarded as "no
      // marks collected" just because the CI runner was busy.
      const ms = await page.evaluate(async () => {
        const deadline = Date.now() + 30_000;
        const read = () => {
          const start = performance.getEntriesByName('pf:boot:start')[0];
          const end = performance.getEntriesByName('pf:boot:end')[0];
          return start && end ? end.startTime - start.startTime : null;
        };
        let value = read();
        while (value === null && Date.now() < deadline) {
          await new Promise((r) => setTimeout(r, 50));
          value = read();
        }
        return value;
      });

      if (typeof ms === 'number' && Number.isFinite(ms)) samples.push(Math.round(ms * 100) / 100);
    } finally {
      await context.close();
    }
  }
} finally {
  await browser.close();
}

if (samples.length === 0) {
  console.error(
    `No boot marks collected from ${fixture} — is the widget loading there? ` +
      `(it marks pf:boot:start / pf:boot:end)`,
  );
  process.exit(1);
}

// Median, not mean: one scheduling hiccup on a shared machine should not move the reported number,
// and a mean over five runs is easily dragged by a single outlier.
const sorted = [...samples].sort((a, b) => a - b);
const metricMs = sorted[Math.floor(sorted.length / 2)];

const payload = { metricMs, runs: samples, gate: GATE };
mkdirSync(dirname(out), { recursive: true });
writeFileSync(out, JSON.stringify(payload, null, 2) + '\n', 'utf8');

const verdict =
  metricMs <= GATE.warnMs
    ? `ok ${metricMs} ms`
    : metricMs <= GATE.failMs
      ? `warn: ${metricMs} ms`
      : GATE.hardFail
        ? `FAIL: ${metricMs} ms (over ${GATE.failMs} ms)`
        : `warn: ${metricMs} ms (hard-fail disabled)`;

console.log(`perf-init ${verdict} — ${samples.length} run(s): ${samples.join(', ')} → ${out}`);

// Non-zero only when the gate is armed AND breached, so the default run reports without blocking.
process.exit(metricMs > GATE.failMs && GATE.hardFail ? 1 : 0);
