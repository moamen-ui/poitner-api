import { promises as fs } from 'node:fs';
import { join } from 'node:path';

export interface PointerConfig {
  projectKey?: string;
  environment?: string;
  server?: string;
}

const CONFIG_FILE = '.pointerrc.json';

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
  const data = JSON.stringify(config, null, 2) + '\n';
  await fs.writeFile(file, data, 'utf8');
}
