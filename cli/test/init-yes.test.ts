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
            res.end(JSON.stringify({
                productName: 'Pointer Test',
                urls: { app: 'http://test' },
                extension: { storeUrl: 'https://chromewebstore.google.com/detail/test', zipUrl: '' },
            }));
        } else if (req.url === '/api/auth/login-with-key') {
            // Models the real endpoint: an API key is exchanged for a JWT, it is NOT a bearer token.
            // The earlier stub accepted the key as `Authorization: Bearer`, which let a CLI bug
            // (calling /api/auth/me with the raw key) pass here and fail against every real server.
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
                            data: { status: 'ok', token: 'jwt-for-test', user: { displayName: 'Test User', roleName: 'Developer' } },
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
                res.end(JSON.stringify({ data: { displayName: 'Test User', roleName: 'Developer' }, isSuccess: true }));
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

// `exec` gives the child a pipe for stdin, never a TTY — which is exactly the condition under test,
// and the same one a user hits from CI, a pipe, or an editor-embedded shell.
//
// Before the guard this exited 0 having written nothing: readline's question() never resolves on
// EOF, so the event loop drained and node reported success. A silent no-op that claims to have
// worked is worse than any crash, because there is nothing to search for when it happens.
test('interactive init without a TTY refuses loudly instead of exiting 0', () => withTempDir(async (dir) => {
  try {
    await execAsync(`node ${cliPath} init --server ${serverUrl}`, { cwd: dir });
    assert.fail('a non-interactive init must not report success');
  } catch (err: any) {
    assert.strictEqual(err.code, 2, 'must exit 2 (usage error), not 0');
    const out = err.stdout + err.stderr;
    assert.match(out, /stdin is not a terminal/i, 'must say why it cannot continue');
    assert.match(out, /--yes/, 'must point at the non-interactive escape hatch');
  }

  // And it must not have half-written an install on its way out.
  await assert.rejects(fs.stat(path.join(dir, '.pointer')), 'a refused init must leave no .pointer/');
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
  // A first install (no prior .pointer/config.json) is reported as such — the counterpart to a
  // "join", which reports mode: 'join' below.
  assert.strictEqual(json.mode, 'install');
}));

test('a default --yes run records delivery: embed', () => withTempDir(async (dir) => {
  const { stdout } = await execAsync(`node ${cliPath} init --yes --key ptr_good --create "My App" --server ${serverUrl}`, { cwd: dir });
  assert.match(stdout, /is set up/);
  const config = JSON.parse(await fs.readFile(path.join(dir, '.pointer/config.json'), 'utf8'));
  assert.strictEqual(config.delivery, 'embed');
}));

/**
 * A "join": .pointer/config.json already names a server and a project (written by whoever ran
 * `init` here first, then committed). A second developer cloning the repo should only be asked
 * for their API key — server, project, environment, AI tool and delivery are all read back from
 * the committed config, and nothing is injected (the embed snippet is already in the app's
 * committed source).
 */
test('init --yes joins an already-configured repo, asking only for the key', () => withTempDir(async (dir) => {
  await fs.mkdir(path.join(dir, '.pointer'), { recursive: true });
  await fs.writeFile(
    path.join(dir, '.pointer/config.json'),
    JSON.stringify({
      server: serverUrl,
      project: 'existing',
      environment: 'local',
      aiTool: 'claude-code',
      delivery: 'embed',
    }),
    'utf8',
  );
  const indexPath = path.join(dir, 'index.html');
  const original = '<html><head></head><body></body></html>';
  await fs.writeFile(indexPath, original, 'utf8');

  // No --project, no --create: a join must succeed with --key alone.
  const { stdout } = await execAsync(`node ${cliPath} init --yes --key ptr_good --server ${serverUrl}`, { cwd: dir });
  assert.match(stdout, /Joined/);

  const html = await fs.readFile(indexPath, 'utf8');
  assert.strictEqual(html, original, 'a join must never inject into the app');
  assert.doesNotMatch(html, /<pointer-feedback/);

  const creds = await fs.readFile(path.join(dir, '.pointer/credentials.env'), 'utf8');
  assert.match(creds, /POINTER_API_KEY=ptr_good/);

  const config = JSON.parse(await fs.readFile(path.join(dir, '.pointer/config.json'), 'utf8'));
  assert.strictEqual(config.project, 'existing', 'the joined project must not change');

  // Skills and pointer.sh are gitignored now, so a join is the thing that installs them.
  await fs.access(path.join(dir, '.claude/skills/pointer-feedback/SKILL.md'));
  await fs.access(path.join(dir, '.pointer/pointer.sh'));
}));

test('init --json in join mode reports mode: join and does not ask for --project', () => withTempDir(async (dir) => {
  await fs.mkdir(path.join(dir, '.pointer'), { recursive: true });
  await fs.writeFile(
    path.join(dir, '.pointer/config.json'),
    JSON.stringify({
      server: serverUrl,
      project: 'existing',
      environment: 'local',
      aiTool: 'claude-code',
      delivery: 'embed',
    }),
    'utf8',
  );

  const { stdout } = await execAsync(`node ${cliPath} init --json --key ptr_good --server ${serverUrl}`, { cwd: dir });
  const json = JSON.parse(stdout.trim().split('\n')[0]);
  assert.strictEqual(json.mode, 'join');
  assert.strictEqual(json.project.key, 'existing');
  assert.strictEqual(json.injected, false);
}));

test('--delivery bogus exits 2', () => withTempDir(async (dir) => {
  try {
    await execAsync(`node ${cliPath} init --yes --key ptr_good --create "My App" --server ${serverUrl} --delivery bogus`, { cwd: dir });
    assert.fail('Should have exited');
  } catch (err: any) {
    assert.strictEqual(err.code, 2);
    assert.match(err.stdout + err.stderr, /Invalid --delivery/);
  }
}));

test('init --yes --delivery extension skips injection and records delivery', () => withTempDir(async (dir) => {
  const indexPath = path.join(dir, 'index.html');
  const original = '<html><head></head><body></body></html>';
  await fs.writeFile(indexPath, original, 'utf8');

  const { stdout } = await execAsync(
    `node ${cliPath} init --yes --key ptr_good --create "My App" --server ${serverUrl} --delivery extension`,
    { cwd: dir },
  );

  const html = await fs.readFile(indexPath, 'utf8');
  assert.strictEqual(html, original, 'extension delivery must not inject anything into the app');
  assert.doesNotMatch(html, /<pointer-feedback/);

  const config = JSON.parse(await fs.readFile(path.join(dir, '.pointer/config.json'), 'utf8'));
  assert.strictEqual(config.delivery, 'extension');

  assert.match(stdout, /chromewebstore\.google\.com\/detail\/test/, 'summary must print the Web Store URL');
}));

test('init --json --delivery extension reports delivery and the extension URLs', () => withTempDir(async (dir) => {
  const { stdout } = await execAsync(
    `node ${cliPath} init --json --key ptr_good --create "My App" --server ${serverUrl} --delivery extension`,
    { cwd: dir },
  );
  const json = JSON.parse(stdout.trim().split('\n')[0]);
  assert.strictEqual(json.delivery, 'extension');
  assert.strictEqual(json.extension.storeUrl, 'https://chromewebstore.google.com/detail/test');
  assert.strictEqual(json.extension.zipUrl, '');
  assert.strictEqual(json.injected, false);
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

// -----------------------------------------------------------------------------------------------
// Multi-project (monorepo) support: `init --path`
// -----------------------------------------------------------------------------------------------

test('init --yes --path adds a second app to a multi-project config (real Nx-app shape: src/index.html)', () => withTempDir(async (dir) => {
  // The real shape every app in an Nx workspace (e.g. tuwaiq-mono-spa) has: index.html lives
  // under src/, not at the app's own root, and there is no package.json inside the app dir either
  // — apps/a additionally has an Nx project.json (application), apps/b does not (still a valid
  // no-project.json app, per discoverNxApps).
  await fs.mkdir(path.join(dir, 'apps/a/src'), { recursive: true });
  await fs.mkdir(path.join(dir, 'apps/b/src'), { recursive: true });
  await fs.writeFile(
    path.join(dir, 'apps/a/project.json'),
    JSON.stringify({ name: 'a', projectType: 'application', sourceRoot: 'apps/a/src' }),
    'utf8',
  );
  await fs.writeFile(path.join(dir, 'apps/a/src/index.html'), '<html><head></head><body></body></html>', 'utf8');
  await fs.writeFile(path.join(dir, 'apps/b/src/index.html'), '<html><head></head><body></body></html>', 'utf8');

  await execAsync(
    `node ${cliPath} init --yes --key ptr_good --project p1 --path apps/a --server ${serverUrl}`,
    { cwd: dir },
  );
  await execAsync(
    `node ${cliPath} init --yes --key ptr_good --project p2 --path apps/b --server ${serverUrl}`,
    { cwd: dir },
  );

  const config = JSON.parse(await fs.readFile(path.join(dir, '.pointer/config.json'), 'utf8'));
  assert.strictEqual(config.project, undefined, 'a multi-project config carries no top-level project');
  assert.ok(config.projects, 'config.projects must exist');
  assert.deepStrictEqual(Object.keys(config.projects).sort(), ['p1', 'p2']);
  assert.strictEqual(config.projects.p1.path, 'apps/a');
  assert.strictEqual(config.projects.p2.path, 'apps/b');
  assert.strictEqual(config.projects.p1.htmlPath, 'apps/a/src/index.html', 'the src/index.html candidate must be found and recorded');
  assert.strictEqual(config.projects.p2.htmlPath, 'apps/b/src/index.html');

  const htmlA = await fs.readFile(path.join(dir, 'apps/a/src/index.html'), 'utf8');
  const htmlB = await fs.readFile(path.join(dir, 'apps/b/src/index.html'), 'utf8');
  assert.match(htmlA, /<pointer-feedback project="p1"/);
  assert.match(htmlB, /<pointer-feedback project="p2"/);

  await fs.access(path.join(dir, '.pointer/projects/p1.stack.json'));
  await fs.access(path.join(dir, '.pointer/projects/p2.stack.json'));

  const gitignore = await fs.readFile(path.join(dir, '.gitignore'), 'utf8');
  assert.match(gitignore, /!\.pointer\/projects\//);
}));

test('init --yes --path migrates an existing single-project config into `projects`', () => withTempDir(async (dir) => {
  // Mirrors the real tuwaiq-mono-spa config: single-project, delivery: extension, no htmlPath
  // (nothing was ever injected for an extension-delivery install).
  await fs.mkdir(path.join(dir, '.pointer'), { recursive: true });
  await fs.writeFile(
    path.join(dir, '.pointer/config.json'),
    JSON.stringify({
      server: serverUrl,
      project: 'tuwaiq-profile',
      environment: 'local',
      aiTool: 'claude-code',
      delivery: 'extension',
    }),
    'utf8',
  );
  await fs.mkdir(path.join(dir, 'apps/landing'), { recursive: true });
  await fs.writeFile(path.join(dir, 'apps/landing/index.html'), '<html><head></head><body></body></html>', 'utf8');

  await execAsync(
    `node ${cliPath} init --yes --key ptr_good --project tuwaiq-landing --path apps/landing --server ${serverUrl}`,
    { cwd: dir },
  );

  const config = JSON.parse(await fs.readFile(path.join(dir, '.pointer/config.json'), 'utf8'));
  assert.strictEqual(config.project, undefined);
  assert.deepStrictEqual(Object.keys(config.projects).sort(), ['tuwaiq-landing', 'tuwaiq-profile']);
  // No htmlPath was ever recorded (extension delivery), so the derived path falls back to '.'.
  assert.strictEqual(config.projects['tuwaiq-profile'].path, '.');
  assert.strictEqual(config.projects['tuwaiq-profile'].delivery, 'extension');
  assert.strictEqual(config.projects['tuwaiq-landing'].path, 'apps/landing');
}));
