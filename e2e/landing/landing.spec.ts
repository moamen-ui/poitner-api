// e2e/landing/landing.spec.ts
// Automated scenarios R3-06-01 ... R3-06-05, R3-06-07 from docs/roadmap/testing/R3-06-tests.md
import { test, expect } from '@playwright/test';
import { get, put, login } from '../scripts/lib/api.mjs';
import { credentials as loadCredentials } from '../scripts/lib/state.mjs';
import { preAuthWidget } from '../widget/lib/auth';

const LANDING_URL = 'http://localhost:8099/';

test.describe('R3-06 Landing page refresh', () => {

  // R3-06-01: page renders fully with zero network
  test('R3-06-01: page renders fully with zero network', async ({ page }) => {
    const consoleErrors: string[] = [];
    page.on('console', (msg) => {
      if (msg.type() === 'error') {
        consoleErrors.push(msg.text());
      }
    });

    // 1. Abort all /api/ calls and the widget script before goto
    await page.route('**/api/**', (route) => route.abort());
    await page.route('**/pointer.js', (route) => route.abort());

    // 2. Load page
    await page.goto(LANDING_URL, { waitUntil: 'domcontentloaded' });

    // 3. Assert hero heading, brief section, loop section and footer are all visible
    await expect(page.locator('h1[data-i18n="hero.title"]')).toBeVisible();
    await expect(page.locator('#brief')).toBeVisible();
    await expect(page.locator('#how')).toBeVisible();
    await expect(page.locator('footer.footer')).toBeVisible();

    // 4. Assert no loading spinner/skeleton remains after 2s
    await page.waitForTimeout(2000);
    const skeletons = page.locator('.pf-skeleton, [aria-busy="true"], .loading');
    expect(await skeletons.count()).toBe(0);

    // 5. page.content() contains no literal undefined/null/[object Object]
    const content = await page.content();
    expect(content).not.toContain('undefined');
    expect(content).not.toContain('[object Object]');
    // Check that 'null' does not appear outside code/markup attributes
    expect(content).not.toMatch(/>\s*null\s*</);

    // 6. Zero uncaught errors
    // Filter out net::ERR_FAILED caused by our intentional route.abort()
    const uncaughtErrors = consoleErrors.filter(
      (err) => !err.includes('Failed to load resource') && !err.includes('net::ERR_FAILED')
    );
    expect(uncaughtErrors.length).toBe(0);
  });

  // R3-06-02: each live section degrades independently
  test('R3-06-02: each live section degrades independently', async ({ page }) => {
    await page.route('**/pointer.js', (route) => route.abort());

    // (a) abort /api/plans -> pricing shows fallback CTA
    await page.route('**/api/plans', (route) => route.abort());
    await page.goto(LANDING_URL, { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(500);
    await expect(page.locator('#pricing-grid')).toContainText('Pricing is just a click away');

    await page.unroute('**/api/plans');

    // (b) abort stacks-summary -> stack section is hidden
    await page.route('**/api/public/stacks-summary', (route) => route.abort());
    await page.goto(LANDING_URL, { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(500);
    const stackSec = page.locator('#stack');
    expect(await stackSec.getAttribute('hidden')).not.toBeNull();

    await page.unroute('**/api/public/stacks-summary');

    // (c) abort branding -> bundled name/logo retained
    await page.route('**/api/branding', (route) => route.abort());
    await page.goto(LANDING_URL, { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(500);
    await expect(page.locator('.brand span[data-brand-name]').first()).toHaveText('Pointer');

    await page.unroute('**/api/branding');

    // (d) stacks-summary fulfilled with totalProjects: 0 -> hidden
    await page.route('**/api/public/stacks-summary', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ isSuccess: true, data: { totalProjects: 0, frontend: {}, backend: {}, aiTools: {} } }),
      })
    );
    await page.goto(LANDING_URL, { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(500);
    expect(await page.locator('#stack').getAttribute('hidden')).not.toBeNull();
  });

  // R3-06-03: white-label swap, including the docs link
  test('R3-06-03: white-label swap, including the docs link', async ({ browser }) => {
    const creds = loadCredentials();
    const admin = await login(creds.superAdmin.email, creds.superAdmin.password);
    
    // 1. Capture current branding
    const orig = await get('/api/admin/branding', { token: admin.token });

    try {
      // 2. PUT /api/admin/branding with Acme Review
      await put(
        '/api/admin/branding',
        {
          productName: 'Acme Review',
          tagline: 'Point at the UI',
          primaryColor: '#2563eb',
          urls: {
            app: 'https://app.acme.test',
            demo: 'https://demo.acme.test',
            docs: 'https://docs.acme.test',
            landing: 'https://acme.test',
          },
        },
        { token: admin.token }
      );

      // 3. Fresh context & load page
      const context = await browser.newContext();
      const page = await context.newPage();
      await page.route('**/pointer.js', (route) => route.abort());

      const brandingPromise = page.waitForResponse('**/api/branding');
      await page.goto(LANDING_URL, { waitUntil: 'domcontentloaded' });
      await brandingPromise;
      await page.waitForTimeout(500);

      // 4. Assert nav + footer brand text, hero CTA hrefs, footer docs link
      await expect(page.locator('.nav .brand span[data-brand-name]')).toHaveText('Acme Review');
      await expect(page.locator('.footer .brand span[data-brand-name]')).toHaveText('Acme Review');
      await expect(page.locator('a[data-i18n="hero.demo"]')).toHaveAttribute('href', 'https://demo.acme.test');
      await expect(page.locator('a[data-brand-docs]')).toHaveAttribute('href', 'https://docs.acme.test');

      // 5. Leak check outside frozen names
      const bodyText = await page.locator('body').innerText();
      const titleAttrs = await page.$$eval('[title]', (els) => els.map((e) => e.getAttribute('title') || ''));
      const ariaAttrs = await page.$$eval('[aria-label]', (els) => els.map((e) => e.getAttribute('aria-label') || ''));
      const allText = [bodyText, ...titleAttrs, ...ariaAttrs].join(' ');

      // Regex ignoring frozen names: pointer.js, <pointer-feedback>, .pointer/
      const leakRegex = /(?<!-)\bPointer\b(?!-)/g;
      const matches = allText.match(leakRegex) || [];
      expect(matches.length).toBe(0);

      await context.close();
    } finally {
      // 6. Restore original branding
      await put('/api/admin/branding', orig, { token: admin.token });
    }
  });

  // R3-06-04: en <-> ar + RTL, and dark mode
  test('R3-06-04: en <-> ar + RTL, and dark mode', async ({ page }) => {
    await page.route('**/pointer.js', (route) => route.abort());
    await page.goto(LANDING_URL, { waitUntil: 'domcontentloaded' });

    // 1. Click language toggle
    await page.click('#lang-toggle');

    // 2. Assert dir === 'rtl'
    const dir = await page.evaluate(() => document.documentElement.dir);
    expect(dir).toBe('rtl');

    // 3. New sections text is translated into Arabic
    const heroTitleAr = await page.locator('h1[data-i18n="hero.title"]').innerText();
    expect(heroTitleAr).not.toBe('Point at the UI. Your AI gets the brief.');
    expect(heroTitleAr).toContain('أشِر');

    const briefTitleAr = await page.locator('h2[data-i18n="brief.title"]').innerText();
    expect(briefTitleAr).not.toBe('The structured brief — what a comment actually carries');

    const loopStep4Ar = await page.locator('h3[data-i18n="how.s4t"]').innerText();
    expect(loopStep4Ar).not.toBe('Committed, never pushed');

    const trustTitleAr = await page.locator('h2[data-i18n="trust.title"]').innerText();
    expect(trustTitleAr).not.toBe('Trust & privacy');

    // 4. Toggle back to English
    await page.click('#lang-toggle');
    const dirEn = await page.evaluate(() => document.documentElement.dir);
    expect(dirEn).toBe('ltr');

    // 5. Dark mode toggle & media emulation
    await page.emulateMedia({ colorScheme: 'dark' });
    await page.click('#theme-toggle'); // Switch to dark
    const darkTheme = await page.evaluate(() => document.documentElement.getAttribute('data-theme'));
    expect(darkTheme).toBe('dark');

    // Check background color and text contrast on new sections
    const briefBg = await page.locator('#brief').evaluate((el) => getComputedStyle(el).backgroundColor);
    const briefColor = await page.locator('#brief').evaluate((el) => getComputedStyle(el).color);
    expect(briefBg).not.toBe(briefColor);
    expect(briefColor).not.toBe('transparent');
  });

  // R3-06-05: no horizontal scroll at 390 px and 320 px
  test('R3-06-05: no horizontal scroll at 390 px and 320 px', async ({ page }) => {
    await page.route('**/pointer.js', (route) => route.abort());

    for (const width of [390, 320]) {
      await page.setViewportSize({ width, height: 844 });
      await page.goto(LANDING_URL, { waitUntil: 'domcontentloaded' });

      // 2. Assert document scrollWidth <= clientWidth
      const fits = await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth);
      expect(fits).toBe(true);

      // 3. Assert body does not have overflow-x: hidden
      const bodyOverflowX = await page.evaluate(() => getComputedStyle(document.body).overflowX);
      expect(bodyOverflowX).not.toBe('hidden');
    }
  });

  // R3-06-07: dogfooded widget still works on the page
  test('R3-06-07: dogfooded widget still works on the page', async ({ page }) => {
    const creds = loadCredentials();
    const tester = await login(creds.tester.email, creds.tester.password);

    // 1. Do NOT block pointer.js
    // 2. Pre-auth widget as tester
    await preAuthWidget(page, tester.token, tester.user);

    // 3. Register capture-config waiter BEFORE goto
    const cfgPromise = page.waitForResponse((r) => r.url().includes('/capture-config') && r.status() === 200);

    // 4. Load page
    await page.goto(LANDING_URL, { waitUntil: 'domcontentloaded' });
    await cfgPromise;

    // 5. The widget booted on the real page
    const widget = page.locator('pointer-feedback');
    await expect(widget).toBeAttached();
  });
});
