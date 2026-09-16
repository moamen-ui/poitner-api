import { test } from 'node:test';
import * as assert from 'node:assert';
import { execFile, execFileSync } from 'node:child_process';
import { promisify } from 'node:util';
import * as fs from 'node:fs/promises';
import * as os from 'node:os';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';

const execFileAsync = promisify(execFile);
const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const shPath = path.resolve(__dirname, '../../API/wwwroot/pointer.sh');

async function withTempDir(fn: (dir: string) => Promise<void>) {
  const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'pointer-sh-test-'));
  try {
    await fn(dir);
  } finally {
    await fs.rm(dir, { recursive: true, force: true });
  }
}

async function installShTo(dir: string, config: unknown) {
  const pointerDir = path.join(dir, '.pointer');
  await fs.mkdir(pointerDir, { recursive: true });
  const script = await fs.readFile(shPath, 'utf8');
  await fs.writeFile(path.join(pointerDir, 'pointer.sh'), script, { mode: 0o755 });
  await fs.writeFile(path.join(pointerDir, 'config.json'), JSON.stringify(config), 'utf8');
  await fs.writeFile(
    path.join(pointerDir, 'credentials.env'),
    'POINTER_API_KEY=ptr_x\nPOINTER_SERVER=http://127.0.0.1:1\n',
    'utf8',
  );
}

test('pointer.sh is valid bash syntax (bash -n)', () => {
  // Never skip this — it is the one check that catches a shell syntax error before it reaches a
  // consumer repo, since nothing else in the CLI's own test suite parses the served shell script.
  execFileSync('bash', ['-n', shPath]);
});

test('pointer.sh: a multi-project config with no -p prints the configured keys and exits 2', () =>
  withTempDir(async (dir) => {
    await installShTo(dir, {
      server: 'http://127.0.0.1:1',
      projects: { a: { path: 'apps/a' }, b: { path: 'apps/b' } },
    });

    await assert.rejects(
      execFileAsync('bash', [path.join(dir, '.pointer/pointer.sh'), 'list'], { cwd: dir }),
      (err: any) => {
        assert.strictEqual(err.code, 2);
        assert.match(err.stderr, /Several projects configured/);
        assert.match(err.stderr, /a, b|b, a/);
        return true;
      },
    );
  }));

test('pointer.sh: -p <key> resolves the project and proceeds past the multi-project check', () =>
  withTempDir(async (dir) => {
    await installShTo(dir, {
      server: 'http://127.0.0.1:1',
      projects: { a: { path: 'apps/a' }, b: { path: 'apps/b' } },
    });

    await assert.rejects(
      execFileAsync('bash', [path.join(dir, '.pointer/pointer.sh'), '-p', 'a', 'list'], { cwd: dir }),
      (err: any) => {
        // Fails on the network call (there is no real server) — the point is it got PAST the
        // "several projects configured" refusal, which a bare `list` (no -p) hits instead.
        assert.doesNotMatch(err.stderr ?? '', /Several projects configured/);
        return true;
      },
    );
  }));

test('pointer.sh: POINTER_PROJECT env resolves the project just like -p', () =>
  withTempDir(async (dir) => {
    await installShTo(dir, {
      server: 'http://127.0.0.1:1',
      projects: { a: { path: 'apps/a' }, b: { path: 'apps/b' } },
    });

    await assert.rejects(
      execFileAsync('bash', [path.join(dir, '.pointer/pointer.sh'), 'list'], {
        cwd: dir,
        env: { ...process.env, POINTER_PROJECT: 'b' },
      }),
      (err: any) => {
        assert.doesNotMatch(err.stderr ?? '', /Several projects configured/);
        return true;
      },
    );
  }));

test('pointer.sh: a single-project config never hits the multi-project check', () =>
  withTempDir(async (dir) => {
    await installShTo(dir, { server: 'http://127.0.0.1:1', project: 'solo' });

    await assert.rejects(
      execFileAsync('bash', [path.join(dir, '.pointer/pointer.sh'), 'list'], { cwd: dir }),
      (err: any) => {
        assert.doesNotMatch(err.stderr ?? '', /Several projects configured/);
        return true;
      },
    );
  }));
