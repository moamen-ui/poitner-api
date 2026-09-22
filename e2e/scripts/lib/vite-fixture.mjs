// Copies the committed vite-react fixture into a scratch repo.
//
// The fixture is a real Vite + React + Tailwind project shape — index.html, package.json,
// vite.config.ts, tailwind.config.ts, src/styles/globals.css — but it is never installed or
// built here. Stack and design-token detection reads files; it does not need node_modules, and
// an `npm ci` per scenario would put minutes on a suite that otherwise runs in seconds.
import { cpSync, existsSync, readdirSync } from 'node:fs';
import { readFileSync, mkdirSync, rmSync, symlinkSync } from 'node:fs';
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
    // Passing `dir` used to mean "build here" AND "the fixture is already here" — two things, one
    // argument. A caller handing over a fresh tempRepo() got an empty directory built, and the
    // failure was `npm run build` exiting 1 with no hint that the sources were simply absent.
    // Populate it when it is empty, so `dir` only ever means where.
    if (!existsSync(join(targetDir, 'package.json'))) {
      copyFixture({ into: targetDir });
      execFileSync('git', ['add', '-A'], { cwd: targetDir });
      execFileSync('git', ['commit', '-m', 'fixture: initial commit'], { cwd: targetDir });
    }
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
      const fixtureNodeModules = join(VITE_FIXTURE_DIR, 'node_modules');
      if (!existsSync(fixtureNodeModules)) {
        execFileSync('npm', ['ci', '--no-audit', '--no-fund'], { cwd: VITE_FIXTURE_DIR, stdio: 'pipe' });
      }
      cpSync(fixtureNodeModules, cachedNodeModules, { recursive: true });
      cpSync(cachedNodeModules, targetNodeModules, { recursive: true });
    }
  }

  // Point `pointer-feedback` at the repo's CLI, absolutely.
  //
  // The fixture declares it as `file:../../../cli`, which is correct where the fixture lives but
  // dangles the moment it is copied into a temp repo — vite.config.ts then fails to load with
  // ERR_MODULE_NOT_FOUND and the build dies before the plugin under test ever runs. Linking here
  // also guarantees every scenario exercises the CLI as just built, not a stale copy npm cached.
  const linkTarget = resolve(here, '..', '..', '..', 'cli');
  const linkPath = join(targetNodeModules, 'pointer-feedback');
  if (!existsSync(join(linkTarget, 'dist', 'vite.js'))) {
    throw new Error(
      `cli/dist/vite.js is missing — run \`npm run build\` in cli/ before the R3-01 scenarios`,
    );
  }
  rmSync(linkPath, { recursive: true, force: true });
  mkdirSync(dirname(linkPath), { recursive: true });
  symlinkSync(linkTarget, linkPath, 'dir');

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
  // A manifest that exists but will not parse is a bug in the plugin, and returning null for it
  // hands every caller the same value as "the plugin was switched off" — the two cases then look
  // identical at the assertion site.
  let manifest = null;
  if (existsSync(manifestPath)) {
    const raw = readFileSync(manifestPath, 'utf8');
    try {
      manifest = JSON.parse(raw);
    } catch (err) {
      throw new Error(`${manifestPath} exists but is not valid JSON: ${err.message}`);
    }
  }

  return { distDir, manifest, repo };
}

/**
 * Serves distDir on `port`, and does not return until the port actually answers.
 *
 * Spawning and returning immediately loses the race against `page.goto`, which then fails with
 * ERR_CONNECTION_REFUSED — a message that points at the page under test rather than at a server
 * that had not finished starting. Waiting here means every caller gets a server that is up.
 */
export async function serveFixture(distDir, port = 4175) {
  const serveScript = resolve(here, '..', 'serve-dir.mjs');
  const child = spawn(process.execPath, [serveScript, distDir, String(port)], { stdio: 'pipe' });

  const url = `http://localhost:${port}/`;
  const stop = () => {
    try {
      child.kill('SIGTERM');
    } catch {}
  };

  const deadline = Date.now() + 15_000;
  let lastError;
  while (Date.now() < deadline) {
    if (child.exitCode !== null) {
      throw new Error(`serve-dir exited (${child.exitCode}) before serving ${distDir} on :${port}`);
    }
    try {
      const res = await fetch(url);
      if (res.ok) return { process: child, stop };
    } catch (err) {
      lastError = err;
    }
    await new Promise((r) => setTimeout(r, 200));
  }

  stop();
  throw new Error(`fixture server never came up on :${port} (${lastError?.message ?? 'no response'})`);
}

