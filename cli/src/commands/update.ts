import { promises as fs } from 'node:fs';
import { dirname, join } from 'node:path';
import { readConfig } from '../config.js';
import { api } from '../api.js';
import { readStamp } from '../lib/skill-stamp.js';
import { skillFilesFor } from '../lib/skill-paths.js';
import type { MetaResponse } from '../checks.js';

export interface UpdateOptions {
  server?: string;
  check?: boolean;
}

/** Which served file backs each installed path. */
function sourceFor(path: string): string | null {
  if (path.endsWith('pointer.sh')) return '/pointer.sh';
  if (path.includes('pointer-init')) return '/pointer-init.md';
  if (path.includes('pointer-feedback')) return '/skill.md';
  return null;
}

/**
 * Refreshes the served skills and pointer.sh in place.
 *
 * A skill file installed months ago is frozen prose describing an API that has moved on — the
 * problem the version stamp exists to make visible and this command exists to fix.
 *
 * Symlinks are preserved deliberately: installSkills points `.agents/<name>/SKILL.md` at the
 * tool-specific copy for several tools, so writing through the link keeps that arrangement intact,
 * whereas replacing the file would break it.
 */
export async function updateCommand(cwd: string, options: UpdateOptions): Promise<number> {
  const config = await readConfig(cwd);
  const server = (options.server || config.server || '').replace(/\/$/, '');

  if (!server) {
    console.error('No server configured — run `npx -y pointer-feedback init` first.');
    return 1;
  }

  let served: string | null = null;
  try {
    const meta = await api<MetaResponse>(server, '/api/meta');
    served = meta?.skillVersion ?? null;
  } catch {
    console.error(`Could not reach ${server} — check the URL.`);
    return 1;
  }

  const files = skillFilesFor(config);
  const stale: { path: string; installed: string | null }[] = [];

  for (const rel of files) {
    const abs = join(cwd, rel);
    try {
      await fs.access(abs);
    } catch {
      continue; // not installed for this tool — nothing to refresh
    }
    const installed = await readStamp(abs);
    if (installed !== served) stale.push({ path: rel, installed });
  }

  if (stale.length === 0) {
    console.log(`Up to date (skill version ${served ?? 'unknown'}).`);
    return 0;
  }

  if (options.check) {
    console.log(`${stale.length} file${stale.length === 1 ? '' : 's'} out of date (server ${served ?? 'unknown'}):`);
    for (const f of stale) console.log(`  ${f.path} (${f.installed ?? 'unstamped'})`);
    return 0;
  }

  let updated = 0;
  const from = stale[0]?.installed ?? 'unstamped';

  for (const f of stale) {
    const source = sourceFor(f.path);
    if (!source) continue;
    try {
      const res = await fetch(`${server}${source}`);
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const body = await res.text();

      const abs = join(cwd, f.path);
      await fs.mkdir(dirname(abs), { recursive: true });
      // Writing through the path (not unlink+create) is what preserves a symlink.
      await fs.writeFile(abs, body, 'utf8');
      if (abs.endsWith('.sh')) await fs.chmod(abs, 0o755).catch(() => {});
      updated++;
    } catch (err: any) {
      console.error(`  failed to update ${f.path}: ${err?.message ?? err}`);
    }
  }

  console.log(`updated ${updated} file${updated === 1 ? '' : 's'} (skill version ${from} → ${served ?? 'unknown'})`);
  return updated === stale.length ? 0 : 1;
}
