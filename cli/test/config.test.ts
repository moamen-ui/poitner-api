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

/**
 * The `.gitignore` block must leave the four shareable files trackable.
 *
 * It used to write `.pointer/` — the directory. Git does not descend into an excluded directory,
 * so it never considered the files inside and all four `!` lines were inert: stack.json,
 * config.json and pointer.sh were silently ignored, which is precisely the opposite of what the
 * block's own comment says it does. Asserting on the file's TEXT would not have caught it; only
 * asking git what it ignores does.
 */
test('gitignore keeps the shareable .pointer files trackable', async () => {
    const { execFileSync } = await import('node:child_process');

    const starts: Array<[string, string | null]> = [
        ['fresh', null],
        // Written by a version that excluded the directory.
        ['legacy directory form', 'node_modules\n.pointer/\n'],
        // Written by the earliest version, which ignored only the credentials file.
        ['legacy narrow rule', 'node_modules\n\n# Pointer\n.pointer/credentials.env\n'],
    ];

    for (const [label, initial] of starts) {
        const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'pointer-test-ignore-'));
        try {
            execFileSync('git', ['init'], { cwd: dir, stdio: 'ignore' });
            if (initial) await fs.writeFile(path.join(dir, '.gitignore'), initial, 'utf8');

            await upsertGitignore(dir, 'Pointer');
            await fs.mkdir(path.join(dir, '.pointer'), { recursive: true });
            for (const f of ['stack.json', 'config.json', 'pointer.sh', 'credentials.env']) {
                await fs.writeFile(path.join(dir, '.pointer', f), 'x', 'utf8');
            }

            const ignored = (rel: string): boolean => {
                try {
                    execFileSync('git', ['check-ignore', rel], { cwd: dir, stdio: 'ignore' });
                    return true;
                } catch {
                    return false;
                }
            };

            for (const f of ['.pointer/stack.json', '.pointer/config.json', '.pointer/pointer.sh']) {
                assert.strictEqual(ignored(f), false, `${label}: ${f} must be committable`);
            }
            // The one that must stay out: it holds an API key.
            assert.strictEqual(
                ignored('.pointer/credentials.env'),
                true,
                `${label}: credentials.env must stay ignored`,
            );
        } finally {
            await fs.rm(dir, { recursive: true, force: true });
        }
    }
});
