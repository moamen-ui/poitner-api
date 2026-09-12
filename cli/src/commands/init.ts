import { ask, select } from '../prompt.js';
import { BUILD_DEFAULT_SERVER } from '../build-constants.js';
import { readConfig, writeConfig, writeCredentials, upsertGitignore } from '../config.js';
import { detectStack, detectAppUrl, extractTokens } from '../detect.js';
import { injectVite, injectStatic } from '../inject/index.js';
import { installSkills } from '../skills.js';
import { getBranding } from '../branding.js';
import { api, ApiError } from '../api.js';
import { postEvent } from '../events.js';
import { runInitChecks } from '../checks.js';
import { promises as fs } from 'node:fs';
import { join } from 'node:path';

export async function initCommand(cwd: string, options: Record<string, string | boolean> = {}) {
    const isYes = options['yes'] || options['json'];
    const isJson = options['json'];

    if (isYes) {
        if (!options['key']) {
            console.error("Missing flag: --key is required with --yes");
            process.exit(2);
        }
        if (!options['project'] && !options['create']) {
            console.error("Missing flag: --project or --create is required with --yes");
            process.exit(2);
        }
    }

    const config: any = await readConfig(cwd).catch(() => ({}));

    let server = options['server'] || config.server || process.env.POINTER_SERVER || BUILD_DEFAULT_SERVER;
    
    if (!isYes && !options['server'] && !config.server) {
        server = await ask('Server URL', { default: server as string });
    }

    const branding = await getBranding(server as string);
    const product = branding.productName || 'Pointer';

    let key = options['key'] as string;
    let me: any = null;
    let token: string | undefined;
    
    if (isYes) {
        try {
            // An API key is not a JWT: it must be exchanged for one. Sending it as a Bearer token
            // to /api/auth/me fails for every valid key — which unit tests against a permissive
            // stub did not catch, but a real server does immediately.
            const login = await api<any>(server as string, '/api/auth/login-with-key', {
                method: 'POST',
                body: { apiKey: key },
            });
            if (login?.status !== 'ok' || !login?.token) throw new Error(login?.status || 'invalid');
            token = login.token;
            me = login.user ?? (await api(server as string, '/api/auth/me', { token }));
        } catch (err: any) {
            if (isJson) {
                console.log(JSON.stringify({ ok: false, error: { code: 3, message: "Invalid API key" } }));
            } else {
                console.error("Invalid API key.");
            }
            process.exit(3);
        }
    } else {
        let attempts = 0;
        while (!me) {
            if (!key) {
                key = await ask(`API key (from ${product} -> profile -> API key; input hidden)`, { secret: true });
            }
            try {
                // Same exchange as the --yes branch: key -> token -> profile.
                const login = await api<any>(server as string, '/api/auth/login-with-key', {
                    method: 'POST',
                    body: { apiKey: key },
                });
                if (login?.status !== 'ok' || !login?.token) throw new Error(login?.status || 'invalid');
                token = login.token;
                me = login.user ?? (await api(server as string, '/api/auth/me', { token }));
            } catch (err: any) {
                attempts++;
                if (attempts >= 3) {
                    console.error("Invalid API key.");
                    process.exit(3);
                }
                console.error("Invalid API key.");
                key = '';
            }
        }
        console.log(`✔ Signed in as ${me.displayName} (${me.roleName || 'User'})`);
    }

    await writeCredentials(cwd, key);
    await upsertGitignore(cwd);

    let project = options['project'] as string;
    let create = options['create'] as string;
    let finalProjectKey = project || '';
    let projectName = create || finalProjectKey;
    let created = false;

    if (!isYes && !project && !create) {
        const projects = await api<any[]>(server as string, '/api/admin/projects', { token: key }).catch(() => []);
        const createOpt = '＋ Create a new project…';
        const choices = projects.map(p => `${p.name}  (${p.key})`).concat(createOpt);
        
        let choice = createOpt;
        if (projects.length > 0) {
            choice = await select('Which project is this app?', choices);
        }
        
        if (choice === createOpt) {
            const name = await ask('Project name');
            let derivedKey = name.toLowerCase().replace(/[^a-z0-9-]/g, '-').replace(/-+/g, '-').replace(/^-|-$/g, '');
            finalProjectKey = await ask('Project key', { default: derivedKey, validate: (v) => /^[a-z0-9-]+$/.test(v) ? undefined : 'Must match ^[a-z0-9-]+$' });
            projectName = name;
            created = true;
        } else {
            const match = choice.match(/\((.*?)\)$/);
            if (match) finalProjectKey = match[1];
        }
    } else if (create) {
        created = true;
        let derivedKey = create.toLowerCase().replace(/[^a-z0-9-]/g, '-').replace(/-+/g, '-').replace(/^-|-$/g, '');
        finalProjectKey = project || derivedKey;
        projectName = create;
    }

    if (created) {
        try {
            await api(server as string, '/api/admin/projects', { method: 'POST', body: { key: finalProjectKey, name: projectName }, token: key });
        } catch (err: any) {
            if (err instanceof ApiError && err.code === 409) {
                console.error("Key already exists, choose another.");
                process.exit(1);
            } else if (err instanceof ApiError && err.code === 403) {
                console.error("This account cannot create projects.");
                process.exit(3);
            } else if (err instanceof ApiError && err.code === 400) {
                console.error(err.message);
                process.exit(1);
            }
        }
    }

    let env = options['environment'] as string || 'local';
    if (!isYes && !options['environment']) {
        env = await select('Environment', ['local', 'staging', 'production'], env);
    }

    const appInfo = await detectStack(cwd);
    
    let appUrl = options['app-url'] as string | undefined;
    let noAppUrl = options['no-app-url'] as boolean;
    let source = '';
    
    if (!noAppUrl && !appUrl) {
        const detected = await detectAppUrl(cwd, appInfo.kind, env);
        source = detected.source;
        if (!isYes) {
            const displayDefault = detected.url ? detected.url : '';
            const ans = await ask(`Where does this app run in ${env}?`, { default: displayDefault });
            appUrl = ans || undefined;
        } else {
            appUrl = detected.url || undefined;
        }
    }

    // Set Environment URL if needed (only if env !== 'local')
    if (appUrl && env !== 'local') {
        // According to spec, would call environment URL endpoint but R1-09 handles that.
    }

    let tool = options['tool'] as string;
    if (!tool) {
        // auto-detect
        if (process.env.CLAUDECODE || process.env.CLAUDE_CODE_ENTRYPOINT) tool = 'claude-code';
        else if (process.env.ANTIGRAVITY_AGENT || process.env.GEMINI_CLI) tool = 'antigravity';
        else if (process.env.TERM_PROGRAM && process.env.TERM_PROGRAM.includes('Cursor')) tool = 'cursor';
        else if (process.env.WINDSURF) tool = 'windsurf';
        else if (process.env.OPENCODE) tool = 'opencode';
        else tool = isYes ? 'other' : 'claude-code';
        
        if (!isYes && !options['tool']) {
            tool = await select('AI tool', ['claude-code', 'cursor', 'windsurf', 'opencode', 'antigravity', 'other'], tool);
        }
    }
    
    if (!isJson) console.log(`Detecting your stack... -> ${appInfo.kind} (${appInfo.evidence.join(', ')})`);

    let injected = false;
    let routedToSkill = false;
    let filesMod: string[] = [];
    
    if (!options['no-inject']) {
        if (appInfo.kind === 'vite') {
            filesMod = await injectVite(cwd, { server: server as string, key: finalProjectKey, environment: env }, options['html'] as string);
            injected = true;
            if (!isJson) console.log(`Injected widget into ${filesMod.join(', ')}`);
        } else if (appInfo.kind === 'static') {
            const htmlPath = await injectStatic(cwd, options['html'] as string, { server: server as string, key: finalProjectKey, environment: env });
            filesMod = [htmlPath];
            injected = true;
            if (!isJson) console.log(`Injected widget into ${htmlPath}`);
        } else if (appInfo.kind !== 'unknown') {
            routedToSkill = true;
            if (!isJson) {
                console.log(`ℹ ${appInfo.kind} detected — automatic injection isn't supported for this stack yet.
  The pointer-init skill was installed for ${tool}. Run it and it will mount the widget for you:
    claude -> /pointer-init (or @pointer-init for cursor)
  Config is already saved in .pointer/config.json, so the skill won't ask for the key or project again.`);
            }
        }
    }

    if (!options['no-skills']) {
        if (!isJson) console.log('Installing AI skills');
        const skillsDir = options['skills-dir'] as string;
        const installed = await installSkills(server as string, tool, cwd, skillsDir);
        filesMod.push(...installed);
    }

    const pkgStr = await fs.readFile(join(cwd, 'package.json'), 'utf8').catch(() => '{}');
    const tokens = extractTokens(JSON.parse(pkgStr));
    const stackMeta = { frontend: tokens.frontend, backend: tokens.backend, aiTool: tool };
    
    try {
        await api(server as string, `/api/projects/${finalProjectKey}/stack`, { method: 'POST', body: stackMeta, token: key });
        await fs.mkdir(join(cwd, '.pointer'), { recursive: true });
        await fs.writeFile(join(cwd, '.pointer/stack.json'), JSON.stringify(stackMeta, null, 2), 'utf8');
    } catch (e: any) {
        if (!isJson) console.log(`⚠ Stack not registered (${e.code || 500})`);
    }

    await writeConfig(cwd, { server: server as string, project: finalProjectKey, environment: env, aiTool: tool, skillsDir: options['skills-dir'] as string, cliVersion: '0.1.0' });
    filesMod.push('.pointer/config.json');

    if (!isJson) console.log('Verifying...');
    const checks = await runInitChecks(server as string, finalProjectKey, env, key);
    if (!isJson) {
        for (const c of checks) {
            console.log(`${c.status === 'ok' ? '✔' : '✘'} ${c.id}: ${c.message}`);
        }
    }

    await postEvent(server as string, key, { type: 'installed', projectKey: finalProjectKey, meta: { stack: stackMeta, aiTool: tool, injected, cliVersion: '0.1.0' } });

    if (isJson) {
        console.log(JSON.stringify({
            ok: true,
            product,
            server,
            project: { key: finalProjectKey, name: projectName, created },
            environment: env,
            appUrl: appUrl || null,
            appUrlSource: source,
            aiTool: tool,
            stack: { kind: appInfo.kind, evidence: appInfo.evidence },
            injected,
            routedToSkill,
            files: filesMod,
            checks,
            cliVersion: '0.1.0'
        }));
        process.exit(0);
    }

    console.log(`Summary:
✔ ${product} is set up for project "${projectName}" (${finalProjectKey})
  • Widget: ${injected ? `injected into index.html (${appInfo.kind})` : `run the pointer-init skill in ${tool} (${appInfo.kind})`}
  • Key: .pointer/credentials.env (gitignored)
  • Skills: installed

Next: start your dev server, open the app, click the ${product} button and sign in.
      Dashboard: ${branding.urls?.app || server}`);

    process.exit(0);
}
