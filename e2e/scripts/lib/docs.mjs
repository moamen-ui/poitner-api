// Helpers for the documentation-site checks.
//
// The docs site has one failure mode that matters and is invisible from inside a page: a page that
// exists but nothing links to, or a link that points at a file nobody wrote. Both look fine in a
// browser you navigate by hand.
import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
export const DOCS_DIR = resolve(here, '..', '..', '..', 'landing', 'docs');

/** The pages.json manifest that drives both the index and every page's nav. */
export function readManifest() {
  return JSON.parse(readFileSync(join(DOCS_DIR, 'pages.json'), 'utf8')).pages;
}

/** Every .html file actually on disk. */
export function htmlFiles() {
  return readdirSync(DOCS_DIR).filter((f) => f.endsWith('.html')).sort();
}

/** The generated nav block of one page, as an array of hrefs. */
export function navBlock(file) {
  const html = readFileSync(join(DOCS_DIR, file), 'utf8');
  const match = html.match(/<!-- nav:start -->([\s\S]*?)<!-- nav:end -->/);
  if (!match) return null;
  return [...match[1].matchAll(/href="([^"]+)"/g)].map((m) => m[1]);
}

/** Internal (same-directory) links a page makes, excluding anchors and external URLs. */
export function internalLinks(file) {
  const html = readFileSync(join(DOCS_DIR, file), 'utf8');
  return [...html.matchAll(/href="([^"]+)"/g)]
    .map((m) => m[1])
    .filter((h) => !/^(https?:|mailto:|#|\.\.\/)/.test(h))
    .map((h) => h.split('#')[0])
    .filter(Boolean);
}

/** One page's raw HTML. */
export function readPage(file) {
  return readFileSync(join(DOCS_DIR, file), 'utf8');
}

export function exists(relative) {
  return existsSync(join(DOCS_DIR, relative));
}
