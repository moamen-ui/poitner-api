// Orchestrates Layer B (docs/E2E_TEST_PLAN.md): runs TC1-TC5 against every configured AI CLI,
// scoring each via scripts/audit.mjs. Called by run-e2e.sh --with-ai, after reset+seed+probe+widget
// have already run once (zero-AI). TC3 resets+reseeds before each of its 5 repetitions so one run's
// PATCHes never leak into the next; every other case reuses whatever state is already there.
import { readFileSync, mkdirSync, appendFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { runCase } from './harness.mjs';
import { scoreTc3Run, scoreListCase } from '../scripts/audit.mjs';
import { PROJECTS } from '../scripts/lib/constants.mjs';

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
}

async function main() {
  mkdirSync(STATE_DIR, { recursive: true });
  const reportPath = join(STATE_DIR, 'report.md');
  appendFileSync(reportPath, `\n## Layer B — AI-under-test runs\n`);

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
      const fixture = c.project === PROJECTS.beta.key ? 'beta' : 'alpha';

      for (let i = 1; i <= c.repeat; i++) {
        const runLabel = c.repeat > 1 ? `${c.id}-run-${i}` : c.id;
        console.log(`--- ${tool} / ${runLabel} ---`);

        if (c.resetPerRun) {
          console.log('    (resetting + reseeding before this run)');
          await resetAndReseed();
        }

        let result;
        try {
          result = await runCase(tool, fixture, prompt, `${runLabel}`);
        } catch (err) {
          console.error(`    tool invocation failed: ${err.message}`);
          appendFileSync(reportPath, `\n### ${runLabel} — ${tool}: TOOL UNAVAILABLE/ERRORED\n\n${err.message}\n`);
          toolFailedOnce = true;
          break;
        }

        if (c.id === 'tc3') {
          const criteria = await scoreTc3Run(`${tool}-${runLabel}`);
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
