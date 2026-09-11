import { promises as fs } from 'node:fs';
import { join } from 'node:path';

export async function injectStatic(cwd: string, htmlPath: string | undefined, cfg: { server: string, key: string, environment: string }): Promise<string> {
    const p = htmlPath || join(cwd, 'index.html');
    let content = await fs.readFile(p, 'utf8').catch(() => '');
    if (!content) throw new Error(`HTML file not found at ${p}`);
    
    const block = `<!-- pointer-feedback:start -->
<script src="${cfg.server}/pointer.js" defer></script>
<pointer-feedback project="${cfg.key}" server="${cfg.server}" environment="${cfg.environment}" source-attr="data-component-source"></pointer-feedback>
<!-- pointer-feedback:end -->`;

    const re = /<!-- pointer-feedback:start -->[\s\S]*?<!-- pointer-feedback:end -->/;
    if (re.test(content)) {
        content = content.replace(re, block);
    } else if (content.toLowerCase().includes('</body>')) {
        content = content.replace(/(<\/body>)/i, `${block}\n$1`);
    } else {
        content += `\n${block}`;
    }
    
    await fs.writeFile(p, content, 'utf8');
    return p;
}
