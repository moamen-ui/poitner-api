// R3-02: the design tokens `init` detects and writes into .pointer/stack.json.
//
// The point of the block is that an AI agent applying a comment reaches for the project's OWN
// tokens — `text-primary`, `var(--brand)` — instead of inventing a hex colour. So what matters is
// that detection is complete, deterministic, and that the file is committable: a stack.json that
// only exists on the machine that ran init helps nobody else on the team.
//
// Nightly: each scenario copies the vite-react fixture and runs the real CLI against the real API.
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { expect, test } from '@playwright/test';
import { spawnCli } from '../scripts/lib/cli.mjs';
import { tempRepo } from '../scripts/lib/git.mjs';
import { copyFixture } from '../scripts/lib/vite-fixture.mjs';
import { initArgs, INIT_ENV, SERVER } from '../scripts/lib/init-args.mjs';
import { record } from '../scripts/lib/report.mjs';

test.describe.configure({ timeout: 180_000 });

const RUN_ID = Math.random().toString(36).slice(2, 7);

/** A scratch repo holding the vite-react fixture on one committed baseline. */
function fixtureRepo() {
  const repo = tempRepo();
  copyFixture({ into: repo.dir });
  execFileSync('git', ['add', '-A'], { cwd: repo.dir });
  execFileSync('git', ['commit', '-m', 'fixture'], { cwd: repo.dir });
  return repo;
}

const stackPath = (dir) => join(dir, '.pointer', 'stack.json');
const readStackRaw = (dir) => readFileSync(stackPath(dir), 'utf8');
const readStack = (dir) => JSON.parse(readStackRaw(dir));
const sha256 = (s) => createHash('sha256').update(s).digest('hex');

/** True when git would ignore `rel` in `dir`. */
function isIgnored(dir, rel) {
  try {
    execFileSync('git', ['check-ignore', rel], { cwd: dir, stdio: 'ignore' });
    return true;
  } catch {
    return false;
  }
}

test('R3-02-01 — init-writes-design-tokens', async () => {
  test.skip(process.env.TIER === 'pr', 'nightly tier only');
  const start = Date.now();
  const repo = fixtureRepo();

  const res = await spawnCli({ cwd: repo.dir, args: initArgs(), env: INIT_ENV });
  expect(res.code, res.stderr).toBe(0);

  // 2. The human-facing line. Counts are interpolated, so the shape is pinned and the numbers are
  // left free — hardcoding "3 colors" would break the moment the fixture gains a token, which is
  // a fixture edit, not a regression.
  expect(res.stdout).toMatch(
    /^✔ Design tokens: tailwind \(\d+ colors\), css vars \(\d+\) → \.pointer\/stack\.json$/m,
  );

  // 3. The block itself.
  const stack = readStack(repo.dir);
  expect(stack.design?.version).toBe(1);
  expect(stack.design.tokens.tailwind.colors).toEqual(['muted', 'primary', 'secondary']);
  expect(stack.design.tokens.tailwind.config).toBe('tailwind.config.ts');
  expect(stack.design.tokens.tailwind.radius).toContain('md');
  expect(stack.design.tokens.cssVars.names).toContain('--brand');
  expect(stack.design.tokens.cssVars.names).toContain('--radius-md');
  expect(stack.design.guidance).toMatch(/Prefer existing tokens/);

  // The fixture ships no component library, and the key is emitted regardless — an ABSENT
  // `libraries` is a bug, not the same thing as an empty one, because a consumer reading
  // `design.libraries.length` would throw rather than see "none".
  expect(stack.design.libraries, 'libraries must be present even when empty').toEqual([]);

  // No timestamp anywhere: it would make the file differ on every run and turn a committed
  // stack.json into permanent diff noise (R3-02-02 depends on this).
  expect(readStackRaw(repo.dir)).not.toContain('detectedAt');

  // 4. Committable. This is the whole reason the file is written to disk rather than only POSTed.
  const gitignore = readFileSync(join(repo.dir, '.gitignore'), 'utf8');
  expect(
    gitignore,
    'the ignore rule must exclude the DIRECTORY CONTENTS (.pointer/*) — with the `.pointer/` ' +
      'directory form git never descends into it and every `!` re-include below is inert',
  ).toMatch(/^\.pointer\/\*$/m);
  expect(isIgnored(repo.dir, '.pointer/stack.json'), 'stack.json must be committable').toBe(false);
  const porcelain = execFileSync('git', ['status', '--porcelain', '-uall'], {
    cwd: repo.dir,
    encoding: 'utf8',
  });
  expect(porcelain).toMatch(/^\?\? \.pointer\/stack\.json$/m);

  // 5. Detection speed, measured by the detector rather than the process. A wall-clock bound on
  // the whole CLI run is dominated by node boot, which makes a 2.9s detector and a 0.2s one
  // indistinguishable — the number that matters is the one doctor reports.
  const doctor = await spawnCli({ cwd: repo.dir, args: ['doctor', '--refresh-stack'], env: INIT_ENV });
  expect(doctor.code).toBe(0);
  const m = doctor.stdout.match(/detectMs=(\d+)/);
  expect(m, `doctor --refresh-stack must report detectMs:\n${doctor.stdout}`).not.toBeNull();
  expect(Number(m[1]), 'token detection must stay under 2s').toBeLessThan(2000);

  record({
    id: 'R3-02-01', tier: 'nightly', layer: 'cli', role: 'DEV', result: 'PASS',
    ms: Date.now() - start,
    detail: `design.version=1, colors=${stack.design.tokens.tailwind.colors.join('/')}, detectMs=${m[1]}`,
  });
});

test('R3-02-02 — design-determinism-byte-identical', async () => {
  test.skip(process.env.TIER === 'pr', 'nightly tier only');
  const start = Date.now();
  const repo = fixtureRepo();

  const first = await spawnCli({ cwd: repo.dir, args: initArgs(), env: INIT_ENV });
  expect(first.code, first.stderr).toBe(0);
  const sha1 = sha256(readStackRaw(repo.dir));

  // Re-running init must not rewrite the file. Anything non-deterministic in here — a timestamp,
  // an unsorted array, an absolute path — shows up in a teammate's diff every time either of them
  // runs the CLI, and a file that churns is a file people stop committing.
  const second = await spawnCli({ cwd: repo.dir, args: initArgs(), env: INIT_ENV });
  expect(second.code, second.stderr).toBe(0);
  expect(sha256(readStackRaw(repo.dir)), 're-running init must not change stack.json').toBe(sha1);

  // …and neither must an explicit refresh, which recomputes from scratch rather than short-
  // circuiting on the existing file.
  const refreshed = await spawnCli({ cwd: repo.dir, args: ['doctor', '--refresh-stack'], env: INIT_ENV });
  expect(refreshed.code).toBe(0);
  expect(sha256(readStackRaw(repo.dir)), 'doctor --refresh-stack must be byte-identical').toBe(sha1);

  record({
    id: 'R3-02-02', tier: 'nightly', layer: 'cli', role: 'DEV', result: 'PASS',
    ms: Date.now() - start, detail: `sha256 stable across init, init, doctor --refresh-stack`,
  });
});

test('R3-02-04 — apply-prompt-design-section', async () => {
  test.skip(process.env.TIER === 'pr', 'nightly tier only');
  const start = Date.now();

  // With tokens: the apply prompt must carry them, or the agent has no reason to prefer
  // `text-primary` over a hex value it made up.
  const repo = fixtureRepo();
  const init = await spawnCli({ cwd: repo.dir, args: initArgs(), env: INIT_ENV });
  expect(init.code, init.stderr).toBe(0);

  // Check the INPUT before the output. The prompt's design section is built from this repo's
  // .pointer/stack.json, so if detection did not write one, "stdout has no /primary/" is a true
  // but useless message that points at the prompt builder instead of at the missing block. This
  // ordering turns that into "init wrote no design block", which is the actual fact.
  const localDesign = readStack(repo.dir).design;
  expect(localDesign, 'init must have written a design block for the prompt to carry').toBeTruthy();
  expect(localDesign.tokens?.tailwind?.colors, 'the fixture palette must have been detected')
    .toContain('primary');

  const planned = await spawnCli({ cwd: repo.dir, args: ['apply', '--plan'], env: INIT_ENV });
  expect(planned.code, planned.stderr).toBe(0);

  // Assert on the section, not on a bare substring. `primary` appears in the prompt's own prose
  // ("text-primary" in the guidance line), so matching the whole document would pass even with no
  // Design system section at all.
  const section = planned.stdout.match(/^## Design system$([\s\S]*?)(?=^## )/m);
  expect(section, `apply --plan must carry a Design system section:\n${planned.stdout.slice(0, 600)}`)
    .not.toBeNull();
  expect(section[1], 'the section must list the detected tokens').toMatch(/Tokens:.*\bprimary\b/);
  expect(section[1]).toMatch(/--brand/);

  // Without tokens: a plain-HTML project has none, and the prompt must simply omit the section
  // rather than emit an empty heading an agent then tries to satisfy.
  //
  // Its own project key, not the shared fixture one: a project's frontend/backend stack is
  // WRITE-ONCE-IF-EMPTY, so posting this HTML detection against e2e-alpha would pin alpha's stack
  // to "html" for the lifetime of the database and break every later scenario asserting react.
  const emptyKey = `e2e-r302-empty-${RUN_ID}`;
  const plain = tempRepo();
  execFileSync('git', ['commit', '--allow-empty', '-m', 'base'], { cwd: plain.dir });

  const plainInit = await spawnCli({
    cwd: plain.dir,
    args: initArgs({ project: emptyKey, extra: ['--create', `R3-02 empty ${RUN_ID}`] }),
    env: INIT_ENV,
  });
  expect(plainInit.code, plainInit.stderr).toBe(0);

  if (existsSync(stackPath(plain.dir))) {
    const plainStack = readStack(plain.dir);
    const tokens = plainStack.design?.tokens ?? {};
    const names = tokens.cssVars?.names ?? [];
    const colors = tokens.tailwind?.colors ?? [];
    expect(names, 'a plain project has no css vars to find').toHaveLength(0);
    expect(colors, 'a plain project has no tailwind palette to find').toHaveLength(0);
  }

  record({
    id: 'R3-02-04', tier: 'nightly', layer: 'cli', role: 'WA', result: 'PASS',
    ms: Date.now() - start,
    detail: `tokens surfaced in apply --plan; ${emptyKey} detects none`,
  });
});

test('R3-02-05 — no-design-flag', async () => {
  test.skip(process.env.TIER === 'pr', 'nightly tier only');
  const start = Date.now();
  const repo = fixtureRepo();

  const res = await spawnCli({
    cwd: repo.dir,
    args: initArgs({ extra: ['--no-design'] }),
    env: INIT_ENV,
  });
  expect(res.code, res.stderr).toBe(0);

  // 2. The opt-out has to be complete: no line about tokens, and nothing in the file. A flag that
  // only suppresses the message while still writing (and POSTing) the block would be worse than
  // no flag, because someone who asked not to share their palette would believe they hadn't.
  expect(res.stdout, '--no-design must not announce tokens').not.toMatch(/Design tokens/);

  const raw = readStackRaw(repo.dir);
  expect(raw, '--no-design must not write a design block').not.toContain('"design"');

  // 3. The rest of stack detection is unaffected — --no-design is about the palette, not about
  // turning the stack file off.
  const stack = JSON.parse(raw);
  expect(String(stack.frontend ?? ''), 'the frontend stack must still be detected').toMatch(/react|vite/i);

  record({
    id: 'R3-02-05', tier: 'nightly', layer: 'cli', role: 'DEV', result: 'PASS',
    ms: Date.now() - start, detail: 'no design line, no design block, stack still detected',
  });
});

test('R3-01-07 — init --pin covers the Vite stack, not only static', async () => {
  test.skip(process.env.TIER === 'pr', 'nightly tier only');
  const start = Date.now();
  const repo = fixtureRepo();

  const res = await spawnCli({
    cwd: repo.dir,
    args: initArgs({ extra: ['--pin'] }),
    env: INIT_ENV,
  });
  expect(res.code, res.stderr).toBe(0);

  const html = readFileSync(join(repo.dir, 'index.html'), 'utf8');

  // Vite's loader builds the tag in JS rather than writing it as markup, so the pin arrives as
  // property assignments. Asserting on those — not on a `<script integrity=...>` string — is what
  // matches how this stack actually injects, and `--pin` used to be silently ignored here: it was
  // wired only into the static branch, so a Vite project got an unpinned tag and no warning.
  const manifest = JSON.parse(
    execFileSync('curl', ['-s', `${SERVER}/pointer.version.json`], { encoding: 'utf8' }),
  );
  expect(html, 'the loader must request the pinned build').toContain(`/widget.js?v=${manifest.hash}`);
  expect(html, 'and carry the integrity hash the server published').toContain(
    `s.integrity = '${manifest.files['widget.js'].integrity}'`,
  );
  expect(html, 'SRI on a cross-origin script requires crossOrigin').toContain("s.crossOrigin = 'anonymous'");

  record({
    id: 'R3-01-07', tier: 'nightly', layer: 'cli', role: 'DEV', result: 'PASS',
    ms: Date.now() - start,
    detail: `vite loader pinned to ${manifest.hash} with SRI`,
  });
});
