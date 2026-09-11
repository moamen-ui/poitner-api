import { test, expect } from '@playwright/test';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
import { raw } from './lib/api.mjs';
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

test('R1-06-02 — legacy-key-still-logs-in-after-upgrade', async () => {
  await assertUpgrade();
});

// Direct CLI invocation: node e2e/scripts/upgrade-assert.mjs [rawKey]
const isMain = process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url);
if (isMain) {
  const cliKey = process.argv[2];
  assertUpgrade({ rawLegacyKey: cliKey })
    .then(() => {
      console.log('==> R1-06-02 upgrade assertion passed successfully.');
    })
    .catch((err) => {
      console.error('==> R1-06-02 upgrade assertion failed:', err);
      process.exit(1);
    });
}
