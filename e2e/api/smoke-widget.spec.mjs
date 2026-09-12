// R3-03-05 — the post-deploy smoke script.
//
// scripts/smoke-widget.sh is meant to be run against production immediately after a deploy, by a
// person or a pipeline step, with nothing but bash and curl. Its value is entirely in whether it
// FAILS when the deploy is broken — a smoke check that cannot go red is a ritual, not a check.
// So this spec asserts both directions: every check passes against the live stack, and the script
// exits non-zero naming the first failure when pointed at a dead server.
import { execFile } from 'node:child_process';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import { expect, test } from '@playwright/test';
import { BASE_URL } from '../scripts/lib/api.mjs';
import { record } from '../scripts/lib/report.mjs';

const execFileAsync = promisify(execFile);
const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const SCRIPT = join('e2e', 'scripts', 'smoke-widget.sh');

/** Runs the script and returns its exit code plus combined output, without throwing. */
async function runSmoke(server) {
  try {
    const { stdout, stderr } = await execFileAsync('bash', [SCRIPT, server], {
      cwd: repoRoot,
      encoding: 'utf8',
      timeout: 120_000,
    });
    return { code: 0, out: `${stdout}${stderr}` };
  } catch (err) {
    return { code: err.code ?? 1, out: `${err.stdout || ''}${err.stderr || ''}` };
  }
}

// The eight checks the script must report on, per R3-03 §E.
const CHECKS = [
  'version-json',
  'banner-hash',
  'pinned-immutable',
  'unknown-404',
  'css',
  'embed',
  'branding',
  'meta',
];

test('R3-03-05 — deploy-smoke-local', async () => {
  const start = Date.now();

  // 1. Against the live stack: exit 0, with one `ok <check>` line per check.
  const live = await runSmoke(BASE_URL);
  expect(live.code, `smoke script must pass against ${BASE_URL}:\n${live.out}`).toBe(0);

  const metaSkipped = live.out.includes('warn: /api/meta unavailable');
  for (const check of CHECKS) {
    // meta is allowed to warn instead of pass on a server predating R1-04 — but only meta, and
    // only with that explicit line. Every other check must actually report ok.
    if (check === 'meta' && metaSkipped) continue;
    expect(live.out, `check "${check}" did not report ok:\n${live.out}`).toContain(`ok ${check}`);
  }

  // No check may report a failure while the script still exits 0 — that combination would mean the
  // script is reporting problems nobody acts on.
  expect(live.out, 'a passing run must contain no FAIL lines').not.toMatch(/^FAIL /m);

  // 2. Against a dead server: non-zero, and the output names the check that failed first rather
  //    than dumping curl's error alone.
  const dead = await runSmoke('http://localhost:9');
  expect(dead.code, `smoke script must fail against a dead server:\n${dead.out}`).not.toBe(0);
  expect(dead.out, 'the failure must name the check').toMatch(/FAIL version-json/);

  record({
    id: 'R3-03-05', tier: 'PR', layer: 'api', role: '—', result: 'PASS', ms: Date.now() - start,
    detail: metaSkipped
      ? '7 checks ok, meta skipped (no /api/meta); dead server exits non-zero'
      : 'all 8 checks ok; dead server exits non-zero naming version-json',
  });
});
