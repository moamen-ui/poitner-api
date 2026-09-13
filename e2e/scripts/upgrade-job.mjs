#!/usr/bin/env node
// The two-image upgrade rehearsal.
//
// Every other phase tests the candidate against a database the candidate created. That never
// exercises the thing an upgrade actually is: an OLD database, written by an OLD server, meeting
// new code and new migrations. The failures that matters here — a credential that stops working, a
// column that back-fills wrong — are invisible to a suite that always starts from a fresh schema.
//
// So: build the legacy ref, seed it with the CANDIDATE's seed (reduced, since a legacy API does
// not know today's endpoints), capture what a user would still be holding afterwards, then swap in
// the candidate image and check those same things still work.
//
//   node e2e/scripts/upgrade-job.mjs --legacy-ref <sha> [--out state/upgrade.json]
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const e2eRoot = resolve(here, '..');
const repoRoot = resolve(e2eRoot, '..');

function arg(name, fallback) {
  const i = process.argv.indexOf(`--${name}`);
  return i !== -1 && process.argv[i + 1] ? process.argv[i + 1] : fallback;
}

const legacyRef = arg('legacy-ref', process.env.LEGACY_REF || '');
const outPath = resolve(arg('out', join(e2eRoot, 'state', 'upgrade.json')));
const BASE = process.env.E2E_BASE_URL || 'http://localhost:8090';

if (!legacyRef) {
  console.error('Usage: node e2e/scripts/upgrade-job.mjs --legacy-ref <sha>  (or set LEGACY_REF)');
  process.exit(2);
}

const run = (cmd, args, opts = {}) =>
  execFileSync(cmd, args, { cwd: repoRoot, encoding: 'utf8', stdio: 'pipe', ...opts });

async function waitForApi(label, timeoutMs = 180_000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const res = await fetch(`${BASE}/api/meta`);
      if (res.ok) return await res.json().catch(() => null);
    } catch {
      /* not up yet */
    }
    // Some legacy refs predate /api/meta entirely; fall back to any answer at all.
    try {
      const res = await fetch(`${BASE}/swagger/v1/swagger.json`);
      if (res.ok) return null;
    } catch {
      /* still not up */
    }
    await new Promise((r) => setTimeout(r, 2000));
  }
  throw new Error(`${label} never became reachable at ${BASE}`);
}

const result = {
  legacyRef,
  seedMode: 'minimal',
  steps: [],
};
const step = (name, detail) => {
  result.steps.push({ name, detail });
  console.log(`  ${name}: ${detail}`);
};

try {
  // 1. A worktree at the legacy ref, and an image from it.
  const worktree = resolve(repoRoot, '..', 'pointer-legacy-ref');
  try {
    run('git', ['worktree', 'add', worktree, '--detach', legacyRef]);
  } catch {
    // Already checked out from a previous run — point it at the ref we were asked for rather than
    // silently testing whatever it happened to contain.
    run('git', ['-C', worktree, 'checkout', '--detach', legacyRef]);
  }
  const actualRef = run('git', ['-C', worktree, 'rev-parse', '--short', 'HEAD']).trim();
  step('legacy-worktree', `${worktree} at ${actualRef}`);

  run('docker', ['build', '-t', 'pointer-api:legacy', '--target', 'final', '.'], { cwd: worktree });
  step('legacy-image', 'pointer-api:legacy built');

  // 2. Start the legacy image on a CLEAN database. A leftover schema from the candidate would
  //    defeat the whole exercise — the point is a database only the old code has ever written.
  run('docker', ['compose', 'down', '-v'], { stdio: 'pipe' });
  run('docker', ['compose', 'up', '-d', 'db', 'mailpit']);

  // Wait for Postgres to ACCEPT CONNECTIONS before starting the API. `depends_on` only waits for
  // the container to start, and the published image runs migrations immediately on boot — against
  // a database still coming up it throws and the container exits, which surfaces as "the API never
  // became reachable" with the real cause buried in container logs.
  const dbDeadline = Date.now() + 120_000;
  let dbReady = false;
  while (Date.now() < dbDeadline) {
    const probe = spawnSync('docker', ['compose', 'exec', '-T', 'db', 'pg_isready', '-U', 'pointer'], {
      cwd: repoRoot,
      encoding: 'utf8',
    });
    if (probe.status === 0) {
      dbReady = true;
      break;
    }
    await new Promise((r) => setTimeout(r, 2000));
  }
  if (!dbReady) throw new Error('postgres never became ready');
  step('db-ready', 'postgres accepting connections on a fresh volume');

  run('docker', [
    'compose', '-f', 'docker-compose.yaml', '-f', 'e2e/compose.legacy.yaml',
    'up', '-d', 'api-legacy',
  ]);
  await waitForApi('legacy api');
  step('legacy-up', 'legacy API serving on a fresh database');

  // 3. The CANDIDATE's seed, reduced. Reduced because the legacy API does not know today's
  //    endpoints; the candidate's because the upgrade being rehearsed is of this checkout.
  run('node', [join('e2e', 'scripts', 'seed.mjs'), '--minimal'], { stdio: 'inherit' });
  step('legacy-seed', 'minimal seed written by the candidate against the legacy API');

  // 4. Capture what a user is holding when the upgrade happens.
  const keys = JSON.parse(run('cat', [join(e2eRoot, 'state', 'keys.json')]));
  result.legacyApiKey = keys.wsAdmin?.apiKey ?? null;
  result.legacyKeyEmail = keys.wsAdmin?.email ?? null;
  // `prefix` is absent on a legacy response — it arrived with the very migration this job
  // rehearses. Recorded as such rather than reported as a missing key, which is a different and
  // much worse thing.
  result.legacyKeyPrefix = keys.wsAdmin?.prefix ?? null;
  if (!result.legacyApiKey) throw new Error('the legacy seed produced no API key to carry across');
  step(
    'capture',
    `legacy key captured for ${result.legacyKeyEmail}` +
      (result.legacyKeyPrefix ? ` (prefix ${result.legacyKeyPrefix})` : ' (legacy response had no prefix field)'),
  );

  // 5. Swap in the candidate over the SAME database volume. Migrations run on start. The legacy
  //    service is stopped first because both publish :8090.
  run('docker', [
    'compose', '-f', 'docker-compose.yaml', '-f', 'e2e/compose.legacy.yaml', 'stop', 'api-legacy',
  ]);
  run('docker', ['compose', 'up', '-d', '--build', 'api']);
  const meta = await waitForApi('candidate api');
  result.candidateVersion = meta?.data?.version ?? meta?.version ?? null;
  step('upgrade', `candidate API serving (${result.candidateVersion ?? 'version unknown'})`);

  result.ok = true;
} catch (err) {
  result.ok = false;
  result.error = String(err?.message ?? err);
  console.error(`upgrade-job failed: ${result.error}`);
} finally {
  mkdirSync(dirname(outPath), { recursive: true });
  writeFileSync(outPath, JSON.stringify(result, null, 2) + '\n', 'utf8');
  console.log(`==> ${outPath}`);
}

process.exit(result.ok ? 0 : 1);
