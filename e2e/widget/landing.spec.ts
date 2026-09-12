// Real Playwright browser automation for R3-05: Public "Data & self-hosting" page.
// Scenarios:
// - R3-05-01: landing-data-page-links
// - R3-05-03: dark mode + 390 px + section budget
// Contract: docs/roadmap/execution/R3-05-privacy-page.md
// Test spec: docs/roadmap/testing/R3-05-tests.md
import { test, expect } from '@playwright/test';
import { spawn, type ChildProcess } from 'node:child_process';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { PORTS } from '../scripts/lib/constants.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(here, '..', '..');
const LANDING_URL = `http://localhost:${PORTS.landing}/`;

// Preconditions flag per R3-05-tests.md line 11:
// R3-04 is unmerged; flip to true when R3-04 merge commit lands.
export const R3_04_MERGED = false;

let serverProcess: ChildProcess | null = null;

test.beforeAll(async () => {
  const isUp = await fetch(LANDING_URL).then((r) => r.ok).catch(() => false);
  if (!isUp) {
    const serveScript = join(here, '..', 'scripts', 'serve-dir.mjs');
    serverProcess = spawn('node', [serveScript, 'landing', String(PORTS.landing)], {
      cwd: repoRoot,
      stdio: 'ignore',
    });

    const deadline = Date.now() + 10_000;
    while (Date.now() < deadline) {
      const ready = await fetch(LANDING_URL).then((r) => r.ok).catch(() => false);
      if (ready) break;
      await new Promise((r) => setTimeout(r, 200));
    }
  }
});

test.afterAll(() => {
  if (serverProcess) {
    serverProcess.kill();
  }
});

test.describe('R3-05: Data & self-hosting page', () => {

  test('R3-05-01: landing-data-page-links', async ({ page }) => {
    // 2. Navigate to root landing page
    await page.goto(LANDING_URL);
    const footerLink = page.locator('footer a[href="/data.html"]');
    await expect(footerLink).toBeVisible();
    await expect(footerLink).toHaveText('Data & self-hosting');
    await expect(footerLink).toHaveAttribute('data-i18n', 'foot.data');

    // Sits next to /privacy.html
    const privacyLink = page.locator('footer a[href="/privacy.html"]');
    await expect(privacyLink).toBeVisible();

    // 3. Click -> navigates to /data.html
    await footerLink.click();
    await page.waitForURL('**/data.html');
    expect(await page.title()).toBe('Data & self-hosting — Pointer');
    expect(await page.locator('a[href="/privacy.html"]').count()).toBeGreaterThanOrEqual(1);

    // 4. Required honest strings
    const bodyText = await page.innerText('body');
    const expectedDataStrings = [
      'never captured',
      'self-hosted',
      ...(R3_04_MERGED ? ['data-snapshot-mask'] : []),
    ];
    for (const str of expectedDataStrings) {
      expect(bodyText).toContain(str);
    }

    // 5. Soft-delete honesty
    expect(bodyText).toMatch(/Deleting a comment or project hides it everywhere immediately/);
    expect(bodyText).toMatch(/contact us for a manual purge/);
    expect(bodyText).not.toMatch(/deletes everything/);

    // 6. Shared shell
    await expect(page.locator('header.top .brand')).toBeVisible();
    await expect(page.locator('footer.bottom .wrap')).toBeVisible();
    await page.goto(new URL('/privacy.html', LANDING_URL).toString());
    await expect(page.locator('header.top .brand')).toBeVisible();
    await expect(page.locator('footer.bottom .wrap')).toBeVisible();

    // 7. TODO scan on /data.html
    await page.goto(new URL('/data.html', LANDING_URL).toString());
    const dataContent = await page.content();
    const todoMatches = dataContent.match(/TODO\(founder\)/g) || [];
    expect(todoMatches.length).toBe(1);
    expect(dataContent).toMatch(/TODO\(founder\): region\/provider/);

    // 8. Privacy agreement
    await page.goto(new URL('/privacy.html', LANDING_URL).toString());
    const privacyBody = await page.innerText('body');
    if (R3_04_MERGED) {
      expect(privacyBody).toContain('data-snapshot-mask');
      expect(privacyBody).toContain('Form field values are never captured');
    }
    const effectiveMatch = privacyBody.match(/Effective date:\s*(\d{4}-\d{2}-\d{2})/);
    expect(effectiveMatch).not.toBeNull();
    const effectiveDate = effectiveMatch![1];
    const effectiveYear = parseInt(effectiveDate.split('-')[0], 10);
    expect(effectiveYear).toBeGreaterThanOrEqual(2026);
    expect(effectiveDate).not.toBe('2026-09-01');

    const backLink = page.locator('a[href="/data.html"]');
    expect(await backLink.count()).toBeGreaterThanOrEqual(1);
    await backLink.first().click();
    await page.waitForURL('**/data.html');

    // 9. v2 + Arabic
    await page.goto(new URL('/v2/', LANDING_URL).toString());
    const v2DataLink = page.locator('footer a[href="/data.html"]');
    expect(await v2DataLink.count()).toBeGreaterThanOrEqual(1);
    const v2Content = await page.content();
    expect(v2Content).toContain('البيانات والاستضافة الذاتية');

    await page.goto(LANDING_URL);
    const rootContent = await page.content();
    expect(rootContent).toContain('Data & self-hosting');
    expect(rootContent).toContain('البيانات والاستضافة الذاتية');
  });

  test('R3-05-03: dark mode + 390 px + section budget', async ({ page }) => {
    try {
      // 0. Assert prefers-color-scheme: dark exists in page source
      await page.goto(new URL('/data.html', LANDING_URL).toString());
      const source = await page.content();
      expect(source).toContain('prefers-color-scheme: dark');

      // 1. Light pass
      await page.emulateMedia({ colorScheme: 'light' });
      await page.reload();
      const bgLight = await page.evaluate(() => getComputedStyle(document.body).backgroundColor);

      // 2. Dark pass
      await page.emulateMedia({ colorScheme: 'dark' });
      await page.reload();
      const bgDark = await page.evaluate(() => getComputedStyle(document.body).backgroundColor);
      expect(bgDark).not.toBe(bgLight);

      // 3. Mobile width
      await page.setViewportSize({ width: 390, height: 844 });
      await page.reload();
      const scrollWidth = await page.evaluate(() => document.scrollingElement!.scrollWidth);
      expect(scrollWidth).toBeLessThanOrEqual(390);

      // 4. Desktop section budget
      await page.setViewportSize({ width: 1280, height: 800 });
      await page.reload();
      const sectionCount = await page.locator('section').count();
      expect(sectionCount).toBeGreaterThanOrEqual(7);

      const sections = page.locator('section');
      for (let i = 0; i < sectionCount; i++) {
        const box = await sections.nth(i).boundingBox();
        expect(box).not.toBeNull();
        expect(box!.height).toBeLessThanOrEqual(800);
      }
    } finally {
      // 5. Reset media emulation
      await page.emulateMedia({ colorScheme: 'light' });
    }
  });

});
