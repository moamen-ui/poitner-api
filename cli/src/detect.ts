import { promises as fs } from 'node:fs';
import { join } from 'node:path';

export type AppType = 'nextjs' | 'vite' | 'static' | 'unknown';

export interface DetectionResult {
  type: AppType;
  port?: number;
}

export async function detectApp(cwd: string): Promise<DetectionResult> {
  try {
    const pkgJsonPath = join(cwd, 'package.json');
    const content = await fs.readFile(pkgJsonPath, 'utf8');
    const pkg = JSON.parse(content);
    const deps = { ...pkg.dependencies, ...pkg.devDependencies };
    
    if (deps.next) {
      let port = 3000;
      if (pkg.scripts && pkg.scripts.dev) {
        const m = pkg.scripts.dev.match(/-p\s+(\d+)/);
        if (m) port = parseInt(m[1], 10);
      }
      return { type: 'nextjs', port };
    }
    
    if (deps.vite) {
      let port = 5173;
      if (pkg.scripts && pkg.scripts.dev) {
        const m = pkg.scripts.dev.match(/--port\s+(\d+)/);
        if (m) port = parseInt(m[1], 10);
      }
      return { type: 'vite', port };
    }
    
    return { type: 'static', port: 8080 };
  } catch (err: any) {
    if (err.code !== 'ENOENT') throw err;
    // No package.json, assume static
    return { type: 'static', port: 8080 };
  }
}
