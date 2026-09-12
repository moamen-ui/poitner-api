// R2-03: doctor notices a stale installed skill copy, and update fixes it.
//
// A skill file installed months ago is frozen prose describing an API that has moved on, and
// nothing about it looks wrong. These scenarios drive the whole loop the version stamp exists for:
// install → doctor clean → server moves → doctor warns → update → doctor clean.
import { test, expect } from '@playwright/test';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { spawnCli } from '../scripts/lib/cli.mjs';
import { tempRepo } from '../scripts/lib/git.mjs';
import { getRaw } from '../scripts/lib/api.mjs';
import { keys } from '../scripts/lib/state.mjs';
import { record } from '../scripts/lib/report.mjs';

const SERVER = process.env.E2E_API_URL || 'http://localhost:8090';

// Restarting the API to change its skill version is destructive to anything sharing the stack, so
// it runs only in the phase that owns that (see run-e2e.sh's upgrade phase).
const isDestructiveRun = process.env.E2E_DESTRUCTIVE === '1';

async function freshInstall(tool = 'claude-code') {
  // Fail here, with the reason, rather than let `init` report "could not reach the server" — which
  // reads like a CLI bug when it actually means a previous destructive scenario left the API down.
  const health = await getRaw('/api/meta').catch(() => ({ status: 0 }));
  expect(health.status, `the API is not up at ${SERVER} — a previous restart may not have finished`).toBe(200);

  const repo = tempRepo();
  writeFileSync(join(repo.dir, 'package.json'), JSON.stringify({ name: 't', devDependencies: { vite: '^5' } }));
  writeFileSync(join(repo.dir, 'index.html'), '<html><body><h1>x</h1></body></html>');

  const res = await spawnCli({
    cwd: repo.dir,
    args: ['init', '--server', SERVER, '--key', keys().developer.apiKey,
           '--create', `Stamp ${Date.now()}`, '--environment', 'local', '--tool', tool, '--yes', '--json'],
  });
  expect(res.code, `init failed: ${res.stderr}`).toBe(0);
  return repo;
}

function doctorChecks(json) {
  return Object.fromEntries((json.checks || []).map((c) => [c.id, c]));
}

test('R2-03-03 — doctor flags a stale skill copy, and update fixes it', async () => {
  const start = Date.now();
  const repo = await freshInstall();

  try {
    // A fresh install matches the server.
    const clean = await spawnCli({ cwd: repo.dir, args: ['doctor', '--json'] });
    expect(doctorChecks(clean.json).stale?.status, 'a fresh install must not be stale').toBe('ok');

    // Simulate drift the way it actually happens — the installed copy is older than the server —
    // by rewriting the stamp in place. This needs no API restart, so it runs in every tier.
    const skillPath = join(repo.dir, '.claude/skills/pointer-feedback/SKILL.md');
    const before = readFileSync(skillPath, 'utf8');
    writeFileSync(skillPath, before.replace(/pointer-skill-version:\s*[^\s>]+/, 'pointer-skill-version: 0.0.1-ancient'));

    const stale = await spawnCli({ cwd: repo.dir, args: ['doctor', '--json'] });
    const staleCheck = doctorChecks(stale.json).stale;
    expect(staleCheck.status, 'doctor must notice the older copy').toBe('warn');
    expect(staleCheck.message).toContain('pointer-feedback');
    // A stale skill still works — it is a warning, never an error, and must not fail the exit code.
    expect(stale.code, 'staleness alone must not fail doctor').toBe(0);

    // --check reports without writing.
    const checkOnly = await spawnCli({ cwd: repo.dir, args: ['update', '--check'] });
    expect(checkOnly.code).toBe(0);
    expect(readFileSync(skillPath, 'utf8'), '--check must not write').toContain('0.0.1-ancient');

    const updated = await spawnCli({ cwd: repo.dir, args: ['update'] });
    expect(updated.code).toBe(0);
    expect(updated.stdout).toMatch(/updated \d+ file/);

    const after = await spawnCli({ cwd: repo.dir, args: ['doctor', '--json'] });
    expect(doctorChecks(after.json).stale?.status, 'update must clear the warning').toBe('ok');

    record({ id: 'R2-03-03', tier: 'PR', layer: 'cli', role: 'DEV', result: 'PASS',
      ms: Date.now() - start, detail: 'doctor warn → update → doctor ok' });
  } finally {
    repo.cleanup();
  }
});

test('R2-03-04 ⛓ — a server-side version bump makes every installed copy stale', async () => {
  test.skip(!isDestructiveRun, 'restarts the api container — runs only in the isolated upgrade phase');
  // TWO container restarts, and the dev image rebuilds on start (dotnet restore) — roughly 40s
  // each. The 30s default aborts mid-restart, which leaves the API down for every scenario after
  // this one and reports a confusing "could not reach the server" instead of a timeout.
  test.setTimeout(300_000);
  const start = Date.now();

  const repo = await freshInstall('other');
  const { restartApi } = await import('../scripts/restart-api.mjs');

  try {
    expect(doctorChecks((await spawnCli({ cwd: repo.dir, args: ['doctor', '--json'] })).json).stale?.status).toBe('ok');

    // The override exists so fixing wrong prose in a skill can invalidate installed copies WITHOUT
    // shipping code — this is that path, end to end.
    const bumped = `9.9.9-e2e-${Date.now()}`;
    await restartApi({ env: { Pointer__SkillVersion: bumped } });

    const meta = await getRaw('/api/meta');
    expect(meta.data?.skillVersion).toBe(bumped);

    const stale = await spawnCli({ cwd: repo.dir, args: ['doctor', '--json'] });
    expect(doctorChecks(stale.json).stale?.status).toBe('warn');
    expect(doctorChecks(stale.json).stale?.message).toContain(bumped);

    await spawnCli({ cwd: repo.dir, args: ['update'] });
    const fixed = await spawnCli({ cwd: repo.dir, args: ['doctor', '--json'] });
    expect(doctorChecks(fixed.json).stale?.status).toBe('ok');

    record({ id: 'R2-03-04', tier: 'nightly', layer: 'cli', role: 'DEV', result: 'PASS',
      ms: Date.now() - start, detail: `bumped to ${bumped}, doctor warned, update cleared it` });
  } finally {
    // Restore the derived default, or every later scenario sees a bumped server. Wrapped because a
    // failure to restore must not replace the assertion error that actually explains the failure.
    try {
      await restartApi({});
    } catch (err) {
      console.error(`could not restore the api after the version bump: ${err?.message ?? err}`);
    }
    repo.cleanup();
  }
});
