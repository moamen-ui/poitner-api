import { promises as fs } from 'node:fs';
import { join } from 'node:path';
import { injectStatic } from './static.js';

export async function injectVite(
    cwd: string,
    cfg: {
        server: string;
        key: string;
        environment: string;
        /** Forwarded to the injector — see injectStatic's `pin`. */
        pin?: { version: string; integrity: string } | null;
        /**
         * `--environment` was given, so the install is deliberately pinned to one environment and
         * VITE_POINTER_ENV is written for the snippet's `environment` attribute. Otherwise the
         * environment is resolved by the server from the page origin, nothing in the app carries
         * it, and any VITE_POINTER_ENV left by an older init is removed.
         */
        environmentPinned?: boolean;
    },
    htmlPath?: string,
): Promise<string[]> {
    const modified = [];
    
    // Inject HTML
    const p = await injectStatic(cwd, htmlPath, { ...cfg, envGuarded: true });
    modified.push('index.html');
    
    // Upsert .env
    const envPath = join(cwd, '.env');
    let envContent = await fs.readFile(envPath, 'utf8').catch(() => '');
    
    // No VITE_POINTER_ENABLED: the snippet mounts when VITE_POINTER_SERVER is a URL and not
    // otherwise, so leaving it empty in a build is the off switch. Same for the environment (see
    // `environmentPinned`). Stale lines from older inits are removed below.
    const envVars: Record<string, string> = {
        VITE_POINTER_SERVER: cfg.server,
        VITE_POINTER_PROJECT: cfg.key,
    };
    if (cfg.environmentPinned) envVars.VITE_POINTER_ENV = cfg.environment;
    
    for (const [k, v] of Object.entries(envVars)) {
        const re = new RegExp(`^${k}=.*$`, 'm');
        if (re.test(envContent)) {
            envContent = envContent.replace(re, `${k}=${v}`);
        } else {
            envContent += `${envContent.endsWith('\n') || envContent === '' ? '' : '\n'}${k}=${v}\n`;
        }
    }
    envContent = dropStaleLines(envContent, cfg.environmentPinned);
    await fs.writeFile(envPath, envContent, 'utf8');
    modified.push('.env');
    
    // Upsert .env.example if exists
    for (const example of ['.env.example', '.env.sample']) {
        const exPath = join(cwd, example);
        let exContent = await fs.readFile(exPath, 'utf8').catch(() => null);
        if (exContent !== null) {
            const exVars: Record<string, string> = {};
            for (const k of Object.keys(envVars)) exVars[k] = '';
            for (const [k, v] of Object.entries(exVars)) {
                const re = new RegExp(`^${k}=.*$`, 'm');
                if (re.test(exContent)) {
                    exContent = exContent.replace(re, `${k}=${v}`);
                } else {
                    exContent += `${exContent.endsWith('\n') || exContent === '' ? '' : '\n'}${k}=${v}\n`;
                }
            }
            exContent = dropStaleLines(exContent, cfg.environmentPinned);
            await fs.writeFile(exPath, exContent, 'utf8');
            modified.push(example);
        }
    }
    
    return modified;
}

/**
 * Removes lines an older init wrote that nothing reads any more: `VITE_POINTER_ENABLED` (the
 * snippet keys off VITE_POINTER_SERVER now) and, unless the install is pinned, `VITE_POINTER_ENV`.
 */
function dropStaleLines(content: string, environmentPinned?: boolean): string {
    let out = content.replace(/^VITE_POINTER_ENABLED=.*\n?/m, '');
    if (!environmentPinned) out = out.replace(/^VITE_POINTER_ENV=.*\n?/m, '');
    return out;
}
