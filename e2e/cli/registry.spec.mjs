// E2E spec for R1-04: Upgrade hint against a genuinely old published CLI (Verdaccio registry).
// Proves:
// - R1-04-06 ⛓: Published version 0.1.0 is rejected with exit 5 and upgrade hint under MinCliVersion=99.0.0;
//   resolving @latest resolves newly published 99.1.0 which passes against the same server.
// - Strict registry isolation per harness §6.1: developer's ~/.npmrc and user config remain untouched.
// Contract: docs/roadmap/testing/R1-04-tests.md
// Tier: nightly (runs in --registry phase)
import { test, expect } from '@playwright/test';
import { mkdtempSync, rmSync, readdirSync, existsSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync, spawn } from 'node:child_process';
import { BASE_URL, raw, login } from '../scripts/lib/api.mjs';
import { USERS } from '../scripts/lib/constants.mjs';
import { record } from '../scripts/lib/report.mjs';
import { tempRepo } from '../scripts/lib/git.mjs';
import { restartApi } from '../scripts/restart-api.mjs';
import {
  REGISTRY_URL,
  captureNpmConfig,
  assertNpmConfigUnchanged,
  createScratchEnv,
  publishTarball,
} from '../scripts/lib/registry.mjs';

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
const isRegistryRun =
  process.env.E2E_REGISTRY === '1' ||
  process.argv.some((arg) => arg.includes('registry') || arg.includes('R1-04-06'));

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
  throw new Error('Unable to obtain developer API key for CLI registry scenario');
}

function runNpx(args, { cwd, env }) {
  return new Promise((resolvePromise) => {
    const proc = spawn('npx', args, {
      cwd,
      env: { ...process.env, ...env },
      stdio: ['ignore', 'pipe', 'pipe'],
    });

    let stdout = '';
    let stderr = '';

    proc.stdout.on('data', (d) => (stdout += d.toString()));
    proc.stderr.on('data', (d) => (stderr += d.toString()));

    proc.on('close', (code) => {
      resolvePromise({ stdout, stderr, code: code ?? 0 });
    });
  });
}

test('R1-04-06 ⛓ — upgrade hint against a genuinely old published CLI', async () => {
  test.skip(!isRegistryRun, 'Scenario R1-04-06 runs only in nightly/registry phase');
  const start = Date.now();

  // Capture npm config before anything else (harness §6.1 rule 4)
  const baselineConfig = captureNpmConfig();

  const scratch = mkdtempSync(join(tmpdir(), 'pointer-registry-scratch-'));
  const scratchEnv = createScratchEnv(scratch);
  let cwd = '';
  let openedWindow = false;

  let exitCode4 = -1;
  let exitCode5 = -1;
  let resolvedVersion = '';

  try {
    const cliDir = join(repoRoot, 'cli');

    // 1. Publish (once, before the window): cd cli && npm pack --pack-destination <scratch>
    execFileSync('npm', ['pack', '--pack-destination', scratch], {
      cwd: cliDir,
      env: { ...process.env, ...scratchEnv },
      stdio: 'pipe',
    });

    const packedFiles = readdirSync(scratch).filter((f) => f.endsWith('.tgz'));
    expect(packedFiles.length, 'Packed tarball must exist in scratch dir').toBeGreaterThan(0);
    const baseTgz = join(scratch, packedFiles[0]);

    // Publish 0.1.0 to Verdaccio
    publishTarball(baseTgz, { scratchDir: scratch });

    // 2. Extract that same tarball into <scratch>/v2, npm version 99.1.0 --no-git-tag-version there, re-pack, publish
    publishTarball(baseTgz, { version: '99.1.0', scratchDir: scratch });

    // 3. git -C <repo> status --porcelain cli/ -> empty (the repo was not dirtied)
    const cliStatus = execFileSync('git', ['status', '--porcelain', 'cli/'], {
      cwd: repoRoot,
      encoding: 'utf8',
    }).trim();
    expect(cliStatus, 'git status in cli/ must be clean; repo must not be dirtied').toBe('');

    // 4. (in-window, after R1-04-04 step 2 set min=99.0.0)
    // If running standalone, ensure API has Cli__MinVersion=99.0.0
    const metaCheck = await raw('GET', '/api/meta');
    if (metaCheck.data?.minCliVersion !== '99.0.0') {
      await restartApi({ env: { Cli__MinVersion: '99.0.0' } });
      openedWindow = true;
      await expect
        .poll(
          async () => {
            const m = await raw('GET', '/api/meta');
            return m.data?.minCliVersion;
          },
          { timeout: 120_000, intervals: [1_000, 2_000] }
        )
        .toBe('99.0.0');
    }

    const key = await getDeveloperApiKey();
    cwd = tempRepo(VITE_FIXTURE).dir;

    // In a fresh tempRepo() seeded from vite fixture:
    // npx --registry http://localhost:4873 -y pointer-feedback@0.1.0 init --server http://localhost:8090 --key K --project e2e-alpha --yes --json
    const res4 = await runNpx(
      [
        '--registry',
        REGISTRY_URL,
        '-y',
        'pointer-feedback@0.1.0',
        'init',
        '--server',
        BASE_URL,
        '--key',
        key,
        '--project',
        'e2e-alpha',
        '--yes',
        '--json',
      ],
      { cwd, env: scratchEnv }
    );
    exitCode4 = res4.code;
    expect(exitCode4, '0.1.0 CLI init under min=99.0.0 must exit 5').toBe(5);

    const outText4 = `${res4.stdout} ${res4.stderr}`;
    expect(outText4, 'output must contain older than the server requires').toContain(
      'older than the server requires'
    );
    expect(outText4, 'output must contain upgrade hint').toContain('npx -y pointer-feedback@latest');
    expect(
      existsSync(join(cwd, '.pointer', 'config.json')),
      'no .pointer/config.json should be written when init is refused'
    ).toBe(false);

    // 5. Same temp dir:
    // npx --registry http://localhost:4873 -y pointer-feedback@latest init --server http://localhost:8090 --key K --project e2e-alpha --yes --json
    const res5 = await runNpx(
      [
        '--registry',
        REGISTRY_URL,
        '-y',
        'pointer-feedback@latest',
        'init',
        '--server',
        BASE_URL,
        '--key',
        key,
        '--project',
        'e2e-alpha',
        '--yes',
        '--json',
      ],
      { cwd, env: scratchEnv }
    );
    exitCode5 = res5.code;
    expect(exitCode5, `@latest CLI init must succeed with exit 0: ${res5.stderr}`).toBe(0);

    let json5;
    try {
      json5 = JSON.parse(res5.stdout.trim());
    } catch {}
    resolvedVersion = json5?.cliVersion || '';
    expect(
      resolvedVersion,
      'Resolved @latest cliVersion must be 99.1.0, proving newer version resolved'
    ).toBe('99.1.0');

    const durationMs = Date.now() - start;
    record({
      id: 'R1-04-06',
      tier: 'nightly',
      layer: 'cli',
      role: 'developer',
      result: 'PASS',
      ms: durationMs,
      detail: `exit4=${exitCode4}, exit5=${exitCode5}, resolvedVersion=${resolvedVersion}, reg=${baselineConfig.registry}`,
    });
  } finally {
    // 7. Teardown: compare npm config get registry --location=user and sha256(~/.npmrc)
    try {
      assertNpmConfigUnchanged(baselineConfig);
    } catch (npmErr) {
      console.error('NPM config assertion failed:', npmErr);
      throw npmErr;
    }

    if (openedWindow) {
      try {
        await restartApi();
      } catch (rstErr) {
        console.error('Failed to restore API after R1-04-06 window:', rstErr);
      }
    }

    if (cwd) {
      try {
        rmSync(cwd, { recursive: true, force: true });
      } catch {}
    }
    try {
      rmSync(scratch, { recursive: true, force: true });
    } catch {}
  }
});
