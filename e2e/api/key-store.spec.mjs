import { test, expect } from '@playwright/test';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
import crypto from 'node:crypto';
import { record } from '../scripts/lib/report.mjs';
import { getComposeLogs } from '../scripts/lib/docker.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const e2eRoot = resolve(here, '..');
const repoRoot = resolve(e2eRoot, '..');
const STATE_DIR = join(e2eRoot, 'state');

function psql(sql) {
  return execFileSync(
    'docker',
    ['compose', 'exec', '-T', 'db', 'psql', '-U', 'pointer', '-d', 'pointer', '-tAc', sql],
    { cwd: repoRoot, encoding: 'utf8' }
  ).trim();
}

// Since R5-58 the API logs structured JSON, so an unbounded `docker compose logs api` can exceed
// Node's default 1 MB maxBuffer (ENOBUFS). getComposeLogs bounds the read (--tail) and raises
// maxBuffer well past anything that bounded read can produce.
function getApiLogs() {
  return getComposeLogs(repoRoot, 'api');
}

function getKeys() {
  const keysPath = join(STATE_DIR, 'keys.json');
  if (existsSync(keysPath)) {
    return JSON.parse(readFileSync(keysPath, 'utf8'));
  }
  return {};
}

test('R1-06-01 — DB is hash-only + indexes + boot warning', async () => {
  const start = Date.now();

  // 1. Boot warning check: without Auth:ApiKeyEncryptionKey, derive-from-JWT emits a warning
  const logs = getApiLogs();
  // The real message, from ApiKeyProtector.cs, is a structured log:
  //   "{Path} is not set; deriving the API-key encryption key from {Fallback}. Set a dedicated
  //    32-byte base64 key in production ..."
  // The previously-asserted "set Auth:ApiKeyEncryptionKey for production" is not a string the
  // product ever emits. Match a distinctive fragment of the actual text instead.
  const warningPattern = /deriving the API-key encryption key from/g;
  const matchCount = (logs.match(warningPattern) || []).length;
  expect(matchCount, 'boot warning "set Auth:ApiKeyEncryptionKey for production" must appear in api logs').toBeGreaterThanOrEqual(1);

  // 2. DB-07 (2026-09-22, docs/db/execution/DB-07-drop-users-api-key.md) dropped the legacy
  // plaintext users.api_key column outright — it is no longer merely nulled by a boot backfill,
  // it does not exist. API/Startup/ApiKeyBackfillService.cs (the backfill this used to check) was
  // deleted in the same change, so "SELECT ... FROM users WHERE api_key ..." is itself now a
  // column-does-not-exist error (confirmed against a live DB while writing this fix). The distinct
  // "backfill ran" boot warning this step used to also imply is gone with it; step 1's warning is
  // a different, still-current message (ApiKeyProtector.cs deriving-key warning), left as is.
  const legacyColumnCountStr = psql(
    "SELECT count(*) FROM information_schema.columns WHERE table_name = 'users' AND column_name = 'api_key'",
  );
  const legacyColumnCount = parseInt(legacyColumnCountStr, 10);
  expect(legacyColumnCount, 'users.api_key column must not exist (DB-07)').toBe(0);

  // 3. Row count check: one row per seeded persona key (at least 8 seeded accounts)
  const apiKeysCountStr = psql('SELECT count(*) FROM api_keys');
  const apiKeysCount = parseInt(apiKeysCountStr, 10);
  expect(apiKeysCount, 'api_keys table must contain at least 8 seeded persona keys').toBeGreaterThanOrEqual(8);

  // 4. Hash + scopes + revoked_at check for wsAdmin key
  const keys = getKeys();
  const rawKey = keys?.wsAdmin?.apiKey;
  expect(rawKey, 'keys.json must contain wsAdmin.apiKey').toBeTruthy();

  const expectedPrefix = rawKey.slice(0, 12);
  const expectedHash = crypto.createHash('sha256').update(rawKey).digest('hex');

  const row = psql(
    `SELECT prefix, hash, scopes, revoked_at FROM api_keys WHERE prefix = '${expectedPrefix}'`
  );
  expect(row, `api_keys row for prefix ${expectedPrefix} must exist`).toBeTruthy();

  const [prefix, hash, scopes, revokedAt] = row.split('|');
  expect(prefix, 'prefix in DB must match first 12 characters').toBe(expectedPrefix);
  expect(hash.toLowerCase(), 'hash in DB must match sha256hex of full raw key').toBe(expectedHash.toLowerCase());
  expect(parseInt(scopes, 10), 'scopes must be 7 (Full)').toBe(7);
  expect(revokedAt === '' || revokedAt === null || revokedAt === undefined, 'revoked_at must be NULL').toBe(true);

  // 5. Plaintext scan: verify no full key survived anywhere in hash or encrypted columns
  const plaintextScanStr = psql(
    "SELECT count(*) FROM api_keys WHERE hash ~ 'ptr_' OR encrypted ~ 'ptr_[0-9a-f]{40}'"
  );
  const plaintextScanCount = parseInt(plaintextScanStr, 10);
  expect(plaintextScanCount, 'no full key plaintext must appear in hash or encrypted columns').toBe(0);

  // 6. Indexes check: unique hash index and partial unique active-per-user index
  const indexes = psql("SELECT indexdef FROM pg_indexes WHERE tablename = 'api_keys'");
  const hasUniqueHashIndex = /UNIQUE.*\(hash\)/i.test(indexes);
  const hasPartialUniqueActivePerUser = /UNIQUE.*\(user_id\).*WHERE.*revoked_at IS NULL/i.test(indexes);

  expect(hasUniqueHashIndex, 'must have UNIQUE index on (hash)').toBe(true);
  expect(hasPartialUniqueActivePerUser, 'must have partial UNIQUE index on (user_id) WHERE revoked_at IS NULL').toBe(true);

  const durationMs = Date.now() - start;
  const detail = `logsWarningCount=${matchCount}, legacyUserApiKeyColumnCount=${legacyColumnCount}, apiKeysCount=${apiKeysCount}, row=[${row}], plaintextScanCount=${plaintextScanCount}, uniqueHash=${hasUniqueHashIndex}, partialUniqueUser=${hasPartialUniqueActivePerUser}`;

  record({
    id: 'R1-06-01',
    tier: 'nightly',
    layer: 'api',
    role: '—',
    result: 'PASS',
    ms: durationMs,
    detail,
  });
});
