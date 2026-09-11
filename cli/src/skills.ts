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

    if (aiTool === 'claude-code') {
        await writeOrLink('.claude/skills/pointer-init/SKILL.md', 'pointer-init', false);
        await writeOrLink('.claude/skills/pointer-feedback/SKILL.md', 'pointer-feedback', false);
    } else if (aiTool === 'cursor') {
        await writeOrLink('.cursor/rules/pointer-init.md', 'pointer-init', true);
        await writeOrLink('.cursor/rules/pointer-feedback.md', 'pointer-feedback', true);
    } else if (aiTool === 'windsurf') {
        await writeOrLink('.windsurf/rules/pointer-init.md', 'pointer-init', true);
        await writeOrLink('.windsurf/rules/pointer-feedback.md', 'pointer-feedback', true);
    } else {
        await writeOrLink('.agents/pointer-init/SKILL.md', 'pointer-init', false);
        await writeOrLink('.agents/pointer-feedback/SKILL.md', 'pointer-feedback', false);
    }
    
    return files;
}
