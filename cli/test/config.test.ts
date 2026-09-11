import { test } from 'node:test';
import * as assert from 'node:assert';
import { readConfig, writeConfig, writeCredentials, upsertGitignore } from '../src/config.js';
import * as fs from 'node:fs/promises';
import * as path from 'node:path';
import * as os from 'node:os';

test('config read/write and gitignore upsert idempotency', async () => {
    const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'pointer-test-config-'));
    try {
        let conf = await readConfig(dir);
        assert.deepStrictEqual(conf, {});
        
        await writeConfig(dir, { server: 'https://test' });
        conf = await readConfig(dir);
        assert.strictEqual(conf.server, 'https://test');
        
        await writeCredentials(dir, 'test-key');
        const creds = await fs.readFile(path.join(dir, '.pointer/credentials.env'), 'utf8');
        assert.match(creds, /POINTER_API_KEY=test-key/);
        
        await upsertGitignore(dir);
        let gitignore = await fs.readFile(path.join(dir, '.gitignore'), 'utf8');
        assert.match(gitignore, /\.pointer\/credentials\.env/);
        
        // idempotency
        await upsertGitignore(dir);
        let gitignore2 = await fs.readFile(path.join(dir, '.gitignore'), 'utf8');
        assert.strictEqual(gitignore, gitignore2);
    } finally {
        await fs.rm(dir, { recursive: true, force: true });
    }
});
