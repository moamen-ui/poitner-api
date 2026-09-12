// Lazy readers for the files scripts/seed.mjs writes.
//
// Specs used to do this at module scope:
//
//   const credentials = JSON.parse(readFileSync(join(STATE_DIR, 'credentials.json'), 'utf8'));
//
// Playwright evaluates every spec file to DISCOVER tests, long before it runs any. So on a
// workspace that has not been seeded, that line throws during discovery and
// `npx playwright test --list` reports "Total: 0 tests in 0 files" — not an error, an empty suite.
// Anything reading that listing (CI, an IDE, a coverage check) concludes there are no tests.
//
// Reading lazily moves the failure to where it belongs: the test that actually needs the seed,
// with a message that says what to run.
import { readFileSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
export const STATE_DIR = join(here, '..', '..', 'state');

function load(name) {
  const path = join(STATE_DIR, name);
  if (!existsSync(path)) {
    throw new Error(
      `${path} is missing — the suite has not been seeded. Run: bash e2e/run-e2e.sh --pr (or node e2e/scripts/seed.mjs)`,
    );
  }
  try {
    return JSON.parse(readFileSync(path, 'utf8'));
  } catch (err) {
    throw new Error(`${path} exists but is not valid JSON — the seed did not finish: ${err.message}`);
  }
}

let credentialsCache;
let keysCache;

/** The seeded personas, keyed by role (wsAdmin, tester, client, …). */
export function credentials() {
  credentialsCache ??= load('credentials.json');
  return credentialsCache;
}

/** One minted API key per persona. */
export function keys() {
  keysCache ??= load('keys.json');
  return keysCache;
}

/** The expected-visibility fixture the probe asserts against. */
export function expected() {
  return load('expected.json');
}

/**
 * Signs in as the seeded quick-access client.
 *
 * A quick-access account is PASSWORDLESS (R2-05): there is no password to log in with, and
 * password login refuses the account outright. Signing in means redeeming the magic link, which is
 * exactly what a real invited client's browser does. Falls back to password login so a workspace
 * seeded before R2-05 still works.
 */
export async function loginClient(api) {
  const client = credentials().client;
  if (client.inviteToken) {
    const res = await api.post('/api/auth/login-with-invite', { token: client.inviteToken });
    if (res?.status !== 'ok' || !res.token) throw new Error('magic-link redemption failed for the seeded client');
    return { token: res.token, user: res.user };
  }
  return api.login(client.email, client.password);
}
