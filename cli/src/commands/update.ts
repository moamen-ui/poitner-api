import { promises as fs } from 'node:fs';
import { dirname, join } from 'node:path';
import { readConfig, removeLegacyRepoFiles } from '../config.js';
import { api } from '../api.js';
import { readStamp } from '../lib/skill-stamp.js';
import { skillFilesFor } from '../lib/skill-paths.js';
import { installSkills } from '../skills.js';
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
 * Refreshes the served skills and pointer.sh in place, and installs them when they are missing
 * entirely.
 *
 * A skill file installed months ago is frozen prose describing an API that has moved on — the
 * problem the version stamp exists to make visible and this command exists to fix. Since skills
 * and pointer.sh are gitignored (derived, per-machine state — see `config.ts`'s
 * `upsertGitignore`), a fresh clone of a repo that already has Pointer set up has NEITHER: there
 * is nothing to refresh, only something to install, which used to be silently skipped here.
 *
 * Symlinks are preserved deliberately: installSkills points `.agents/<name>/SKILL.md` at the
 * tool-specific copy for several tools, so writing through the link keeps that arrangement intact,
 * whereas replacing the file would break it.
 */
export async function updateCommand(cwd: string, options: UpdateOptions): Promise<number> {
  // A repo installed by an older CLI may still have files that version wrote and this one no
  // longer does — see `removeLegacyRepoFiles`. Unconditional: this must happen whether or not the
  // rest of the command finds anything to update.
  const removedLegacyFiles = await removeLegacyRepoFiles(cwd);
  for (const f of removedLegacyFiles) console.log(`\x1b[2mremoved legacy ${f}\x1b[0m`);

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
  const missing: string[] = [];
  const stale: { path: string; installed: string | null }[] = [];

  for (const rel of files) {
    const abs = join(cwd, rel);
    try {
      await fs.access(abs);
    } catch {
      missing.push(rel);
      continue;
    }
    const installed = await readStamp(abs);
    if (installed !== served) stale.push({ path: rel, installed });
  }

  if (missing.length === 0 && stale.length === 0) {
    console.log(`Up to date (skill version ${served ?? 'unknown'}).`);
    return 0;
  }

  if (options.check) {
    if (missing.length > 0) {
      console.log(`${missing.length} file${missing.length === 1 ? '' : 's'} not installed:`);
      for (const f of missing) console.log(`  ${f}`);
    }
    if (stale.length > 0) {
      console.log(`${stale.length} file${stale.length === 1 ? '' : 's'} out of date (server ${served ?? 'unknown'}):`);
      for (const f of stale) console.log(`  ${f.path} (${f.installed ?? 'unstamped'})`);
    }
    return 0;
  }

  let installedCount = 0;
  if (missing.length > 0) {
    if (!config.aiTool) {
      // No tool recorded at all (a config written before `aiTool` existed, and never re-run
      // through `init`): there is nothing to tell `installSkills` to install FOR.
      console.error('No AI tool configured — run `npx -y pointer-feedback init` to record one, then `update` again.');
    } else {
      try {
        await installSkills(server, config.aiTool, cwd, config.skillsDir);
        installedCount = missing.length;
        console.log(`installed ${installedCount} file${installedCount === 1 ? '' : 's'}: ${missing.join(', ')}`);
      } catch (err: any) {
        console.error(`  failed to install missing skills: ${err?.message ?? err}`);
      }
    }
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

  if (stale.length > 0) {
    console.log(`updated ${updated} file${updated === 1 ? '' : 's'} (skill version ${from} → ${served ?? 'unknown'})`);
  }

  const installedOk = missing.length === 0 || installedCount === missing.length;
  const updatedOk = updated === stale.length;
  return installedOk && updatedOk ? 0 : 1;
}
