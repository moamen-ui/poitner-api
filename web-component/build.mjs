// Builds the <pointer-feedback> web component into the served static files:
//   src/index.ts          → ../API/wwwroot/widget.js    (single bundled IIFE, no runtime deps)
//   src/styles/index.scss → ../API/wwwroot/widget.css (compiled, with --fbk-* CSS variables)
//   retained versions     → ../API/wwwroot/widget/<hash>/widget.{js,css}
//   version descriptor    → ../API/wwwroot/pointer.version.json
//
// pointer.js/pointer.css (top level, retained dirs, and version.json's `files`) are written as
// byte-identical copies of widget.js/widget.css — the pre-rename names, kept as permanent aliases
// per docs/ON-DISK-CONTRACT.md so an already-integrated site (or an older CLI's `--pin`, which
// reads files['pointer.js']) never breaks.
//
// Usage:  node build.mjs          (one-shot build)
//         node build.mjs --watch  (rebuild on change)
import { build, context } from 'esbuild';
import * as sass from 'sass';
import {
  writeFileSync,
  readFileSync,
  mkdirSync,
  readdirSync,
  rmSync,
  existsSync,
} from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { execSync } from 'node:child_process';
import * as crypto from 'node:crypto';
import * as zlib from 'node:zlib';

const here = dirname(fileURLToPath(import.meta.url));
const OUT_DIR = resolve(here, '../API/wwwroot');
const watch = process.argv.includes('--watch');

const BANNER = '/* GENERATED from web-component/src — DO NOT EDIT. Run `npm run build` in web-component/. */';
const GZIP_BUDGET = 61440; // 60 KB

function compileCss() {
  const res = sass.compile(resolve(here, 'src/styles/index.scss'), { style: 'expanded' });
  const cssContent = BANNER + '\n' + res.css;
  const cssBuffer = Buffer.from(cssContent, 'utf8');
  const bytes = cssBuffer.length;
  const gzipBytes = zlib.gzipSync(cssBuffer).length;
  const integrity = 'sha384-' + crypto.createHash('sha384').update(cssBuffer).digest('base64');
  return { content: cssContent, buffer: cssBuffer, bytes, gzipBytes, integrity };
}

async function runBuild() {
  mkdirSync(OUT_DIR, { recursive: true });

  // 1. Build CSS first so integrity can be embedded into JS
  const css = compileCss();
  writeFileSync(resolve(OUT_DIR, 'widget.css'), css.content);
  writeFileSync(resolve(OUT_DIR, 'pointer.css'), css.content); // pre-rename alias, see header comment

  // 2. Build JS with esbuild and embed CSS_INTEGRITY
  const jsOptions = {
    entryPoints: [resolve(here, 'src/index.ts')],
    outfile: resolve(OUT_DIR, 'widget.js'),
    bundle: true,
    format: 'iife',
    target: 'es2019',
    charset: 'utf8',
    legalComments: 'none',
    banner: { js: BANNER },
    // Kept unminified on purpose: the served file stays debuggable in the browser.
    minify: false,
    define: {
      __CSS_INTEGRITY__: JSON.stringify(css.integrity),
    },
  };

  await build(jsOptions);

  // 3. Compute JS stats
  const jsBuffer = readFileSync(resolve(OUT_DIR, 'widget.js'));
  writeFileSync(resolve(OUT_DIR, 'pointer.js'), jsBuffer); // pre-rename alias, see header comment
  const jsBytes = jsBuffer.length;
  const jsGzipBytes = zlib.gzipSync(jsBuffer).length;
  const jsIntegrity = 'sha384-' + crypto.createHash('sha384').update(jsBuffer).digest('base64');
  const hash = crypto.createHash('sha256').update(jsBuffer).digest('hex').slice(0, 12);

  // 4. Read package version
  let pkgVersion = '0.1.0';
  try {
    const pkg = JSON.parse(readFileSync(resolve(here, 'package.json'), 'utf8'));
    if (pkg.version) pkgVersion = pkg.version;
  } catch {
    // keep default
  }

  // 5. Read existing version JSON
  const versionFile = resolve(OUT_DIR, 'pointer.version.json');
  let existingVersion = null;
  if (existsSync(versionFile)) {
    try {
      existingVersion = JSON.parse(readFileSync(versionFile, 'utf8'));
    } catch {
      existingVersion = null;
    }
  }

  // 6. Resolve commit and committedAt (from git log -- web-component/src, no wall clock)
  let commit = existingVersion?.commit || 'unknown';
  let committedAt = existingVersion?.committedAt || '2026-09-11T10:40:12Z';
  try {
    const repoRoot = resolve(here, '..');
    const gitOut = execSync('git log -1 --format=%h,%cI -- web-component/src', {
      cwd: repoRoot,
      encoding: 'utf8',
      stdio: ['pipe', 'pipe', 'ignore'],
    }).trim();
    if (gitOut) {
      const [c, d] = gitOut.split(',');
      if (c) commit = c;
      if (d) committedAt = d;
    }
  } catch {
    // Git unavailable; keep existing values
  }

  // 7. Write pinned artifacts
  const pinnedDir = resolve(OUT_DIR, 'widget', hash);
  mkdirSync(pinnedDir, { recursive: true });
  writeFileSync(resolve(pinnedDir, 'widget.js'), jsBuffer);
  writeFileSync(resolve(pinnedDir, 'widget.css'), css.buffer);
  writeFileSync(resolve(pinnedDir, 'pointer.js'), jsBuffer); // pre-rename alias
  writeFileSync(resolve(pinnedDir, 'pointer.css'), css.buffer);

  // 8. Update retained builds list (max 10)
  const currentRetained = {
    hash,
    version: pkgVersion,
    files: {
      'widget.js': { integrity: jsIntegrity },
      'widget.css': { integrity: css.integrity },
      'pointer.js': { integrity: jsIntegrity },
      'pointer.css': { integrity: css.integrity },
    },
  };

  const prevRetained = (existingVersion?.retained || []).filter((r) => r.hash !== hash);
  const retained = [currentRetained, ...prevRetained].slice(0, 10);

  // 9. Prune widget/ directory to the retained hashes
  const widgetDir = resolve(OUT_DIR, 'widget');
  if (existsSync(widgetDir)) {
    const keptHashes = new Set(retained.map((r) => r.hash));
    for (const entry of readdirSync(widgetDir, { withFileTypes: true })) {
      if (entry.isDirectory() && !keptHashes.has(entry.name)) {
        rmSync(resolve(widgetDir, entry.name), { recursive: true, force: true });
      }
    }
  }

  // 10. Write pointer.version.json
  const versionData = {
    version: pkgVersion,
    hash,
    commit,
    committedAt,
    files: {
      'widget.js': {
        bytes: jsBytes,
        gzipBytes: jsGzipBytes,
        integrity: jsIntegrity,
      },
      'widget.css': {
        bytes: css.bytes,
        gzipBytes: css.gzipBytes,
        integrity: css.integrity,
      },
      // Pre-rename aliases — byte-identical to widget.{js,css} above (see header comment).
      'pointer.js': {
        bytes: jsBytes,
        gzipBytes: jsGzipBytes,
        integrity: jsIntegrity,
      },
      'pointer.css': {
        bytes: css.bytes,
        gzipBytes: css.gzipBytes,
        integrity: css.integrity,
      },
    },
    retained,
    budget: {
      pointerJsGzipMax: GZIP_BUDGET,
    },
  };

  writeFileSync(versionFile, JSON.stringify(versionData, null, 2) + '\n');

  // 11. Enforce size budget
  if (jsGzipBytes > GZIP_BUDGET) {
    console.error(
      `Budget breach: widget.js gzip size (${jsGzipBytes} B) exceeds max budget of ${GZIP_BUDGET} B!`
    );
    process.exit(1);
  }

  // 12. One-line size report
  console.log(
    `widget.js ${jsBytes} raw / ${jsGzipBytes} gz (budget ${GZIP_BUDGET}) · hash ${hash} · retained ${retained.length}`
  );
}

if (watch) {
  const css = compileCss();
  const ctx = await context({
    entryPoints: [resolve(here, 'src/index.ts')],
    outfile: resolve(OUT_DIR, 'widget.js'),
    bundle: true,
    format: 'iife',
    target: 'es2019',
    charset: 'utf8',
    legalComments: 'none',
    banner: { js: BANNER },
    minify: false,
    define: {
      __CSS_INTEGRITY__: JSON.stringify(css.integrity),
    },
  });
  await ctx.watch();
  await runBuild();
  console.log('watching…');
} else {
  await runBuild();
}
