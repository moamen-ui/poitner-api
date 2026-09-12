// Copies the committed vite-react fixture into a scratch repo.
//
// The fixture is a real Vite + React + Tailwind project shape — index.html, package.json,
// vite.config.ts, tailwind.config.ts, src/styles/globals.css — but it is never installed or
// built here. Stack and design-token detection reads files; it does not need node_modules, and
// an `npm ci` per scenario would put minutes on a suite that otherwise runs in seconds.
import { cpSync, existsSync, readdirSync } from 'node:fs';
import { readFileSync, mkdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync, spawn } from 'node:child_process';
import { tempRepo } from './git.mjs';

const here = dirname(fileURLToPath(import.meta.url));

/** e2e/fixture-app/vite-react */
export const VITE_FIXTURE_DIR = resolve(here, '..', '..', 'fixture-app', 'vite-react');

/**
 * Copies the fixture into `into` (a directory that already exists — typically tempRepo()'s).
 *
 * Returns the list of top-level entries it placed, so a caller can assert the fixture arrived
 * rather than discovering an empty directory three assertions later.
 */
export function copyFixture({ into }) {
  if (!existsSync(VITE_FIXTURE_DIR)) {
    throw new Error(`vite-react fixture is missing at ${VITE_FIXTURE_DIR}`);
  }
  if (!into) throw new Error('copyFixture({ into }) requires a target directory');

  cpSync(VITE_FIXTURE_DIR, into, { recursive: true });

  const placed = readdirSync(into).filter((e) => e !== '.git');
  if (placed.length === 0) {
    throw new Error(`copyFixture placed nothing into ${into}`);
  }
  return placed;
}

/** The fixture's own path for a given relative file, for tests that read the source of truth. */
export function fixtureFile(rel) {
  return join(VITE_FIXTURE_DIR, rel);
}

const STATE_DIR = resolve(here, '..', '..', 'state');
const CACHE_DIR = join(STATE_DIR, 'vite-react');

/**
 * Builds the vite-react fixture inside a temp git repo or the specified directory.
 * @param {{ dir?: string, buildSha?: string | boolean, enabled?: boolean }} [options]
 * @returns {{ distDir: string, manifest: any, repo: { dir: string, cleanup: () => void } }}
 */
export function buildFixture({ dir, buildSha, enabled = true } = {}) {
  let repo;
  let targetDir;
  if (dir) {
    targetDir = dir;
    repo = { dir: targetDir, cleanup: () => {} };
  } else {
    repo = tempRepo();
    targetDir = repo.dir;
    copyFixture({ into: targetDir });
    execFileSync('git', ['add', '-A'], { cwd: targetDir });
    execFileSync('git', ['commit', '-m', 'fixture: initial commit'], { cwd: targetDir });
  }

  const cachedNodeModules = join(CACHE_DIR, 'node_modules');
  const targetNodeModules = join(targetDir, 'node_modules');
  if (!existsSync(targetNodeModules)) {
    if (existsSync(cachedNodeModules)) {
      cpSync(cachedNodeModules, targetNodeModules, { recursive: true });
    } else {
      mkdirSync(CACHE_DIR, { recursive: true });
      execFileSync('npm', ['ci'], { cwd: targetDir, stdio: 'pipe' });
      cpSync(targetNodeModules, cachedNodeModules, { recursive: true });
    }
  }

  const env = {
    ...process.env,
    VITE_POINTER_SOURCE: String(enabled),
  };
  if (buildSha !== undefined) {
    env.VITE_BUILD_SHA = String(buildSha);
  }

  execFileSync('npm', ['run', 'build'], { cwd: targetDir, env, stdio: 'pipe' });

  const distDir = join(targetDir, 'dist');
  const manifestPath = join(targetDir, '.pointer', 'manifest.json');
  let manifest = null;
  if (existsSync(manifestPath)) {
    try {
      manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
    } catch {
      manifest = null;
    }
  }

  return { distDir, manifest, repo };
}

/**
 * Serves distDir on port using serve-dir.mjs.
 */
export function serveFixture(distDir, port = 4175) {
  const serveScript = resolve(here, '..', 'serve-dir.mjs');
  const child = spawn(process.execPath, [serveScript, distDir, String(port)], {
    stdio: 'pipe',
  });
  return {
    process: child,
    stop: () => {
      try {
        child.kill('SIGTERM');
      } catch {}
    },
  };
}

