// Per-invocation harness: builds a scratch git repo from a fixture-app copy, installs the served
// skill files per the AI CLI's own convention, writes automation credentials, runs the CLI
// non-interactively with exactly ONE prompt, captures the transcript, then stops. No AI tool ever
// runs setup itself — see docs/E2E_TEST_PLAN.md's token-cost framing.
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { mkdirSync, rmSync, cpSync, writeFileSync, readFileSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { USERS } from '../scripts/lib/constants.mjs';
import { BASE_URL } from '../scripts/lib/api.mjs';

const execFileP = promisify(execFile);
const here = dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = join(here, '..', '..');
const FIXTURE_DIR = join(here, '..', 'fixture-app');
const SCRATCH_ROOT = join(here, '..', 'state', 'scratch');

// Per-tool skill-install directory + invocation. Claude Code and opencode+GLM are verified in this
// session; Antigravity's exact CLI invocation/skill-directory convention is NOT verified here (see
// docs/E2E_TEST_PLAN.md, "Known limitations") — its entry throws until confirmed against the real
// Antigravity CLI on a machine that has it installed.
const TOOLS = {
  'claude-code': {
    skillDir: '.claude/skills',
    async invoke(scratchDir, prompt) {
      const { stdout } = await execFileP(
        'claude',
        ['-p', prompt, '--dangerously-skip-permissions', '--output-format', 'text'],
        { cwd: scratchDir, timeout: 10 * 60 * 1000 },
      );
      return stdout;
    },
  },
  'opencode-glm': {
    // opencode has no fixed "skills" directory convention of its own; reusing .claude/skills is an
    // assumption (opencode reads it as plain repo files, not a first-class skill mechanism) — noted
    // as unverified in docs/E2E_TEST_PLAN.md.
    skillDir: '.claude/skills',
    async invoke(scratchDir, prompt) {
      const { stdout } = await execFileP(
        'opencode',
        ['run', '-m', 'zai-coding-plan/glm-5.2', '--dir', scratchDir, prompt],
        { cwd: scratchDir, timeout: 10 * 60 * 1000 },
      );
      return stdout;
    },
  },
  antigravity: {
    skillDir: '.claude/skills',
    async invoke() {
      throw new Error('Antigravity CLI invocation is not yet verified in this environment — see docs/E2E_TEST_PLAN.md "Known limitations".');
    },
  },
};

function sh(cmd, args, cwd) {
  return execFileP(cmd, args, { cwd });
}

async function installSkills(scratchDir, skillDir) {
  mkdirSync(join(scratchDir, skillDir, 'pointer-feedback'), { recursive: true });
  mkdirSync(join(scratchDir, skillDir, 'pointer-init'), { recursive: true });
  // Fetched from the live server (not read from source) so the harness tests exactly what's
  // actually served — same as a real developer running install.sh.
  const skill = await (await fetch(`${BASE_URL}/skill.md`)).text();
  const init = await (await fetch(`${BASE_URL}/pointer-init.md`)).text();
  writeFileSync(join(scratchDir, skillDir, 'pointer-feedback', 'SKILL.md'), skill);
  writeFileSync(join(scratchDir, skillDir, 'pointer-init', 'SKILL.md'), init);
}

function installCredentials(scratchDir) {
  mkdirSync(join(scratchDir, '.pointer'), { recursive: true });
  writeFileSync(
    join(scratchDir, '.pointer', 'credentials.env'),
    `POINTER_EMAIL=${USERS.developer.email}\nPOINTER_PASSWORD=${USERS.developer.password}\n`,
  );
  writeFileSync(join(scratchDir, '.gitignore'), '.pointer/\n');
}

/**
 * @param {string} toolKey one of 'claude-code' | 'opencode-glm' | 'antigravity'
 * @param {'alpha'|'beta'} fixture which fixture-app copy to use as the target repo
 * @param {string} prompt the single natural-language prompt to run
 * @param {string} runLabel unique label for this run, e.g. 'tc3-run-3'
 * @returns {{ scratchDir: string, transcriptPath: string, diff: string, answerText: string }}
 */
export async function runCase(toolKey, fixture, prompt, runLabel) {
  const tool = TOOLS[toolKey];
  if (!tool) throw new Error(`unknown tool: ${toolKey}`);

  const scratchDir = join(SCRATCH_ROOT, `${toolKey}-${runLabel}`);
  rmSync(scratchDir, { recursive: true, force: true });
  mkdirSync(scratchDir, { recursive: true });
  cpSync(join(FIXTURE_DIR, fixture), scratchDir, { recursive: true });

  await installSkills(scratchDir, tool.skillDir);
  installCredentials(scratchDir);

  await sh('git', ['init', '-q'], scratchDir);
  await sh('git', ['add', '-A'], scratchDir);
  await sh('git', ['-c', 'user.email=e2e@example.test', '-c', 'user.name=e2e', 'commit', '-q', '-m', 'baseline'], scratchDir);

  let answerText = '';
  let error = null;
  try {
    answerText = await tool.invoke(scratchDir, prompt);
  } catch (err) {
    error = err;
    answerText = `[harness error]\n${err.message}\n${err.stdout || ''}\n${err.stderr || ''}`;
  }

  const transcriptDir = join(here, '..', 'state', 'transcripts');
  mkdirSync(transcriptDir, { recursive: true });
  const transcriptPath = join(transcriptDir, `${toolKey}-${runLabel}.log`);
  writeFileSync(transcriptPath, `PROMPT:\n${prompt}\n\n---\n\nRESPONSE:\n${answerText}`);

  let diff = '';
  try {
    const { stdout } = await sh('git', ['diff', 'HEAD'], scratchDir);
    diff = stdout;
  } catch {
    // no commits to diff against if invoke() failed before any tool edits — fine, diff stays empty
  }

  if (error) throw Object.assign(error, { scratchDir, transcriptPath, diff, answerText });
  return { scratchDir, transcriptPath, diff, answerText };
}
