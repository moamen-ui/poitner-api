// R2-07: the documentation site holds together.
//
// These checks exist because the two ways a docs site rots are both invisible when you click
// through it yourself: a page nobody links to, and a link to a page nobody wrote.
import { test, expect } from '@playwright/test';
import { readManifest, localPages, siteAbsolutePages, existsAtSiteRoot, htmlFiles, navBlock, internalLinks, exists, readPage } from '../scripts/lib/docs.mjs';
import { record } from '../scripts/lib/report.mjs';

test('R2-07-01 — every page on disk is in the manifest, and every manifest entry exists', () => {
  const start = Date.now();
  const manifest = localPages();
  const listed = manifest.map((p) => p.file);
  const onDisk = htmlFiles().filter((f) => f !== 'index.html'); // the index lists the others

  for (const file of onDisk) {
    expect(listed, `${file} exists but is not in pages.json — nothing links to it`).toContain(file);
  }
  for (const entry of listed) {
    expect(exists(entry), `pages.json lists ${entry}, which is not on disk`).toBe(true);
  }

  // Site-absolute entries must still resolve — at the landing root rather than under /docs/.
  for (const page of siteAbsolutePages()) {
    expect(existsAtSiteRoot(page.file), `pages.json lists ${page.file}, which is not at the landing root`).toBe(true);
  }

  record({ id: 'R2-07-01', tier: 'PR', layer: 'docs', role: '—', result: 'PASS', ms: Date.now() - start,
    detail: `${onDisk.length} pages, ${listed.length} manifest entries` });
});

test('R2-07-02 — every manifest entry carries the fields the index renders', () => {
  for (const page of readManifest()) {
    expect(page.file, 'file is required').toBeTruthy();
    expect(page.title, `${page.file} needs a title`).toBeTruthy();
    expect(page.nav, `${page.file} needs a nav section`).toBeTruthy();
    expect(page.summary, `${page.file} needs a summary — it is the gallery card's subtitle`).toBeTruthy();
  }
});

test('R2-07-03 — every page shares the generated nav block, listing every page', () => {
  const listed = localPages().map((p) => p.file);

  for (const file of htmlFiles()) {
    const nav = navBlock(file);
    expect(nav, `${file} has no <!-- nav:start --> block`).not.toBeNull();

    for (const target of listed) {
      expect(nav, `${file}'s nav omits ${target}`).toContain(target);
    }
    // Every page can get back to the index and to the landing page.
    expect(nav).toContain('index.html');
    expect(nav).toContain('../index.html');
  }
});

test('R2-07-04 — no page links to a file that does not exist', () => {
  for (const file of htmlFiles()) {
    for (const link of internalLinks(file)) {
      expect(exists(link), `${file} links to ${link}, which is not on disk`).toBe(true);
    }
  }
});

test('R2-07-05 — pages use the shared stylesheet rather than private copies', () => {
  // Seven private <style> blocks is how a colour change gets made six times and missed once.
  for (const file of htmlFiles()) {
    const html = readPage(file);
    expect(html, `${file} must link the shared stylesheet`).toContain('assets/docs.css');
    expect(html, `${file} still has an inline <style> block`).not.toContain('<style>');
  }
});
