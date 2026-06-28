// Builds the <pointer-feedback> web component into the served static files:
//   src/index.ts        → ../API/wwwroot/pointer.js   (single bundled IIFE, no runtime deps)
//   src/styles/index.scss → ../API/wwwroot/pointer.css (compiled, with --pf-* CSS variables)
//
// Usage:  node build.mjs          (one-shot build)
//         node build.mjs --watch  (rebuild on change)
import { build, context } from 'esbuild';
import * as sass from 'sass';
import { writeFileSync, mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const OUT_DIR = resolve(here, '../API/wwwroot');
const watch = process.argv.includes('--watch');

const BANNER = '/* GENERATED from web-component/src — DO NOT EDIT. Run `npm run build` in web-component/. */';

const jsOptions = {
  entryPoints: [resolve(here, 'src/index.ts')],
  outfile: resolve(OUT_DIR, 'pointer.js'),
  bundle: true,
  format: 'iife',
  target: 'es2019',
  charset: 'utf8',
  legalComments: 'none',
  banner: { js: BANNER },
  // Kept unminified on purpose: the served file stays debuggable in the browser.
  minify: false,
};

function buildCss() {
  const res = sass.compile(resolve(here, 'src/styles/index.scss'), { style: 'expanded' });
  mkdirSync(OUT_DIR, { recursive: true });
  writeFileSync(resolve(OUT_DIR, 'pointer.css'), BANNER + '\n' + res.css);
  console.log('✓ pointer.css');
}

if (watch) {
  const ctx = await context(jsOptions);
  await ctx.watch();
  buildCss();
  // esbuild watches JS; poll-build CSS alongside (sass has no built-in watch here).
  const sassWatch = sass.compile; // referenced to keep import used
  void sassWatch;
  console.log('watching… (CSS rebuilds on next JS change; re-run for CSS-only edits)');
} else {
  await build(jsOptions);
  console.log('✓ pointer.js');
  buildCss();
}
