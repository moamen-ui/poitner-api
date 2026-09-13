import { test, expect } from '@playwright/test';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
import crypto from 'node:crypto';
import { raw } from '../scripts/lib/api.mjs';
import { USERS } from '../scripts/lib/constants.mjs';
import { record } from '../scripts/lib/report.mjs';
import { restartApi } from '../scripts/restart-api.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const e2eRoot = resolve(here, '..');
const repoRoot = resolve(e2eRoot, '..');
const STATE_DIR = join(e2eRoot, 'state');
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

// This scenario FORCE-RECREATES the api container to change its encryption key. Anything sharing
// the stack while it runs dies with ECONNRESET — which is exactly what happened when it ran inside
// the ordinary `api` phase and took six unrelated specs down with it. It runs only where the
// runner has isolated it.
// Gated on the PHASE flag only, never on TIER.
//
// Tier decides which phases run; the phase decides what is safe to run inside it. Conflating them
// put this scenario back inside the ordinary `api` phase for every nightly run, where it restarted
// the api container and ECONNRESET'd ten unrelated specs. The runner sets the flag below in the
// one phase that has this scenario to itself.
const isDestructiveRun = process.env.E2E_DESTRUCTIVE === '1';

test('R1-06-04 — reveal round-trip + encryption-key rotation', async () => {
  test.skip(!isDestructiveRun, 'restarts the api container — runs only in the isolated upgrade/nightly phase');
  // Three container restarts at ~40s each (the dev image rebuilds on start). The 30s default
  // cannot cover one, and the failure is worse than slow: the test dies mid-rotation holding the
  // rotated key in state/compose.restart-override.yml, so every LATER run starts against a key
  // that cannot decrypt the seeded keys and fails with "the encryption key changed" — a message
  // that points at the product rather than at a timeout three runs ago.
  test.setTimeout(600_000);
  const start = Date.now();
  const credentials = getCredentials();
  const deputyEmail = credentials?.deputy?.email || USERS.deputy.email;
  const deputyPassword = credentials?.deputy?.password || USERS.deputy.password;

  let restarted = false;

  try {
    // 1. Reveal + login round-trip as deputy; record baseline DB counts
    const login1 = await raw('POST', '/api/auth/login', {
      body: { email: deputyEmail, password: deputyPassword },
    });
    expect(login1.status, 'deputy password login must return 200').toBe(200);
    const token1 = login1.data?.token;
    expect(token1, 'deputy login must issue token').toBeTruthy();

    const reveal1 = await raw('GET', '/api/me/api-key', { token: token1 });
    expect(reveal1.status, 'deputy GET /api/me/api-key must return 200').toBe(200);
    const key1 = reveal1.data?.apiKey;
    expect(key1, 'key1 must be returned').toBeTruthy();

    const key1Login = await raw('POST', '/api/auth/login-with-key', {
      body: { apiKey: key1 },
    });
    expect(key1Login.status, 'login-with-key with key1 must return 200').toBe(200);
    expect(key1Login.data?.status).toBe('ok');

    const baselineTotalCount = parseInt(psql('SELECT count(*) FROM api_keys'), 10);
    const baselineActiveCount = parseInt(psql('SELECT count(*) FROM api_keys WHERE revoked_at IS NULL'), 10);

    // 2. Rotate Auth:ApiKeyEncryptionKey via restart-api.mjs
    const rotatedEncryptionKey = crypto.randomBytes(32).toString('base64');
    await restartApi({ env: { Auth__ApiKeyEncryptionKey: rotatedEncryptionKey } });
    restarted = true;

    // 3. Login with key1: hash lookup is unaffected by the rotation -> must still succeed
    const loginAfterRotate = await raw('POST', '/api/auth/login-with-key', {
      body: { apiKey: key1 },
    });
    expect(loginAfterRotate.status, 'login-with-key after rotation must return 200 via hash match').toBe(200);
    expect(loginAfterRotate.data?.status).toBe('ok');
    const tokenAfterRotate = loginAfterRotate.data?.token;
    expect(tokenAfterRotate, 'token must be issued on successful login').toBeTruthy();

    // 4. Reveal attempt with that token: decrypt failure returns 400 with isSuccess: false
    const revealAfterRotate = await raw('GET', '/api/me/api-key', { token: tokenAfterRotate });
    expect(revealAfterRotate.status, 'reveal under rotated encryption key must fail with 400').toBe(400);
    expect(revealAfterRotate.isSuccess, 'envelope isSuccess must be false on decrypt failure').toBe(false);

    // 5. Verify no row was minted or revoked: counts must be identical to baseline
    const postRotateTotalCount = parseInt(psql('SELECT count(*) FROM api_keys'), 10);
    const postRotateActiveCount = parseInt(psql('SELECT count(*) FROM api_keys WHERE revoked_at IS NULL'), 10);
    expect(postRotateTotalCount, 'total api_keys count must remain unchanged').toBe(baselineTotalCount);
    expect(postRotateActiveCount, 'active api_keys count must remain unchanged').toBe(baselineActiveCount);

    // 6. Restore: clean recreate with no override -> decryption works again with derived key
    await restartApi();
    restarted = false;

    const freshLogin = await raw('POST', '/api/auth/login', {
      body: { email: deputyEmail, password: deputyPassword },
    });
    expect(freshLogin.status, 'deputy login after clean restore must return 200').toBe(200);
    const freshToken = freshLogin.data?.token;

    const restoredReveal = await raw('GET', '/api/me/api-key', { token: freshToken });
    expect(restoredReveal.status, 'GET /api/me/api-key after restore must return 200').toBe(200);
    expect(restoredReveal.data?.apiKey, 'restored key must match original key1').toBe(key1);

    const durationMs = Date.now() - start;
    record({
      id: 'R1-06-04',
      tier: 'nightly',
      layer: 'api',
      role: 'deputy',
      result: 'PASS',
      ms: durationMs,
      detail: `baselineTotal=${baselineTotalCount}, baselineActive=${baselineActiveCount}, postRotateTotal=${postRotateTotalCount}, postRotateActive=${postRotateActiveCount}, keyRestored=true`,
    });
  } catch (err) {
    // Print container logs on failure as specified in contract
    try {
      const logs = execFileSync('docker', ['compose', 'logs', 'api', '--tail', '50'], {
        cwd: repoRoot,
        encoding: 'utf8',
      });
      console.error('API logs after failure:\n', logs);
    } catch {}
    throw err;
  } finally {
    // Contract requirement: always restore any created state in finally block
    if (restarted) {
      try {
        await restartApi();
      } catch (restoreErr) {
        console.error('Failed to restore API in finally block:', restoreErr);
      }
    }
  }
});
