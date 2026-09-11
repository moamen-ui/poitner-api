import { promises as fs } from 'node:fs';
import { join } from 'node:path';
import { AppType } from '../detect.js';

export async function injectWidget(cwd: string, type: AppType, server: string, projectKey: string, env: string): Promise<boolean> {
  const url = `${server.replace(/\/$/, '')}/embed.js?project=${encodeURIComponent(projectKey)}&environment=${encodeURIComponent(env)}`;
  
  if (type === 'vite' || type === 'static') {
    return injectHtml(cwd, url);
  } else if (type === 'nextjs') {
    return injectNext(cwd, url);
  }
  
  return false;
}

async function injectHtml(cwd: string, url: string): Promise<boolean> {
  const file = join(cwd, 'index.html');
  try {
    let content = await fs.readFile(file, 'utf8');
    const script = `<script src="${url}"></script>\n`;
    if (content.includes('embed.js')) return true; // Already injected
    
    if (content.includes('</head>')) {
      content = content.replace('</head>', `${script}</head>`);
    } else {
      content += `\n${script}`;
    }
    await fs.writeFile(file, content, 'utf8');
    return true;
  } catch (err: any) {
    if (err.code === 'ENOENT') {
      const content = `<!DOCTYPE html>\n<html>\n<head>\n<script src="${url}"></script>\n</head>\n<body>\n</body>\n</html>`;
      await fs.writeFile(file, content, 'utf8');
      return true;
    }
    return false;
  }
}

async function injectNext(cwd: string, url: string): Promise<boolean> {
  // Try app/layout.tsx
  const file = join(cwd, 'app', 'layout.tsx');
  try {
    let content = await fs.readFile(file, 'utf8');
    if (content.includes('embed.js')) return true;
    
    const importScript = `import Script from 'next/script';\n`;
    const script = `<Script src="${url}" strategy="beforeInteractive" />`;
    
    if (!content.includes('next/script')) {
      content = importScript + content;
    }
    
    if (content.includes('</body>')) {
      content = content.replace('</body>', `${script}\n      </body>`);
    } else {
      content += `\n${script}`;
    }
    await fs.writeFile(file, content, 'utf8');
    return true;
  } catch (err: any) {
    // Just a basic fallback or fail if layout doesn't exist
    return false;
  }
}
