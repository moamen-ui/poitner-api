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
 */
export const SKILL_FILES: Record<string, string[]> = {
  'claude-code': ['.claude/skills/pointer-init/SKILL.md', '.claude/skills/pointer-feedback/SKILL.md'],
  cursor: ['.cursor/rules/pointer-init.md', '.cursor/rules/pointer-feedback.md'],
  windsurf: ['.windsurf/rules/pointer-init.md', '.windsurf/rules/pointer-feedback.md'],
  other: ['.agents/pointer-init/SKILL.md', '.agents/pointer-feedback/SKILL.md'],
};

export async function installSkills(server: string, aiTool: string, cwd: string, overrideDir?: string): Promise<string[]> {
    const files: string[] = [];
    server = server.replace(/\/$/, '');
    
    // Download pointer.sh
    const pointerSh = join(cwd, '.pointer', 'pointer.sh');
    await download(`${server}/pointer.sh`, pointerSh, true);
    files.push('.pointer/pointer.sh');
    
    const agentsDir = join(cwd, '.agents');
    
    async function writeOrLink(primaryPath: string, skillName: string, isMd: boolean) {
        const url = skillName === 'pointer-init' ? `${server}/pointer-init.md` : `${server}/skill.md`;
        
        const finalPath = overrideDir ? join(cwd, overrideDir, skillName, 'SKILL.md') : join(cwd, primaryPath);
        await download(url, finalPath);
        files.push(overrideDir ? join(overrideDir, skillName, 'SKILL.md') : primaryPath);
        
        if (!overrideDir && (aiTool === 'claude-code' || aiTool === 'cursor' || aiTool === 'windsurf')) {
            const symDest = join(cwd, '.agents', skillName, 'SKILL.md');
            await makeSymlink(join('..', '..', primaryPath), symDest);
            files.push(`.agents/${skillName}/SKILL.md`);
        }
    }

    const layout = SKILL_FILES[aiTool] ?? SKILL_FILES.other;
    const isMd = aiTool === 'cursor' || aiTool === 'windsurf';
    await writeOrLink(layout[0], 'pointer-init', isMd);
    await writeOrLink(layout[1], 'pointer-feedback', isMd);
    
    return files;
}
