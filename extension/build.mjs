// Bundles the extension's TS entry points into dist/ and copies static assets.
// Mirrors web-component/build.mjs (esbuild, no runtime deps).
import * as esbuild from 'esbuild';
import { copyFileSync, mkdirSync, rmSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const OUT = resolve(here, 'dist');
const STATIC = ['manifest.json', 'popup.html', 'options.html'];
const ICONS = ['16.png', '32.png', '48.png', '128.png'];
// pointer.js/pointer.css load LIVE from the server (see src/inject-main.ts) — a plain page-context
// <script src>/<link>, same as the non-extension embed, not extension-privileged code. Only snapdom
// (screenshot capture, changes rarely) stays bundled from the API's wwwroot.
const WWWROOT = resolve(here, '..', 'API', 'wwwroot');

function copyStatic() {
  for (const f of STATIC) copyFileSync(resolve(here, f), resolve(OUT, f));
  mkdirSync(resolve(OUT, 'vendor'), { recursive: true });
  copyFileSync(resolve(WWWROOT, 'vendor', 'snapdom.js'), resolve(OUT, 'vendor', 'snapdom.js'));
  mkdirSync(resolve(OUT, 'icons'), { recursive: true });
  for (const i of ICONS) copyFileSync(resolve(here, 'icons', i), resolve(OUT, 'icons', i));
}

const options = {
  entryPoints: {
    background: resolve(here, 'src/background.ts'),
    'content-bridge': resolve(here, 'src/content-bridge.ts'),
    popup: resolve(here, 'src/popup.ts'),
    options: resolve(here, 'src/options.ts'),
  },
  outdir: OUT,
  bundle: true,
  format: 'iife',
  target: 'chrome110',
  legalComments: 'none',
  logLevel: 'info',
};

rmSync(OUT, { recursive: true, force: true });
mkdirSync(OUT, { recursive: true });

if (process.argv.includes('--watch')) {
  const ctx = await esbuild.context(options);
  await ctx.watch();
  copyStatic();
  console.log('✓ watching extension/src → dist/ (static copied once)');
} else {
  await esbuild.build(options);
  copyStatic();
  console.log('✓ extension → dist/');
}
