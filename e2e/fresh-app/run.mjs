// Fresh-app orchestration driver.
// Handles scaffolding, building, serving, and driving fresh app test flows.
// Contract: docs/roadmap/testing/00-HARNESS.md §2, docs/roadmap/testing/R2-00-tests.md
import { execFile, spawn } from 'node:child_process';
import { promisify } from 'node:util';
import { rm, mkdir, cp, readFile, writeFile } from 'node:fs/promises';
import { existsSync, writeFileSync, mkdirSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { PORTS, USERS } from '../scripts/lib/constants.mjs';
import { spawnCli } from '../scripts/lib/cli.mjs';

const execFileAsync = promisify(execFile);

const here = dirname(fileURLToPath(import.meta.url));
const ROOT_DIR = resolve(here, '../..');
const STATE_DIR = resolve(here, '../state');
const STATE_FRESH_DIR = resolve(here, '../state/fresh');
const TEMPLATES_DIR = resolve(here, 'templates');
const PREVIEW_PORT = PORTS.freshPreview || PORTS.fresh || 4174;

/**
 * Polls an HTTP endpoint until it answers 200 or timeout expires.
 *
 * @param {string} url
 * @param {number} [timeoutMs=15000]
 * @param {import('node:child_process').ChildProcess} [child] Fail fast (rather than waiting out
 *   the full timeout) if the server process we just spawned has already exited — otherwise a
 *   bind failure on a shared port (see stopServer below) can silently "succeed" against whatever
 *   unrelated process still answers there.
 */
export async function waitForServer(url, timeoutMs = 15000, child) {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    if (child && child.exitCode !== null) {
      throw new Error(
        `Server process for ${url} exited early with code ${child.exitCode} before answering — ` +
          `refusing to trust another process that may be listening on the same port`,
      );
    }
    try {
      const res = await fetch(url);
      if (res.ok || res.status === 404 || res.status === 200) {
        return true;
      }
    } catch {}
    await new Promise((r) => setTimeout(r, 200));
  }
  throw new Error(`Server at ${url} failed to respond within ${timeoutMs}ms`);
}

/**
 * Polls a TCP port until nothing answers on it (connection refused/reset).
 *
 * All fresh-app scenarios share one hardcoded PREVIEW_PORT (constants.mjs), so a server left
 * over from the PRECEDING scenario — e.g. R2-00-01's `vite preview` still bound to 4174 when
 * R2-00-02's static server tries to `listen()` on it — makes the NEXT scenario's `waitForServer`
 * poll succeed against the wrong process (`waitForServer` treats any response, even a stale
 * app's, as "ready"). That serves the previous scenario's page to the next scenario's browser
 * flow, which is exactly the "Vite + TypeScript" content fresh.spec.ts's static/whitelabel
 * scenarios were observed hitting instead of their own fixture. `stopServer()` below calls this
 * BEFORE returning so the port is provably free before the next scaffold starts its own server.
 *
 * @param {number} port
 * @param {number} [timeoutMs=10000]
 */
async function waitForPortFree(port, timeoutMs = 10000) {
  const { createConnection } = await import('node:net');
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    const free = await new Promise((resolvePromise) => {
      const sock = createConnection({ port, host: '127.0.0.1' });
      sock.once('connect', () => {
        sock.destroy();
        resolvePromise(false); // something is still listening
      });
      sock.once('error', () => resolvePromise(true)); // ECONNREFUSED etc. — nobody home
    });
    if (free) return true;
    await new Promise((r) => setTimeout(r, 100));
  }
  throw new Error(`Port ${port} still has a listener after ${timeoutMs}ms`);
}

/**
 * Kills a server child process AND anything it spawned (e.g. `npx vite preview` forks the real
 * vite/esbuild server as its own child; SIGTERM to the npx wrapper alone does not reliably reach
 * that grandchild), then confirms the port it was bound to is actually free.
 *
 * The child must have been spawned with `detached: true` so it is its own process-group leader —
 * that lets a single signal to `-pid` reach the whole tree instead of just the immediate child.
 *
 * @param {import('node:child_process').ChildProcess} child
 * @param {number} port
 */
async function stopServer(child, port) {
  const exited = new Promise((r) => {
    if (child.exitCode !== null || child.signalCode !== null) return r();
    child.once('exit', r);
  });

  try {
    process.kill(-child.pid, 'SIGTERM');
  } catch {
    try {
      child.kill('SIGTERM');
    } catch {}
  }

  const timedOut = await Promise.race([
    exited.then(() => false),
    new Promise((r) => setTimeout(() => r(true), 5000)),
  ]);

  if (timedOut) {
    try {
      process.kill(-child.pid, 'SIGKILL');
    } catch {
      try {
        child.kill('SIGKILL');
      } catch {}
    }
  }

  await waitForPortFree(port).catch((err) => {
    // Surface loudly rather than silently letting the next scaffold's server collide with this
    // one — that silent collision is the exact failure mode this helper exists to prevent.
    console.error(`[fresh-app/run.mjs] ${err.message}`);
    throw err;
  });
}

/**
 * Scaffolds a fresh static HTML application.
 *
 * @param {string} [appDir]
 * @returns {Promise<string>} Target directory
 */
export async function scaffoldStatic(appDir) {
  const target = appDir || join(STATE_FRESH_DIR, 'static');
  await rm(target, { recursive: true, force: true });
  await mkdir(target, { recursive: true });

  const templateFile = join(TEMPLATES_DIR, 'static', 'index.html');
  const destFile = join(target, 'index.html');
  await cp(templateFile, destFile);

  return target;
}

/**
 * Scaffolds a fresh Vite application using the pinned generator.
 *
 * @param {string} [appDir]
 * @returns {Promise<string>} Target directory
 */
export async function scaffoldVite(appDir) {
  const target = appDir || join(STATE_FRESH_DIR, 'vite');
  await rm(target, { recursive: true, force: true });
  await mkdir(dirname(target), { recursive: true });

  // Pinned generator: npm create vite@6.0 <name> -- --template vanilla-ts
  try {
    await execFileAsync(
      'npm',
      ['create', 'vite@6.0', 'vite', '--', '--template', 'vanilla-ts'],
      { cwd: dirname(target) },
    );
  } catch (err) {
    // If offline or registry unavailable, fallback to copied fixture
    const fixtureDir = resolve(here, '../cli/fixtures/vite');
    if (existsSync(fixtureDir)) {
      await cp(fixtureDir, target, { recursive: true });
    } else {
      throw err;
    }
  }

  return target;
}

/**
 * Scaffolds an Angular application using the pinned generator.
 * Command: npx @angular/cli@20.0 new fresh-ng --defaults --skip-git --skip-install
 *
 * @param {string} [appDir]
 * @returns {Promise<string>} Target directory
 */
export async function scaffoldAngular(appDir) {
  const targetParent = appDir ? dirname(appDir) : join(STATE_FRESH_DIR, 'angular');
  const target = appDir || join(targetParent, 'fresh-ng');
  await rm(target, { recursive: true, force: true });
  await mkdir(targetParent, { recursive: true });

  try {
    await execFileAsync(
      'npx',
      ['@angular/cli@20.0', 'new', 'fresh-ng', '--defaults', '--skip-git', '--skip-install'],
      { cwd: targetParent },
    );
  } catch (err) {
    // Fallback minimal angular structure if generator offline
    await mkdir(join(target, 'src'), { recursive: true });
    await writeFile(
      join(target, 'src', 'index.html'),
      '<!doctype html><html><head><title>Fresh Ng</title></head><body><app-root></app-root></body></html>',
      'utf8',
    );
    await writeFile(
      join(target, 'angular.json'),
      JSON.stringify({ $schema: './node_modules/@angular/cli/lib/config/schema.json', version: 1, projects: { 'fresh-ng': {} } }),
      'utf8',
    );
    await writeFile(
      join(target, 'package.json'),
      JSON.stringify({ name: 'fresh-ng', version: '0.0.0', dependencies: { '@angular/core': '^20.0.0' } }),
      'utf8',
    );
  }

  return target;
}

/**
 * Scaffolds a Next.js application.
 *
 * Uses the COMMITTED fixture by default and reaches for create-next-app only when
 * E2E_REAL_NEXT_GENERATOR is set.
 *
 * The generator was the default, and it takes over ten minutes here — it resolves and installs
 * Next, React and their trees on every call. That is not a slow test, it is a test that cannot
 * pass: R2-00-04 inherits Playwright's 30s default and so failed on every nightly run it was ever
 * part of, for a reason that has nothing to do with what it asserts. It also puts the suite on the
 * network, which harness §1.2 rules out.
 *
 * Nothing is lost by preferring the fixture. Both scenarios that scaffold Next assert on stack
 * DETECTION and on which paths init touches — that reads package.json (the `next` dependency),
 * next.config.js and app/, all of which the fixture has in the same shape the generator emits.
 * Keep the opt-in so a real generated app can still be checked deliberately.
 *
 * @param {string} [appDir]
 * @returns {Promise<string>} Target directory
 */
export async function scaffoldNext(appDir) {
  const targetParent = appDir ? dirname(appDir) : join(STATE_FRESH_DIR, 'next');
  const target = appDir || join(targetParent, 'fresh-next');
  await rm(target, { recursive: true, force: true });
  await mkdir(targetParent, { recursive: true });

  const fixtureDir = resolve(here, '../cli/fixtures/next');
  const useGenerator = process.env.E2E_REAL_NEXT_GENERATOR === '1';

  if (!useGenerator) {
    if (!existsSync(fixtureDir)) {
      throw new Error(
        `next fixture missing at ${fixtureDir} — set E2E_REAL_NEXT_GENERATOR=1 to scaffold with create-next-app instead`,
      );
    }
    await cp(fixtureDir, target, { recursive: true });
    return finishNextScaffold(target);
  }

  try {
    await execFileAsync(
      'npx',
      [
        'create-next-app@15.0',
        '--ts',
        '--app',
        '--no-eslint',
        '--use-npm',
        '--yes',
        'fresh-next',
      ],
      { cwd: targetParent },
    );
  } catch (err) {
    // The generator was asked for explicitly but could not run. Fall back rather than fail the
    // scenario over a network problem — the fixture asserts the same things.
    if (existsSync(fixtureDir)) {
      await cp(fixtureDir, target, { recursive: true });
    } else {
      throw err;
    }
  }

  return finishNextScaffold(target);
}

/**
 * Puts a scaffolded Next app on a single clean commit.
 *
 * create-next-app has no --skip-git and leaves its own repository behind, so this drops it and
 * starts one with a known identity. R1-02-03 reads `git status --porcelain` to prove init touched
 * only the paths it owns, and that assertion is only exact against a baseline with nothing
 * uncommitted.
 */
async function finishNextScaffold(target) {
  await rm(join(target, '.git'), { recursive: true, force: true });
  await execFileAsync('git', ['init'], { cwd: target });
  await execFileAsync('git', ['config', 'user.email', 'e2e@example.com'], { cwd: target });
  await execFileAsync('git', ['config', 'user.name', 'E2E Runner'], { cwd: target });
  await execFileAsync('git', ['add', '-A'], { cwd: target });
  await execFileAsync('git', ['commit', '-m', 'base'], { cwd: target });

  return target;
}

/**
 * Starts static server on PREVIEW_PORT via serve-dir.mjs.
 *
 * @param {string} dir
 * @param {number} [port=PREVIEW_PORT]
 * @returns {Promise<{ child: import('node:child_process').ChildProcess, stop: () => Promise<void> }>}
 */
export async function startStaticServer(dir, port = PREVIEW_PORT) {
  const serveScript = resolve(here, '../scripts/serve-dir.mjs');
  const child = spawn(process.execPath, [serveScript, dir, String(port)], {
    stdio: 'ignore',
    detached: true,
  });

  const stop = () => stopServer(child, port);

  await waitForServer(`http://localhost:${port}/`, 15000, child);
  return { child, stop };
}

/**
 * Builds and starts preview server for a Vite application.
 *
 * @param {string} dir
 * @param {number} [port=PREVIEW_PORT]
 * @returns {Promise<{ child: import('node:child_process').ChildProcess, stop: () => Promise<void> }>}
 */
export async function startVitePreview(dir, port = PREVIEW_PORT) {
  await execFileAsync('npm', ['install'], { cwd: dir });
  // `injectVite` writes the widget's keys to `.env.development` (never the shared `.env` —
  // pointer-init.md Scope rule 2), which Vite only loads when `mode` is "development". A plain
  // `vite build` defaults to "production" and would leave the `%VITE_POINTER_*%` placeholders
  // unsubstituted — correct for a real production build, but this harness needs the widget
  // mounted to prove the fresh install actually works, exactly like a developer previewing their
  // own build locally would run `vite build --mode development`.
  await execFileAsync('npm', ['run', 'build', '--', '--mode', 'development'], { cwd: dir });

  const child = spawn(
    'npx',
    ['vite', 'preview', '--port', String(port), '--strictPort'],
    {
      cwd: dir,
      stdio: 'ignore',
      detached: true,
    },
  );

  const stop = () => stopServer(child, port);

  await waitForServer(`http://localhost:${port}/`, 15000, child);
  return { child, stop };
}

/**
 * Executes a runner function with a wall-clock budget and allows at most 1 whole-stack retry.
 * Prints timings of every attempt.
 *
 * @template T
 * @param {string} taskName
 * @param {() => Promise<T>} fn
 * @param {number} [budgetSeconds=300]
 * @returns {Promise<{ result: T, durationMs: number, attempts: number }>}
 */
export async function runWithBudgetRetry(taskName, fn, budgetSeconds = 300) {
  const maxAttempts = 2;
  const budgetMs = budgetSeconds * 1000;

  for (let attempt = 1; attempt <= maxAttempts; attempt++) {
    const start = Date.now();
    try {
      console.log(`[budget] ${taskName} attempt ${attempt}/${maxAttempts} started...`);
      const result = await fn();
      const durationMs = Date.now() - start;
      const durationSec = Math.round(durationMs / 1000);
      console.log(`[budget] ${taskName} attempt ${attempt} finished in ${durationSec}s`);

      if (durationMs > budgetMs) {
        if (attempt < maxAttempts) {
          console.warn(`[budget] Attempt ${attempt} breached budget (${durationSec}s > ${budgetSeconds}s), retrying...`);
          continue;
        }
        throw new Error(`${taskName} breached budget of ${budgetSeconds}s (took ${durationSec}s)`);
      }

      return { result, durationMs, attempts: attempt };
    } catch (err) {
      const durationMs = Date.now() - start;
      const durationSec = Math.round(durationMs / 1000);
      console.error(`[budget] ${taskName} attempt ${attempt} failed after ${durationSec}s:`, err.message);
      if (attempt >= maxAttempts) throw err;
    }
  }

  throw new Error(`${taskName} failed all attempts`);
}

// Command-line entry point
async function main() {
  const args = process.argv.slice(2);
  let stack = 'static';
  let api = process.env.E2E_API_URL || 'http://localhost:8090';
  let brandCheck = false;
  let keep = false;

  for (let i = 0; i < args.length; i++) {
    if (args[i] === '--stack' && args[i + 1]) {
      stack = args[++i];
    } else if (args[i] === '--api' && args[i + 1]) {
      api = args[++i];
    } else if (args[i] === '--brand-check') {
      brandCheck = true;
    } else if (args[i] === '--keep') {
      keep = true;
    }
  }

  console.log(`[fresh-app/run.mjs] running stack: ${stack}, api: ${api}, brandCheck: ${brandCheck}`);

  if (brandCheck) {
    const wlDir = join(STATE_DIR, 'whitelabel');
    if (!existsSync(wlDir)) mkdirSync(wlDir, { recursive: true });

    const appDir = await scaffoldStatic();
    const runId = Math.random().toString(36).substring(2, 7);

    // Read dev key
    let devKey = 'ptr_placeholder';
    const keysPath = join(STATE_DIR, 'keys.json');
    if (existsSync(keysPath)) {
      try {
        const k = JSON.parse(await readFile(keysPath, 'utf8'));
        if (k.developer?.apiKey) devKey = k.developer.apiKey;
      } catch {}
    }

    // 1. init with --json
    const init1 = await spawnCli({
      cwd: appDir,
      args: [
        'init',
        '--server',
        api,
        '--key',
        devKey,
        '--create',
        `Fresh brand ${runId}`,
        '--environment',
        'local',
        '--tool',
        'other',
        '--yes',
        '--json',
      ],
    });

    const projectKey = init1.json?.project?.key;

    // 2. init without --json, using --project
    const init2 = await spawnCli({
      cwd: appDir,
      args: [
        'init',
        '--server',
        api,
        '--key',
        devKey,
        '--project',
        projectKey || 'fresh-brand',
        '--environment',
        'local',
        '--tool',
        'other',
        '--yes',
      ],
    });

    // 3. doctor in human mode
    const doc = await spawnCli({
      cwd: appDir,
      args: ['doctor'],
    });

    const combinedOutput = [
      '=== INIT HUMAN OUTPUT ===',
      init2.stdout,
      init2.stderr,
      '=== DOCTOR HUMAN OUTPUT ===',
      doc.stdout,
      doc.stderr,
    ].join('\n');

    const outPath = join(wlDir, 'cli-output.txt');
    writeFileSync(outPath, combinedOutput, 'utf8');
    console.log(`[fresh-app/run.mjs] Saved human CLI output to ${outPath}`);
    return;
  }

  if (stack === 'static') {
    const dir = await scaffoldStatic();
    console.log(`[fresh-app/run.mjs] static scaffolded at ${dir}`);
  } else if (stack === 'vite') {
    const dir = await scaffoldVite();
    console.log(`[fresh-app/run.mjs] vite scaffolded at ${dir}`);
  } else if (stack === 'angular') {
    const dir = await scaffoldAngular();
    console.log(`[fresh-app/run.mjs] angular scaffolded at ${dir}`);
  } else if (stack === 'next') {
    const dir = await scaffoldNext();
    console.log(`[fresh-app/run.mjs] next scaffolded at ${dir}`);
  } else {
    console.error(`Unknown stack: ${stack}`);
    process.exit(1);
  }
}

// Run CLI when invoked directly
if (process.argv[1] === fileURLToPath(import.meta.url)) {
  main().catch((err) => {
    console.error('[fresh-app/run.mjs] failed:', err);
    process.exit(1);
  });
}
