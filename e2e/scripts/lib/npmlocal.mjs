import { execSync, execFileSync, spawn } from 'node:child_process';
import { existsSync, readFileSync, mkdirSync } from 'node:fs';
import { join, dirname, resolve } from 'node:path';
import { homedir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { PORTS } from './constants.mjs';

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);
const e2eRoot = resolve(__dirname, '..', '..');
const repoRoot = resolve(e2eRoot, '..');

export const REGISTRY_URL =
  (process.env.CLIENTS_REGISTRY || process.env.VERDACCIO_URL || `http://localhost:${PORTS.registry || 4873}`)
    .trim()
    .replace(/\/+$/, '');

/**
 * Captures ~/.npmrc sha256 hash and npm registry config for before/after comparison (AC-7).
 * If ~/.npmrc does not exist, sha256 is recorded as 'absent'.
 */
export function npmrcFingerprint() {
  const npmrcPath = join(homedir(), '.npmrc');
  let sha256 = 'absent';
  if (existsSync(npmrcPath)) {
    try {
      const content = readFileSync(npmrcPath);
      sha256 = createHash('sha256').update(content).digest('hex');
    } catch {
      sha256 = 'absent';
    }
  }

  let registry = '';
  try {
    registry = execSync('npm config get registry --location=user', { encoding: 'utf8' }).trim();
  } catch {
    try {
      registry = execSync('npm config get registry', { encoding: 'utf8' }).trim();
    } catch {
      registry = 'unknown';
    }
  }

  return { sha256, registry };
}

/**
 * Queries package versions from Verdaccio (or configured registry) via npm view --json.
 * Returns an array of version strings.
 */
export async function viewVersions(pkg, { registry = REGISTRY_URL, env = process.env, cwd = repoRoot } = {}) {
  const npmStateDir = join(e2eRoot, 'state', 'npm');
  const childEnv = {
    ...process.env,
    ...env,
    npm_config_userconfig: env.npm_config_userconfig || join(npmStateDir, '.npmrc'),
    npm_config_cache: env.npm_config_cache || npmStateDir,
  };

  try {
    const stdout = execFileSync('npm', ['view', pkg, 'versions', '--registry', registry, '--json'], {
      cwd,
      env: childEnv,
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'pipe'],
    }).trim();

    if (!stdout) return [];
    const parsed = JSON.parse(stdout);
    if (Array.isArray(parsed)) return parsed;
    if (typeof parsed === 'string') return [parsed];
    return [];
  } catch {
    return [];
  }
}

/**
 * Runs `npm run clients:local` from repo root and parses the output.
 * Returns { version, installCommands[], angularInstallCommand, stdout, stderr, code }.
 */
export async function publishLocal({ cwd = repoRoot, env = {}, unsetEnv = [], timeout = 180_000 } = {}) {
  const npmStateDir = join(e2eRoot, 'state', 'npm');
  mkdirSync(npmStateDir, { recursive: true });

  const childEnv = {
    ...process.env,
    CLIENTS_REGISTRY: REGISTRY_URL,
    npm_config_userconfig: join(npmStateDir, '.npmrc'),
    npm_config_cache: npmStateDir,
    ...env,
  };

  if (Array.isArray(unsetEnv)) {
    for (const key of unsetEnv) {
      delete childEnv[key];
    }
  }

  return new Promise((resolvePromise) => {
    const proc = spawn('npm', ['run', 'clients:local'], {
      cwd,
      env: childEnv,
      stdio: ['ignore', 'pipe', 'pipe'],
    });

    let stdout = '';
    let stderr = '';

    proc.stdout.on('data', (d) => {
      stdout += d.toString();
    });

    proc.stderr.on('data', (d) => {
      stderr += d.toString();
    });

    let timer = null;
    if (timeout > 0) {
      timer = setTimeout(() => {
        proc.kill();
        resolvePromise({
          code: 124,
          stdout,
          stderr: stderr + `\nTimeout of ${timeout}ms exceeded`,
          version: '',
          installCommands: [],
          angularInstallCommand: '',
        });
      }, timeout);
    }

    proc.on('close', (code) => {
      if (timer) clearTimeout(timer);

      // Parse version V: 0.0.0-local.<unix>
      const versionMatch = stdout.match(/0\.0\.0-local\.\d+/);
      const version = versionMatch ? versionMatch[0] : '';

      // Parse install commands (lines containing --no-save)
      const lines = stdout.split('\n').map((l) => l.trim());
      const installCommands = lines.filter((l) => l.includes('--no-save'));
      const angularInstallCommand =
        installCommands.find((c) => c.includes('pointer-angular')) || '';

      resolvePromise({
        code: code ?? 0,
        stdout,
        stderr,
        version,
        installCommands,
        angularInstallCommand,
      });
    });

    proc.on('error', (err) => {
      if (timer) clearTimeout(timer);
      resolvePromise({
        code: 1,
        stdout,
        stderr: stderr + `\n${err.message}`,
        version: '',
        installCommands: [],
        angularInstallCommand: '',
      });
    });
  });
}
