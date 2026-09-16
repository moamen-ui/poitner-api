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

        // `delivery` round-trips like any other field, and a config an older CLI wrote (before this
        // field existed) simply omits it — readers treat that absence as `embed`, not as an error.
        assert.strictEqual(conf.delivery, undefined, 'an older config has no delivery field at all');
        await writeConfig(dir, { delivery: 'extension' });
        conf = await readConfig(dir);
        assert.strictEqual(conf.delivery, 'extension');
        await writeConfig(dir, { delivery: 'embed' });
        conf = await readConfig(dir);
        assert.strictEqual(conf.delivery, 'embed');

        await writeCredentials(dir, 'test-key');
        const creds = await fs.readFile(path.join(dir, '.pointer/credentials.env'), 'utf8');
        assert.match(creds, /POINTER_API_KEY=test-key/);

        await upsertGitignore(dir);
        let gitignore = await fs.readFile(path.join(dir, '.gitignore'), 'utf8');
        // Only config.json and stack.json are re-included; everything else in .pointer/ (including
        // credentials.env) stays covered by the `.pointer/*` wildcard with no negation of its own.
        assert.match(gitignore, /\.pointer\/\*/);
        assert.match(gitignore, /!\.pointer\/config\.json/);
        assert.match(gitignore, /!\.pointer\/stack\.json/);
        assert.doesNotMatch(gitignore, /!\.pointer\/credentials\.env/);
        assert.doesNotMatch(gitignore, /!\.pointer\/pointer\.sh/);
        // Skill directories are gitignored too — every clone installs its own copy.
        assert.match(gitignore, /\.claude\/skills\/pointer-init\//);
        assert.match(gitignore, /\.claude\/skills\/pointer-feedback\//);
        assert.match(gitignore, /\.agents\/pointer-init\//);
        assert.match(gitignore, /\.agents\/pointer-feedback\//);

        // idempotency
        await upsertGitignore(dir);
        let gitignore2 = await fs.readFile(path.join(dir, '.gitignore'), 'utf8');
        assert.strictEqual(gitignore, gitignore2);
    } finally {
        await fs.rm(dir, { recursive: true, force: true });
    }
});

/**
 * The `.gitignore` block must leave exactly the TEAM config trackable: `stack.json` and
 * `config.json`. Everything else — `credentials.env` (a secret) and, as of this version,
 * `pointer.sh` and the skill directories (derived, per-machine; every clone re-installs its own
 * copy via `init`/`update`) — must stay ignored.
 *
 * It used to write `.pointer/` — the directory. Git does not descend into an excluded directory,
 * so it never considered the files inside and the `!` lines were inert: stack.json and
 * config.json were silently ignored, which is precisely the opposite of what the block's own
 * comment says it does. Asserting on the file's TEXT would not have caught it; only asking git
 * what it ignores does.
 */
test('gitignore keeps config.json/stack.json trackable and ignores everything else', async () => {
    const { execFileSync } = await import('node:child_process');

    const starts: Array<[string, string | null]> = [
        ['fresh', null],
        // Written by a version that excluded the directory.
        ['legacy directory form', 'node_modules\n.pointer/\n'],
        // Written by the earliest version, which ignored only the credentials file.
        ['legacy narrow rule', 'node_modules\n\n# Pointer\n.pointer/credentials.env\n'],
        // Written by the version immediately before this one: pointer.sh and
        // credentials.env.example were still re-included, and the skill directories were not
        // mentioned at all.
        [
            'current block (pre-this-version)',
            [
                'node_modules',
                '',
                '# Pointer',
                '.pointer/*',
                '!.pointer/credentials.env.example',
                '!.pointer/stack.json',
                '!.pointer/pointer.sh',
                '!.pointer/config.json',
                '',
            ].join('\n'),
        ],
    ];

    for (const [label, initial] of starts) {
        const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'pointer-test-ignore-'));
        try {
            execFileSync('git', ['init'], { cwd: dir, stdio: 'ignore' });
            if (initial) await fs.writeFile(path.join(dir, '.gitignore'), initial, 'utf8');

            await upsertGitignore(dir, 'Pointer');
            await fs.mkdir(path.join(dir, '.pointer'), { recursive: true });
            for (const f of ['stack.json', 'config.json', 'pointer.sh', 'credentials.env', 'credentials.env.example']) {
                await fs.writeFile(path.join(dir, '.pointer', f), 'x', 'utf8');
            }
            for (const rel of [
                '.claude/skills/pointer-init/SKILL.md',
                '.claude/skills/pointer-feedback/SKILL.md',
                '.agents/pointer-init/SKILL.md',
                '.agents/pointer-feedback/SKILL.md',
            ]) {
                await fs.mkdir(path.join(dir, path.dirname(rel)), { recursive: true });
                await fs.writeFile(path.join(dir, rel), 'x', 'utf8');
            }

            const ignored = (rel: string): boolean => {
                try {
                    execFileSync('git', ['check-ignore', rel], { cwd: dir, stdio: 'ignore' });
                    return true;
                } catch {
                    return false;
                }
            };

            for (const f of ['.pointer/stack.json', '.pointer/config.json']) {
                assert.strictEqual(ignored(f), false, `${label}: ${f} must be committable`);
            }
            // Everything else in .pointer/ is derived or a secret, and must stay ignored.
            for (const f of ['.pointer/credentials.env', '.pointer/credentials.env.example', '.pointer/pointer.sh']) {
                assert.strictEqual(ignored(f), true, `${label}: ${f} must be ignored`);
            }
            // The skill directories, gitignored as of this version.
            for (const rel of [
                '.claude/skills/pointer-init/SKILL.md',
                '.claude/skills/pointer-feedback/SKILL.md',
                '.agents/pointer-init/SKILL.md',
                '.agents/pointer-feedback/SKILL.md',
            ]) {
                assert.strictEqual(ignored(rel), true, `${label}: ${rel} must be ignored`);
            }
        } finally {
            await fs.rm(dir, { recursive: true, force: true });
        }
    }
});
