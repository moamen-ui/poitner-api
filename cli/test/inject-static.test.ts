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

// Multi-environment installs.
//
// The single-environment block writes environment="local" into the markup, which is correct until
// the same index.html is also built for staging and production — then every comment from every
// deployment is tagged `local`. These cover the form that resolves the environment at runtime.
test('injectStatic: several environments resolve the environment at runtime, not at install time', async () => {
    const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'pointer-test-multienv-'));
    try {
        const html = path.join(dir, 'index.html');
        await fs.writeFile(html, '<html><head></head><body>Hi</body></html>');

        await injectStatic(dir, undefined, {
            server: 'https://api.example.test',
            key: 'proj',
            environment: 'local',
            environments: ['local', 'staging', 'production'],
            envMap: { 'https://staging.example.test': 'staging', 'https://example.test': 'production' },
        });
        const content = await fs.readFile(html, 'utf8');

        // The origin map is what makes one committed file correct on every deployment.
        assert.match(content, /https:\/\/staging\.example\.test/);
        assert.match(content, /"staging"/);
        assert.match(content, /"production"/);
        // No baked-in environment attribute in the markup.
        assert.doesNotMatch(content, /environment="local"/);
        // localhost is matched by host, because a dev server's port changes more often than any
        // registered URL list does.
        assert.match(content, /localhost/);
        // And it must survive being pasted anywhere, not just above </body>: document.body is null
        // while the parser is still inside <head>.
        assert.match(content, /DOMContentLoaded/);
    } finally {
        await fs.rm(dir, { recursive: true, force: true });
    }
});

test('injectStatic: a single environment keeps the simpler declarative block', async () => {
    const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'pointer-test-singleenv-'));
    try {
        const html = path.join(dir, 'index.html');
        await fs.writeFile(html, '<html><body>Hi</body></html>');
        await injectStatic(dir, undefined, {
            server: 'https://api.example.test',
            key: 'proj',
            environment: 'staging',
            environments: ['staging'],
        });
        const content = await fs.readFile(html, 'utf8');
        assert.match(content, /environment="staging"/, 'one environment needs no runtime resolution');
        assert.doesNotMatch(content, /DOMContentLoaded/);
    } finally {
        await fs.rm(dir, { recursive: true, force: true });
    }
});
