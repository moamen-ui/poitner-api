// R3-02-03 — the design block stays local.
//
// `init` detects a project's design tokens and writes them into .pointer/stack.json, which the
// apply prompt reads. It also registers the project's stack with the server. Those are different
// audiences: the tokens are for the agent working in this repo, and a customer's palette and CSS
// variable names are not something the server needs a copy of.
//
// This can only be checked on the wire. Reading the server's response cannot distinguish "never
// sent" from "sent and quietly ignored" — so the CLI runs against a recording proxy.
import { existsSync, readFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, test } from '@playwright/test';
import { raw, login } from '../scripts/lib/api.mjs';
import { credentials } from '../scripts/lib/state.mjs';
import { PORTS } from '../scripts/lib/constants.mjs';
import { spawnCli } from '../scripts/lib/cli.mjs';
import { tempRepo } from '../scripts/lib/git.mjs';
import { copyFixture } from '../scripts/lib/vite-fixture.mjs';
import { initArgs, INIT_ENV, SERVER } from '../scripts/lib/init-args.mjs';
import { startRecordingProxy, readRecording } from '../scripts/lib/recording-proxy.mjs';
import { record } from '../scripts/lib/report.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const RECORDING = resolve(here, '..', 'state', 'http-recording.jsonl');

test.describe.configure({ timeout: 180_000 });

test('R3-02-03 ⛓ — stack-post-excludes-design', async () => {
  test.skip(process.env.TIER === 'pr', 'nightly tier only');
  const start = Date.now();

  const proxy = await startRecordingProxy({
    port: PORTS.recorder ?? 4179,
    target: SERVER,
    recording: RECORDING,
  });

  try {
    const repo = tempRepo();
    copyFixture({ into: repo.dir });
    execFileSync('git', ['add', '-A'], { cwd: repo.dir });
    execFileSync('git', ['commit', '-m', 'fixture'], { cwd: repo.dir });

    // Its OWN project key, created here.
    //
    // A project's frontend/backend stack is WRITE-ONCE-IF-EMPTY: the first POST wins for the
    // lifetime of the database and every later one is ignored. The shared e2e-alpha was already
    // pinned to `vite` by an earlier scenario, so asserting this fixture's react+tailwind
    // detection against it would fail on history rather than on behaviour.
    const projectKey = `e2e-r3023-${Math.random().toString(36).slice(2, 7)}`;
    const res = await spawnCli({
      cwd: repo.dir,
      args: initArgs({
        server: proxy.url,
        project: projectKey,
        extra: ['--create', `R3-02-03 ${projectKey}`],
      }),
      env: INIT_ENV,
    });
    expect(res.code, res.stderr).toBe(0);

    // 3. Exactly one stack POST, and its RAW body must not mention design at all — parsed-key
    // assertions alone would miss a design block nested somewhere unexpected.
    const entries = readRecording(RECORDING);
    const stackPosts = entries.filter(
      (e) => e.method === 'POST' && /^\/api\/projects\/[^/]+\/stack$/.test(e.path),
    );
    expect(stackPosts, 'init must register the stack exactly once').toHaveLength(1);

    const rawBody = stackPosts[0].body || '';
    expect(rawBody, 'the stack POST must not carry the design block').not.toContain('"design"');
    expect(rawBody, 'nor any token name from it').not.toContain('--brand');

    // `aiTool`, singular — the contract says `aiTools`, but that is the SERVER's field, which
    // accumulates across installs. One init registers one tool, so the request carries one.
    const parsed = JSON.parse(rawBody);
    expect(Object.keys(parsed).sort()).toEqual(['aiTool', 'backend', 'frontend']);

    // 4. The server's own view agrees: it holds the stack, and no design.
    const dev = await login(credentials().developer.email, credentials().developer.password);
    const stackRes = await raw('GET', `/api/projects/${projectKey}/stack`, { token: dev.token });
    expect(stackRes.status).toBe(200);
    expect(stackRes.data, 'the server must not hold a design block').not.toHaveProperty('design');
    expect(String(stackRes.data?.frontend ?? '')).toContain('react');
    expect(String(stackRes.data?.frontend ?? '')).toContain('tailwind');

    // 5. And the local file still has everything — the tokens were withheld from the server, not
    // dropped. This is what makes the split meaningful rather than a silent data loss.
    const localPath = join(repo.dir, '.pointer', 'stack.json');
    expect(existsSync(localPath), 'init must still write the local stack file').toBe(true);
    const local = JSON.parse(readFileSync(localPath, 'utf8'));
    expect(local.design, 'the design block must remain local').toBeTruthy();
    expect(JSON.stringify(local.design)).toContain('--brand');

    record({
      id: 'R3-02-03', tier: 'nightly', layer: 'cli + api', role: 'DEV', result: 'PASS',
      ms: Date.now() - start,
      detail: 'one stack POST, no design on the wire or on the server, design intact locally',
    });
  } finally {
    await proxy.stop();
  }
});
