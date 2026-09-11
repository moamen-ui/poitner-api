import { promises as fs } from 'node:fs';
import { join } from 'node:path';
import { fetchApi } from './api.js';

export async function fetchSkill(server: string): Promise<string> {
  const url = `${server.replace(/\/$/, '')}/skill.md`;
  const res = await fetch(url);
  if (!res.ok) {
    throw new Error(`Failed to fetch skill: ${res.statusText}`);
  }
  return res.text();
}

export async function injectSkill(cwd: string, server: string): Promise<void> {
  const skillContent = await fetchSkill(server);
  const dir = join(cwd, '.gemini', 'config', 'skills', 'pointer');
  await fs.mkdir(dir, { recursive: true });
  await fs.writeFile(join(dir, 'SKILL.md'), skillContent, 'utf8');
}
