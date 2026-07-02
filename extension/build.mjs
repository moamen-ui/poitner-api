// Bundles the extension's TS entry points into dist/ and copies static assets.
// Mirrors web-component/build.mjs (esbuild, no runtime deps).
import * as esbuild from 'esbuild';
import { copyFileSync, mkdirSync, rmSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const OUT = resolve(here, 'dist');
const STATIC = ['manifest.json', 'popup.html', 'options.html'];

function copyStatic() {
  for (const f of STATIC) copyFileSync(resolve(here, f), resolve(OUT, f));
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
