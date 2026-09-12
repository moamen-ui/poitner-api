// E2E spec for R1-04: pointer doctor checks and min-version gate.
// Covers:
// - R1-04-02: doctor-green-after-init (PR)
// - R1-04-03 ⛓: doctor-detects-tracked-credentials (PR)
// - R1-04-04 ⛓: min-version gate Cli__MinVersion=99.0.0 (Nightly)
// Contract: docs/roadmap/testing/R1-04-tests.md
import { test, expect } from '@playwright/test';
import { readFileSync, writeFileSync, existsSync, rmSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
import { BASE_URL, raw, login } from '../scripts/lib/api.mjs';
import { USERS } from '../scripts/lib/constants.mjs';
import { record } from '../scripts/lib/report.mjs';
import { spawnCli } from '../scripts/lib/cli.mjs';
import { tempRepo } from '../scripts/lib/git.mjs';
import { restartApi } from '../scripts/restart-api.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const e2eRoot = resolve(here, '..');
const repoRoot = resolve(e2eRoot, '..');
const STATE_DIR = join(e2eRoot, 'state');
const KEYS_PATH = join(STATE_DIR, 'keys.json');
const CREDENTIALS_PATH = join(STATE_DIR, 'credentials.json');
const VITE_FIXTURE = join(repoRoot, 'cli', 'test', 'fixtures', 'vite');

// Gated on the PHASE flag only, never on TIER.
//
// Tier decides which phases run; the phase decides what is safe to run inside it. Conflating them
// put this scenario back inside the ordinary `api` phase for every nightly run, where it restarted
// the api container and ECONNRESET'd ten unrelated specs. The runner sets the flag below in the
// one phase that has this scenario to itself.
const isDestructiveRun =
  process.env.E2E_DESTRUCTIVE === '1' ||
  process.argv.some((arg) => arg.includes('R1-04-04'));

async function getDeveloperApiKey() {
  if (existsSync(KEYS_PATH)) {
    try {
      const keys = JSON.parse(readFileSync(KEYS_PATH, 'utf8'));
      if (keys.developer?.apiKey) return keys.developer.apiKey;
    } catch {}
  }

  let email = USERS.developer.email;
  let password = USERS.developer.password;
  if (existsSync(CREDENTIALS_PATH)) {
    try {
      const creds = JSON.parse(readFileSync(CREDENTIALS_PATH, 'utf8'));
      if (creds.developer) {
        email = creds.developer.email;
        password = creds.developer.password;
      }
    } catch {}
  }

  const devLogin = await login(email, password);
  const keyRes = await raw('GET', '/api/me/api-key', { token: devLogin.token });
  if (keyRes.data?.apiKey) return keyRes.data.apiKey;
  throw new Error('Unable to obtain developer API key for CLI doctor scenarios');
}

test('R1-04-02 — doctor-green-after-init', async () => {
  const start = Date.now();
  const cwd = tempRepo(VITE_FIXTURE).dir;
  let stdoutDetail = '';

  try {
    const key = await getDeveloperApiKey();

    // 2. pointer init --server http://localhost:8090 --key <K> --project e2e-alpha --environment local --tool other --yes --json
    const initRes = await spawnCli({
      cwd,
      args: [
        'init',
        '--server',
        BASE_URL,
        '--key',
        key,
        '--project',
        'e2e-alpha',
        '--environment',
        'local',
        '--tool',
        'other',
        '--yes',
        '--json',
      ],
    });
    expect(initRes.code, `init must exit 0: ${initRes.stderr}`).toBe(0);

    // 3. pointer doctor --json
    const docJsonRes = await spawnCli({ cwd, args: ['doctor', '--json'] });
    expect(docJsonRes.code, `doctor --json must exit 0: ${docJsonRes.stderr}`).toBe(0);

    // AC-5: doctor --json is valid JSON and nothing else on stdout
    expect(
      docJsonRes.json,
      `doctor --json must output valid JSON without extraneous text. Raw stdout: ${docJsonRes.stdout}`
    ).toBeTruthy();
    expect(docJsonRes.json.ok, 'doctor outcome ok must be true').toBe(true);

    const expectedChecks = [
      'config',
      'server',
      'meta',
      'clock',
      'key',
      'project',
      'widget',
      'widget-served',
      'skills',
      'gitignore',
      'stack',
    ];
    const checks = docJsonRes.json.checks || [];
    const checkIds = checks.map((c) => c.id);

    expect(
      checkIds.slice().sort(),
      'checks ids must be exactly config, server, meta, clock, key, project, widget, widget-served, skills, gitignore, stack'
    ).toEqual(expectedChecks.slice().sort());

    for (const c of checks) {
      expect(c.status, `check ${c.id} status must be 'ok'`).toBe('ok');
    }

    // 4. pointer doctor (human mode)
    const docHumanRes = await spawnCli({ cwd, args: ['doctor'] });
    expect(docHumanRes.code, `human doctor must exit 0: ${docHumanRes.stderr}`).toBe(0);
    const checkLines = docHumanRes.stdout
      .split('\n')
      .map((l) => l.trim())
      .filter((l) => l.startsWith('✔') || l.startsWith('✘') || l.startsWith('⚠'));
    expect(checkLines.length, 'human mode must print check status lines').toBeGreaterThan(0);
    for (const line of checkLines) {
      expect(line.startsWith('✔'), `every check line must start with ✔: ${line}`).toBe(true);
    }

    stdoutDetail = `jsonOk=${docJsonRes.json.ok}, checkCount=${checks.length}`;

    // 5. Decision — dropped (the exec doc's doctor posts event without projectKey, dropped in spec).

    // 6. Stale-key negative: overwrite .pointer/credentials.env with invalid key
    const staleCreds = 'POINTER_API_KEY=ptr_0000000000000000000000000000000000000000\n';
    writeFileSync(join(cwd, '.pointer', 'credentials.env'), staleCreds, 'utf8');

    const staleRes = await spawnCli({ cwd, args: ['doctor', '--json'] });
    expect(staleRes.code, 'stale-key negative must exit 3 (precedence rule 2: key ✘ outranks others)').toBe(3);
    expect(staleRes.json, 'stale-key doctor must return valid JSON').toBeTruthy();
    expect(staleRes.json.ok, 'stale-key ok must be false').toBe(false);

    const keyCheck = (staleRes.json.checks || []).find((c) => c.id === 'key');
    expect(keyCheck, 'key check must be present in checks list').toBeTruthy();
    expect(keyCheck.status, "key check status must be 'error'").toBe('error');

    const durationMs = Date.now() - start;
    record({
      id: 'R1-04-02',
      tier: 'PR',
      layer: 'cli',
      role: 'developer',
      result: 'PASS',
      ms: durationMs,
      detail: stdoutDetail,
    });
  } finally {
    try {
      rmSync(cwd, { recursive: true, force: true });
    } catch {}
  }
});

test('R1-04-03 ⛓ — doctor-detects-tracked-credentials', async () => {
  const start = Date.now();
  const cwd = tempRepo(VITE_FIXTURE).dir;

  try {
    const key = await getDeveloperApiKey();

    // 1. Initialize repo
    const initRes = await spawnCli({
      cwd,
      args: [
        'init',
        '--server',
        BASE_URL,
        '--key',
        key,
        '--project',
        'e2e-alpha',
        '--environment',
        'local',
        '--tool',
        'other',
        '--yes',
        '--json',
      ],
    });
    expect(initRes.code, `init must exit 0: ${initRes.stderr}`).toBe(0);

    // 2. git add .pointer/credentials.env && git commit -m leak
    execFileSync('git', ['add', '-f', '.pointer/credentials.env'], { cwd });
    execFileSync('git', ['commit', '-m', 'leak'], { cwd });

    // 3. pointer doctor --json
    const leakDocRes = await spawnCli({ cwd, args: ['doctor', '--json'] });
    expect(leakDocRes.code, 'doctor with tracked credentials.env must exit 1').toBe(1);
    expect(leakDocRes.json, 'doctor must return valid JSON').toBeTruthy();
    expect(leakDocRes.json.ok, 'doctor ok must be false').toBe(false);

    const gitignoreCheck = (leakDocRes.json.checks || []).find((c) => c.id === 'gitignore');
    expect(gitignoreCheck, 'gitignore check must exist').toBeTruthy();
    expect(gitignoreCheck.status, "gitignore check status must be 'error'").toBe('error');
    expect(
      gitignoreCheck.message,
      'message must mention credentials.env is tracked'
    ).toMatch(/credentials\.env is tracked/);

    // 4. git rm --cached .pointer/credentials.env (untrack, file stays on disk)
    execFileSync('git', ['rm', '--cached', '.pointer/credentials.env'], { cwd });
    expect(
      existsSync(join(cwd, '.pointer', 'credentials.env')),
      '.pointer/credentials.env must still exist on disk'
    ).toBe(true);

    const untrackedDocRes = await spawnCli({ cwd, args: ['doctor', '--json'] });
    expect(untrackedDocRes.code, 'doctor must exit 0 again after file is untracked').toBe(0);
    expect(untrackedDocRes.json?.ok, 'ok must be true after untracking').toBe(true);

    const untrackedGitignoreCheck = (untrackedDocRes.json?.checks || []).find((c) => c.id === 'gitignore');
    expect(untrackedGitignoreCheck?.status, "gitignore check status must return to 'ok'").toBe('ok');

    // 5. Decision: no --fix rescue is asserted.

    const durationMs = Date.now() - start;
    record({
      id: 'R1-04-03',
      tier: 'PR',
      layer: 'cli',
      role: 'developer',
      result: 'PASS',
      ms: durationMs,
      detail: 'leakExit=1, untrackedExit=0',
    });
  } finally {
    try {
      rmSync(cwd, { recursive: true, force: true });
    } catch {}
  }
});

test('R1-04-04 ⛓ — min-version gate (Cli__MinVersion=99.0.0)', async () => {
  test.skip(
    !isDestructiveRun,
    'Scenario R1-04-04 restarts the api container — runs only in nightly/upgrade phase'
  );

  const start = Date.now();
  const cwd = tempRepo(VITE_FIXTURE).dir;
  let restarted = false;

  try {
    const key = await getDeveloperApiKey();

    // 1. Fresh fixture+init repo; baseline doctor --json -> exit 0
    const initRes = await spawnCli({
      cwd,
      args: [
        'init',
        '--server',
        BASE_URL,
        '--key',
        key,
        '--project',
        'e2e-alpha',
        '--environment',
        'local',
        '--tool',
        'other',
        '--yes',
        '--json',
      ],
    });
    expect(initRes.code, `init must exit 0: ${initRes.stderr}`).toBe(0);

    const baseDocRes = await spawnCli({ cwd, args: ['doctor', '--json'] });
    expect(baseDocRes.code, 'baseline doctor must exit 0').toBe(0);

    // 2. restart-api with Cli__MinVersion=99.0.0
    await restartApi({ env: { Cli__MinVersion: '99.0.0' } });
    restarted = true;

    // Re-wait gate: verify GET /api/meta reflects minCliVersion === '99.0.0'
    await expect
      .poll(
        async () => {
          const meta = await raw('GET', '/api/meta');
          return meta.data?.minCliVersion;
        },
        { timeout: 120_000, intervals: [1_000, 2_000] }
      )
      .toBe('99.0.0');

    // 3. spawnCli doctor --json -> exit 5
    const docRes = await spawnCli({ cwd, args: ['doctor', '--json'] });
    expect(docRes.code, 'doctor must exit 5 when cli is older than minCliVersion').toBe(5);

    const outText = `${docRes.stdout} ${docRes.stderr}`;
    expect(outText, 'must contain older than the server requires').toContain('older than the server requires');
    expect(outText, 'must contain upgrade hint with pointer-feedback@latest').toContain('npx -y pointer-feedback@latest');

    // Precedence rule 1: checks that ran are exactly config, server, meta (stop immediately)
    const executedCheckIds = (docRes.json?.checks || []).map((c) => c.id);
    expect(
      executedCheckIds,
      'checks that ran under exit 5 must be exactly config, server, meta'
    ).toEqual(['config', 'server', 'meta']);

    // 4. spawnCli init --server http://localhost:8090 --key K --project e2e-alpha --yes -> exit 5
    const initRefuseRes = await spawnCli({
      cwd,
      args: ['init', '--server', BASE_URL, '--key', key, '--project', 'e2e-alpha', '--yes'],
    });
    expect(initRefuseRes.code, 'init must refuse with exit 5 when cli is older than minCliVersion').toBe(5);

    const initOutText = `${initRefuseRes.stdout} ${initRefuseRes.stderr}`;
    expect(initOutText, 'init must contain older than the server requires').toContain('older than the server requires');
    expect(initOutText, 'init must contain upgrade hint').toContain('npx -y pointer-feedback@latest');

    // 5. Restore: clean restart-api with no override -> minCliVersion back to 0.1.0; doctor -> exit 0
    await restartApi();
    restarted = false;

    await expect
      .poll(
        async () => {
          const meta = await raw('GET', '/api/meta');
          return meta.data?.minCliVersion;
        },
        { timeout: 120_000, intervals: [1_000, 2_000] }
      )
      .toBe('0.1.0');

    const restoredDocRes = await spawnCli({ cwd, args: ['doctor', '--json'] });
    expect(restoredDocRes.code, 'doctor must exit 0 after restoring minCliVersion').toBe(0);

    const durationMs = Date.now() - start;
    record({
      id: 'R1-04-04',
      tier: 'nightly',
      layer: 'cli',
      role: 'developer',
      result: 'PASS',
      ms: durationMs,
      detail: 'overriddenExit=5, restoredExit=0',
    });
  } catch (err) {
    try {
      const logs = execFileSync('docker', ['compose', 'logs', 'api', '--tail', '50'], {
        cwd: repoRoot,
        encoding: 'utf8',
      });
      console.error('API logs after failure:\n', logs);
    } catch {}
    throw err;
  } finally {
    if (restarted) {
      try {
        await restartApi();
      } catch (restoreErr) {
        console.error('Failed to restore API in finally block:', restoreErr);
      }
    }
    try {
      rmSync(cwd, { recursive: true, force: true });
    } catch {}
  }
});
