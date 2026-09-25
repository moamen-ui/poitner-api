// Orchestrates Layer B (docs/E2E_TEST_PLAN.md): runs TC1-TC6 against every configured AI CLI,
// scoring each via scripts/audit.mjs. Called by run-e2e.sh --with-ai, after reset+seed+probe+widget
// have already run once (zero-AI). TC3 and TC6 reset+reseed before each of their repetitions so one
// run's PATCHes never leak into the next; every other case reuses whatever state is already there.
import { readFileSync, mkdirSync, appendFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { runCase, isToolBinaryAvailable } from './harness.mjs';
import { scoreTc3Run, scoreTc4Run, scoreTc6Run, scoreListCase } from '../scripts/audit.mjs';
import { PROJECTS } from '../scripts/lib/constants.mjs';
import { restartApi } from '../scripts/restart-api.mjs';

// Project key -> fixture-app/ subdirectory to copy into the scratch repo. Was a two-way ternary
// (alpha/beta only) before TC6 added a third project; kept as an explicit map rather than more
// branches so a fourth case's project needs only one new entry here.
const FIXTURE_BY_PROJECT_KEY = {
  [PROJECTS.alpha.key]: 'alpha',
  [PROJECTS.beta.key]: 'beta',
  [PROJECTS.tc6.key]: 'tc6',
};

const execFileP = promisify(execFile);
const here = dirname(fileURLToPath(import.meta.url));
const STATE_DIR = join(here, '..', 'state');
// Read fresh wherever used, not cached here — see audit.mjs's loadExpected() for why a
// module-level read-once would be stale relative to TC3/TC4/TC6's own resetAndReseed() calls
// below (harmless today only because comment ids happen to be deterministic across a full
// `docker compose down -v` reset; still the wrong thing to rely on).
function loadExpected() {
  return JSON.parse(readFileSync(join(STATE_DIR, 'expected.json'), 'utf8'));
}
const manifest = JSON.parse(readFileSync(join(here, 'cases', 'manifest.json'), 'utf8'));

// Tools to attempt. A tool whose binary genuinely is not on PATH is skipped entirely (checked via
// isToolBinaryAvailable() — see harness.mjs); a tool whose binary IS present but errors on a given
// invocation is NOT skipped for the rest of the suite — that one run/case is recorded as ERROR and
// the tool keeps going. (Previously: any single invocation error, for any reason, permanently
// skipped every remaining case for that tool — a transient/model-side error on case 1 of 9 hid the
// other 8 entirely, e.g. tc3-run-2 erroring took out tc4-tc6 too.)
const TOOLS = (process.env.E2E_AI_TOOLS || 'claude-code,opencode-glm,antigravity').split(',');

// Optional subset filter, e.g. `E2E_AI_CASES=tc3,tc6` — cheaper reproduction of a specific
// regression without paying for the full TC1-TC6 sweep across every tool. Unset (the normal case)
// runs every case in manifest.json, unchanged from before this existed.
const CASE_FILTER = process.env.E2E_AI_CASES
  ? new Set(process.env.E2E_AI_CASES.split(',').map((s) => s.trim()).filter(Boolean))
  : null;
const casesToRun = CASE_FILTER ? manifest.cases.filter((c) => CASE_FILTER.has(c.id)) : manifest.cases;

async function resetAndReseed() {
  await execFileP('bash', [join(here, '..', 'scripts', 'reset.sh')], { cwd: join(here, '..') });
  await execFileP('node', [join(here, '..', 'scripts', 'seed.mjs')], { cwd: join(here, '..') });
  // reset.sh brings the api container back up from the base compose file alone (no restart-api.mjs
  // override), which already reverts an env-backed override like Cli__MinVersion in the normal
  // case — but TC3/TC6 each reset+reseed several times per tool, and a prior phase's teardown that
  // died mid-restore (e2e/cli/doctor.spec.mjs R1-04-04, e2e/cli/registry.spec.mjs R1-04-06 both
  // restore it in a `finally`, but a killed process skips that) can still leave the min-CLI-version
  // gate raised. An explicit no-override restartApi() here is a cheap, unconditional guarantee
  // that every TC3/TC6 repetition starts from the server's real configured minimum, regardless of
  // what any earlier phase left behind.
  await restartApi();
}

// One row per (tool, case-run) attempted or skipped — printed as a scorecard at the end and used
// to decide the phase's own exit code (see main()'s bottom). `status` is one of PASS/FAIL/ERROR/SKIP.
const scorecard = [];

function padEnd(s, n) {
  s = String(s);
  return s.length >= n ? s : s + ' '.repeat(n - s.length);
}

function printScorecard() {
  console.log('\n================================================================================');
  console.log(' AI PHASE SCORECARD');
  console.log('================================================================================');
  const header = `${padEnd('TOOL', 16)} ${padEnd('CASE', 14)} ${padEnd('STATUS', 8)} DETAIL`;
  console.log(header);
  console.log('-'.repeat(header.length + 40));
  for (const row of scorecard) {
    console.log(`${padEnd(row.tool, 16)} ${padEnd(row.case, 14)} ${padEnd(row.status, 8)} ${row.detail || ''}`);
  }
  console.log('================================================================================');

  const reportPath = join(STATE_DIR, 'report.md');
  const lines = ['\n## Layer B — AI phase scorecard\n', '| tool | case | status | detail |', '|---|---|---|---|'];
  for (const row of scorecard) {
    lines.push(`| ${row.tool} | ${row.case} | ${row.status} | ${(row.detail || '').replace(/\|/g, '/')} |`);
  }
  appendFileSync(reportPath, lines.join('\n') + '\n');
}

async function main() {
  mkdirSync(STATE_DIR, { recursive: true });
  const reportPath = join(STATE_DIR, 'report.md');
  appendFileSync(reportPath, `\n## Layer B — AI-under-test runs\n`);

  // The ai phase runs LAST (after registry/upgrade/429), any of which can raise the server's
  // minimum CLI version (Cli__MinVersion, env/config-backed via restart-api.mjs's compose
  // override) and is supposed to restore it in its own teardown. A run that dies before that
  // teardown — a killed process, a timed-out phase — leaves the override applied, and every CLI
  // invocation in this phase then fails "CLI x.y.z is older than the server requires" for a
  // reason that has nothing to do with what TC1-TC6 actually test. Resetting unconditionally here
  // makes the ai phase self-healing regardless of which earlier phase (or bug) left it raised.
  console.log('==> Restoring the server\'s normal minimum CLI version before any AI case runs');
  await restartApi();

  let casesRun = 0; // real invocations attempted (success or error) — zero of these means nothing
                     // was measured at all (see the bottom check, unchanged from before).
  let casesFailed = 0; // ERROR or SKIP entries — these are what make the phase dishonest if ignored.

  for (const tool of TOOLS) {
    const avail = await isToolBinaryAvailable(tool);
    if (!avail.available) {
      console.log(`\n=== Tool: ${tool} — UNAVAILABLE (${avail.reason}) — skipping all its cases ===`);
      for (const c of casesToRun) {
        const label = `${tool}-${c.id}`;
        appendFileSync(reportPath, `\n### ${c.id} — ${tool}: SKIPPED (${avail.reason})\n`);
        scorecard.push({ tool, case: c.id, status: 'SKIP', detail: avail.reason });
        casesFailed++;
      }
      continue;
    }

    console.log(`\n=== Tool: ${tool} ===`);

    for (const c of casesToRun) {
      const prompt = readFileSync(join(here, 'cases', c.promptFile), 'utf8').trim();
      const fixture = FIXTURE_BY_PROJECT_KEY[c.project] || 'alpha';

      for (let i = 1; i <= c.repeat; i++) {
        const runLabel = c.repeat > 1 ? `${c.id}-run-${i}` : c.id;
        console.log(`--- ${tool} / ${runLabel} ---`);

        if (c.resetPerRun) {
          console.log('    (resetting + reseeding before this run)');
          await resetAndReseed();
        }

        let result;
        try {
          result = await runCase(tool, fixture, c.project, prompt, `${runLabel}`);
        } catch (err) {
          // A single invocation erroring (agent crash, transient network blip, model refusal,
          // timeout, npx registry hiccup, ...) is recorded as ERROR for THIS case/run only — the
          // tool is NOT marked unavailable and the remaining cases/runs still execute. Only a
          // missing binary (checked once, above, before any case runs) skips the rest of a tool.
          console.error(`    ${runLabel} — ${tool}: ERROR: ${err.message}`);
          const detail =
            `exit code: ${err.code ?? 'n/a'}, signal: ${err.signal ?? 'n/a'}, ` +
            `stderr: ${(err.stderr || '').trim().slice(0, 300)}`;
          appendFileSync(
            reportPath,
            `\n### ${runLabel} — ${tool}: ERROR\n\n${err.message}\n\n${detail}\n` +
              (err.transcriptPath ? `\nTranscript: ${err.transcriptPath}\n` : ''),
          );
          scorecard.push({ tool, case: runLabel, status: 'ERROR', detail });
          casesFailed++;
          continue; // next repeat/case for this same tool — do not abort the tool
        }

        casesRun++;
        let scored;
        if (c.id === 'tc3') {
          scored = await scoreTc3Run(`${tool}-${runLabel}`);
        } else if (c.id === 'tc4') {
          // Dedicated scorer, not scoreListCase — see audit.mjs's scoreTc4Run for why (TC4's
          // ground truth is apply STATUS, not list visibility).
          scored = await scoreTc4Run(`${tool}-${runLabel}`, {
            projectKey: c.project,
            answerText: result.answerText,
          });
        } else if (c.id === 'tc6') {
          scored = await scoreTc6Run(`${tool}-${runLabel}`, {
            diff: result.diff,
            answerText: result.answerText,
          });
        } else {
          const ea = loadExpected().expectedAnswers[c.id] || {};
          scored = await scoreListCase(`${runLabel} — ${tool}`, {
            projectKey: c.project,
            includeIds: ea.includeIds || [],
            excludeIds: ea.excludeIds || [],
            answerText: result.answerText,
          });
        }
        console.log('   ', scored.criteria);
        if (scored.result !== 'PASS') casesFailed++;
        scorecard.push({ tool, case: runLabel, status: scored.result, detail: scored.detail });
      }
    }
  }

  printScorecard();

  console.log(`\n==> Layer B complete. See ${reportPath}`);
  // A run where every tool failed to start measured nothing — report that as a failure, not a pass
  // (a Verdaccio that was down once made both tools "unavailable" and the phase still went green).
  if (casesRun === 0) {
    console.error('Layer B ran zero cases: every AI tool failed to start (see the TOOL UNAVAILABLE/SKIPPED entries above).');
    process.exit(1);
  }
  // Make the phase's own exit code match the scorecard: previously this only ever failed on the
  // zero-cases case above, so `run_phase`'s `node ai/run-cases.mjs && node scripts/audit.mjs`
  // reported the "ai" phase as PASS in run-e2e.sh's report.md even when every case underneath it
  // FAILed, ERRORed, or was SKIPped — e.g. tc3-run-2 erroring and tc4-6 being silently skipped
  // (the bug this whole harness pass exists to fix) still showed `ai | PASS`. Any ERROR or SKIP
  // (a tool/case that never really ran) fails the phase outright; a scoring FAIL also fails it —
  // the agent's own bad behavior is exactly what this phase exists to catch, so it should read as
  // a failed phase too, not a quietly-swallowed one.
  if (casesFailed > 0) {
    console.error(`Layer B: ${casesFailed} case/run(s) were not a clean PASS (see the scorecard above) — failing the ai phase.`);
    process.exit(1);
  }
}

main().catch((err) => {
  console.error('run-cases.mjs crashed:', err);
  process.exit(1);
});
