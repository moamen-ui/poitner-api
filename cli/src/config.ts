import { promises as fs } from 'node:fs';
import { join, dirname } from 'node:path';

export interface PointerConfig {
  project?: string;
  environment?: string;
  server?: string;
  aiTool?: string;
  skillsDir?: string;
  cliVersion?: string;
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

export async function upsertGitignore(cwd: string): Promise<void> {
  const file = join(cwd, '.gitignore');
  let content = await fs.readFile(file, 'utf8').catch(() => '');
  const entry = '\n# Pointer\n.pointer/credentials.env\n';
  if (!content.includes('.pointer/credentials.env')) {
    content += entry;
    await fs.writeFile(file, content, 'utf8');
  }
}
