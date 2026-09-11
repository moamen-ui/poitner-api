import { test } from 'node:test';
import * as assert from 'node:assert';
import { injectStatic } from '../src/inject/static.js';
import * as fs from 'node:fs/promises';
import * as path from 'node:path';
import * as os from 'node:os';

test('injectStatic idempotency', async () => {
    const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'pointer-test-static-'));
    try {
        const html = path.join(dir, 'index.html');
        await fs.writeFile(html, '<html><body>Hello</body></html>');
        
        await injectStatic(dir, undefined, { server: 's', key: 'k', environment: 'e' });
        let content = await fs.readFile(html, 'utf8');
        assert.match(content, /pointer-feedback:start/);
        
        await injectStatic(dir, undefined, { server: 's2', key: 'k2', environment: 'e2' });
        let content2 = await fs.readFile(html, 'utf8');
        assert.match(content2, /s2/);
        assert.strictEqual(content2.match(/pointer-feedback:start/g)?.length, 1);
    } finally {
        await fs.rm(dir, { recursive: true, force: true });
    }
});
