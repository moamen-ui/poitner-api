import { test } from 'node:test';
import * as assert from 'node:assert';
import { injectVite } from '../src/inject/vite.js';
import * as fs from 'node:fs/promises';
import * as path from 'node:path';
import * as os from 'node:os';

test('injectVite idempotency and .env upsert', async () => {
    const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'pointer-test-vite-'));
    try {
        await fs.writeFile(path.join(dir, 'index.html'), '<html><body>Hello</body></html>');
        await fs.writeFile(path.join(dir, '.env.example'), 'EXISTING=1\n');
        
        await injectVite(dir, { server: 's', key: 'k', environment: 'e' });
        
        const envContent = await fs.readFile(path.join(dir, '.env'), 'utf8');
        assert.match(envContent, /VITE_POINTER_SERVER=s/);
        
        const envExContent = await fs.readFile(path.join(dir, '.env.example'), 'utf8');
        assert.match(envExContent, /^VITE_POINTER_SERVER=$/m);
        assert.match(envExContent, /^VITE_POINTER_PROJECT=$/m);
        assert.doesNotMatch(envExContent, /VITE_POINTER_ENABLED/);
        assert.match(envExContent, /EXISTING=1/);
        
        // idempotency
        await injectVite(dir, { server: 's2', key: 'k2', environment: 'e2' });
        const envContent2 = await fs.readFile(path.join(dir, '.env'), 'utf8');
        assert.match(envContent2, /VITE_POINTER_SERVER=s2/);
        assert.strictEqual(envContent2.match(/VITE_POINTER_SERVER/g)?.length, 1);
    } finally {
        await fs.rm(dir, { recursive: true, force: true });
    }
});

test('injectVite writes no VITE_POINTER_ENV unless the environment is pinned, and drops a stale one', async () => {
    const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'pointer-test-vite-env-'));
    try {
        await fs.writeFile(path.join(dir, 'index.html'), '<html><body>Hello</body></html>');
        // An older init wrote the env var; the widget no longer reads it (environment is resolved
        // from the page origin server-side), so a re-run must remove it rather than keep it current.
        await fs.writeFile(path.join(dir, '.env'), 'VITE_POINTER_ENABLED=true\nVITE_POINTER_ENV=staging\nOTHER=1\n');
        await fs.writeFile(path.join(dir, '.env.example'), 'VITE_POINTER_ENABLED=false\nVITE_POINTER_ENV=\n');

        await injectVite(dir, { server: 's', key: 'k', environment: 'local' });
        const env = await fs.readFile(path.join(dir, '.env'), 'utf8');
        assert.doesNotMatch(env, /VITE_POINTER_ENV/);
        assert.doesNotMatch(env, /VITE_POINTER_ENABLED/);
        assert.match(env, /OTHER=1/);
        assert.match(env, /VITE_POINTER_PROJECT=k/);
        const ex = await fs.readFile(path.join(dir, '.env.example'), 'utf8');
        assert.doesNotMatch(ex, /VITE_POINTER_ENV/);
        assert.doesNotMatch(ex, /VITE_POINTER_ENABLED/);
        assert.match(ex, /VITE_POINTER_PROJECT=/);

        // `--environment` given → pinned → the var is written so the snippet's attribute resolves.
        await injectVite(dir, { server: 's', key: 'k', environment: 'staging', environmentPinned: true });
        const pinned = await fs.readFile(path.join(dir, '.env'), 'utf8');
        assert.match(pinned, /^VITE_POINTER_ENV=staging$/m);
        assert.strictEqual(pinned.match(/VITE_POINTER_ENV/g)?.length, 1);
    } finally {
        await fs.rm(dir, { recursive: true, force: true });
    }
});
