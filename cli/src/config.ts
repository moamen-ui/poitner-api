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
   * Where the widget was actually mounted, relative to the repo root.
   *
   * Recorded because `doctor` otherwise guesses from a fixed list of conventional paths
   * (`index.html`, `src/index.html`, …) and reports "Widget not found" for a monorepo app it was
   * explicitly told about — in the same run that just said it injected there.
   */
  htmlPath?: string;
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

export async function writeCredentials(cwd: string, token: string): Promise<void> {
  const file = join(cwd, CREDENTIALS_FILE);
  await fs.mkdir(dirname(file), { recursive: true });
  await fs.writeFile(file, `POINTER_API_KEY=${token}\n`, { encoding: 'utf8', mode: 0o600 });
  const exampleFile = join(cwd, '.pointer/credentials.env.example');
  await fs.writeFile(exampleFile, `POINTER_API_KEY=\n`, { encoding: 'utf8' });
}

export async function upsertGitignore(cwd: string, productName = 'Feedback tool'): Promise<void> {
  const file = join(cwd, '.gitignore');
  let content = await fs.readFile(file, 'utf8').catch(() => '');
  // The frozen on-disk contract (R1-01): ignore the whole directory, then re-include the files a
  // team is meant to commit. Ignoring only credentials.env left .pointer/.token_cache — a cached
  // JWT — and manifest.json committable, which is how a token ends up in someone's git history.
  const entry = [
    '',
    `# ${productName}`,
    // `.pointer/*`, not `.pointer/`. Git does not descend into an excluded DIRECTORY, so the
    // directory form makes every `!` line below inert and the four files this block exists to keep
    // committable are silently ignored instead.
    '.pointer/*',
    '!.pointer/credentials.env.example',
    '!.pointer/stack.json',
    '!.pointer/pointer.sh',
    '!.pointer/config.json',
    '',
  ].join('\n');

  const before = content;

  // Migrate the directory form an earlier version wrote. Left in place it still wins: any
  // exclusion of the directory stops git looking inside it, so the negations never get a chance.
  content = content.replace(/^\.pointer\/$/m, '.pointer/*');

  // Replace the narrower rule if an early version of this CLI wrote it. Matches the comment those
  // versions emitted (always the literal product name).
  content = content.replace(/\n?# [^\n]*\n\.pointer\/credentials\.env\n/, '');

  // The negations are what make the migration useful — a .gitignore that only ever said
  // `.pointer/` has none of them, so rewriting that one line would leave stack.json still ignored.
  // Keying on stack.json rather than on the exclusion line is what catches that case.
  if (!/^!\.pointer\/stack\.json$/m.test(content)) {
    content += entry;
  }

  if (content !== before) {
    await fs.writeFile(file, content, 'utf8');
  }
}
