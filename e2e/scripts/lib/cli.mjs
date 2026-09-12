import { spawn } from 'node:child_process';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const e2eRoot = resolve(here, '..', '..');
const repoRoot = resolve(e2eRoot, '..');

export const CLI_ENTRY = process.env.CLI_ENTRY || resolve(repoRoot, 'cli', 'dist', 'cli.js');

/**
 * Spawns the CLI under test without throwing on non-zero exit codes.
 * Returns { stdout, stderr, code, json? }.
 */
export async function spawnCli(optionsOrArgs, maybeOptions = {}) {
  let cwd = process.cwd();
  let args = [];
  let env = {};
  let timeout = 30_000;

  if (Array.isArray(optionsOrArgs)) {
    args = optionsOrArgs;
    cwd = maybeOptions.cwd || cwd;
    env = maybeOptions.env || env;
    timeout = maybeOptions.timeout || timeout;
  } else if (typeof optionsOrArgs === 'string') {
    args = optionsOrArgs.split(' ').filter(Boolean);
    cwd = maybeOptions.cwd || cwd;
    env = maybeOptions.env || env;
    timeout = maybeOptions.timeout || timeout;
  } else if (typeof optionsOrArgs === 'object' && optionsOrArgs !== null) {
    cwd = optionsOrArgs.cwd || cwd;
    args = optionsOrArgs.args || [];
    env = optionsOrArgs.env || env;
    timeout = optionsOrArgs.timeout || timeout;
  }

  let command = process.execPath;
  let finalArgs = [];

  const entry = process.env.CLI_ENTRY || CLI_ENTRY;
  const parts = entry.trim().split(/\s+/);
  if (parts[0] === 'node') {
    command = process.execPath;
    finalArgs = [...parts.slice(1), ...args];
  } else {
    command = process.execPath;
    finalArgs = [parts[0], ...args];
  }

  return new Promise((resolvePromise, rejectPromise) => {
    const proc = spawn(command, finalArgs, {
      cwd,
      env: { ...process.env, ...env },
      stdio: ['ignore', 'pipe', 'pipe'],
    });

    let stdout = '';
    let stderr = '';

    proc.stdout.on('data', (chunk) => {
      stdout += chunk.toString();
    });

    proc.stderr.on('data', (chunk) => {
      stderr += chunk.toString();
    });

    let timer = null;
    if (timeout > 0) {
      timer = setTimeout(() => {
        proc.kill();
        rejectPromise(new Error(`CLI execution timed out after ${timeout}ms: ${command} ${finalArgs.join(' ')}`));
      }, timeout);
    }

    proc.on('error', (err) => {
      if (timer) clearTimeout(timer);
      rejectPromise(err);
    });

    proc.on('close', (code) => {
      if (timer) clearTimeout(timer);
      let json;
      try {
        json = JSON.parse(stdout.trim());
      } catch {
        json = undefined;
      }

      resolvePromise({
        stdout,
        stderr,
        code: code ?? 0,
        json,
      });
    });
  });
}
