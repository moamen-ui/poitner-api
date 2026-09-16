import { promises as fs } from 'node:fs';
import { join, dirname } from 'node:path';

async function download(url: string, dest: string, chmod = false) {
    const res = await fetch(url);
    if (!res.ok) throw new Error(`Failed to fetch ${url}: ${res.status}`);
    const txt = await res.text();
    await fs.mkdir(dirname(dest), { recursive: true });
    await fs.writeFile(dest, txt, 'utf8');
    if (chmod) {
        await fs.chmod(dest, 0o755).catch(() => {});
    }
}

async function makeSymlink(target: string, path: string) {
    await fs.mkdir(dirname(path), { recursive: true });
    try {
        await fs.symlink(target, path);
    } catch (err: any) {
        if (err.code === 'EPERM' && process.platform === 'win32') {
            await fs.copyFile(target, path);
        } else if (err.code !== 'EEXIST') {
            throw err;
        }
    }
}

/**
 * Where each AI tool's two skill files live, relative to the project root.
 *
 * Exported because `doctor` must check exactly the paths `installSkills` writes. When these lived
 * inline in the install routine, the only way to verify an install was to re-derive the layout —
 * and a second copy of a path table drifts.
 *
 * `other` and `antigravity` both use `.agents/skills/<name>/SKILL.md` — the Agent Skills layout
 * read by Antigravity, Codex and other non-Claude, non-Cursor, non-Windsurf tools. Claude Code,
 * Cursor and Windsurf each have their own native location as their PRIMARY path, and additionally
 * get a symlink at this same `.agents/skills/...` path (see `writeOrLink`) so any tool that reads
 * the standard location finds the skill regardless of which tool actually installed it.
 */
export const SKILL_FILES: Record<string, string[]> = {
  'claude-code': ['.claude/skills/pointer-init/SKILL.md', '.claude/skills/pointer-feedback/SKILL.md'],
  cursor: ['.cursor/rules/pointer-init.md', '.cursor/rules/pointer-feedback.md'],
  windsurf: ['.windsurf/rules/pointer-init.md', '.windsurf/rules/pointer-feedback.md'],
  other: ['.agents/skills/pointer-init/SKILL.md', '.agents/skills/pointer-feedback/SKILL.md'],
  antigravity: ['.agents/skills/pointer-init/SKILL.md', '.agents/skills/pointer-feedback/SKILL.md'],
};

/**
 * Removes the pre-2026-09-16 `.agents/pointer-init/SKILL.md` / `.agents/pointer-feedback/SKILL.md`
 * layout (file or symlink), plus its now-empty parent directories, so a repo never ends up
 * carrying both that location and the current `.agents/skills/...` one. Safe to call
 * unconditionally — a repo that never had the old layout simply has nothing to remove.
 */
async function removeLegacyAgentsLayout(cwd: string): Promise<void> {
    for (const name of ['pointer-init', 'pointer-feedback']) {
        const filePath = join(cwd, '.agents', name, 'SKILL.md');
        const dirPath = join(cwd, '.agents', name);
        try {
            await fs.rm(filePath, { force: true });
        } catch {
            // Best-effort: a permissions issue here must not block installing the current layout.
        }
        try {
            const remaining = await fs.readdir(dirPath);
            if (remaining.length === 0) await fs.rmdir(dirPath);
        } catch {
            // Directory missing, non-empty, or already gone — nothing to do.
        }
    }
}

export async function installSkills(server: string, aiTool: string, cwd: string, overrideDir?: string): Promise<string[]> {
    const files: string[] = [];
    server = server.replace(/\/$/, '');

    await removeLegacyAgentsLayout(cwd);

    // Download pointer.sh
    const pointerSh = join(cwd, '.pointer', 'pointer.sh');
    await download(`${server}/pointer.sh`, pointerSh, true);
    files.push('.pointer/pointer.sh');

    async function writeOrLink(primaryPath: string, skillName: string) {
        const url = skillName === 'pointer-init' ? `${server}/pointer-init.md` : `${server}/skill.md`;

        const finalPath = overrideDir ? join(cwd, overrideDir, skillName, 'SKILL.md') : join(cwd, primaryPath);
        await download(url, finalPath);
        files.push(overrideDir ? join(overrideDir, skillName, 'SKILL.md') : primaryPath);

        if (!overrideDir && (aiTool === 'claude-code' || aiTool === 'cursor' || aiTool === 'windsurf')) {
            // The standard Agent Skills location, three levels deep (.agents/skills/<name>/SKILL.md)
            // — one deeper than the pre-2026-09-16 layout (.agents/<name>/SKILL.md), so the relative
            // symlink target needs one more `..` to still land back at the repo root.
            const symDest = join(cwd, '.agents', 'skills', skillName, 'SKILL.md');
            await makeSymlink(join('..', '..', '..', primaryPath), symDest);
            files.push(`.agents/skills/${skillName}/SKILL.md`);
        }
    }

    const layout = SKILL_FILES[aiTool] ?? SKILL_FILES.other;
    await writeOrLink(layout[0], 'pointer-init');
    await writeOrLink(layout[1], 'pointer-feedback');

    return files;
}
