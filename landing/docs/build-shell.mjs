#!/usr/bin/env node
// Rewrites the shared shell (sidebar + content wrapper) into every docs page from pages.json.
//
// The nav used to be hand-maintained in twelve files. The docs e2e suite (R2-07-03) asserts that
// every page's <!-- nav:start --> block lists every page, which caught the drift but could not fix
// it — adding a page meant editing thirteen files by hand and getting all of them right.
//
//   node landing/docs/build-shell.mjs [--check]
//
// --check reports what would change and exits 1 if anything would, for CI.
import { readFileSync, writeFileSync, readdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const checkOnly = process.argv.includes('--check');

const { pages } = JSON.parse(readFileSync(join(here, 'pages.json'), 'utf8'));

/** Pages that live at the landing root (marked `external`) are linked, never rewritten. */
const local = pages.filter((p) => !p.external && p.file.endsWith('.html'));

/**
 * Sidebar markup, grouped by pages.json's `nav` field in first-appearance order.
 *
 * `index.html` and `../index.html` are both included because R2-07-03 requires them by name.
 */
function navBlock(currentFile) {
  const groups = new Map();
  for (const page of pages) {
    const section = page.nav || 'Guides';
    if (!groups.has(section)) groups.set(section, []);
    groups.get(section).push(page);
  }

  const lines = ['<!-- nav:start -->'];
  lines.push('        <div class="nav-group">');
  lines.push('          <h2>Documentation</h2>');
  lines.push(
    `          <a href="index.html"${currentFile === 'index.html' ? ' class="active"' : ''}>All docs</a>`,
  );
  lines.push('          <a href="../index.html">Home</a>');
  lines.push('        </div>');

  for (const [section, items] of groups) {
    lines.push('        <div class="nav-group">');
    lines.push(`          <h2>${esc(section)}</h2>`);
    for (const page of items) {
      // A root-level page ("/" or "/data.html") is linked relative to /docs/.
      const href = page.file.startsWith('/') ? `..${page.file === '/' ? '/index.html' : page.file}` : page.file;
      const active = page.file === currentFile ? ' class="active"' : '';
      const current = page.file === currentFile ? ' aria-current="page"' : '';
      lines.push(`          <a href="${href}"${active}${current}>${esc(page.title)}</a>`);
    }
    lines.push('        </div>');
  }
  lines.push('      <!-- nav:end -->');
  return lines.join('\n');
}

const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

/** The shell around whatever the page's <main> already contained. */
function shell(currentFile, mainInner, pageScripts) {
  return `<body>
  <a class="skip" href="#doc">Skip to content</a>
  <div class="shell">
    <aside class="sidebar">
      <a href="../index.html" class="brand">
        <img src="../assets/dog-mascot.png" alt="" />
        <span>Pointer Docs</span>
      </a>
      <button class="nav-btn" type="button" aria-expanded="false" aria-controls="docs-nav">Menu</button>
      <nav class="nav-links" id="docs-nav" aria-label="Documentation">
      ${navBlock(currentFile)}
      </nav>
    </aside>

    <main class="content" id="doc">
      <div class="wrap">
${mainInner}
      </div>

      <footer>
        <div class="wrap">Pointer &mdash; click-to-comment feedback for teams building with AI.</div>
      </footer>
    </main>
  </div>
  <script src="assets/docs.js" defer></script>
${pageScripts}</body>
</html>
`;
}

const files = readdirSync(here).filter((f) => f.endsWith('.html'));
const changed = [];
const skipped = [];

for (const file of files) {
  const path = join(here, file);
  const html = readFileSync(path, 'utf8');

  const headEnd = html.indexOf('</head>');
  const mainOpen = html.indexOf('<main class="content"');
  const mainClose = html.lastIndexOf('</main>');
  if (headEnd === -1 || mainOpen === -1 || mainClose === -1) {
    // Never write a half-understood file: report it and move on.
    skipped.push(`${file}: could not locate <head>/<main> anchors`);
    continue;
  }

  let inner = html.slice(html.indexOf('>', mainOpen) + 1, mainClose);

  // A page may carry its own <script> after </main> — the index renders itself from pages.json that
  // way. An earlier revision of this generator kept only what was inside <main> and silently
  // deleted that script, which is exactly the kind of loss a "rewrites every page" tool must not
  // risk. Anything of the page's own is carried across verbatim.
  const pageScripts = (html.slice(mainClose).match(/<script\b[\s\S]*?<\/script>/g) || [])
    .filter((s) => !s.includes('assets/docs.js'))
    .map((s) => `  ${s.trim()}\n`)
    .join('');

  // Unwrap the existing .wrap, drop the old footer if it sat inside <main>, and remove the eyebrow
  // badge above <h1> — the sidebar now says which section you are in.
  inner = inner.replace(/^\s*<div class="wrap">/, '').replace(/<\/div>\s*$/, '');
  inner = inner.replace(/<footer>[\s\S]*?<\/footer>/g, '');
  inner = inner.replace(/^\s*<(div|span) class="badge">[\s\S]*?<\/\1>\s*\n/m, '');
  inner = inner.trimEnd();

  // `no-js` is removed by docs.js on load. Without it the stylesheet's scriptless fallback (nav
  // stacked and visible instead of behind a button that cannot open) would never apply.
  const head = html
    .slice(0, headEnd + '</head>'.length)
    .replace(/<html(?![^>]*\bclass=)([^>]*)>/, '<html$1 class="no-js">');
  const next = `${head}\n${shell(file, inner, pageScripts)}`;

  if (next !== html) {
    changed.push(file);
    if (!checkOnly) writeFileSync(path, next, 'utf8');
  }
}

for (const s of skipped) console.error(`SKIPPED ${s}`);
console.log(
  checkOnly
    ? `${changed.length} page(s) would change: ${changed.join(', ') || 'none'}`
    : `rewrote ${changed.length} page(s): ${changed.join(', ') || 'none'}`,
);
if (skipped.length) process.exit(2);
process.exit(checkOnly && changed.length ? 1 : 0);
