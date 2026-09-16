import { test, before, after } from 'node:test';
import * as assert from 'node:assert';
import { exec } from 'node:child_process';
import { promisify } from 'node:util';
import * as fs from 'node:fs/promises';
import * as os from 'node:os';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import * as http from 'node:http';
import {
  resolveApiKey,
  saveGlobalCredential,
  getGlobalCredential,
  removeGlobalCredential,
  normalizeServerOrigin,
  sourceLabel,
  globalCredentialsPath,
} from '../src/credentials.js';

const execAsync = promisify(exec);
const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const cliPath = path.resolve(__dirname, '../dist/cli.js');

async function withTempDir(fn: (dir: string) => Promise<void>) {
  const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'pointer-credentials-test-'));
  try {
    await fn(dir);
  } finally {
    await fs.rm(dir, { recursive: true, force: true });
  }
}

/**
 * Every test below sets `POINTER_CONFIG_DIR` to an isolated temp directory — the one override that
 * redirects both `globalConfigDir()` and `globalCacheDir()` (see credentials.ts) away from a real
 * machine's `~/.config`/`~/.cache`. Never rely on the ambient environment here.
 */
async function withGlobalDir(fn: (globalDir: string) => Promise<void>) {
  const globalDir = await fs.mkdtemp(path.join(os.tmpdir(), 'pointer-credentials-global-'));
  const prev = process.env.POINTER_CONFIG_DIR;
  process.env.POINTER_CONFIG_DIR = globalDir;
  try {
    await fn(globalDir);
  } finally {
    if (prev === undefined) delete process.env.POINTER_CONFIG_DIR;
    else process.env.POINTER_CONFIG_DIR = prev;
    await fs.rm(globalDir, { recursive: true, force: true });
  }
}

// -----------------------------------------------------------------------------------------------
// resolveApiKey: precedence (env > repo > global), in-process
// -----------------------------------------------------------------------------------------------

test('resolveApiKey: nothing resolves when env, repo, and global are all empty', () =>
  withGlobalDir(() =>
    withTempDir(async (repo) => {
      const prevEnv = process.env.POINTER_API_KEY;
      delete process.env.POINTER_API_KEY;
      try {
        const resolved = await resolveApiKey(repo, 'https://example.test');
        assert.strictEqual(resolved.key, undefined);
        assert.strictEqual(resolved.source, null);
      } finally {
        if (prevEnv !== undefined) process.env.POINTER_API_KEY = prevEnv;
      }
    }),
  ));

test('resolveApiKey: the global store answers when nothing else does', () =>
  withGlobalDir(() =>
    withTempDir(async (repo) => {
      const prevEnv = process.env.POINTER_API_KEY;
      delete process.env.POINTER_API_KEY;
      try {
        await saveGlobalCredential('https://example.test', { apiKey: 'ptr_global', displayName: 'Global User' });
        const resolved = await resolveApiKey(repo, 'https://example.test');
        assert.strictEqual(resolved.key, 'ptr_global');
        assert.strictEqual(resolved.source, 'global');
      } finally {
        if (prevEnv !== undefined) process.env.POINTER_API_KEY = prevEnv;
      }
    }),
  ));

test('resolveApiKey: repo credentials.env wins over the global store', () =>
  withGlobalDir(() =>
    withTempDir(async (repo) => {
      const prevEnv = process.env.POINTER_API_KEY;
      delete process.env.POINTER_API_KEY;
      try {
        await saveGlobalCredential('https://example.test', { apiKey: 'ptr_global' });
        await fs.mkdir(path.join(repo, '.pointer'), { recursive: true });
        await fs.writeFile(path.join(repo, '.pointer/credentials.env'), 'POINTER_API_KEY=ptr_repo\n', 'utf8');

        const resolved = await resolveApiKey(repo, 'https://example.test');
        assert.strictEqual(resolved.key, 'ptr_repo');
        assert.strictEqual(resolved.source, 'repo');
      } finally {
        if (prevEnv !== undefined) process.env.POINTER_API_KEY = prevEnv;
      }
    }),
  ));

test('resolveApiKey: POINTER_API_KEY env var wins over both repo and global', () =>
  withGlobalDir(() =>
    withTempDir(async (repo) => {
      const prevEnv = process.env.POINTER_API_KEY;
      process.env.POINTER_API_KEY = 'ptr_env';
      try {
        await saveGlobalCredential('https://example.test', { apiKey: 'ptr_global' });
        await fs.mkdir(path.join(repo, '.pointer'), { recursive: true });
        await fs.writeFile(path.join(repo, '.pointer/credentials.env'), 'POINTER_API_KEY=ptr_repo\n', 'utf8');

        const resolved = await resolveApiKey(repo, 'https://example.test');
        assert.strictEqual(resolved.key, 'ptr_env');
        assert.strictEqual(resolved.source, 'env');
      } finally {
        if (prevEnv === undefined) delete process.env.POINTER_API_KEY;
        else process.env.POINTER_API_KEY = prevEnv;
      }
    }),
  ));

test('resolveApiKey: the global store is keyed by server origin, not by the exact URL string', () =>
  withGlobalDir(() =>
    withTempDir(async (repo) => {
      const prevEnv = process.env.POINTER_API_KEY;
      delete process.env.POINTER_API_KEY;
      try {
        await saveGlobalCredential('https://example.test/some/path', { apiKey: 'ptr_global' });
        const resolved = await resolveApiKey(repo, 'https://example.test/');
        assert.strictEqual(resolved.key, 'ptr_global');
        assert.strictEqual(normalizeServerOrigin('https://example.test/some/path'), 'https://example.test');
      } finally {
        if (prevEnv !== undefined) process.env.POINTER_API_KEY = prevEnv;
      }
    }),
  ));

// -----------------------------------------------------------------------------------------------
// Global store mechanics: 0600 mode, get/save/remove
// -----------------------------------------------------------------------------------------------

test('saveGlobalCredential writes the store file with mode 0600', () =>
  withGlobalDir(async () => {
    await saveGlobalCredential('https://example.test', { apiKey: 'ptr_x' });
    const stat = await fs.stat(globalCredentialsPath());
    assert.strictEqual(stat.mode & 0o777, 0o600);
  }));

test('getGlobalCredential returns the saved entry; removeGlobalCredential deletes it', () =>
  withGlobalDir(async () => {
    await saveGlobalCredential('https://example.test', {
      apiKey: 'ptr_x',
      email: 'dev@example.test',
      displayName: 'Dev',
    });

    const entry = await getGlobalCredential('https://example.test');
    assert.strictEqual(entry?.apiKey, 'ptr_x');
    assert.strictEqual(entry?.email, 'dev@example.test');
    assert.strictEqual(entry?.displayName, 'Dev');
    assert.ok(entry?.savedAt);

    const removed = await removeGlobalCredential('https://example.test');
    assert.strictEqual(removed, true);
    assert.strictEqual(await getGlobalCredential('https://example.test'), undefined);

    // Removing again reports nothing-to-remove rather than throwing.
    assert.strictEqual(await removeGlobalCredential('https://example.test'), false);
  }));

test('saving a second server does not disturb the first', () =>
  withGlobalDir(async () => {
    await saveGlobalCredential('https://a.example.test', { apiKey: 'ptr_a' });
    await saveGlobalCredential('https://b.example.test', { apiKey: 'ptr_b' });

    assert.strictEqual((await getGlobalCredential('https://a.example.test'))?.apiKey, 'ptr_a');
    assert.strictEqual((await getGlobalCredential('https://b.example.test'))?.apiKey, 'ptr_b');

    await removeGlobalCredential('https://a.example.test');
    assert.strictEqual(await getGlobalCredential('https://a.example.test'), undefined);
    assert.strictEqual((await getGlobalCredential('https://b.example.test'))?.apiKey, 'ptr_b');
  }));

test('sourceLabel gives a human label for every source, including none', () => {
  assert.strictEqual(sourceLabel('env'), 'env var');
  assert.strictEqual(sourceLabel('repo'), 'repo credentials.env');
  assert.strictEqual(sourceLabel('global'), 'global store');
  assert.strictEqual(sourceLabel(null), 'none');
});

// -----------------------------------------------------------------------------------------------
// `pointer login` / `logout` / `whoami`, end-to-end against a stub server
// -----------------------------------------------------------------------------------------------

let server: http.Server;
let serverUrl: string;

before(async () => {
  server = http.createServer((req, res) => {
    res.setHeader('Content-Type', 'application/json');
    if (req.url === '/api/branding') {
      res.end(JSON.stringify({ productName: 'Pointer Test', urls: { app: 'http://test' } }));
    } else if (req.url === '/api/auth/login-with-key') {
      let body = '';
      req.on('data', (c) => (body += c));
      req.on('end', () => {
        const apiKey = (() => {
          try {
            return JSON.parse(body || '{}').apiKey;
          } catch {
            return undefined;
          }
        })();
        if (apiKey === 'ptr_good') {
          res.end(
            JSON.stringify({
              data: { status: 'ok', token: 'jwt-for-test', user: { displayName: 'Test User', email: 'test@example.com' } },
              isSuccess: true,
            }),
          );
        } else {
          res.writeHead(401);
          res.end(JSON.stringify({ message: 'Invalid API key' }));
        }
      });
      return;
    } else if (req.url === '/api/auth/me') {
      if (req.headers.authorization === 'Bearer jwt-for-test') {
        res.end(JSON.stringify({ data: { displayName: 'Test User', email: 'test@example.com' }, isSuccess: true }));
      } else {
        res.writeHead(401);
        res.end(JSON.stringify({ message: 'Unauthorized' }));
      }
    } else {
      res.writeHead(404);
      res.end(JSON.stringify({ message: 'Not found' }));
    }
  });
  await new Promise<void>((resolve) =>
    server.listen(0, () => {
      const addr = server.address() as import('net').AddressInfo;
      serverUrl = `http://localhost:${addr.port}`;
      resolve();
    }),
  );
});

after(() => {
  server.close();
});

function envFor(globalDir: string): NodeJS.ProcessEnv {
  return { ...process.env, POINTER_CONFIG_DIR: globalDir };
}

test('login saves the key globally; whoami reports it; logout removes it', () =>
  withTempDir(async (repo) =>
    withGlobalDir(async (globalDir) => {
      const { stdout: loginOut } = await execAsync(
        `node ${cliPath} login --key ptr_good --server ${serverUrl}`,
        { cwd: repo, env: envFor(globalDir) },
      );
      assert.match(loginOut, /Signed in to/);
      assert.match(loginOut, /saved for all repos on this machine/);

      const store = JSON.parse(await fs.readFile(path.join(globalDir, 'credentials.json'), 'utf8'));
      assert.strictEqual(store[new URL(serverUrl).origin].apiKey, 'ptr_good');

      const { stdout: whoamiOut } = await execAsync(`node ${cliPath} whoami --server ${serverUrl} --json`, {
        cwd: repo,
        env: envFor(globalDir),
      });
      const whoami = JSON.parse(whoamiOut.trim().split('\n').pop()!);
      assert.strictEqual(whoami.ok, true);
      assert.strictEqual(whoami.source, 'global');
      assert.strictEqual(whoami.displayName, 'Test User');
      assert.strictEqual(whoami.email, 'test@example.com');

      const { stdout: logoutOut } = await execAsync(`node ${cliPath} logout --server ${serverUrl}`, {
        cwd: repo,
        env: envFor(globalDir),
      });
      assert.match(logoutOut, /Removed the saved key/);

      await assert.rejects(
        execAsync(`node ${cliPath} whoami --server ${serverUrl} --json`, { cwd: repo, env: envFor(globalDir) }),
        (err: any) => {
          assert.strictEqual(err.code, 3);
          return true;
        },
        'whoami must fail once the key is gone',
      );
    }),
  ));

test('whoami reports source env when POINTER_API_KEY is set, even with a global entry present', () =>
  withTempDir(async (repo) =>
    withGlobalDir(async (globalDir) => {
      await execAsync(`node ${cliPath} login --key ptr_good --server ${serverUrl}`, {
        cwd: repo,
        env: envFor(globalDir),
      });

      const { stdout } = await execAsync(`node ${cliPath} whoami --server ${serverUrl} --json`, {
        cwd: repo,
        env: { ...envFor(globalDir), POINTER_API_KEY: 'ptr_good' },
      });
      const whoami = JSON.parse(stdout.trim().split('\n').pop()!);
      assert.strictEqual(whoami.source, 'env');
    }),
  ));

test('logout on a server with no saved key reports nothing removed', () =>
  withTempDir(async (repo) =>
    withGlobalDir(async (globalDir) => {
      const { stdout } = await execAsync(`node ${cliPath} logout --server ${serverUrl} --json`, {
        cwd: repo,
        env: envFor(globalDir),
      });
      const json = JSON.parse(stdout.trim());
      assert.strictEqual(json.removed, false);
    }),
  ));

test('login --scope repo writes .pointer/credentials.env and leaves the global store alone', () =>
  withTempDir(async (repo) =>
    withGlobalDir(async (globalDir) => {
      const { stdout } = await execAsync(
        `node ${cliPath} login --key ptr_good --server ${serverUrl} --scope repo`,
        { cwd: repo, env: envFor(globalDir) },
      );
      assert.match(stdout, /this repo only/);
      const creds = await fs.readFile(path.join(repo, '.pointer/credentials.env'), 'utf8');
      assert.match(creds, /POINTER_API_KEY=ptr_good/);
      assert.match(creds, new RegExp(`POINTER_SERVER=${serverUrl.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}`));
      await assert.rejects(fs.access(path.join(globalDir, 'credentials.json')), 'global store must not be created');
    }),
  ));

test('login --scope with an unknown value exits 2', () =>
  withTempDir(async (repo) =>
    withGlobalDir(async (globalDir) => {
      await assert.rejects(
        execAsync(`node ${cliPath} login --key ptr_good --server ${serverUrl} --scope machine`, { cwd: repo, env: envFor(globalDir) }),
        (err: any) => err.code === 2 && /Invalid --scope/.test(err.stderr),
      );
    }),
  ));
