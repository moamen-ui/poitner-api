// R2-07's browser half: the docs shell renders correctly, and the footer's Docs link follows
// branding. Static structure lives in docs-site.spec.mjs; these two need a real browser.
import { test, expect } from '@playwright/test';
import { spawn, type ChildProcess } from 'node:child_process';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { get, put, login } from '../scripts/lib/api.mjs';
import { credentials } from '../scripts/lib/state.mjs';
import { PORTS } from '../scripts/lib/constants.mjs';
import { record } from '../scripts/lib/report.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const LANDING_DIR = resolve(here, '..', '..', 'landing');
const PORT = PORTS.landing;
const BASE = `http://localhost:${PORT}`;

let server: ChildProcess | null = null;

test.beforeAll(async () => {
  server = spawn('node', [join(here, '..', 'scripts', 'serve-dir.mjs'), LANDING_DIR, String(PORT)], { stdio: 'ignore' });

  const deadline = Date.now() + 10_000;
  let up = false;
  while (Date.now() < deadline) {
    up = await fetch(`${BASE}/docs/`).then((r) => r.ok).catch(() => false);
    if (up) break;
    await new Promise((r) => setTimeout(r, 200));
  }
  // Fail here rather than let every assertion below time out against nothing.
  if (!up) throw new Error(`landing server never came up on ${BASE} — is port ${PORT} in use?`);
});

test.afterAll(() => {
  server?.kill();
  server = null;
});

test('R2-07-06 — dark mode, RTL and logical properties', async ({ page }) => {
  const start = Date.now();

  await page.emulateMedia({ colorScheme: 'light' });
  await page.goto(`${BASE}/docs/`);
  const light = await page.evaluate(() => getComputedStyle(document.body).backgroundColor);

  await page.emulateMedia({ colorScheme: 'dark' });
  await page.reload();
  const dark = await page.evaluate(() => getComputedStyle(document.body).backgroundColor);

  // Both painted, and different. A transparent body would borrow whatever the host paints behind
  // it, which is how a "dark mode" ships looking fine locally and white in production.
  expect(light).not.toBe('rgba(0, 0, 0, 0)');
  expect(dark).not.toBe('rgba(0, 0, 0, 0)');
  expect(light).not.toBe(dark);

  // RTL must not produce a horizontally scrolling page — the Arabic landing copy sets dir="rtl",
  // and a docs page that overflows sideways is unreadable on a phone.
  await page.evaluate(() => document.documentElement.setAttribute('dir', 'rtl'));
  const overflow = await page.evaluate(() =>
    document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow, 'the page must not scroll horizontally in RTL').toBeLessThanOrEqual(1);

  // Logical properties, not physical ones: padding-inline-start resolves on BOTH sides, which
  // padding-left cannot do. This is what makes one stylesheet serve both directions.
  const rtlPad = await page.evaluate(() =>
    getComputedStyle(document.querySelector('.nav .wrap')!).paddingInlineStart);
  await page.evaluate(() => document.documentElement.setAttribute('dir', 'ltr'));
  const ltrPad = await page.evaluate(() =>
    getComputedStyle(document.querySelector('.nav .wrap')!).paddingInlineStart);

  expect(parseFloat(rtlPad)).toBeGreaterThan(0);
  expect(parseFloat(ltrPad)).toBeGreaterThan(0);

  record({ id: 'R2-07-06', tier: 'nightly', layer: 'widget', role: '—', result: 'PASS',
    ms: Date.now() - start, detail: `light=${light} dark=${dark} rtlOverflow=${overflow}` });
});

test('R2-07-05 ⛓ — the footer Docs link follows /api/branding', async ({ browser }) => {
  test.skip(process.env.TIER !== 'nightly', 'nightly only — mutates global branding');
  const start = Date.now();

  const sa = await login(credentials().superAdmin.email, credentials().superAdmin.password);
  const original = await get('/api/admin/branding', { token: sa.token });

  const hrefIn = async (expected?: string, block?: (ctx: any) => Promise<void>) => {
    // A NEW context each time: branding is fetched once per page load, so reusing one would read
    // a value cached before the change.
    const ctx = await browser.newContext();
    const p = await ctx.newPage();
    // Point the page at THIS server. The landing page defaults to the hosted API, so without this
    // the test would assert against production branding and could never observe a local change.
    await p.addInitScript(() => { (window as any).__POINTER_API__ = 'http://localhost:8090'; });
    if (block) await block(p);
    await p.goto(`${BASE}/`);

    // Poll rather than sleep. applyBrand runs after an async /api/branding fetch, so a fixed wait
    // is a race that passes on a fast machine and fails on a loaded one.
    if (expected) {
      await expect
        .poll(async () => p.locator('[data-brand-docs]').first().getAttribute('href'), { timeout: 10_000 })
        .toBe(expected);
    } else {
      await p.waitForTimeout(1000);
    }

    const href = await p.locator('[data-brand-docs]').first().getAttribute('href');
    await ctx.close();
    return href ?? '';
  };

  try {
    const before = await hrefIn();
    expect(before, 'the default Docs link must point somewhere').not.toBe('');

    await put('/api/admin/branding', { ...original, urls: { ...original.urls, docs: 'https://docs.selfhost.test' } }, { token: sa.token });
    expect(await hrefIn('https://docs.selfhost.test')).toBe('https://docs.selfhost.test');

    // Branding unreachable → the BUNDLED default, never empty and never the stale override. A
    // self-hoster whose API hiccups must still get working documentation.
    const blocked = await hrefIn(undefined, async (p) => { await p.route('**/api/branding', (r) => r.abort()); });
    expect(blocked).not.toBe('');
    expect(blocked).not.toBe('https://docs.selfhost.test');
  } finally {
    await put('/api/admin/branding', original, { token: sa.token });
  }

  const restored = await get('/api/admin/branding', { token: sa.token });
  expect(restored.urls?.docs).toBe(original.urls?.docs);

  record({ id: 'R2-07-05', tier: 'nightly', layer: 'widget', role: 'SA', result: 'PASS',
    ms: Date.now() - start, detail: 'docs href follows branding, falls back when unreachable' });
});
