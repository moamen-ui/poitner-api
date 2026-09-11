import { promises as fs } from 'node:fs';
import { join } from 'node:path';
import { injectStatic } from './static.js';

export async function injectVite(cwd: string, cfg: { server: string, key: string, environment: string }, htmlPath?: string): Promise<string[]> {
    const modified = [];
    
    // Inject HTML
    const p = await injectStatic(cwd, htmlPath, cfg);
    modified.push('index.html');
    
    // Upsert .env
    const envPath = join(cwd, '.env');
    let envContent = await fs.readFile(envPath, 'utf8').catch(() => '');
    
    const envVars = {
        VITE_POINTER_ENABLED: 'true',
        VITE_POINTER_SERVER: cfg.server,
        VITE_POINTER_PROJECT: cfg.key,
        VITE_POINTER_ENV: cfg.environment
    };
    
    for (const [k, v] of Object.entries(envVars)) {
        const re = new RegExp(`^${k}=.*$`, 'm');
        if (re.test(envContent)) {
            envContent = envContent.replace(re, `${k}=${v}`);
        } else {
            envContent += `${envContent.endsWith('\n') || envContent === '' ? '' : '\n'}${k}=${v}\n`;
        }
    }
    await fs.writeFile(envPath, envContent, 'utf8');
    modified.push('.env');
    
    // Upsert .env.example if exists
    for (const example of ['.env.example', '.env.sample']) {
        const exPath = join(cwd, example);
        let exContent = await fs.readFile(exPath, 'utf8').catch(() => null);
        if (exContent !== null) {
            const exVars = { ...envVars, VITE_POINTER_ENABLED: 'false' };
            for (const k of ['VITE_POINTER_SERVER', 'VITE_POINTER_PROJECT', 'VITE_POINTER_ENV']) {
                exVars[k as keyof typeof exVars] = '';
            }
            for (const [k, v] of Object.entries(exVars)) {
                const re = new RegExp(`^${k}=.*$`, 'm');
                if (re.test(exContent)) {
                    exContent = exContent.replace(re, `${k}=${v}`);
                } else {
                    exContent += `${exContent.endsWith('\n') || exContent === '' ? '' : '\n'}${k}=${v}\n`;
                }
            }
            await fs.writeFile(exPath, exContent, 'utf8');
            modified.push(example);
        }
    }
    
    return modified;
}
