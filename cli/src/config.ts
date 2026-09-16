import { promises as fs } from 'node:fs';
import { join, dirname } from 'node:path';

export interface PointerConfig {
  project?: string;
  environment?: string;
  server?: string;
  aiTool?: string;
  skillsDir?: string;
  cliVersion?: string;
  /**
   * Every environment this install covers, when more than one was chosen.
   *
   * `environment` stays the primary one — doctor, apply and the server's stack record all read a
   * single value and predate multi-environment installs.
   */
  environments?: string[];
  /**
   * Where the widget was actually mounted, relative to the repo root.
   *
   * Recorded because `doctor` otherwise guesses from a fixed list of conventional paths
   * (`index.html`, `src/index.html`, …) and reports "Widget not found" for a monorepo app it was
   * explicitly told about — in the same run that just said it injected there.
   */
  htmlPath?: string;
  /**
   * How reviewers open the widget: `embed` (the `<pointer-feedback>` loader is injected into the
   * app, today's default) or `extension` (no code injection — reviewers install the Chrome
   * extension and activate it on the tab). `init` always writes this, including `'embed'` for an
   * embed install, so a config missing the field (written by an older CLI) can only mean `embed`.
   */
  delivery?: 'embed' | 'extension';
}

const CONFIG_FILE = '.pointer/config.json';
const CREDENTIALS_FILE = '.pointer/credentials.env';

export async function readConfig(cwd: string): Promise<PointerConfig> {
  try {
    const content = await fs.readFile(join(cwd, CONFIG_FILE), 'utf8');
    return JSON.parse(content);
  } catch (err: any) {
    if (err.code !== 'ENOENT') throw err;
    return {};
  }
}

export async function writeConfig(cwd: string, config: PointerConfig): Promise<void> {
  const file = join(cwd, CONFIG_FILE);
  await fs.mkdir(dirname(file), { recursive: true });
  const existing = await readConfig(cwd);
  const data = JSON.stringify({ ...existing, ...config }, null, 2) + '\n';
  await fs.writeFile(file, data, 'utf8');
}

/**
 * Writes `.pointer/credentials.env`. Besides the key it also records POINTER_SERVER / POINTER_PROJECT
 * when known: `.pointer/pointer.sh` (the no-Node fallback) resolves them from the app's `.env`, the
 * built bundle, or — its documented last resort — this very file. A repo with no `.env` (Angular,
 * Rails, static HTML…) therefore only works if we write them here; `init` used to write the key
 * alone and `pointer.sh list` failed with "Missing configuration".
 */
export async function writeCredentials(
  cwd: string,
  token: string,
  extra: { server?: string; project?: string } = {},
): Promise<void> {
  const file = join(cwd, CREDENTIALS_FILE);
  await fs.mkdir(dirname(file), { recursive: true });
  const lines = [`POINTER_API_KEY=${token}`];
  if (extra.server) lines.push(`POINTER_SERVER=${extra.server}`);
  if (extra.project) lines.push(`POINTER_PROJECT=${extra.project}`);
  await fs.writeFile(file, lines.join('\n') + '\n', { encoding: 'utf8', mode: 0o600 });
  const exampleFile = join(cwd, '.pointer/credentials.env.example');
  await fs.writeFile(exampleFile, `POINTER_API_KEY=\nPOINTER_SERVER=\nPOINTER_PROJECT=\n`, { encoding: 'utf8' });
}

/**
 * Directories a full install (or a join, see `init`'s "join" mode) writes outside `.pointer/` —
 * derived, per-machine state, never committed. `.claude/skills/pointer-init/` and
 * `.claude/skills/pointer-feedback/` are frozen skill directories for Claude Code; `.agents/...`
 * is the equivalent for every other tool (and the Claude Code symlink target).
 */
const IGNORED_SKILL_DIRS = [
  '.claude/skills/pointer-init/',
  '.claude/skills/pointer-feedback/',
  '.agents/pointer-init/',
  '.agents/pointer-feedback/',
];

export async function upsertGitignore(cwd: string, productName = 'Feedback tool'): Promise<void> {
  const file = join(cwd, '.gitignore');
  let content = await fs.readFile(file, 'utf8').catch(() => '');
  const before = content;

  // The frozen on-disk contract (R1-01): ignore the whole directory, then re-include only the
  // TEAM config a repo is meant to commit — `config.json` and `stack.json`. Everything else in
  // `.pointer/` (credentials.env, credentials.env.example, pointer.sh, manifest.json,
  // .token_cache, …) is derived or per-machine and stays ignored; so do the skill directories,
  // which live outside `.pointer/` entirely. Committing pointer.sh and the skills used to mean
  // every consumer repo carried ~800 lines of server-served markdown that went stale the moment
  // the server changed it — `init`/`update` now install a fresh copy into every clone instead.
  const entry = [
    '',
    `# ${productName}`,
    // `.pointer/*`, not `.pointer/`. Git does not descend into an excluded DIRECTORY, so the
    // directory form makes every `!` line below inert and the files this block exists to keep
    // committable are silently ignored instead.
    '.pointer/*',
    '!.pointer/stack.json',
    '!.pointer/config.json',
    ...IGNORED_SKILL_DIRS,
    '',
  ].join('\n');

  // Migrate the directory form an earlier version wrote. Left in place it still wins: any
  // exclusion of the directory stops git looking inside it, so the negations never get a chance.
  content = content.replace(/^\.pointer\/$/m, '.pointer/*');

  // Replace the narrower rule if an early version of this CLI wrote it. Matches the comment those
  // versions emitted (always the literal product name).
  content = content.replace(/\n?# [^\n]*\n\.pointer\/credentials\.env\n/, '');

  // Drop the re-includes an earlier version of THIS block wrote for files that are gitignored as
  // of this version: pointer.sh and credentials.env.example were committable, now every clone
  // installs its own copy (see `installSkills`/`update`) so there is nothing to share via git.
  content = content.replace(/^!\.pointer\/pointer\.sh\n/m, '');
  content = content.replace(/^!\.pointer\/credentials\.env\.example\n/m, '');

  // The stack.json negation is what makes the migration useful — a .gitignore that only ever said
  // `.pointer/` has none of them, so rewriting that one line would leave stack.json still ignored.
  // Keying on stack.json rather than on the exclusion line is what catches that case.
  if (!/^!\.pointer\/stack\.json$/m.test(content)) {
    content += entry;
  }

  // Independent of the block above (and its migration state): a repo whose .gitignore already has
  // the `.pointer/*` block from an older CLI still needs the skill directories added, since they
  // were never ignored before this version at all.
  const missingSkillDirs = IGNORED_SKILL_DIRS.filter((d) => !content.includes(d));
  if (missingSkillDirs.length > 0) {
    content += missingSkillDirs.map((d) => `${d}\n`).join('');
  }

  if (content !== before) {
    await fs.writeFile(file, content, 'utf8');
  }
}
