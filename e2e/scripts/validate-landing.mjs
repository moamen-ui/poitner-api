#!/usr/bin/env node
// R3-05-04 — the landing site's two offline oracles: markup validity and link resolution.
//
// Both are deliberately local. The harness rule (§1.2) is that the PR tier runs deterministically
// with no network, which rules out `npx --yes html-validate@8` (an unpinned install that fetches
// on every run and fails outright air-gapped) and `lychee --offline` (a binary nobody has). So
// html-validate is a committed devDependency invoked through node_modules/.bin, and the link check
// is this file.
//
// Usage:  node e2e/scripts/validate-landing.mjs
// Exit 0 and a `links=<n> internal-ok external-listed=<m>` line on success; exit 1 with each
// unresolved link named on failure.
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(here, '..', '..');
const landingRoot = join(repoRoot, 'landing');

/**
 * The pages html-validate must find clean.
 *
 * Only these two. landing/docs/*.html currently carries 82 style-level findings (void-element
 * style, raw `&`, inline style) that are lint debt rather than breakage; pinning them here would
 * hand this scenario a failure it is not the owner of. The link check below DOES cover them,
 * because a broken link is breakage.
 */
const VALIDATE = ['landing/data.html', 'landing/privacy.html'];

/** Every .html under landing/, discovered rather than listed. */
function htmlFiles(dir = landingRoot) {
  const out = [];
  for (const entry of readdirSync(dir)) {
    if (entry === 'node_modules' || entry.startsWith('.')) continue;
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) out.push(...htmlFiles(full));
    else if (entry.endsWith('.html')) out.push(full);
  }
  return out.sort();
}

function fail(lines) {
  for (const l of lines) console.error(l);
  process.exit(1);
}

// ── 1. Markup validity ──────────────────────────────────────────────────────
const bin = join(repoRoot, 'node_modules', '.bin', 'html-validate');
if (!existsSync(bin)) {
  fail([
    `html-validate is not installed at ${relative(repoRoot, bin)}.`,
    'It is a committed devDependency precisely so this never reaches the network — run `npm install`.',
  ]);
}
try {
  execFileSync(bin, VALIDATE, { cwd: repoRoot, stdio: 'pipe' });
} catch (err) {
  fail(['html-validate reported problems:', String(err.stdout || ''), String(err.stderr || '')]);
}

// ── 2. Link resolution ──────────────────────────────────────────────────────
const pages = htmlFiles();
if (pages.length === 0) fail([`no .html files found under ${relative(repoRoot, landingRoot)}`]);

const broken = [];
const external = new Set();
let total = 0;
let internal = 0;

for (const page of pages) {
  const html = readFileSync(page, 'utf8');
  for (const m of html.matchAll(/\bhref\s*=\s*"([^"]*)"/g)) {
    const href = m[1].trim();
    total++;
    if (!href || href.startsWith('#')) continue;                      // same-page anchor
    if (/^(mailto:|tel:|javascript:|data:)/i.test(href)) continue;    // not a document
    if (/^[a-z][a-z0-9+.-]*:\/\//i.test(href) || href.startsWith('//')) {
      external.add(href);                                             // listed, never fetched
      continue;
    }

    // Internal: strip the fragment and query, then resolve on disk. A root-relative href is
    // relative to landing/ (what the static host serves), not to the file that contains it.
    const clean = href.split('#')[0].split('?')[0];
    if (!clean) continue;
    const base = clean.startsWith('/') ? landingRoot : dirname(page);
    let target = resolve(base, clean.replace(/^\//, ''));
    if (existsSync(target) && statSync(target).isDirectory()) target = join(target, 'index.html');

    internal++;
    if (!existsSync(target)) {
      broken.push(`${relative(repoRoot, page)}: href="${href}" -> ${relative(repoRoot, target)} (missing)`);
    }
  }
}

if (broken.length) {
  fail([`${broken.length} internal link(s) do not resolve on disk:`, ...broken.map((b) => `  ${b}`)]);
}

console.log(`pages=${pages.length} links=${total} internal-ok=${internal} external-listed=${external.size}`);
for (const url of [...external].sort()) console.log(`  external ${url}`);
