import { test, before, after } from 'node:test';
import * as assert from 'node:assert';
import { exec } from 'node:child_process';
import { promisify } from 'node:util';
import * as fs from 'node:fs/promises';
import * as os from 'node:os';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import * as http from 'node:http';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const cliPath = path.resolve(__dirname, '../dist/cli.js');

const execAsync = promisify(exec);

let server: http.Server;
let serverUrl: string;

before(async () => {
    server = http.createServer((req, res) => {
        res.setHeader('Content-Type', 'application/json');
        
        if (req.url === '/api/branding') {
            res.end(JSON.stringify({ productName: 'Pointer Test', urls: { app: 'http://test' } }));
        } else if (req.url === '/api/auth/me' || req.url === '/api/auth/login-with-key') {
            if (req.headers.authorization === 'Bearer ptr_good') {
                res.end(JSON.stringify({ displayName: 'Test User' }));
            } else {
                res.writeHead(401);
                res.end(JSON.stringify({ message: 'Unauthorized' }));
            }
        } else if (req.url === '/api/admin/projects') {
            if (req.method === 'POST') {
                res.end(JSON.stringify({ key: 'my-app', name: 'My App' }));
            } else {
                res.end(JSON.stringify([]));
            }
        } else if (req.url === '/skill.md' || req.url === '/pointer-init.md' || req.url === '/pointer.sh') {
            res.setHeader('Content-Type', 'text/plain');
            res.end('skill content');
        } else if (req.url?.startsWith('/api/projects/') && req.url.endsWith('/stack')) {
            res.end(JSON.stringify({}));
        } else if (req.url === '/api/events') {
            res.end(JSON.stringify({}));
        } else {
            res.writeHead(404);
            res.end(JSON.stringify({ message: 'Not found' }));
        }
    });
    await new Promise<void>(resolve => server.listen(0, () => {
        const addr = server.address() as import('net').AddressInfo;
        serverUrl = `http://localhost:${addr.port}`;
        resolve();
    }));
});

after(() => {
    server.close();
});

async function withTempDir(fn: (dir: string) => Promise<void>) {
  const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'pointer-init-yes-test-'));
  try {
    await fn(dir);
  } finally {
    await fs.rm(dir, { recursive: true, force: true });
  }
}

test('init --yes without --key exits 2', () => withTempDir(async (dir) => {
  try {
    await execAsync(`node ${cliPath} init --yes --create "My App" --server ${serverUrl}`, { cwd: dir });
    assert.fail('Should have exited');
  } catch (err: any) {
    assert.strictEqual(err.code, 2);
    assert.match(err.stdout + err.stderr, /--key is required/);
  }
}));

test('init --yes with bad key exits 3', () => withTempDir(async (dir) => {
  try {
    await execAsync(`node ${cliPath} init --yes --key ptr_bogus --create "My App" --server ${serverUrl}`, { cwd: dir });
    assert.fail('Should have exited');
  } catch (err: any) {
    assert.strictEqual(err.code, 3);
  }
}));

test('init --json prints JSON and nothing else', () => withTempDir(async (dir) => {
  const { stdout } = await execAsync(`node ${cliPath} init --json --key ptr_good --create "My App" --server ${serverUrl}`, { cwd: dir });
  const lines = stdout.trim().split('\n');
  assert.strictEqual(lines.length, 1, 'Should output exactly one line');
  const json = JSON.parse(lines[0]);
  assert.strictEqual(json.ok, true);
  assert.strictEqual(json.project.name, "My App");
}));

test('unknown command exits 2', () => withTempDir(async (dir) => {
  try {
    await execAsync(`node ${cliPath} unknowncmd`, { cwd: dir });
    assert.fail('Should have exited');
  } catch (err: any) {
    assert.strictEqual(err.code, 2);
    assert.match(err.stdout + err.stderr, /Unknown command/);
  }
}));

test('--help works', () => withTempDir(async (dir) => {
  const { stdout } = await execAsync(`node ${cliPath} --help`, { cwd: dir });
  assert.match(stdout, /Usage: pointer/);
}));
