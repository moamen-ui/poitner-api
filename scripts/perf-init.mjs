#!/usr/bin/env node
// Boot-time perf guard (§D.4):
// Measures widget boot long-task duration using Playwright on e2e/fixture-app/smoke.
// Runs 5 times, taking median of (with widget - without widget).
import { createServer } from 'node:http';
import { readFileSync } from 'node:fs';
import { readFile } from 'node:fs/promises';
import { resolve, dirname, join, extname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const ROOT = resolve(here, '..');
const SMOKE_DIR = resolve(ROOT, 'e2e/fixture-app/smoke');
const WWWROOT = resolve(ROOT, 'API/wwwroot');

// Resolve playwright from e2e/node_modules
const playwrightModulePath = pathToFileURL(
  resolve(ROOT, 'e2e/node_modules/@playwright/test/index.mjs')
).href;
const { chromium } = await import(playwrightModulePath);

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.js': 'application/javascript; charset=utf-8',
};

// Start local static server for smoke fixture
const server = createServer(async (req, res) => {
  const pathname = new URL(req.url, 'http://localhost').pathname;
  const relPath = pathname === '/' ? 'index.html' : pathname.replace(/^\//, '');
  const filePath = resolve(SMOKE_DIR, relPath);
  if (!filePath.startsWith(SMOKE_DIR)) {
    res.writeHead(403);
    return res.end('Forbidden');
  }
  try {
    const data = await readFile(filePath);
    res.writeHead(200, { 'Content-Type': MIME[extname(filePath)] || 'application/octet-stream' });
    res.end(data);
  } catch {
    res.writeHead(404);
    res.end('Not Found');
  }
});

await new Promise((res) => server.listen(0, '127.0.0.1', res));
const port = server.address().port;
const fixtureUrl = `http://127.0.0.1:${port}/`;

console.log(`Perf guard: fixture server running at ${fixtureUrl}`);

const browser = await chromium.launch({
  headless: true,
  args: ['--enable-precise-memory-info'],
});

const pointerJsContent = readFileSync(resolve(WWWROOT, 'pointer.js'), 'utf8');
const pointerCssContent = readFileSync(resolve(WWWROOT, 'pointer.css'), 'utf8');

async function measureRun(withWidget) {
  const context = await browser.newContext();
  const page = await context.newPage();

  // Route mocking for API and widget
  await page.route('http://localhost:8090/**', async (route) => {
    const url = route.request().url();
    if (url.includes('/pointer.js')) {
      if (!withWidget) {
        return route.abort();
      }
      return route.fulfill({
        status: 200,
        contentType: 'application/javascript; charset=utf-8',
        body: pointerJsContent,
      });
    }
    if (url.includes('/pointer.css')) {
      return route.fulfill({
        status: 200,
        contentType: 'text/css; charset=utf-8',
        body: pointerCssContent,
      });
    }
    if (url.includes('/api/public/projects/') && url.includes('/widget-status')) {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ isSuccess: true, data: { active: true } }),
      });
    }
    if (url.includes('/api/branding')) {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ isSuccess: true, data: { productName: 'Pointer' } }),
      });
    }
    if (url.includes('/api/projects/') && url.includes('/comments')) {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ isSuccess: true, data: { items: [], totalCount: 0 } }),
      });
    }
    return route.fulfill({ status: 200, body: '' });
  });

  // Observe longtask entries
  await page.addInitScript(() => {
    window.__longTasks = [];
    try {
      const obs = new PerformanceObserver((list) => {
        for (const entry of list.getEntries()) {
          window.__longTasks.push({
            name: entry.name,
            startTime: entry.startTime,
            duration: entry.duration,
          });
        }
      });
      obs.observe({ entryTypes: ['longtask'] });
    } catch {
      // Not supported in this engine
    }
  });

  await page.goto(fixtureUrl, { waitUntil: 'load' });

  let longTaskTotal = 0;
  if (withWidget) {
    try {
      await page.waitForFunction(
        () => performance.getEntriesByName('pf:boot:end').length > 0,
        null,
        { timeout: 5000 }
      );
    } catch {
      // Timeout waiting for mark; continue
    }

    longTaskTotal = await page.evaluate(() => {
      const startEntries = performance.getEntriesByName('pf:boot:start');
      const endEntries = performance.getEntriesByName('pf:boot:end');
      const start = startEntries.length > 0 ? startEntries[0].startTime : 0;
      const end = endEntries.length > 0 ? endEntries[0].startTime : performance.now();

      const tasks = window.__longTasks || [];
      return tasks
        .filter((t) => t.startTime + t.duration >= start && t.startTime <= end)
        .reduce((sum, t) => sum + t.duration, 0);
    });
  } else {
    // Settle baseline for 300ms
    await page.waitForTimeout(300);
    longTaskTotal = await page.evaluate(() => {
      const tasks = window.__longTasks || [];
      return tasks.reduce((sum, t) => sum + t.duration, 0);
    });
  }

  await context.close();
  return longTaskTotal;
}

try {
  const NUM_RUNS = 5;
  const deltas = [];

  console.log(`Measuring boot-time overhead over ${NUM_RUNS} runs...`);
  for (let i = 1; i <= NUM_RUNS; i++) {
    const withoutVal = await measureRun(false);
    const withVal = await measureRun(true);
    const delta = Math.max(0, withVal - withoutVal);
    deltas.push(delta);
    console.log(`  Run ${i}: with=${withVal.toFixed(1)}ms, without=${withoutVal.toFixed(1)}ms, delta=${delta.toFixed(1)}ms`);
  }

  deltas.sort((a, b) => a - b);
  const median = Math.round(deltas[Math.floor(deltas.length / 2)] * 10) / 10;
  console.log(`Boot-time long-task overhead median: ${median}ms (runs: ${deltas.map(d => d.toFixed(1)).join(', ')})`);

  // Write to GITHUB_STEP_SUMMARY if available
  if (process.env.GITHUB_STEP_SUMMARY) {
    const { appendFileSync } = await import('node:fs');
    appendFileSync(
      process.env.GITHUB_STEP_SUMMARY,
      `### Widget Boot-Time Perf Guard\n- **Median overhead**: ${median} ms\n- **Runs**: ${deltas.map(d => d.toFixed(1) + 'ms').join(', ')}\n- **Threshold**: 60 ms hard cap, 50-60 ms warn band\n\n`
    );
  }

  const hardFail = process.env.PERF_HARD_FAIL === 'true';

  if (median > 60) {
    if (hardFail) {
      console.error(`::error::Boot-time long-task overhead ${median}ms exceeds 60ms limit! (PERF_HARD_FAIL=true)`);
      process.exit(1);
    } else {
      console.warn(`::warning::Boot-time long-task overhead ${median}ms exceeds 60ms limit (warn-only mode)`);
    }
  } else if (median >= 50) {
    console.warn(`::warning::Boot-time long-task overhead ${median}ms is in the 50-60ms warning band`);
  } else {
    console.log(`✔ Boot-time overhead ${median}ms is within the safe budget (<50ms).`);
  }
} finally {
  await browser.close();
  server.close();
}
