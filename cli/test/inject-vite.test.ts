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
        assert.match(envExContent, /VITE_POINTER_ENABLED=false/);
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
