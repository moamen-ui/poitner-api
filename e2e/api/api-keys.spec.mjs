import { test, expect } from '@playwright/test';
import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
import { raw } from '../scripts/lib/api.mjs';
import { USERS } from '../scripts/lib/constants.mjs';
import { record } from '../scripts/lib/report.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const e2eRoot = resolve(here, '..');
const repoRoot = resolve(e2eRoot, '..');
const STATE_DIR = join(e2eRoot, 'state');
const KEYS_PATH = join(STATE_DIR, 'keys.json');
const CREDENTIALS_PATH = join(STATE_DIR, 'credentials.json');

function psql(sql) {
  return execFileSync(
    'docker',
    ['compose', 'exec', '-T', 'db', 'psql', '-U', 'pointer', '-d', 'pointer', '-tAc', sql],
    { cwd: repoRoot, encoding: 'utf8' }
  ).trim();
}

function getCredentials() {
  if (existsSync(CREDENTIALS_PATH)) {
    try {
      return JSON.parse(readFileSync(CREDENTIALS_PATH, 'utf8'));
    } catch (err) {
      throw new Error(`${CREDENTIALS_PATH} exists but is not valid JSON — the seed did not finish: ${err.message}`);
    }
  }
  return {};
}

function getKeys() {
  if (existsSync(KEYS_PATH)) {
    try {
      return JSON.parse(readFileSync(KEYS_PATH, 'utf8'));
    } catch (err) {
      throw new Error(`${KEYS_PATH} exists but is not valid JSON — the seed did not finish: ${err.message}`);
    }
  }
  return {};
}

function updatePmKeyInKeysJson(newKey) {
  if (!existsSync(KEYS_PATH)) return;
  try {
    const keys = JSON.parse(readFileSync(KEYS_PATH, 'utf8'));
    if (keys.pm) {
      keys.pm.apiKey = newKey;
      writeFileSync(KEYS_PATH, JSON.stringify(keys, null, 2), 'utf8');
    }
  } catch (err) {
    console.warn('Failed to update pm key in keys.json:', err);
  }
}

test('R1-06-03 — regenerated-key-old-one-rejected', async () => {
  const start = Date.now();
  const credentials = getCredentials();
  const keys = getKeys();

  const pmEmail = credentials?.pm?.email || USERS.pm.email;
  const pmPassword = credentials?.pm?.password || USERS.pm.password;

  let oldKey = '';
  let newKey = '';

  try {
    // 1. Password login as PM and reveal old key
    const loginRes = await raw('POST', '/api/auth/login', {
      body: { email: pmEmail, password: pmPassword },
    });
    expect(loginRes.status, 'PM password login must return 200').toBe(200);
    expect(loginRes.data?.token, 'PM login must issue a token').toBeTruthy();
    const pmToken = loginRes.data.token;

    const revealOldRes = await raw('GET', '/api/me/api-key', { token: pmToken });
    expect(revealOldRes.status, 'GET /api/me/api-key for PM must return 200').toBe(200);
    oldKey = revealOldRes.data?.apiKey;
    expect(oldKey, 'oldKey must be present').toBeTruthy();

    if (keys?.pm?.apiKey) {
      expect(oldKey, 'oldKey should match keys.json.pm.apiKey').toBe(keys.pm.apiKey);
    }

    // 2. Regenerate key: old one revoked, new one minted
    const regenRes = await raw('POST', '/api/me/api-key/regenerate', { token: pmToken });
    expect(regenRes.status, 'POST /api/me/api-key/regenerate must return 200').toBe(200);
    newKey = regenRes.data?.apiKey;
    expect(newKey, 'newKey must match ptr_ pattern').toMatch(/^ptr_[0-9a-f]{40}$/);
    expect(newKey, 'newKey must differ from oldKey').not.toBe(oldKey);
    expect(regenRes.data?.prefix, 'prefix in response must match first 12 chars of newKey').toBe(newKey.slice(0, 12));

    // 3. Old key login attempt: must fail with 400 (InvalidApiKey, isSuccess: false)
    const oldLoginRes = await raw('POST', '/api/auth/login-with-key', {
      body: { apiKey: oldKey },
    });
    expect(oldLoginRes.status, 'login-with-key with revoked oldKey must return 400').toBe(400);
    expect(oldLoginRes.isSuccess, 'envelope isSuccess must be false for revoked key').toBe(false);

    // 4. New key login: must succeed with 200 ok
    const newLoginRes = await raw('POST', '/api/auth/login-with-key', {
      body: { apiKey: newKey },
    });
    expect(newLoginRes.status, 'login-with-key with newKey must return 200').toBe(200);
    expect(newLoginRes.data?.status, 'data.status must be ok').toBe('ok');
    const newToken = newLoginRes.data?.token;
    expect(newToken, 'new key login must issue token').toBeTruthy();

    // Reveal using the new key's token
    const revealNewRes = await raw('GET', '/api/me/api-key', { token: newToken });
    expect(revealNewRes.status, 'GET /api/me/api-key with new token must return 200').toBe(200);
    expect(revealNewRes.data?.apiKey, 'reveal must return newKey').toBe(newKey);
    expect(revealNewRes.data?.lastUsedAt, 'lastUsedAt must be populated after login').toBeTruthy();

    // 5. DB inspection: exactly one active row for PM (1 for new prefix, 0 for old prefix)
    const newPrefix = newKey.slice(0, 12);
    const oldPrefix = oldKey.slice(0, 12);

    const activeNewCountStr = psql(
      `SELECT count(*) FROM api_keys WHERE prefix = '${newPrefix}' AND revoked_at IS NULL`
    );
    const activeNewCount = parseInt(activeNewCountStr, 10);
    expect(activeNewCount, 'exactly 1 active row must exist for new prefix').toBe(1);

    const activeOldCountStr = psql(
      `SELECT count(*) FROM api_keys WHERE prefix = '${oldPrefix}' AND revoked_at IS NULL`
    );
    const activeOldCount = parseInt(activeOldCountStr, 10);
    expect(activeOldCount, '0 active rows must exist for revoked old prefix').toBe(0);

    // 6. Rewrite state/keys.json -> pm.apiKey = newKey so subsequent phases remain functional
    updatePmKeyInKeysJson(newKey);

    const durationMs = Date.now() - start;
    record({
      id: 'R1-06-03',
      tier: 'nightly',
      layer: 'api',
      role: 'pm',
      result: 'PASS',
      ms: durationMs,
      detail: `newKeyPrefix=${newPrefix}, oldKeyPrefix=${oldPrefix}, activeNewCount=${activeNewCount}, activeOldCount=${activeOldCount}`,
    });
  } finally {
    // If newKey was generated but something failed before step 6, ensure keys.json is updated to match DB
    if (newKey) {
      updatePmKeyInKeysJson(newKey);
    }
  }
});
