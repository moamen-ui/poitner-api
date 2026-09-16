import { promises as fs } from 'node:fs';
import { join, dirname, relative, isAbsolute, sep } from 'node:path';

/**
 * One app inside a multi-project (monorepo) repo. Keyed by the Pointer project key in
 * `PointerConfig.projects`.
 *
 * There is NO default project in multi-project mode — every app must be named explicitly, unlike
 * single-project mode where `path` is implicitly `.`.
 */
export interface ProjectEntry {
  /** Repo-relative directory this app lives in, e.g. `apps/profile`. Required. */
  path: string;
  environment?: string;
  environments?: string[];
  htmlPath?: string;
  /** Overrides the repo-level default `delivery` for this app only. */
  delivery?: 'embed' | 'extension';
}

/** A `ProjectEntry` with its key attached — what `listProjects`/`resolveProject` hand back. */
export interface ResolvedProject extends ProjectEntry {
  key: string;
}

export interface PointerConfig {
  /**
   * Single-project mode only. Multi-project configs (see `projects`) never set this — the project
   * for a given app is only ever a key inside `projects`.
   */
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
   *
   * In multi-project mode this is the repo-level DEFAULT; a `ProjectEntry.delivery` overrides it
   * for one app.
   */
  delivery?: 'embed' | 'extension';
  /**
   * Multi-project (monorepo) mode. When set and non-empty, `project`/`environment`/`environments`/
   * `htmlPath` above are unused — every app is a keyed entry here instead. `isMultiProject` is the
   * one place that decides which mode a config is in.
   */
  projects?: Record<string, ProjectEntry>;
}

const CONFIG_FILE = '.pointer/config.json';
const CREDENTIALS_FILE = '.pointer/credentials.env';

/** True when `config.projects` names at least one app — the multi-project (monorepo) schema. */
export function isMultiProject(config: PointerConfig): boolean {
  return !!config.projects && Object.keys(config.projects).length > 0;
}

/**
 * Every project this config knows about, uniformly shaped whichever schema it is written in.
 *
 * Single-project mode yields exactly one entry with `path: '.'` (built from the top-level
 * `project`/`environment`/… fields) — or none at all if `project` was never set. Multi-project
 * mode yields one entry per key in `projects`, in insertion order.
 */
export function listProjects(config: PointerConfig): ResolvedProject[] {
  if (isMultiProject(config)) {
    return Object.entries(config.projects!).map(([key, entry]) => ({ key, ...entry }));
  }
  if (!config.project) return [];
  return [
    {
      key: config.project,
      path: '.',
      environment: config.environment,
      environments: config.environments,
      htmlPath: config.htmlPath,
      delivery: config.delivery,
    },
  ];
}

export type ResolveProjectResult =
  | { ok: true; project: ResolvedProject }
  // `none`: no project configured at all. `not-found`: --project named a key that does not exist.
  // `ambiguous`: several projects exist and neither --project nor cwd picked one out — the caller
  // decides whether to fall back to "every project" or to exit 2.
  | { ok: false; reason: 'none' | 'not-found' | 'ambiguous'; keys: string[] };

/**
 * Resolution order (identical for every command that takes a project): `--project <key>` flag →
 * the project whose `path` contains `cwd` → the only configured project → otherwise ambiguous.
 *
 * `root` is the repo root that holds `.pointer/config.json` (see `findRepoRoot`) — every
 * `ProjectEntry.path` is relative to it, not to `cwd`.
 */
export function resolveProject(
  config: PointerConfig,
  cwd: string,
  root: string,
  flag?: string,
): ResolveProjectResult {
  const projects = listProjects(config);

  if (flag) {
    const found = projects.find((p) => p.key === flag);
    return found
      ? { ok: true, project: found }
      : { ok: false, reason: 'not-found', keys: projects.map((p) => p.key) };
  }

  if (projects.length === 0) return { ok: false, reason: 'none', keys: [] };

  // The project whose directory contains cwd, preferring the most specific (deepest) match — a
  // `path: '.'` entry would otherwise "contain" every cwd in the repo.
  const cwdAbs = resolveAbs(cwd);
  let best: ResolvedProject | undefined;
  let bestDepth = -1;
  for (const p of projects) {
    const dirAbs = resolveAbs(join(root, p.path));
    if (cwdAbs === dirAbs || cwdAbs.startsWith(dirAbs + sep)) {
      const depth = dirAbs.split(sep).length;
      if (depth > bestDepth) {
        best = p;
        bestDepth = depth;
      }
    }
  }
  if (best) return { ok: true, project: best };

  if (projects.length === 1) return { ok: true, project: projects[0] };

  return { ok: false, reason: 'ambiguous', keys: projects.map((p) => p.key) };
}

function resolveAbs(p: string): string {
  return isAbsolute(p) ? normalizeTrailingSlash(p) : normalizeTrailingSlash(join(process.cwd(), p));
}

function normalizeTrailingSlash(p: string): string {
  return p.endsWith(sep) && p.length > 1 ? p.slice(0, -1) : p;
}

/**
 * Walks up from `cwd` to find the nearest ancestor holding `.pointer/config.json`, and returns
 * that directory — the repo root every command must resolve relative paths (project `path`,
 * `.pointer/*`) against. A monorepo app is routinely run from inside `apps/<x>`, and treating THAT
 * as root would look for `.pointer/config.json` (and every stack/skill file) in the wrong place.
 *
 * Falls back to `cwd` unchanged when no `.pointer/config.json` is found anywhere above it — the
 * correct behaviour for `init`'s first run, which is what CREATES that file.
 */
export async function findRepoRoot(cwd: string): Promise<string> {
  let dir = resolveAbs(cwd);
  // A filesystem root's parent is itself; that's the loop's stop condition.
  while (true) {
    try {
      await fs.access(join(dir, '.pointer', 'config.json'));
      return dir;
    } catch {
      // keep walking up
    }
    const parent = dirname(dir);
    if (parent === dir) return cwd;
    dir = parent;
  }
}

/** Convenience: finds the repo root and reads its config in one call. */
export async function resolveRootAndConfig(
  cwd: string,
): Promise<{ root: string; config: PointerConfig }> {
  const root = await findRepoRoot(cwd);
  const config = await readConfig(root);
  return { root, config };
}

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
 * Writes exactly the given object, with no merge against what is already on disk.
 *
 * `writeConfig` merges — the right default, since almost every write is "add/replace one field".
 * The single→multi migration is the one write that must NOT merge: it drops the single-project
 * `project`/`environment`/`htmlPath` top-level fields in favour of `projects`, and a merge would
 * leave the old fields sitting next to the new map.
 */
export async function writeConfigFull(cwd: string, config: PointerConfig): Promise<void> {
  const file = join(cwd, CONFIG_FILE);
  await fs.mkdir(dirname(file), { recursive: true });
  const data = JSON.stringify(config, null, 2) + '\n';
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
 * Directories/files a full install (or a join, see `init`'s "join" mode) writes outside
 * `.pointer/` — derived, per-machine state, never committed. `.claude/skills/pointer-init/` and
 * `.claude/skills/pointer-feedback/` are frozen skill directories for Claude Code; `.agents/...`
 * is the equivalent for every other tool (and the Claude Code symlink target); the `.cursor/` and
 * `.windsurf/` rule files are what `installSkills` writes for those two tools specifically (see
 * `SKILL_FILES` in `skills.ts`) — every layout the CLI can produce must be listed here, or a repo
 * ends up with an uncommitted-looking file the moment someone runs `init --tool cursor` (or
 * `windsurf`) instead of the Claude Code default.
 */
const IGNORED_SKILL_DIRS = [
  '.claude/skills/pointer-init/',
  '.claude/skills/pointer-feedback/',
  // Current Agent Skills layout (2026-09-16+) — used by `other`/`antigravity`, and symlinked into
  // from claude-code/cursor/windsurf (see `writeOrLink` in skills.ts).
  '.agents/skills/pointer-init/',
  '.agents/skills/pointer-feedback/',
  // Legacy pre-2026-09-16 layout. `installSkills` removes these on every install, but a repo that
  // has not run init/update since still has them on disk until it does — keep ignoring them too.
  '.agents/pointer-init/',
  '.agents/pointer-feedback/',
  '.cursor/rules/pointer-init.md',
  '.cursor/rules/pointer-feedback.md',
  '.windsurf/rules/pointer-init.md',
  '.windsurf/rules/pointer-feedback.md',
];

/**
 * Adds/removes the boilerplate that keeps the repo's `.gitignore` covering every path `init` can
 * write. `skillsDir` is `init --skills-dir <dir>`'s override — when set, `installSkills` writes
 * `<dir>/pointer-init/SKILL.md` and `<dir>/pointer-feedback/SKILL.md` instead of the tool-specific
 * layout above, and those two directories must be ignored too, on top of the fixed list.
 */
export async function upsertGitignore(
  cwd: string,
  productName = 'Feedback tool',
  skillsDir?: string,
): Promise<void> {
  const file = join(cwd, '.gitignore');
  let content = await fs.readFile(file, 'utf8').catch(() => '');
  const before = content;

  const skillsDirPaths = skillsDir
    ? [`${skillsDir.replace(/\/+$/, '')}/pointer-init/`, `${skillsDir.replace(/\/+$/, '')}/pointer-feedback/`]
    : [];
  const allSkillPaths = [...IGNORED_SKILL_DIRS, ...skillsDirPaths];

  // The frozen on-disk contract (R1-01): ignore the whole directory, then re-include only the
  // TEAM config a repo is meant to commit — `config.json`, `stack.json` and, in a multi-project
  // repo, every `projects/<key>.stack.json`. Everything else in `.pointer/` (credentials.env,
  // credentials.env.example, pointer.sh, manifest.json, .token_cache, …) is derived or per-machine
  // and stays ignored; so do the skill directories/files, which live outside `.pointer/` entirely.
  // Committing pointer.sh and the skills used to mean every consumer repo carried ~800 lines of
  // server-served markdown that went stale the moment the server changed it — `init`/`update` now
  // install a fresh copy into every clone instead.
  const entry = [
    '',
    `# ${productName}`,
    // `.pointer/*`, not `.pointer/`. Git does not descend into an excluded DIRECTORY, so the
    // directory form makes every `!` line below inert and the files this block exists to keep
    // committable are silently ignored instead.
    '.pointer/*',
    '!.pointer/stack.json',
    '!.pointer/config.json',
    // Re-includes the directory itself (not a wildcard for its contents) — with nothing further
    // ignoring paths inside it, git descends and every `projects/<key>.stack.json` stays
    // committable, exactly like stack.json/config.json above.
    '!.pointer/projects/',
    ...allSkillPaths,
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
  // the `.pointer/*` block from an older CLI still needs the skill paths added, since they were
  // never ignored before this version at all — including a `--skills-dir` override, which is
  // per-run and never part of the static block above.
  const missingSkillDirs = allSkillPaths.filter((d) => !content.includes(d));
  if (missingSkillDirs.length > 0) {
    content += missingSkillDirs.map((d) => `${d}\n`).join('');
  }

  // Same idea for `.pointer/projects/`: a repo whose block predates multi-project support has the
  // stack.json/config.json negations but not this one, and the migration branch above never runs
  // again once stack.json is already re-included.
  if (!content.includes('!.pointer/projects/')) {
    content += '!.pointer/projects/\n';
  }

  if (content !== before) {
    await fs.writeFile(file, content, 'utf8');
  }
}
