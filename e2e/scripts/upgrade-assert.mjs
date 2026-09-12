import { test, expect } from '@playwright/test';
import { readFileSync, existsSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
import { raw, login, postRaw, putRaw } from './lib/api.mjs';
import { TENANT_OWNER } from './lib/constants.mjs';
import { record } from './lib/report.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const e2eRoot = resolve(here, '..');
const repoRoot = resolve(e2eRoot, '..');
const STATE_DIR = join(e2eRoot, 'state');
const KEYS_PATH = join(STATE_DIR, 'keys.json');

function psql(sql) {
  return execFileSync(
    'docker',
    ['compose', 'exec', '-T', 'db', 'psql', '-U', 'pointer', '-d', 'pointer', '-tAc', sql],
    { cwd: repoRoot, encoding: 'utf8' }
  ).trim();
}

function getLegacyKey() {
  if (process.env.LEGACY_API_KEY) {
    return process.env.LEGACY_API_KEY.trim();
  }
  if (existsSync(KEYS_PATH)) {
    try {
      const keys = JSON.parse(readFileSync(KEYS_PATH, 'utf8'));
      if (keys.wsAdmin?.apiKey) return keys.wsAdmin.apiKey;
    } catch {}
  }
  return '';
}

export async function assertUpgrade({ rawLegacyKey: overrideKey, psqlRunner = psql } = {}) {
  const start = Date.now();
  const rawLegacyKey = overrideKey || getLegacyKey();
  expect(rawLegacyKey, 'rawLegacyKey must be available from state/keys.json or LEGACY_API_KEY env').toBeTruthy();

  // 7. Login with legacy key on candidate image: AC-3
  const loginRes = await raw('POST', '/api/auth/login-with-key', {
    body: { apiKey: rawLegacyKey },
  });
  expect(loginRes.status, 'login-with-key with legacy key must return 200').toBe(200);
  expect(loginRes.data?.status, 'data.status must be ok').toBe('ok');
  expect(loginRes.data?.user?.email, 'data.user.email must be e2e-owner@example.com').toBe('e2e-owner@example.com');
  const token = loginRes.data?.token;
  expect(token, 'candidate login must issue a token').toBeTruthy();

  // 8. Reveal key with that token: AC-4 (backfill encrypted the same raw key)
  const revealRes = await raw('GET', '/api/me/api-key', { token });
  expect(revealRes.status, 'GET /api/me/api-key must return 200').toBe(200);
  expect(revealRes.data?.apiKey, 'revealed key must match pre-upgrade rawLegacyKey').toBe(rawLegacyKey);

  // 9. DB assertions: legacy users.api_key column nulled, active row in api_keys
  const legacyUserCountStr = psqlRunner('SELECT count(*) FROM users WHERE api_key IS NOT NULL');
  const legacyUserCount = parseInt(legacyUserCountStr, 10);
  expect(legacyUserCount, 'users.api_key must be all NULL').toBe(0);

  const prefix = rawLegacyKey.slice(0, 12);
  const activeKeyCountStr = psqlRunner(
    `SELECT count(*) FROM api_keys WHERE prefix = '${prefix}' AND revoked_at IS NULL`
  );
  const activeKeyCount = parseInt(activeKeyCountStr, 10);
  expect(activeKeyCount, `api_keys must contain exactly 1 active row for prefix ${prefix}`).toBe(1);

  const durationMs = Date.now() - start;
  record({
    id: 'R1-06-02',
    tier: 'nightly',
    layer: 'api',
    role: 'wsAdmin',
    result: 'PASS',
    ms: durationMs,
    detail: `userEmail=${loginRes.data.user.email}, legacyUsersCount=${legacyUserCount}, activeKeysCount=${activeKeyCount}`,
  });

  return { token, apiKey: revealRes.data?.apiKey };
}

export async function setupLegacyMigration({ token: overrideToken, runId = Math.random().toString(36).substring(2, 7) } = {}) {
  let token = overrideToken;
  if (!token) {
    let wsAdminCreds = TENANT_OWNER;
    if (existsSync(join(STATE_DIR, 'credentials.json'))) {
      try {
        const creds = JSON.parse(readFileSync(join(STATE_DIR, 'credentials.json'), 'utf8'));
        if (creds.wsAdmin) wsAdminCreds = creds.wsAdmin;
      } catch {}
    }
    const loggedIn = await login(wsAdminCreds.email, wsAdminCreds.password);
    token = loggedIn.token;
  }

  // 2. WA POST /api/admin/projects { key: 'e2e-r109-mig-a-<runId>', name: 'Mig A', appUrl: 'https://mig-a.test' }
  const migAKey = `e2e-r109-mig-a-${runId}`;
  const projARes = await postRaw('/api/admin/projects', {
    key: migAKey,
    name: 'Mig A',
    appUrl: 'https://mig-a.test',
  }, { token });
  expect(projARes.status).toBe(200);
  const projectA = projARes.data;

  // 4. WA POST /api/admin/environments { name: 'default' } → tenant-owned default (id tOwn);
  // PUT /api/admin/projects/{a.id}/app-urls/{tOwn} with https://tenant-default.test
  const envRes = await postRaw('/api/admin/environments', { name: 'default' }, { token });
  expect(envRes.status).toBe(200);
  const tOwn = envRes.data;

  const putUrlRes = await putRaw(`/api/admin/projects/${projectA.id}/app-urls/${tOwn.id}`, {
    url: 'https://tenant-default.test',
  }, { token });
  expect(putUrlRes.status).toBe(200);

  // 5. Record GET /api/admin/projects/{a.id}/app-urls
  const preUrlsRes = await raw('GET', `/api/admin/projects/${projectA.id}/app-urls`, { token });
  expect(preUrlsRes.status).toBe(200);

  const upgradeDir = join(STATE_DIR, 'upgrade');
  if (!existsSync(upgradeDir)) mkdirSync(upgradeDir, { recursive: true });
  const migState = {
    runId,
    projectAId: projectA.id,
    projectAKey: migAKey,
    tOwnId: tOwn.id,
    preUrls: preUrlsRes.data,
  };
  writeFileSync(join(upgradeDir, 'migration-r109.json'), JSON.stringify(migState, null, 2), 'utf8');
  return migState;
}

export async function assertMigrationUpgrade({ wsAdminToken: overrideToken, psqlRunner = psql } = {}) {
  const start = Date.now();
  let token = overrideToken;
  if (!token) {
    let wsAdminCreds = TENANT_OWNER;
    if (existsSync(join(STATE_DIR, 'credentials.json'))) {
      try {
        const creds = JSON.parse(readFileSync(join(STATE_DIR, 'credentials.json'), 'utf8'));
        if (creds.wsAdmin) wsAdminCreds = creds.wsAdmin;
      } catch {}
    }
    const loggedIn = await login(wsAdminCreds.email, wsAdminCreds.password);
    token = loggedIn.token;
  }

  // Check state from migration-r109.json if legacy half ran
  const migStatePath = join(STATE_DIR, 'upgrade', 'migration-r109.json');
  let migState = null;
  if (existsSync(migStatePath)) {
    try {
      migState = JSON.parse(readFileSync(migStatePath, 'utf8'));
    } catch {}
  }

  let projectAId = migState?.projectAId;
  if (!projectAId) {
    const listRes = await raw('GET', '/api/admin/projects', { token });
    const found = listRes.ok && Array.isArray(listRes.data)
      ? listRes.data.find((p) => p.key.startsWith('e2e-r109-mig-a-'))
      : null;
    if (found) projectAId = found.id;
  }

  // 7. WA GET /api/admin/projects/{a.id}/app-urls (if projectA was created)
  let appUrlsPayload1 = null;
  if (projectAId) {
    const urlsRes = await raw('GET', `/api/admin/projects/${projectAId}/app-urls`, { token });
    expect(urlsRes.status, 'GET /api/admin/projects/{a.id}/app-urls must return 200').toBe(200);
    appUrlsPayload1 = urlsRes.data;

    // Row with https://mig-a.test is now on environment named 'local', no row on global 'default'
    const migARow = appUrlsPayload1.find((u) => u.url === 'https://mig-a.test');
    expect(migARow, 'Row for https://mig-a.test must exist').toBeTruthy();
    expect(migARow.environmentName, 'https://mig-a.test must have migrated to local').toBe('local');

    // No row on global default
    const globalDefaultUrl = appUrlsPayload1.find((u) => u.environmentName === 'default' && u.isGlobal);
    expect(globalDefaultUrl, 'No URL row should exist on a global default environment').toBeFalsy();

    // Tenant-owned default row is still present and unchanged
    const tenantDefaultRow = appUrlsPayload1.find((u) => u.url === 'https://tenant-default.test');
    expect(tenantDefaultRow, 'Tenant-owned default row must be present and unchanged').toBeTruthy();
    expect(tenantDefaultRow.environmentName).toBe('default');
  }

  // 8. WA GET /api/admin/environments
  const envsRes = await raw('GET', '/api/admin/environments', { token });
  expect(envsRes.status, 'GET /api/admin/environments must return 200').toBe(200);
  const envsPayload1 = envsRes.data;

  // Global default: isEnabled: false, isRetired: true (if it exists)
  const globalDefault = envsPayload1.find((e) => e.isGlobal && e.name === 'default');
  if (globalDefault) {
    expect(globalDefault.isEnabled, 'Global default must be disabled').toBe(false);
    expect(globalDefault.isRetired, 'Global default must be retired').toBe(true);
  }

  // Tenant-owned default (if created): isEnabled: true, isRetired: false (AC-4)
  const tenantDefault = envsPayload1.find((e) => !e.isGlobal && e.name === 'default');
  if (tenantDefault) {
    expect(tenantDefault.isEnabled, 'Tenant-owned default must stay enabled').toBe(true);
    expect(tenantDefault.isRetired, 'Tenant-owned default must not be retired').toBe(false);
  }

  // 9. Idempotency check: repeat 7-8 and verify byte-identical data
  if (projectAId) {
    const urlsResRepeat = await raw('GET', `/api/admin/projects/${projectAId}/app-urls`, { token });
    expect(urlsResRepeat.status).toBe(200);
    expect(JSON.stringify(urlsResRepeat.data)).toBe(JSON.stringify(appUrlsPayload1));
  }
  const envsResRepeat = await raw('GET', '/api/admin/environments', { token });
  expect(envsResRepeat.status).toBe(200);
  expect(JSON.stringify(envsResRepeat.data)).toBe(JSON.stringify(envsPayload1));

  const durationMs = Date.now() - start;
  record({
    id: 'R1-09-02',
    tier: 'nightly',
    layer: 'api',
    role: 'WA',
    result: 'PASS',
    ms: durationMs,
    detail: `projectAId=${projectAId || 'none'}, globalDefaultRetired=${globalDefault ? globalDefault.isRetired : 'absent'}`,
  });

  return { projectAId, appUrls: appUrlsPayload1, environments: envsPayload1 };
}

test('R1-06-02 — legacy-key-still-logs-in-after-upgrade', async () => {
  await assertUpgrade();
});

test('R1-09-02 ⛓ — default URLs migrate to local, idempotently', async () => {
  await assertMigrationUpgrade();
});

// Direct CLI invocation: node e2e/scripts/upgrade-assert.mjs [rawKey|--migration|--legacy-setup]
const isMain = process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url);
if (isMain) {
  const arg = process.argv[2];
  if (arg === '--legacy-setup' || arg === 'legacy-setup') {
    setupLegacyMigration()
      .then((state) => {
        console.log('==> R1-09-02 legacy migration setup complete:', state);
      })
      .catch((err) => {
        console.error('==> R1-09-02 legacy migration setup failed:', err);
        process.exit(1);
      });
  } else if (arg === '--migration' || arg === 'migration') {
    assertMigrationUpgrade()
      .then(() => {
        console.log('==> R1-09-02 migration upgrade assertion passed successfully.');
      })
      .catch((err) => {
        console.error('==> R1-09-02 migration upgrade assertion failed:', err);
        process.exit(1);
      });
  } else {
    assertUpgrade({ rawLegacyKey: arg })
      .then(() => assertMigrationUpgrade())
      .then(() => {
        console.log('==> Upgrade assertions (R1-06-02 and R1-09-02) passed successfully.');
      })
      .catch((err) => {
        console.error('==> Upgrade assertion failed:', err);
        process.exit(1);
      });
  }
}
