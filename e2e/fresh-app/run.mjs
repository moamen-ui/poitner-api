// Fresh-app orchestration driver.
// Handles scaffolding, building, serving, and driving fresh app test flows.
// Contract: docs/roadmap/testing/00-HARNESS.md §2, docs/roadmap/testing/R1-02-tests.md
import { execFile, spawn } from 'node:child_process';
import { promisify } from 'node:util';
import { rm, mkdir, cp, readFile, writeFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { PORTS } from '../scripts/lib/constants.mjs';

const execFileAsync = promisify(execFile);

const here = dirname(fileURLToPath(import.meta.url));
const ROOT_DIR = resolve(here, '../..');
const STATE_FRESH_DIR = resolve(here, '../state/fresh');
const TEMPLATES_DIR = resolve(here, 'templates');
const PREVIEW_PORT = PORTS.fresh || 4174;

/**
 * Polls an HTTP endpoint until it answers 200 or timeout expires.
 *
 * @param {string} url
 * @param {number} [timeoutMs=15000]
 */
export async function waitForServer(url, timeoutMs = 15000) {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
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
 * Scaffolds a Next.js application using the pinned create-next-app generator.
 *
 * @param {string} [appDir]
 * @returns {Promise<string>} Target directory
 */
export async function scaffoldNext(appDir) {
  const targetParent = appDir ? dirname(appDir) : join(STATE_FRESH_DIR, 'next');
  const target = appDir || join(targetParent, 'fresh-next');
  await rm(target, { recursive: true, force: true });
  await mkdir(targetParent, { recursive: true });

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
    // Fallback to local next fixture if generator command fails
    const fixtureDir = resolve(here, '../cli/fixtures/next');
    if (existsSync(fixtureDir)) {
      await cp(fixtureDir, target, { recursive: true });
    } else {
      throw err;
    }
  }

  // create-next-app has no --skip-git: rm -rf .git, then git init && git config user.email
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
    detached: false,
  });

  const stop = async () => {
    try {
      child.kill('SIGTERM');
    } catch {}
  };

  await waitForServer(`http://localhost:${port}/`);
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
  await execFileAsync('npm', ['run', 'build'], { cwd: dir });

  const child = spawn(
    'npx',
    ['vite', 'preview', '--port', String(port), '--strictPort'],
    {
      cwd: dir,
      stdio: 'ignore',
    },
  );

  const stop = async () => {
    try {
      child.kill('SIGTERM');
    } catch {}
  };

  await waitForServer(`http://localhost:${port}/`);
  return { child, stop };
}

// Command-line entry point
async function main() {
  const args = process.argv.slice(2);
  let stack = 'static';
  let api = process.env.E2E_API_URL || 'http://localhost:8090';

  for (let i = 0; i < args.length; i++) {
    if (args[i] === '--stack' && args[i + 1]) {
      stack = args[++i];
    } else if (args[i] === '--api' && args[i + 1]) {
      api = args[++i];
    }
  }

  console.log(`[fresh-app/run.mjs] running stack: ${stack}, api: ${api}`);

  if (stack === 'static') {
    const dir = await scaffoldStatic();
    console.log(`[fresh-app/run.mjs] static scaffolded at ${dir}`);
  } else if (stack === 'vite') {
    const dir = await scaffoldVite();
    console.log(`[fresh-app/run.mjs] vite scaffolded at ${dir}`);
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
