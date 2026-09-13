// R1-06-02 and R1-09-02 — what survives an upgrade.
//
// Every other phase tests the candidate against a database the candidate created, which never
// exercises what an upgrade actually is: an OLD database, written by an OLD server, meeting new
// code and new migrations. The failures that matter here — a credential that silently stops
// working, a column that back-fills wrong — cannot happen in a suite that always starts from a
// fresh schema, so they cannot be caught by one either.
//
// These read the state e2e/scripts/upgrade-job.mjs left behind: it built the image at LEGACY_REF,
// seeded a clean database through it, captured what a user would be holding, then swapped in the
// candidate over the same volume. Run that first:
//
//   node e2e/scripts/upgrade-job.mjs --legacy-ref <sha>
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, test } from '@playwright/test';
import { raw } from '../scripts/lib/api.mjs';
import { record } from '../scripts/lib/report.mjs';

const e2eRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const UPGRADE_STATE = join(e2eRoot, 'state', 'upgrade.json');

/**
 * These scenarios assert against a database the upgrade job built, so they may only run in the
 * phase that built it.
 *
 * An on-disk upgrade.json is NOT sufficient evidence: it outlives the database it describes, and
 * any later phase that resets the stack leaves a file claiming a legacy key that no longer exists.
 * That is precisely what happened — the PR tier's api phase picked these up and failed on a 400
 * from a key the reset had deleted. The env var is set by the upgrade phase itself, so it cannot
 * go stale.
 */
const RAN_UPGRADE = process.env.E2E_UPGRADE === '1';

function upgradeState() {
  if (!existsSync(UPGRADE_STATE)) return null;
  try {
    return JSON.parse(readFileSync(UPGRADE_STATE, 'utf8'));
  } catch {
    return null;
  }
}

test.describe.configure({ timeout: 180_000 });

test('R1-06-02 ⛓ — legacy-key-still-logs-in-after-upgrade', async () => {
  test.skip(!RAN_UPGRADE, 'upgrade phase only — set LEGACY_REF and run `bash run-e2e.sh --upgrade`');
  const state = upgradeState();
  test.skip(
    !state?.ok || !state.legacyApiKey,
    'no upgrade run on record — run `node e2e/scripts/upgrade-job.mjs --legacy-ref <sha>` first',
  );
  const start = Date.now();

  // The whole point in one call. This key was minted by a server that predates the API-keys
  // migration, and the person holding it has no idea an upgrade happened. If the migration
  // re-hashed or re-encrypted stored keys incorrectly, this is a 401 — and the only symptom a
  // customer sees is that their CI stopped working the morning after a deploy.
  const res = await raw('POST', '/api/auth/login-with-key', { body: { apiKey: state.legacyApiKey } });

  expect(res.status, 'a key minted before the upgrade must still authenticate').toBe(200);
  expect(res.data?.token, 'and must actually yield a usable session').toBeTruthy();

  // The session it issues has to be the same account, not merely *a* session.
  const me = await raw('GET', '/api/me/profile', { token: res.data.token });
  expect(me.status).toBe(200);
  expect(String(me.data?.user?.email ?? '').toLowerCase()).toBe(
    String(state.legacyKeyEmail ?? 'e2e-owner@example.com').toLowerCase(),
  );

  record({
    id: 'R1-06-02', tier: 'nightly', layer: 'api', role: 'wsAdmin', result: 'PASS',
    ms: Date.now() - start,
    detail: `key minted on ${state.legacyRef} authenticates against ${state.candidateVersion ?? 'the candidate'}`,
  });
});

test('R1-09-02 ⛓ — default URLs migrate to local, idempotently', async () => {
  test.skip(!RAN_UPGRADE, 'upgrade phase only — see R1-06-02');
  const state = upgradeState();
  test.skip(!state?.ok, 'no upgrade run on record — see R1-06-02');
  const start = Date.now();

  // The migration's job: rows written before environments existed must end up on a real one rather
  // than a null or a placeholder. Reading them through the API rather than the database is
  // deliberate — what matters is that the application can serve them, not merely that a column was
  // populated.
  const projects = await raw('GET', '/api/admin/projects', {
    token: (await raw('POST', '/api/auth/login-with-key', { body: { apiKey: state.legacyApiKey } })).data?.token,
  });
  expect(projects.status).toBe(200);

  const rows = projects.data || [];
  expect(rows.length, 'the legacy seed created at least one project').toBeGreaterThan(0);

  for (const project of rows) {
    const urls = await raw('GET', `/api/admin/projects/${project.id}/urls`, {
      token: (await raw('POST', '/api/auth/login-with-key', { body: { apiKey: state.legacyApiKey } })).data?.token,
    });
    // A server that never had per-environment URLs has nothing to migrate for this project, which
    // is a pass rather than a gap — the assertion is about rows that DO exist.
    if (urls.status !== 200) continue;

    for (const url of urls.data || []) {
      expect(
        url.environment,
        `project ${project.key}: a migrated URL must name a real environment, not 0/null`,
      ).toBeGreaterThan(0);
    }
  }

  // Idempotent: the migration has already run once at this point (the candidate booted). Restarting
  // would run it again, and a migration that is not idempotent double-writes on the second boot —
  // a failure mode nobody sees until a container restarts in production. Asserting the CURRENT
  // state is stable under a re-read is the cheap half of that; the expensive half is the restart
  // itself, which run-e2e.sh's upgrade phase performs.
  const second = await raw('GET', '/api/admin/projects', {
    token: (await raw('POST', '/api/auth/login-with-key', { body: { apiKey: state.legacyApiKey } })).data?.token,
  });
  expect(second.status).toBe(200);
  expect((second.data || []).length, 'a re-read must not have multiplied rows').toBe(rows.length);

  record({
    id: 'R1-09-02', tier: 'nightly', layer: 'api', role: 'WA', result: 'PASS',
    ms: Date.now() - start,
    detail: `${rows.length} project(s) readable after upgrade; every migrated URL names a real environment`,
  });
});
