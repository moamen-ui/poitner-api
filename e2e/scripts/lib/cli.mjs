// Helper for invoking the CLI under test.
// Contract: docs/roadmap/testing/00-HARNESS.md §6
import { spawn } from 'node:child_process';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = resolve(here, '../../..');
const DEFAULT_CLI_ENTRY = resolve(REPO_ROOT, 'cli/dist/cli.js');

/**
 * Spawns the CLI executable.
 *
 * @param {object} options
 * @param {string} options.cwd Working directory
 * @param {string[]} [options.args] Arguments array
 * @param {Record<string, string>} [options.env] Environment variables
 * @returns {Promise<{ stdout: string, stderr: string, code: number, json?: any }>}
 */
export async function spawnCli({ cwd, args = [], env = {} }) {
  const cliEntry = process.env.CLI_ENTRY || DEFAULT_CLI_ENTRY;

  let execCmd = process.execPath;
  let execArgs = [];

  if (cliEntry.startsWith('node ')) {
    execCmd = 'node';
    execArgs = [cliEntry.slice(5).trim(), ...args];
  } else {
    execCmd = process.execPath;
    execArgs = [cliEntry, ...args];
  }

  return new Promise((resolvePromise, reject) => {
    const child = spawn(execCmd, execArgs, {
      cwd,
      env: {
        ...process.env,
        ...env,
      },
      stdio: ['pipe', 'pipe', 'pipe'],
    });

    let stdout = '';
    let stderr = '';

    child.stdout.on('data', (data) => {
      stdout += data.toString();
    });

    child.stderr.on('data', (data) => {
      stderr += data.toString();
    });

    child.on('error', reject);

    child.on('close', (code) => {
      let json;
      const trimmed = stdout.trim();
      if (trimmed) {
        try {
          json = JSON.parse(trimmed);
        } catch {
          // If multi-line stdout, find the JSON line (last line starting with { and ending with })
          const lines = trimmed.split('\n');
          for (let i = lines.length - 1; i >= 0; i--) {
            const line = lines[i].trim();
            if (line.startsWith('{') && line.endsWith('}')) {
              try {
                json = JSON.parse(line);
                break;
              } catch {}
            }
          }
        }
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
