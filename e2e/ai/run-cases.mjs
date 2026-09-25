// Orchestrates Layer B (docs/E2E_TEST_PLAN.md): runs TC1-TC6 against every configured AI CLI,
// scoring each via scripts/audit.mjs. Called by run-e2e.sh --with-ai, after reset+seed+probe+widget
// have already run once (zero-AI). TC3 and TC6 reset+reseed before each of their repetitions so one
// run's PATCHes never leak into the next; every other case reuses whatever state is already there.
import { readFileSync, mkdirSync, appendFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { runCase } from './harness.mjs';
import { scoreTc3Run, scoreTc6Run, scoreListCase } from '../scripts/audit.mjs';
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
const expected = JSON.parse(readFileSync(join(STATE_DIR, 'expected.json'), 'utf8'));
const manifest = JSON.parse(readFileSync(join(here, 'cases', 'manifest.json'), 'utf8'));

// Tools to attempt — a tool that isn't installed/available fails its first run with a clear error
// and is skipped for the rest of the suite (recorded in report.md), rather than aborting everything.
const TOOLS = (process.env.E2E_AI_TOOLS || 'claude-code,opencode-glm,antigravity').split(',');

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

  const availableTools = [];
  for (const tool of TOOLS) {
    availableTools.push(tool); // availability is discovered on first real invocation, not probed ahead of time
  }

  for (const tool of availableTools) {
    console.log(`\n=== Tool: ${tool} ===`);
    let toolFailedOnce = false;

    for (const c of manifest.cases) {
      if (toolFailedOnce) {
        appendFileSync(reportPath, `\n### ${c.id} — ${tool}: SKIPPED (tool unavailable)\n`);
        continue;
      }

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
          console.error(`    tool invocation failed: ${err.message}`);
          appendFileSync(reportPath, `\n### ${runLabel} — ${tool}: TOOL UNAVAILABLE/ERRORED\n\n${err.message}\n`);
          toolFailedOnce = true;
          break;
        }

        if (c.id === 'tc3') {
          const criteria = await scoreTc3Run(`${tool}-${runLabel}`);
          console.log('   ', criteria);
        } else if (c.id === 'tc6') {
          const criteria = await scoreTc6Run(`${tool}-${runLabel}`, {
            diff: result.diff,
            answerText: result.answerText,
          });
          console.log('   ', criteria);
        } else {
          const ea = expected.expectedAnswers[c.id] || {};
          const criteria = await scoreListCase(`${runLabel} — ${tool}`, {
            projectKey: c.project,
            includeIds: ea.includeIds || [],
            excludeIds: ea.excludeIds || [],
            answerText: result.answerText,
          });
          console.log('   ', criteria);
        }
      }
    }
  }

  console.log(`\n==> Layer B complete. See ${reportPath}`);
}

main().catch((err) => {
  console.error('run-cases.mjs crashed:', err);
  process.exit(1);
});
