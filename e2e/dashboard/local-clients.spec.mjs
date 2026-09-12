// E2E spec for R1-10: Dashboard consumption of local clients and restore.
// Covers:
// - R1-10-02 ⛓: dashboard consumes it without dirtying the repo (Nightly)
// - R1-10-03 ⛓: opt-out restores the published client (Nightly)
// Contract: docs/roadmap/testing/R1-10-tests.md
// Tier: nightly
import { test, expect } from '@playwright/test';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync, execSync } from 'node:child_process';
import { record } from '../scripts/lib/report.mjs';
import { REGISTRY_URL, viewVersions } from '../scripts/lib/npmlocal.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const e2eRoot = resolve(here, '..');
const STATE_DIR = join(e2eRoot, 'state');

test.skip(process.env.TIER === 'pr', 'nightly tier only — skipped during PR tier');

test('R1-10-02 ⛓ — dashboard consumes it without dirtying the repo', async () => {
  const start = Date.now();

  // Guard: SKIP unless DASHBOARD_DIR is set (harness §10)
  const DASHBOARD_DIR = process.env.DASHBOARD_DIR;
  if (!DASHBOARD_DIR) {
    record({
      id: 'R1-10-02',
      tier: 'nightly',
      layer: 'dashboard',
      role: '—',
      result: 'SKIP',
      ms: 0,
      detail: 'DASHBOARD_DIR is unset (harness §10)',
    });
    test.skip(true, 'DASHBOARD_DIR is unset (harness §10)');
    return;
  }

  // 1. git -C $DASHBOARD_DIR status --porcelain → record as the baseline (must already be empty; if not, ENVIRONMENT, stop).
  const baselineStatus = execFileSync('git', ['-C', DASHBOARD_DIR, 'status', '--porcelain'], {
    encoding: 'utf8',
  }).trim();

  if (baselineStatus !== '') {
    throw new Error(`ENVIRONMENT: git status in DASHBOARD_DIR is not clean:\n${baselineStatus}`);
  }

  // Resolve V and install line from state or Verdaccio
  let V = '';
  const localClientsPath = join(STATE_DIR, 'local-clients.json');
  if (existsSync(localClientsPath)) {
    try {
      const state = JSON.parse(readFileSync(localClientsPath, 'utf8'));
      V = state.version;
    } catch {}
  }

  if (!V) {
    const versions = await viewVersions('@moamen-ui/pointer-angular');
    const localVersions = versions.filter((v) => v.startsWith('0.0.0-local.'));
    if (localVersions.length > 0) {
      V = localVersions[localVersions.length - 1];
    } else {
      throw new Error('ENVIRONMENT: No local version published. R1-10-01 must run first.');
    }
  }

  // 2. Run the exact angular install line R1-10-01 printed.
  const angularDir = join(DASHBOARD_DIR, 'angular');
  const installCmd = `npm i @moamen-ui/pointer-angular@${V} --registry ${REGISTRY_URL} --@moamen-ui:registry=${REGISTRY_URL} --no-save`;
  execSync(installCmd, { cwd: angularDir, stdio: 'pipe' });

  // 3. jq -r '.version' $DASHBOARD_DIR/angular/node_modules/@moamen-ui/pointer-angular/package.json
  const installedPkgPath = join(angularDir, 'node_modules', '@moamen-ui', 'pointer-angular', 'package.json');
  expect(existsSync(installedPkgPath), 'installed package.json must exist in node_modules').toBe(true);
  const installedPkg = JSON.parse(readFileSync(installedPkgPath, 'utf8'));
  expect(installedPkg.version, 'Installed package version must match local V (AC-3)').toBe(V);

  // 4. git -C $DASHBOARD_DIR status --porcelain -> byte-identical to baseline
  const afterStatus = execFileSync('git', ['-C', DASHBOARD_DIR, 'status', '--porcelain'], {
    encoding: 'utf8',
  }).trim();
  expect(afterStatus, 'git status must be byte-identical to baseline (empty)').toBe(baselineStatus);

  // 5. git -C $DASHBOARD_DIR diff --stat -- angular/package.json angular/package-lock.json -> empty
  const diffStat = execFileSync(
    'git',
    ['-C', DASHBOARD_DIR, 'diff', '--stat', '--', 'angular/package.json', 'angular/package-lock.json'],
    { encoding: 'utf8' }
  ).trim();
  expect(diffStat, 'git diff --stat must be empty (--no-save touched neither file)').toBe('');

  // 6. (cd $DASHBOARD_DIR/angular && npm run build)
  const buildOutput = execFileSync('npm', ['run', 'build'], {
    cwd: angularDir,
    encoding: 'utf8',
    stdio: 'pipe',
  });

  // 7. grep -R "ApiMeta" $DASHBOARD_DIR/angular/node_modules/@moamen-ui/pointer-angular/
  const installedModuleDir = join(angularDir, 'node_modules', '@moamen-ui', 'pointer-angular');
  const grepResult = execFileSync('grep', ['-R', 'ApiMeta', installedModuleDir], {
    encoding: 'utf8',
    stdio: 'pipe',
  });
  expect(grepResult, 'ApiMeta symbol must be present in installed client package').toContain('ApiMeta');

  const buildTail = buildOutput.slice(-300).replace(/\n/g, ' ');
  const durationMs = Date.now() - start;
  record({
    id: 'R1-10-02',
    tier: 'nightly',
    layer: 'dashboard',
    role: '—',
    result: 'PASS',
    ms: durationMs,
    detail: `V=${V}, gitStatusBefore=${baselineStatus || 'clean'}, gitStatusAfter=${afterStatus || 'clean'}, buildTail=${buildTail}`,
  });
});

test('R1-10-03 ⛓ — opt-out restores the published client', async () => {
  const start = Date.now();

  // Guard: SKIP unless DASHBOARD_DIR is set and NODE_AUTH_TOKEN is present
  const DASHBOARD_DIR = process.env.DASHBOARD_DIR;
  const NODE_AUTH_TOKEN = process.env.NODE_AUTH_TOKEN;
  if (!DASHBOARD_DIR || !NODE_AUTH_TOKEN) {
    const reason = !DASHBOARD_DIR
      ? 'DASHBOARD_DIR is unset (harness §10)'
      : 'NODE_AUTH_TOKEN is unset (published packages require GitHub token)';
    record({
      id: 'R1-10-03',
      tier: 'nightly',
      layer: 'dashboard',
      role: '—',
      result: 'SKIP',
      ms: 0,
      detail: reason,
    });
    test.skip(true, reason);
    return;
  }

  const angularDir = join(DASHBOARD_DIR, 'angular');

  // 1. (cd $DASHBOARD_DIR/angular && npm ci)
  execFileSync('npm', ['ci'], {
    cwd: angularDir,
    stdio: 'pipe',
  });

  // 2. jq -r '.version' .../node_modules/@moamen-ui/pointer-angular/package.json
  const pkgPath = join(angularDir, 'node_modules', '@moamen-ui', 'pointer-angular', 'package.json');
  const restoredPkg = JSON.parse(readFileSync(pkgPath, 'utf8'));
  expect(restoredPkg.version, 'Restored version must not be 0.0.0-local.* (AC-4)').not.toMatch(/^0\.0\.0-local/);
  expect(restoredPkg.version, 'Restored version must be a 1.x.y published version (AC-4)').toMatch(/^1\.\d+\.\d+/);

  // 3. grep -R "ApiMeta" .../node_modules/@moamen-ui/pointer-angular/ || echo ABSENT
  const installedModuleDir = join(angularDir, 'node_modules', '@moamen-ui', 'pointer-angular');
  let grepOutput = 'ABSENT';
  try {
    grepOutput = execFileSync('grep', ['-R', 'ApiMeta', installedModuleDir], {
      encoding: 'utf8',
      stdio: 'pipe',
    }).trim();
  } catch {
    grepOutput = 'ABSENT';
  }
  expect(grepOutput, 'ApiMeta symbol must be ABSENT from restored published client (AC-4)').toBe('ABSENT');

  // 4. git -C $DASHBOARD_DIR status --porcelain
  const status = execFileSync('git', ['-C', DASHBOARD_DIR, 'status', '--porcelain'], {
    encoding: 'utf8',
  }).trim();
  expect(status, 'git status in DASHBOARD_DIR must still be clean after npm ci restore').toBe('');

  const durationMs = Date.now() - start;
  record({
    id: 'R1-10-03',
    tier: 'nightly',
    layer: 'dashboard',
    role: '—',
    result: 'PASS',
    ms: durationMs,
    detail: `restoredVersion=${restoredPkg.version}, apiMeta=ABSENT, gitClean=true`,
  });
});
