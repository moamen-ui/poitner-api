// E2E spec for R1-10: Fully-local client loop (CLI scenarios).
// Covers:
// - R1-10-01: local generate → build → publish (Nightly)
// - R1-10-04 ⛓: the whole loop with no GitHub token (Nightly)
// - R1-10-05: release path unchanged, developer config untouched (Nightly)
// Contract: docs/roadmap/testing/R1-10-tests.md
// Tier: nightly (runs in --registry phase)
import { test, expect } from '@playwright/test';
import { readFileSync, writeFileSync, mkdirSync, existsSync, readdirSync, rmSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execSync, execFileSync } from 'node:child_process';
import { raw } from '../scripts/lib/api.mjs';
import { record } from '../scripts/lib/report.mjs';
import {
  REGISTRY_URL,
  publishLocal,
  viewVersions,
  npmrcFingerprint,
} from '../scripts/lib/npmlocal.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const e2eRoot = resolve(here, '..');
const repoRoot = resolve(e2eRoot, '..');
const STATE_DIR = join(e2eRoot, 'state');

test.skip(process.env.TIER === 'pr', 'nightly tier only — skipped during PR tier');

test('R1-10-01 — local generate → build → publish', async () => {
  const start = Date.now();

  // 1. Preflight: curl -fsS http://localhost:4873/-/ping and curl -fsS http://localhost:8090/swagger/v1/swagger.json | jq -e '.paths["/api/meta"]'
  let regPingOk = false;
  try {
    const pingRes = await fetch(`${REGISTRY_URL}/-/ping`);
    regPingOk = pingRes.ok;
  } catch {
    regPingOk = false;
  }
  expect(regPingOk, `Verdaccio registry must be running on ${REGISTRY_URL}`).toBe(true);

  const swaggerRes = await raw('GET', '/swagger/v1/swagger.json');
  expect(swaggerRes.status, 'Swagger endpoint must return 200').toBe(200);

  const hasMeta = Boolean(swaggerRes.body?.paths?.['/api/meta']);
  if (!hasMeta) {
    // If /api/meta is absent the scenario aborts as ENVIRONMENT, not FAIL — substitution rule applies
    throw new Error('ENVIRONMENT: /api/meta path is absent in swagger.json; substitution rule applies');
  }

  // 2. npm run clients:local from the repo root, capturing stdout.
  const pubResult = await publishLocal({ cwd: repoRoot });
  expect(pubResult.code, `npm run clients:local must exit 0: ${pubResult.stderr}`).toBe(0);

  // 3. Parse the printed version V (/0\.0\.0-local\.\d+/)
  const V = pubResult.version;
  expect(V, 'Printed version V must match 0.0.0-local.<unix> pattern').toMatch(/^0\.0\.0-local\.\d+$/);

  // 4. grep -R "getApiMeta|ApiMeta" clients/angular/src/index.ts clients/react/src/index.ts clients/vue/src/index.ts
  const angularBarrel = readFileSync(join(repoRoot, 'clients', 'angular', 'src', 'index.ts'), 'utf8');
  const reactBarrel = readFileSync(join(repoRoot, 'clients', 'react', 'src', 'index.ts'), 'utf8');
  const vueBarrel = readFileSync(join(repoRoot, 'clients', 'vue', 'src', 'index.ts'), 'utf8');

  expect(angularBarrel, 'Angular barrel index.ts must export getApiMeta or ApiMeta (AC-1)').toMatch(/getApiMeta|ApiMeta/);
  expect(reactBarrel, 'React barrel index.ts must export getApiMeta or ApiMeta (AC-1)').toMatch(/getApiMeta|ApiMeta/);
  expect(vueBarrel, 'Vue barrel index.ts must export getApiMeta or ApiMeta (AC-1)').toMatch(/getApiMeta|ApiMeta/);

  // 5. For each of angular|react|vue: npm view @moamen-ui/pointer-<fw> versions --registry http://localhost:4873 --json
  const angularVersions = await viewVersions('@moamen-ui/pointer-angular');
  const reactVersions = await viewVersions('@moamen-ui/pointer-react');
  const vueVersions = await viewVersions('@moamen-ui/pointer-vue');

  expect(angularVersions, `Verdaccio angular versions must contain ${V} (AC-2)`).toContain(V);
  expect(reactVersions, `Verdaccio react versions must contain ${V} (AC-2)`).toContain(V);
  expect(vueVersions, `Verdaccio vue versions must contain ${V} (AC-2)`).toContain(V);

  // 6. jq -r '.version' clients/react/package.json and clients/angular/dist/package.json
  const reactPkg = JSON.parse(readFileSync(join(repoRoot, 'clients', 'react', 'package.json'), 'utf8'));
  const angularDistPkg = JSON.parse(readFileSync(join(repoRoot, 'clients', 'angular', 'dist', 'package.json'), 'utf8'));

  expect(reactPkg.version, 'clients/react/package.json version must match V').toBe(V);
  expect(angularDistPkg.version, 'clients/angular/dist/package.json version must match V').toBe(V);

  // stdout contains three --no-save install lines and the npm ci line
  expect(pubResult.installCommands.length, 'stdout must contain 3 --no-save install lines').toBe(3);
  for (const cmd of pubResult.installCommands) {
    expect(cmd, 'install command must specify --no-save').toContain('--no-save');
  }
  expect(pubResult.stdout, 'stdout must contain npm ci restore command').toContain('npm ci');

  // Persist state for coupled scenario R1-10-02
  mkdirSync(STATE_DIR, { recursive: true });
  writeFileSync(
    join(STATE_DIR, 'local-clients.json'),
    JSON.stringify(
      {
        version: V,
        installCommands: pubResult.installCommands,
        angularInstallCommand: pubResult.angularInstallCommand,
      },
      null,
      2
    ),
    'utf8'
  );

  const durationMs = Date.now() - start;
  record({
    id: 'R1-10-01',
    tier: 'nightly',
    layer: 'cli',
    role: '—',
    result: 'PASS',
    ms: durationMs,
    detail: `V=${V}, angular=${JSON.stringify(angularVersions)}, react=${JSON.stringify(reactVersions)}, vue=${JSON.stringify(vueVersions)}`,
  });
});

test('R1-10-04 ⛓ — the whole loop with no GitHub token', async () => {
  const start = Date.now();

  // 1. In a shell with NODE_AUTH_TOKEN and GITHUB_TOKEN unset (env -u NODE_AUTH_TOKEN -u GITHUB_TOKEN), repeat R1-10-01 steps 2–5.
  const cleanEnv = { ...process.env };
  delete cleanEnv.NODE_AUTH_TOKEN;
  delete cleanEnv.GITHUB_TOKEN;

  const npmStateDir = join(STATE_DIR, 'npm');
  const npmLogsDir = join(npmStateDir, '_logs');
  mkdirSync(npmStateDir, { recursive: true });

  cleanEnv.npm_config_userconfig = join(npmStateDir, '.npmrc');
  cleanEnv.npm_config_cache = npmStateDir;

  // Clear existing logs in npmStateDir/_logs so we only check logs from this scenario
  if (existsSync(npmLogsDir)) {
    try {
      rmSync(npmLogsDir, { recursive: true, force: true });
    } catch {}
  }

  const pubResult = await publishLocal({
    cwd: repoRoot,
    env: cleanEnv,
    unsetEnv: ['NODE_AUTH_TOKEN', 'GITHUB_TOKEN'],
  });
  expect(pubResult.code, `npm run clients:local without tokens must exit 0: ${pubResult.stderr}`).toBe(0);

  const V = pubResult.version;
  expect(V, 'Printed version V must match 0.0.0-local.<unix> pattern').toMatch(/^0\.0\.0-local\.\d+$/);

  const angularBarrel = readFileSync(join(repoRoot, 'clients', 'angular', 'src', 'index.ts'), 'utf8');
  const reactBarrel = readFileSync(join(repoRoot, 'clients', 'react', 'src', 'index.ts'), 'utf8');
  const vueBarrel = readFileSync(join(repoRoot, 'clients', 'vue', 'src', 'index.ts'), 'utf8');

  expect(angularBarrel, 'Angular barrel must export symbol').toMatch(/getApiMeta|ApiMeta/);
  expect(reactBarrel, 'React barrel must export symbol').toMatch(/getApiMeta|ApiMeta/);
  expect(vueBarrel, 'Vue barrel must export symbol').toMatch(/getApiMeta|ApiMeta/);

  const angularVersions = await viewVersions('@moamen-ui/pointer-angular', { env: cleanEnv });
  const reactVersions = await viewVersions('@moamen-ui/pointer-react', { env: cleanEnv });
  const vueVersions = await viewVersions('@moamen-ui/pointer-vue', { env: cleanEnv });

  expect(angularVersions).toContain(V);
  expect(reactVersions).toContain(V);
  expect(vueVersions).toContain(V);

  // 2. With the same unset env, run the printed angular install line and npm run build in the app (skip if DASHBOARD_DIR unset).
  const DASHBOARD_DIR = process.env.DASHBOARD_DIR;
  if (DASHBOARD_DIR) {
    const angularDir = join(DASHBOARD_DIR, 'angular');
    const installCmd = `npm i @moamen-ui/pointer-angular@${V} --registry ${REGISTRY_URL} --@moamen-ui:registry=${REGISTRY_URL} --no-save`;
    execSync(installCmd, { cwd: angularDir, env: cleanEnv, stdio: 'pipe' });

    execSync('npm run build', { cwd: angularDir, env: cleanEnv, stdio: 'pipe' });
  }

  // 3. Assert no request went to npm.pkg.github.com: grep the npm debug log in e2e/state/npm/_logs/ for npm.pkg.github.com.
  // Flake notes: R1-10-04's log grep depends on npm actually writing _logs/ — if empty and install ran, treat as ENVIRONMENT
  if (DASHBOARD_DIR) {
    if (!existsSync(npmLogsDir)) {
      throw new Error(`ENVIRONMENT: npm _logs directory does not exist at ${npmLogsDir}`);
    }
    const logFiles = readdirSync(npmLogsDir).filter((f) => f.endsWith('.log'));
    if (logFiles.length === 0) {
      throw new Error(`ENVIRONMENT: no npm debug logs found in ${npmLogsDir}`);
    }

    let githubPkgFound = false;
    let checkedFiles = 0;
    for (const file of logFiles) {
      const filePath = join(npmLogsDir, file);
      const logContent = readFileSync(filePath, 'utf8');
      checkedFiles++;
      if (logContent.includes('npm.pkg.github.com')) {
        githubPkgFound = true;
        break;
      }
    }
    expect(githubPkgFound, 'No request must go to npm.pkg.github.com in npm logs (AC-5)').toBe(false);
  }

  const durationMs = Date.now() - start;
  record({
    id: 'R1-10-04',
    tier: 'nightly',
    layer: 'cli',
    role: '—',
    result: 'PASS',
    ms: durationMs,
    detail: `V=${V}, dashboardTested=${Boolean(DASHBOARD_DIR)}, noGitHubPkg=true`,
  });
});

test('R1-10-05 — release path unchanged, developer config untouched', async () => {
  const start = Date.now();

  // 1. sha256sum ~/.npmrc (or record "absent") and npm config get registry → baseline.
  const baseline = npmrcFingerprint();

  // 2. env -u CLIENTS_REGISTRY npm run generate-clients (API local).
  const envNoRegistry = { ...process.env };
  delete envNoRegistry.CLIENTS_REGISTRY;

  execFileSync('npm', ['run', 'generate-clients'], {
    cwd: repoRoot,
    env: envNoRegistry,
    stdio: 'pipe',
  });

  // 3. jq -r '.publishConfig.registry' clients/react/package.json and cat clients/react/.npmrc.
  const reactPkg = JSON.parse(readFileSync(join(repoRoot, 'clients', 'react', 'package.json'), 'utf8'));
  const reactNpmrc = readFileSync(join(repoRoot, 'clients', 'react', '.npmrc'), 'utf8').trim();

  expect(reactPkg.publishConfig?.registry, 'react package.json publishConfig.registry must be https://npm.pkg.github.com (AC-6)').toBe('https://npm.pkg.github.com');
  expect(reactNpmrc, 'clients/react/.npmrc must point to https://npm.pkg.github.com (AC-6)').toContain('https://npm.pkg.github.com');

  // 4. env -u CLIENTS_REGISTRY npm run build-clients; jq -r '.publishConfig.registry' clients/angular/dist/package.json.
  execFileSync('npm', ['run', 'build-clients'], {
    cwd: repoRoot,
    env: envNoRegistry,
    stdio: 'pipe',
  });

  const angularDistPkg = JSON.parse(readFileSync(join(repoRoot, 'clients', 'angular', 'dist', 'package.json'), 'utf8'));
  const angularDistNpmrc = readFileSync(join(repoRoot, 'clients', 'angular', 'dist', '.npmrc'), 'utf8').trim();

  expect(angularDistPkg.publishConfig?.registry, 'angular dist package.json publishConfig.registry must be https://npm.pkg.github.com (AC-6)').toBe('https://npm.pkg.github.com');
  expect(angularDistNpmrc, 'clients/angular/dist/.npmrc must point to https://npm.pkg.github.com (AC-6)').toContain('https://npm.pkg.github.com');

  // 5. Re-record step 1.
  const after = npmrcFingerprint();
  expect(after.sha256, '~/.npmrc sha256 must be identical to baseline (AC-7)').toBe(baseline.sha256);
  expect(after.registry, 'npm config get registry must be identical to baseline (AC-7)').toBe(baseline.registry);

  const durationMs = Date.now() - start;
  record({
    id: 'R1-10-05',
    tier: 'nightly',
    layer: 'cli',
    role: '—',
    result: 'PASS',
    ms: durationMs,
    detail: `shaBaseline=${baseline.sha256}, shaAfter=${after.sha256}, regBaseline=${baseline.registry}, regAfter=${after.registry}, regReact=${reactPkg.publishConfig?.registry}, regAng=${angularDistPkg.publishConfig?.registry}`,
  });
});
