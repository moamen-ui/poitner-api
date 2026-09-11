import { test } from 'node:test';
import * as assert from 'node:assert';
import { detectApp } from '../src/detect.js';
import * as fs from 'node:fs/promises';
import * as os from 'node:os';
import * as path from 'node:path';

async function withTempDir(fn: (dir: string) => Promise<void>) {
  const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'pointer-test-'));
  try {
    await fn(dir);
  } finally {
    await fs.rm(dir, { recursive: true, force: true });
  }
}

test('detectApp detects Next.js', () => withTempDir(async (dir) => {
  await fs.writeFile(path.join(dir, 'package.json'), JSON.stringify({
    dependencies: { next: '14.0.0' }
  }));
  const res = await detectApp(dir);
  assert.strictEqual(res.type, 'nextjs');
  assert.strictEqual(res.port, 3000);
}));

test('detectApp detects Vite', () => withTempDir(async (dir) => {
  await fs.writeFile(path.join(dir, 'package.json'), JSON.stringify({
    devDependencies: { vite: '5.0.0' },
    scripts: { dev: 'vite --port 4000' }
  }));
  const res = await detectApp(dir);
  assert.strictEqual(res.type, 'vite');
  assert.strictEqual(res.port, 4000);
}));

test('detectApp fallback to static', () => withTempDir(async (dir) => {
  const res = await detectApp(dir);
  assert.strictEqual(res.type, 'static');
  assert.strictEqual(res.port, 8080);
}));
