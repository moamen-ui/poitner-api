import { test } from 'node:test';
import assert from 'node:assert/strict';
import { promises as fs } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { createServer, type Server } from 'node:http';
import { compareSemver, runInitChecks } from '../src/checks.js';
import { exitCodeFor } from '../src/commands/doctor.js';

async function scratch(config?: Record<string, unknown>, apiKey?: string): Promise<string> {
  const dir = await fs.mkdtemp(join(tmpdir(), 'pointer-doctor-'));
  if (config) {
    await fs.mkdir(join(dir, '.pointer'), { recursive: true });
    await fs.writeFile(join(dir, '.pointer/config.json'), JSON.stringify(config), 'utf8');
  }
  if (apiKey) {
    await fs.mkdir(join(dir, '.pointer'), { recursive: true });
    await fs.writeFile(join(dir, '.pointer/credentials.env'), `POINTER_API_KEY=${apiKey}\n`, 'utf8');
  }
  return dir;
}

/**
 * A stub Pointer server. `routes` maps "METHOD /path" to [status, body]; anything unlisted 404s,
 * which is itself a case doctor must handle (a server that predates /api/meta).
 */
async function stubServer(routes: Record<string, [number, unknown]>): Promise<{ url: string; close: () => Promise<void> }> {
  const server: Server = createServer((req, res) => {
    const key = `${req.method} ${req.url?.split('?')[0]}`;
    const hit = routes[key];
    if (!hit) {
      res.writeHead(404, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ isSuccess: false, message: 'not found' }));
      return;
    }
    const [status, body] = hit;
    const isScript = key.endsWith('/pointer.js');
    res.writeHead(status, { 'content-type': isScript ? 'application/javascript' : 'application/json' });
    res.end(isScript ? String(body) : JSON.stringify({ isSuccess: status < 400, data: body }));
  });

  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
  const port = (server.address() as { port: number }).port;
  return {
    url: `http://127.0.0.1:${port}`,
    close: () => new Promise<void>((resolve) => server.close(() => resolve())),
  };
}

test('compareSemver orders releases, and sorts a pre-release below its release', () => {
  assert.ok(compareSemver('1.0.0', '1.0.1') < 0);
  assert.ok(compareSemver('1.2.0', '1.10.0') < 0, 'must compare numerically, not lexically');
  assert.equal(compareSemver('2.3.4', '2.3.4'), 0);
  assert.ok(compareSemver('1.0.0-beta', '1.0.0') < 0, '1.0.0-beta precedes 1.0.0');
  assert.ok(compareSemver('v1.0.0', '1.0.0') === 0, 'a leading v is not a version difference');
});

test('exit code 5 wins over everything, and is what a too-old CLI reports', () => {
  assert.equal(exitCodeFor([{ id: 'meta', status: 'error', message: '' }]), 5);
  assert.equal(
    exitCodeFor([
      { id: 'meta', status: 'error', message: '' },
      { id: 'key', status: 'error', message: '' },
    ]),
    5,
  );
});

test('exit code 3 marks a key problem specifically — it is fixed somewhere else entirely', () => {
  assert.equal(
    exitCodeFor([
      { id: 'key', status: 'error', message: '' },
      { id: 'widget-served', status: 'error', message: '' },
    ]),
    3,
  );
});

test('warnings alone are a pass', () => {
  assert.equal(exitCodeFor([{ id: 'widget', status: 'warn', message: '' }, { id: 'config', status: 'ok', message: '' }]), 0);
  assert.equal(exitCodeFor([{ id: 'stack', status: 'error', message: '' }]), 1);
});

test('a missing config stops the run — there is no server to ask anything of', async () => {
  const dir = await scratch();
  const checks = await runInitChecks(dir);

  assert.equal(checks.length, 1, 'must not attempt network checks with no server configured');
  assert.equal(checks[0].id, 'config');
  assert.equal(checks[0].status, 'error');
  assert.match(checks[0].hint ?? '', /init/);
});

test('a CLI older than minCliVersion stops immediately and skips the rest', async () => {
  const stub = await stubServer({
    'GET /api/branding': [200, { productName: 'Pointer' }],
    'GET /api/meta': [200, { version: '9.0.0', apiVersion: 2, minCliVersion: '5.0.0', serverTime: new Date().toISOString() }],
  });
  const dir = await scratch({ server: stub.url, project: 'demo', environment: 'local' }, 'ptr_key');

  const checks = await runInitChecks(dir, {}, '1.0.0');
  await stub.close();

  assert.equal(exitCodeFor(checks), 5);
  // The contract is explicit that later checks are skipped: their answers against a newer server
  // are not trustworthy, and printing them would send the user chasing the wrong thing.
  assert.equal(checks.at(-1)?.id, 'meta');
  assert.ok(!checks.some((c) => c.id === 'key'), 'must not have run the key check');
});

test('a server with no /api/meta warns rather than failing', async () => {
  const stub = await stubServer({ 'GET /api/branding': [200, { productName: 'Pointer' }] });
  const dir = await scratch({ server: stub.url, project: 'demo', environment: 'local' });

  const checks = await runInitChecks(dir, {}, '1.0.0');
  await stub.close();

  const meta = checks.find((c) => c.id === 'meta');
  assert.equal(meta?.status, 'warn');
  assert.match(meta?.message ?? '', /predates/);
});

test('an unreachable server fails the server check without hanging the run', async () => {
  // Port 1 is reserved and nothing listens there.
  const dir = await scratch({ server: 'http://127.0.0.1:1', project: 'demo', environment: 'local' });

  const checks = await runInitChecks(dir, {}, '1.0.0');

  assert.equal(checks.find((c) => c.id === 'server')?.status, 'error');
  assert.ok(checks.some((c) => c.id === 'key'), 'the run continues so the user sees every local problem at once');
});

test('a rejected API key is an error, and a valid one unlocks the project check', async () => {
  const now = new Date().toISOString();

  const rejecting = await stubServer({
    'GET /api/branding': [200, {}],
    'GET /api/meta': [200, { minCliVersion: '0.0.1', serverTime: now }],
    'POST /api/auth/login-with-key': [400, { message: 'invalid' }],
  });
  const badDir = await scratch({ server: rejecting.url, project: 'demo', environment: 'local' }, 'ptr_bad');
  const badChecks = await runInitChecks(badDir, {}, '1.0.0');
  await rejecting.close();

  assert.equal(badChecks.find((c) => c.id === 'key')?.status, 'error');
  assert.equal(exitCodeFor(badChecks), 3);

  const accepting = await stubServer({
    'GET /api/branding': [200, {}],
    'GET /api/meta': [200, { minCliVersion: '0.0.1', serverTime: now }],
    'POST /api/auth/login-with-key': [200, { status: 'ok', token: 'jwt' }],
    'GET /api/admin/projects': [200, [{ key: 'demo', isActiveLocal: true }]],
    'GET /pointer.js': [200, 'console.log(1)'],
  });
  const goodDir = await scratch({ server: accepting.url, project: 'demo', environment: 'local' }, 'ptr_good');
  const goodChecks = await runInitChecks(goodDir, {}, '1.0.0');
  await accepting.close();

  assert.equal(goodChecks.find((c) => c.id === 'key')?.status, 'ok');
  assert.equal(goodChecks.find((c) => c.id === 'project')?.status, 'ok');
  assert.equal(goodChecks.find((c) => c.id === 'widget-served')?.status, 'ok');
});

test('a project that exists but is inactive for this environment warns, not errors', async () => {
  const stub = await stubServer({
    'GET /api/branding': [200, {}],
    'GET /api/meta': [200, { minCliVersion: '0.0.1', serverTime: new Date().toISOString() }],
    'POST /api/auth/login-with-key': [200, { status: 'ok', token: 'jwt' }],
    'GET /api/admin/projects': [200, [{ key: 'demo', isActiveProduction: false }]],
    'GET /pointer.js': [200, 'console.log(1)'],
  });
  const dir = await scratch({ server: stub.url, project: 'demo', environment: 'production' }, 'ptr_good');

  const checks = await runInitChecks(dir, {}, '1.0.0');
  await stub.close();

  const project = checks.find((c) => c.id === 'project');
  assert.equal(project?.status, 'warn');
  assert.match(project?.message ?? '', /inactive for production/);
});

test('a project missing from the workspace is an error', async () => {
  const stub = await stubServer({
    'GET /api/branding': [200, {}],
    'GET /api/meta': [200, { minCliVersion: '0.0.1', serverTime: new Date().toISOString() }],
    'POST /api/auth/login-with-key': [200, { status: 'ok', token: 'jwt' }],
    'GET /api/admin/projects': [200, [{ key: 'something-else', isActiveLocal: true }]],
    'GET /pointer.js': [200, 'console.log(1)'],
  });
  const dir = await scratch({ server: stub.url, project: 'demo', environment: 'local' }, 'ptr_good');

  const checks = await runInitChecks(dir, {}, '1.0.0');
  await stub.close();

  assert.equal(checks.find((c) => c.id === 'project')?.status, 'error');
  assert.equal(exitCodeFor(checks), 1);
});

test('clock skew beyond five minutes warns', async () => {
  const skewed = new Date(Date.now() - 20 * 60_000).toISOString();
  const stub = await stubServer({
    'GET /api/branding': [200, {}],
    'GET /api/meta': [200, { minCliVersion: '0.0.1', serverTime: skewed }],
  });
  const dir = await scratch({ server: stub.url, project: 'demo', environment: 'local' });

  const checks = await runInitChecks(dir, {}, '1.0.0');
  await stub.close();

  const clock = checks.find((c) => c.id === 'clock');
  assert.equal(clock?.status, 'warn');
  assert.match(clock?.message ?? '', /skew/);
});

test('the widget check accepts either the marker block or a Vite env var', async () => {
  const stub = await stubServer({ 'GET /api/branding': [200, {}] });

  const markerDir = await scratch({ server: stub.url, project: 'demo', environment: 'local' });
  await fs.writeFile(join(markerDir, 'index.html'), '<html><!-- pointer-feedback:start --></html>', 'utf8');
  const markerChecks = await runInitChecks(markerDir, {}, '1.0.0');
  assert.equal(markerChecks.find((c) => c.id === 'widget')?.status, 'ok');

  const envDir = await scratch({ server: stub.url, project: 'demo', environment: 'local' });
  await fs.writeFile(join(envDir, '.env'), 'VITE_POINTER_PROJECT=demo\n', 'utf8');
  const envChecks = await runInitChecks(envDir, {}, '1.0.0');
  assert.equal(envChecks.find((c) => c.id === 'widget')?.status, 'ok');

  // Absent is a WARNING, never an error: a Next or Angular install mounts the widget in a
  // component this scan never reads, so "not found" is genuinely inconclusive.
  const emptyDir = await scratch({ server: stub.url, project: 'demo', environment: 'local' });
  const emptyChecks = await runInitChecks(emptyDir, {}, '1.0.0');
  assert.equal(emptyChecks.find((c) => c.id === 'widget')?.status, 'warn');

  await stub.close();
});

test('--server and --project override the config file', async () => {
  const stub = await stubServer({ 'GET /api/branding': [200, {}] });
  const dir = await scratch({ server: 'http://127.0.0.1:1', project: 'stale', environment: 'local' });

  const checks = await runInitChecks(dir, { server: stub.url, project: 'fresh' }, '1.0.0');
  await stub.close();

  assert.match(checks.find((c) => c.id === 'config')?.message ?? '', /fresh/);
  assert.equal(checks.find((c) => c.id === 'server')?.status, 'ok');
});
