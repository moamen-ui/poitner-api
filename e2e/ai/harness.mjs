// Per-invocation harness: builds a scratch git repo from a fixture-app copy, installs the served
// skill files per the AI CLI's own convention, writes automation credentials, runs the CLI
// non-interactively with exactly ONE prompt, captures the transcript, then stops. No AI tool ever
// runs setup itself — see docs/E2E_TEST_PLAN.md's token-cost framing.
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { mkdirSync, rmSync, cpSync, writeFileSync, readFileSync, readdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { USERS } from '../scripts/lib/constants.mjs';
import { BASE_URL, raw, login } from '../scripts/lib/api.mjs';
import { REGISTRY_URL, createScratchEnv, publishTarball } from '../scripts/lib/registry.mjs';

const execFileP = promisify(execFile);
const here = dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = join(here, '..', '..');
const FIXTURE_DIR = join(here, '..', 'fixture-app');
const SCRATCH_ROOT = join(here, '..', 'state', 'scratch');
const CLI_DIR = join(REPO_ROOT, 'cli');
const CLI_PUBLISH_SCRATCH = join(here, '..', 'state', 'cli-publish-scratch');

// The served `pointer-feedback` skill is an entry file (`/skill.md`) plus three siblings
// (`/skills/apply.md`, `/skills/translate.md`, `/skills/advanced.md`) — see cli/src/skills.ts's
// `SUB_SKILLS`/`installSkills`, which is the real installer this harness must mirror. Duplicated
// here (not imported) because cli/src is TypeScript and this is a plain-Node .mjs harness; keep
// this literal array in sync with cli/src/skills.ts's `SUB_SKILLS` export.
const SUB_SKILLS = ['apply', 'translate', 'advanced'];

// Per-tool skill-install directory + invocation. Claude Code and opencode+GLM are verified in this
// session; Antigravity's exact CLI invocation/skill-directory convention is NOT verified here (see
// docs/E2E_TEST_PLAN.md, "Known limitations") — its entry throws until confirmed against the real
// Antigravity CLI on a machine that has it installed.
const TOOLS = {
  'claude-code': {
    skillDir: '.claude/skills',
    configAiTool: 'claude-code',
    async invoke(scratchDir, prompt) {
      const { stdout } = await execFileP(
        'claude',
        ['-p', prompt, '--dangerously-skip-permissions', '--output-format', 'text'],
        { cwd: scratchDir, timeout: 10 * 60 * 1000, env: { ...process.env, ...npmRegistryEnv() } },
      );
      return stdout;
    },
  },
  'opencode-glm': {
    // opencode has no fixed "skills" directory convention of its own; reusing .claude/skills is an
    // assumption (opencode reads it as plain repo files, not a first-class skill mechanism) — noted
    // as unverified in docs/E2E_TEST_PLAN.md.
    skillDir: '.claude/skills',
    configAiTool: 'other',
    async invoke(scratchDir, prompt) {
      const { stdout } = await execFileP(
        'opencode',
        ['run', '-m', 'zai-coding-plan/glm-5.2', '--dir', scratchDir, prompt],
        { cwd: scratchDir, timeout: 10 * 60 * 1000, env: { ...process.env, ...npmRegistryEnv() } },
      );
      return stdout;
    },
  },
  antigravity: {
    skillDir: '.claude/skills',
    configAiTool: 'antigravity',
    async invoke() {
      throw new Error('Antigravity CLI invocation is not yet verified in this environment — see docs/E2E_TEST_PLAN.md "Known limitations".');
    },
  },
};

function sh(cmd, args, cwd) {
  return execFileP(cmd, args, { cwd });
}

async function installSkills(scratchDir, skillDir) {
  const feedbackDir = join(scratchDir, skillDir, 'pointer-feedback');
  const initDir = join(scratchDir, skillDir, 'pointer-init');
  mkdirSync(feedbackDir, { recursive: true });
  mkdirSync(initDir, { recursive: true });

  // Fetched from the live server (not read from source) so the harness tests exactly what's
  // actually served — same as a real developer running `pointer init`/`update`. The served skill
  // is split into the entry file plus three sibling files that skill.md's own "read apply.md for
  // the apply workflow" references depend on (cli/src/skills.ts's `installSkills`) — installing
  // only SKILL.md used to silently drop apply.md/translate.md/advanced.md, so the apply workflow
  // and the untrusted-content/security rules it documents were never actually exercised.
  const [init, entry, ...subs] = await Promise.all([
    fetch(`${BASE_URL}/pointer-init.md`).then((r) => r.text()),
    fetch(`${BASE_URL}/skill.md`).then((r) => r.text()),
    ...SUB_SKILLS.map((name) => fetch(`${BASE_URL}/skills/${name}.md`).then((r) => r.text())),
  ]);

  writeFileSync(join(initDir, 'SKILL.md'), init);
  writeFileSync(join(feedbackDir, 'SKILL.md'), entry);
  SUB_SKILLS.forEach((name, i) => writeFileSync(join(feedbackDir, `${name}.md`), subs[i]));
}

// Publishes the CURRENT cli/ build (the branch under test) to the gate's Verdaccio registry, once
// per process (memoized — every runCase() in a run-cases.mjs invocation shares this), so
// `npx -y pointer-feedback@latest` inside every scratch repo resolves to THIS build rather than
// the real, published npmjs package. Without this the harness only ever exercised whatever CLI
// happens to be on npmjs — the branch's own cli/src/apply/prompt.ts changes were never reachable.
// See docs/E2E_TEST_PLAN.md "Layer B tests the branch under test" for the chosen mechanism.
let publishLocalCliPromise = null;
export function publishLocalCli() {
  if (!publishLocalCliPromise) {
    publishLocalCliPromise = (async () => {
      rmSync(CLI_PUBLISH_SCRATCH, { recursive: true, force: true });
      mkdirSync(CLI_PUBLISH_SCRATCH, { recursive: true });
      const env = { ...process.env, ...createScratchEnv(CLI_PUBLISH_SCRATCH) };

      // Packs cli/ EXACTLY as it stands (dist/ must already be built — the gate's own
      // `npm run build` step, or a developer's own `cd cli && npm run build`), at whatever version
      // cli/package.json currently has. No version override/rebuild (unlike registry.spec.mjs's
      // deliberately-fake old/new versions) — this is meant to BE the real branch build, byte for
      // byte, published at its real version.
      await execFileP('npm', ['pack', '--pack-destination', CLI_PUBLISH_SCRATCH], { cwd: CLI_DIR, env });
      const tgz = readdirSync(CLI_PUBLISH_SCRATCH).find((f) => f.endsWith('.tgz'));
      if (!tgz) {
        throw new Error(
          `npm pack produced no tarball in ${CLI_PUBLISH_SCRATCH} — is cli/dist/ built? (cd cli && npm run build)`,
        );
      }
      publishTarball(join(CLI_PUBLISH_SCRATCH, tgz), { scratchDir: CLI_PUBLISH_SCRATCH });

      const pkg = JSON.parse(readFileSync(join(CLI_DIR, 'package.json'), 'utf8'));
      return pkg.version;
    })().catch((err) => {
      publishLocalCliPromise = null; // a transient failure must not permanently poison later cases
      throw err;
    });
  }
  return publishLocalCliPromise;
}

// Points npm/npx resolution at the gate's Verdaccio (which proxies everything else to the real
// npmjs — e2e/verdaccio/config.yaml's `'**': proxy: npmjs`), so a bare `npx -y
// pointer-feedback@latest` — exactly what the served skill tells the AI tool to run, with no
// `--registry` flag of its own — resolves to the version publishLocalCli() just published, not
// the real published package.
//
// The AUTHORITATIVE mechanism is the `npm_config_registry` env var passed to the AI tool's own
// process (see `npmRegistryEnv`/TOOLS above): a project-level `.npmrc` is only honoured from the
// directory npm resolves as the "local prefix" — the nearest ancestor with a package.json — and
// none of fixture-app/{alpha,beta,tc6} has one of its own. Verified directly: a `.npmrc` written
// into e2e/fixture-app/tc6's scratch copy (which lives under e2e/state/…) was silently ignored
// because npm's prefix search walked up to e2e/package.json instead and read (the nonexistent)
// e2e/.npmrc — `npm config get registry` there still reported the real npmjs. The env var has no
// such directory-walk ambiguity. The `.npmrc` file below is still written, for the (real-world)
// case a host repo already has its own package.json — same as what a real developer's project
// would resolve — but is not what this harness itself relies on.
function npmRegistryEnv() {
  return { npm_config_registry: REGISTRY_URL };
}

function installNpmRegistry(scratchDir) {
  writeFileSync(join(scratchDir, '.npmrc'), `registry=${REGISTRY_URL}/\n`, 'utf8');
}

async function fetchDeveloperApiKey() {
  // forceFresh: true — same reason as scripts/audit.mjs's scorer logins. TC3/TC6 reset+reseed
  // between repetitions, which invalidates any cached JWT for this account; a cached token here
  // 404s the /api/me/api-key call exactly like the scorer's did before that fix.
  const dev = await login(USERS.developer.email, USERS.developer.password, { forceFresh: true });
  const keyRes = await raw('GET', '/api/me/api-key', { token: dev.token });
  const apiKey = keyRes.data?.apiKey;
  if (!apiKey) {
    throw new Error('harness: could not resolve the Developer automation API key from GET /api/me/api-key');
  }
  return apiKey;
}

// Mirrors what a real `pointer init` writes under `.pointer/` — NOT what the harness previously
// wrote (POINTER_EMAIL/POINTER_PASSWORD, which cli/src/auth.ts and credentials.ts never read at
// all; the CLI only ever resolves an API key, via env POINTER_API_KEY, `.pointer/credentials.env`,
// or the global per-machine store — see cli/src/credentials.ts's `resolveApiKey`). Without a real
// `.pointer/config.json`, `apply` has no configured server/project (cli/src/commands/apply.ts)
// and falls back to the CLI's baked-in production default server with no project resolvable at
// all — a real user's repo never looks like that.
async function installRepoState(scratchDir, projectKey, toolKey, cliVersion) {
  const apiKey = await fetchDeveloperApiKey();

  mkdirSync(join(scratchDir, '.pointer'), { recursive: true });
  writeFileSync(
    join(scratchDir, '.pointer', 'credentials.env'),
    `POINTER_API_KEY=${apiKey}\nPOINTER_SERVER=${BASE_URL}\nPOINTER_PROJECT=${projectKey}\n`,
    { mode: 0o600 },
  );

  // Same fields `init` itself writes (cli/src/commands/init.ts's `configPatch`) for a single-
  // project, embed-delivery install — the only shape this harness's fixture-apps ever represent.
  const config = {
    server: BASE_URL,
    project: projectKey,
    aiTool: TOOLS[toolKey]?.configAiTool ?? 'other',
    cliVersion,
    delivery: 'embed',
  };
  writeFileSync(join(scratchDir, '.pointer', 'config.json'), `${JSON.stringify(config, null, 2)}\n`, 'utf8');

  writeFileSync(join(scratchDir, '.gitignore'), '.pointer/\n');
}

/**
 * @param {string} toolKey one of 'claude-code' | 'opencode-glm' | 'antigravity'
 * @param {string} fixture which fixture-app copy to use as the target repo ('alpha'|'beta'|'tc6')
 * @param {string} projectKey the Pointer project key the fixture is registered under (e.g. 'e2e-alpha')
 * @param {string} prompt the single natural-language prompt to run
 * @param {string} runLabel unique label for this run, e.g. 'tc3-run-3'
 * @returns {{ scratchDir: string, transcriptPath: string, diff: string, answerText: string }}
 */
export async function runCase(toolKey, fixture, projectKey, prompt, runLabel) {
  const tool = TOOLS[toolKey];
  if (!tool) throw new Error(`unknown tool: ${toolKey}`);

  const scratchDir = join(SCRATCH_ROOT, `${toolKey}-${runLabel}`);
  rmSync(scratchDir, { recursive: true, force: true });
  mkdirSync(scratchDir, { recursive: true });
  cpSync(join(FIXTURE_DIR, fixture), scratchDir, { recursive: true });

  await installSkills(scratchDir, tool.skillDir);
  const cliVersion = await publishLocalCli();
  installNpmRegistry(scratchDir);
  await installRepoState(scratchDir, projectKey, toolKey, cliVersion);

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

// Exported for e2e/ai/dry-run-verify.mjs (no-paid-AI verification of everything above).
export { installSkills, installRepoState, installNpmRegistry, npmRegistryEnv, SUB_SKILLS };
