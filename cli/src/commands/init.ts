import { ask, select, multiSelect, closePrompts } from '../prompt.js';
import { BUILD_DEFAULT_SERVER, BUILD_CLI_VERSION } from '../build-constants.js';
import { readConfig, writeConfig, writeCredentials, upsertGitignore } from '../config.js';
import { detectStack, detectAppUrl, extractTokens } from '../detect.js';
import { injectVite, injectStatic } from '../inject/index.js';
import { injectSourceMap } from '../inject/source-map.js';
import { installSkills } from '../skills.js';
import { getBranding } from '../branding.js';
import { api, ApiError } from '../api.js';
import { postEvent } from '../events.js';
import { runInitChecks, compareSemver, tooOldMessage } from '../checks.js';
import { promises as fs } from 'node:fs';
import { join } from 'node:path';
import { detectDesignTokens, summarizeDesignTokens, type DesignBlock } from '../stack/design.js';
import { buildRequestBody, mergeStack, writeStackFile } from '../stack/stackfile.js';

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

    // Refuse to run against a server that requires a newer CLI — same gate `apply`, `mcp` and
    // `doctor` already apply, and exit 5 as cli.ts documents.
    //
    // init needs it MOST, not least: it is the first command anyone runs, and it is the one that
    // writes files. An init that proceeds against a contract the server no longer accepts leaves
    // a config, an injected snippet and a stack file on disk that all look fine, and the mismatch
    // surfaces much later at apply time, a long way from its cause.
    try {
      const meta = await api<any>(server as string, '/api/meta');
      const minCli = meta?.minCliVersion || '0.0.0';
      if (compareSemver(BUILD_CLI_VERSION, minCli) < 0) {
        console.error(tooOldMessage(BUILD_CLI_VERSION, minCli));
        process.exit(5);
      }
    } catch (err: any) {
      // A server too old to have /api/meta cannot be declaring a minimum, so there is nothing to
      // enforce. Any other transient failure is reported by the calls that follow — this check
      // must not be the thing that stops an install over a blip.
      if (!(err instanceof ApiError && err.code === 404)) {
        // best-effort
      }
    }

    const branding = await getBranding(server as string);
    // No `|| 'Pointer'`: getBranding already guarantees a name or exits. A literal fallback here
    // would print the wrong brand to anyone who rebranded, silently.
    const product = branding.productName;

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
    await upsertGitignore(cwd, product);

    // Detected BEFORE the questions, not after them.
    //
    // This used to run after project, environment and tool had all been chosen, so a repo whose
    // stack cannot be auto-injected (an Nx monorepo, say) answered five questions and only then
    // learned that the widget would not be mounted. Knowing up front lets us say so while the
    // answers still have a visible purpose — the project and key are what the skill needs in order
    // to finish the job, and they are written to .pointer/config.json either way.
    const appInfo = await detectStack(cwd);
    // `--html` names the file outright, so it counts as injectable whatever detection concluded.
    const canInject = appInfo.kind === 'vite' || appInfo.kind === 'static' || !!options['html'];
    if (!isJson && !isYes) {
        console.log(`\nStack: ${appInfo.kind}${appInfo.evidence.length ? ` (${appInfo.evidence.join(', ')})` : ''}`);
        if (!canInject && !options['no-inject']) {
            console.log(
                `\x1b[33mHeads up:\x1b[0m automatic widget injection isn't supported for ${appInfo.kind} yet.\n` +
                `Everything else still applies — the questions below set up your project, key and skills,\n` +
                `and the pointer-init skill uses them to mount the widget for you afterwards.\n`,
            );
        }
    }

    let project = options['project'] as string;
    let create = options['create'] as string;
    let finalProjectKey = project || '';
    let projectName = create || finalProjectKey;
    let created = false;

    if (!isYes && !project && !create) {
        const projects = await api<any[]>(server as string, '/api/admin/projects', { token }).catch(() => []);
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
            // Carry the name across too. Without this, picking an EXISTING project left projectName
            // as the empty string it was initialised to, and the summary read:
            //   ✔ Pointer is set up for project "" (pointer-dashboard)
            const picked = projects.find((p) => p.key === finalProjectKey);
            projectName = picked?.name || finalProjectKey;
        }
    } else if (isYes && project && !create) {
        const existing = await api<any[]>(server as string, '/api/admin/projects', { token }).catch(() => []);
        if (!existing.some((p) => p.key === project)) {
            created = true;
            projectName = project;
        }
    } else if (create) {
        created = true;
        let derivedKey = create.toLowerCase().replace(/[^a-z0-9-]/g, '-').replace(/-+/g, '-').replace(/^-|-$/g, '');
        finalProjectKey = project || derivedKey;
        projectName = create;
    }

    if (created) {
        try {
            await api(server as string, '/api/admin/projects', { method: 'POST', body: { key: finalProjectKey, name: projectName }, token });
        } catch (err: any) {
            if (err instanceof ApiError && err.code === 409) {
                // Exit 3, not 1: under --yes there is nobody to ask for a different key, so this
                // is "cannot proceed with the identity you gave me" — the same class as 403 — and
                // a caller scripting init branches on the code to tell it apart from a generic
                // failure.
                console.error("Key already exists, choose another.");
                process.exit(3);
            } else if (err instanceof ApiError && err.code === 403) {
                console.error("This account cannot create projects.");
                process.exit(3);
            } else if (err instanceof ApiError && err.code === 400) {
                console.error(err.message);
                process.exit(1);
            } else {
                // Previously fell through silently and carried on as though the project existed,
                // writing a config that points at nothing.
                console.error(`Could not create project: ${err?.message ?? err}`);
                process.exit(1);
            }
        }
    }

    // Multi-select: one codebase usually ships to more than one environment, and asking people to
    // re-run init per environment meant three passes that each overwrote the previous one's config.
    // `--environment` accepts a comma-separated list for the non-interactive path.
    const ALL_ENVS = ['local', 'staging', 'production'];
    let envs: string[] = String(options['environment'] ?? '')
        .split(',')
        .map((e) => e.trim())
        .filter(Boolean);

    const badEnv = envs.find((e) => !ALL_ENVS.includes(e));
    if (badEnv) {
        console.error(`Unknown environment "${badEnv}". Valid values: ${ALL_ENVS.join(', ')}.`);
        process.exit(2);
    }

    // No environment question at all.
    //
    // The environment a comment is filed under is resolved by the server from the origin it was
    // left on, matched against the URLs registered for the project. Asking here produced a value
    // baked into the markup that was wrong for every deployment except the one the developer
    // happened to be thinking about — and no answer given at install time can be right for a file
    // that ships to three environments.
    //
    // `--environment` survives as a deliberate override for an install that must be pinned (a
    // server-rendered embed for one environment, say). Given, it is honoured exactly as before.
    const environmentPinned = envs.length > 0;
    if (envs.length === 0) envs = ['local'];

    // The primary environment: what a single-valued field means when several were chosen. First in
    // the canonical order rather than first-picked, so `local,staging` and `staging,local` agree.
    const env = ALL_ENVS.filter((e) => envs.includes(e))[0] ?? 'local';

    // Activate the project for every environment chosen.
    //
    // Without this the multi-select would be decoration: a project is active per environment on the
    // server, and `doctor` reports "Project inactive for staging" for one that was never switched
    // on. Additive — an environment already active stays active, and one not chosen is left alone
    // rather than being switched off, because init should not silently disable an environment
    // someone else enabled.
    const projectRow = await api<any[]>(server as string, '/api/admin/projects', { token })
        .then((rows) => rows.find((p) => p.key === finalProjectKey))
        .catch(() => null);
    if (projectRow?.id) {
        const activation: Record<string, boolean> = {};
        if (envs.includes('local') && !projectRow.isActiveLocal) activation['isActiveLocal'] = true;
        if (envs.includes('staging') && !projectRow.isActiveStaging) activation['isActiveStaging'] = true;
        if (envs.includes('production') && !projectRow.isActiveProduction) activation['isActiveProduction'] = true;
        if (Object.keys(activation).length) {
            try {
                await api(server as string, `/api/admin/projects/${projectRow.id}`, {
                    method: 'PATCH',
                    body: activation,
                    token,
                });
            } catch (err: any) {
                // A developer without project-edit rights can still install the widget; the
                // environment simply stays inactive until an admin enables it. Worth saying out
                // loud, never worth aborting for.
                if (!isJson) {
                    console.error(
                        `Note: could not activate ${Object.keys(activation).length} environment(s) for this project ` +
                        `(${err?.message ?? err}). An admin can switch them on in the dashboard.`,
                    );
                }
            }
        }
    }

    // Origin → environment, read from the URLs already registered against this project rather than
    // asked for again. The widget resolves its own environment from this at runtime, so one
    // committed index.html reports `staging` on staging and `production` on production.
    const envMap: Record<string, string> = {};
    if (projectRow?.id && envs.length > 1) {
        try {
            const [urls, environments] = await Promise.all([
                api<any[]>(server as string, `/api/admin/projects/${projectRow.id}/app-urls`, { token }),
                api<any[]>(server as string, '/api/admin/environments', { token }),
            ]);
            const nameById = new Map((environments ?? []).map((e: any) => [e.id, String(e.name ?? '').toLowerCase()]));
            for (const row of urls ?? []) {
                const name = nameById.get(row.appEnvironmentId);
                if (!name || !row.url || !envs.includes(name)) continue;
                try {
                    envMap[new URL(row.url).origin] = name;
                } catch {
                    // A malformed URL in the dashboard should not stop an install.
                }
            }
        } catch {
            // No rights to read them, or none configured: the block falls back to a localhost check
            // plus the primary environment, which is still better than one baked-in value.
        }
    }

    let appUrl = options['app-url'] as string | undefined;
    let noAppUrl = options['no-app-url'] as boolean;
    let source = '';
    
    // Detected, never asked.
    //
    // This used to prompt "Where does this app run in <env>?" and then drop the answer: the only
    // code that consumed it was an empty `if (appUrl && env !== 'local') {}` block, and the value
    // reaches nothing but `--json` output. Asking someone for a URL that goes nowhere is worse than
    // not asking, so the question is gone while the flags and detection stay — `--app-url` still
    // overrides, `--no-app-url` still skips the scan.
    //
    // Registering it against the project's environment (PUT /api/admin/projects/{id}/app-urls/
    // {environmentId}, which the dashboard already uses) is the real feature this was a stub for.
    // Deliberately still not done — see the note in R1-02.
    if (!noAppUrl && !appUrl) {
        const detected = await detectAppUrl(cwd, appInfo.kind, env);
        source = detected.source;
        appUrl = detected.url || undefined;
    }

    let tool = options['tool'] as string;
    /** Every tool to install skills for; `tool` remains the primary one recorded in config. */
    let tools: string[] = tool ? [tool] : [];
    if (!tool) {
        // auto-detect
        if (process.env.CLAUDECODE || process.env.CLAUDE_CODE_ENTRYPOINT) tool = 'claude-code';
        else if (process.env.ANTIGRAVITY_AGENT || process.env.GEMINI_CLI) tool = 'antigravity';
        else if (process.env.TERM_PROGRAM && process.env.TERM_PROGRAM.includes('Cursor')) tool = 'cursor';
        else if (process.env.WINDSURF) tool = 'windsurf';
        else if (process.env.OPENCODE) tool = 'opencode';
        else tool = isYes ? 'other' : 'claude-code';
        
        if (!isYes && !options['tool']) {
            // Multi-select: a repo is rarely worked on through exactly one agent, and installing a
            // second tool's skills later means re-running init. `all` is a row rather than a
            // keystroke people have to discover.
            const ALL = 'all of them';
            const catalogue = ['claude-code', 'cursor', 'windsurf', 'opencode', 'antigravity', 'other'];
            const picked = await multiSelect(
                'Which AI tools work in this repo?',
                [ALL, ...catalogue],
                [tool],
            );
            tools = picked.includes(ALL) ? catalogue : picked;
            // `aiTool` stays a single value in config and on the wire: every consumer of it —
            // doctor, apply, the server's stack record — predates multi-tool and reads a string.
            // The first pick is the primary; the rest still get their skills installed below.
            tool = tools[0] ?? tool;
        }
    }
    if (tools.length === 0) tools = [tool];

    // Questioning is over; hand stdin back. The shared interface would otherwise keep the event
    // loop alive, which only goes unnoticed because every exit path here calls process.exit.
    closePrompts();
    
    if (!isJson) console.log(`Detecting your stack... -> ${appInfo.kind} (${appInfo.evidence.join(', ')})`);

    let injected = false;
    let routedToSkill = false;
    let filesMod: string[] = [];
    let skillFiles: string[] = [];
    
    // --pin asks the server which build it is serving and nails the page to it, with the integrity
    // hash the server itself publishes. Resolved HERE, before the stack branch, because the static
    // and Vite injectors both need it — and resolved once, so a failure to reach the manifest is
    // reported with context instead of silently producing an unpinned tag the developer believes
    // is pinned.
    let pin: { version: string; integrity: string } | null = null;
    if (options['pin'] === true) {
        try {
            const manifest = await api<any>(server as string, '/pointer.version.json');
            const version = manifest?.hash;
            const integrity = manifest?.files?.['pointer.js']?.integrity;
            if (!version || !integrity) {
                throw new Error('the server published no hash/integrity for pointer.js');
            }
            pin = { version, integrity };
        } catch (err: any) {
            console.error(
                `Could not pin the widget: ${err?.message ?? err}. ` +
                    `Re-run without --pin to install the floating build.`,
            );
            process.exit(1);
        }
    }

    if (!options['no-inject']) {
        const explicitHtml = options['html'] as string | undefined;
        // An explicitly named HTML file outranks stack detection.
        //
        // Detection answers "how would I find the right file myself", which is a different question
        // from "which file did you just tell me to use". A monorepo reports `monorepo` and has no
        // single entry point to guess at — but `--html apps/profile/src/index.html` is not a guess,
        // and silently ignoring it (as this did) leaves the user staring at "Widget not found"
        // having named the file on the command line.
        if (explicitHtml && appInfo.kind !== 'vite') {
            const htmlPath = await injectStatic(cwd, explicitHtml, {
                server: server as string,
                key: finalProjectKey,
                environment: env,
                pin,
                envMap,
                environments: envs,
                environmentPinned,
            });
            filesMod = [htmlPath];
            injected = true;
            if (!isJson) console.log(`Injected widget into ${htmlPath}`);
        } else if (appInfo.kind === 'vite') {
            filesMod = await injectVite(cwd, { server: server as string, key: finalProjectKey, environment: env, pin }, options['html'] as string);
            injected = true;
            if (!isJson) console.log(`Injected widget into ${filesMod.join(', ')}`);
        } else if (appInfo.kind === 'static') {
            const htmlPath = await injectStatic(cwd, options['html'] as string, { server: server as string, key: finalProjectKey, environment: env, pin, envMap, environments: envs, environmentPinned });
            filesMod = [htmlPath];
            injected = true;
            if (!isJson) console.log(`Injected widget into ${htmlPath}`);
        } else if (appInfo.kind !== 'unknown') {
            routedToSkill = true;
            if (!isJson) {
                console.log(`ℹ ${appInfo.kind} detected — there's no single entry point to inject into automatically.
  If you know the file, name it and re-run — that always wins over detection:
    npx -y pointer-feedback init --html path/to/index.html
  Otherwise the pointer-init skill was installed for ${tool}; run it and it will mount the widget:
    claude -> /pointer-init (or @pointer-init for cursor)
  Config is already saved in .pointer/config.json, so neither will ask for the key or project again.`);
            }
        }
    }

    // --source-map: wire in the Vite plugin that stamps component hashes. Opt-in, because it
    // edits the user's build config — the most intrusive thing this CLI does.
    let sourceMapNote = '';
    if (options['source-map']) {
        const res = await injectSourceMap(cwd);
        if (res.ok) {
            sourceMapNote = res.alreadyPresent
                ? 'source mapping already configured'
                : `source mapping enabled (${res.files.join(', ')})`;
            filesMod.push(...res.files);
            if (!isJson) console.log(`✔ ${sourceMapNote}`);
        } else {
            sourceMapNote = `source mapping NOT enabled: ${res.reason}`;
            // Never fail the install for this: the widget works without it.
            if (!isJson) console.error(`⚠ ${sourceMapNote}`);
        }
    }

    if (!options['no-skills']) {
        if (!isJson) console.log(`Installing AI skills for ${tools.join(', ')}`);
        const skillsDir = options['skills-dir'] as string;
        // One pass per selected tool. A --skills-dir override names a single directory, so it can
        // only apply to the primary tool — installing every tool into it would have them overwrite
        // each other's files.
        const installed: string[] = [];
        for (const t of tools) {
            installed.push(...(await installSkills(server as string, t, cwd, t === tool ? skillsDir : undefined)));
        }
        filesMod.push(...installed);
        // Kept for the human summary below: "installed" does not tell anyone WHAT was written into
        // their repository, and these are files they will want to find, read and commit.
        // Deduplicated: several tools share the .agents/ layout, so installing for three of them
        // listed the same two paths three times over in the summary.
        skillFiles = [...new Set(installed.filter((f) => f.includes('SKILL.md') || f.endsWith('.md')))];
    }

    const pkgStr = await fs.readFile(join(cwd, 'package.json'), 'utf8').catch(() => '{}');
    const tokens = extractTokens(JSON.parse(pkgStr));
    const stackMeta = { frontend: tokens.frontend, backend: tokens.backend, aiTool: tool };
    
    let serverStackResponse: any = null;
    try {
        // `token`, not `key`. /api/projects/{key}/stack is [Authorize] and expects the JWT that
        // was already exchanged above; sending the raw API key as a Bearer 401s every time. The
        // catch below swallowed it, so stack.json was never written and `doctor` reported
        // "Stack not registered" on a perfectly good install.
        const body = buildRequestBody(stackMeta);
        serverStackResponse = await api(server as string, `/api/projects/${finalProjectKey}/stack`, { method: 'POST', body, token });
    } catch (e: any) {
        if (!isJson) console.log(`⚠ Stack not registered (${e.code || 500})`);
    }

    const noDesign = Boolean(options['no-design']);
    let designBlock: DesignBlock | null = null;
    if (!noDesign) {
        designBlock = await detectDesignTokens(cwd);
        const designSummary = summarizeDesignTokens(designBlock.tokens, designBlock.libraries);
        if (!isJson) {
            console.log(`✔ Design tokens: ${designSummary} → .pointer/stack.json`);
        }
    }

    const mergedStack = mergeStack(stackMeta, serverStackResponse?.data ?? serverStackResponse, noDesign ? null : designBlock);
    await writeStackFile(cwd, mergedStack);

    // The HTML we actually wrote to, so `doctor` can find the widget in a repo whose layout it
    // would never guess. Relative, because the config is committed and an absolute path would be
    // wrong on every other machine.
    const injectedHtml = injected
        ? filesMod.find((f) => f.toLowerCase().endsWith('.html'))?.replace(`${cwd}/`, '')
        : undefined;
    await writeConfig(cwd, { server: server as string, project: finalProjectKey, environment: env, aiTool: tool, skillsDir: options['skills-dir'] as string, cliVersion: BUILD_CLI_VERSION, htmlPath: injectedHtml, environments: envs.length > 1 ? envs : undefined });
    filesMod.push('.pointer/config.json');
    // Written back at line ~92, long before filesMod exists. It is the one file in this list that
    // holds a secret, so omitting it from `--json`'s `files` is the worst omission of the set: a
    // caller reading that list to know what to gitignore, review or clean up never sees it.
    filesMod.push('.pointer/credentials.env');
    // upsertGitignore also writes; reported for the same reason.
    filesMod.push('.gitignore');

    if (!isJson) console.log('Verifying...');
    // Real checks now, against the install that was just written. This previously called a stub
    // that returned [] — the "Verifying..." line printed and nothing was ever verified.
    const checks = await runInitChecks(cwd, { server: server as string, project: finalProjectKey }, BUILD_CLI_VERSION);
    if (!isJson) {
        const icon = { ok: '✔', warn: '⚠', error: '✘' } as const;
        for (const c of checks) {
            console.log(`${icon[c.status]} ${c.id}: ${c.message}`);
        }
    }

    await postEvent(server as string, token, { type: 'installed', projectKey: finalProjectKey, meta: { stack: stackMeta, aiTool: tool, injected, cliVersion: BUILD_CLI_VERSION } });

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
            cliVersion: BUILD_CLI_VERSION
        }));
        process.exit(0);
    }

    // Formatted so the one thing the reader must DO is the thing they see.
    //
    // The previous summary ran the state and the next action together in one undifferentiated
    // list, then buried both under an MCP JSON blob — so an install that still needed the skill
    // run looked finished. The action now gets its own titled block, in colour, last.
    const bold = (s: string) => `\x1b[1m${s}\x1b[0m`;
    const dim = (s: string) => `\x1b[2m${s}\x1b[0m`;
    const green = (s: string) => `\x1b[32m${s}\x1b[0m`;
    const cyan = (s: string) => `\x1b[36m${s}\x1b[0m`;
    const rule = dim('─'.repeat(60));

    const envLabel = envs.length > 1 ? envs.join(', ') : env;
    console.log(`
${rule}
${green('✔')} ${bold(`${product} is set up`)}

  ${dim('Project')}       ${projectName || finalProjectKey} ${dim(`(${finalProjectKey})`)}
  ${dim('Environments')}  ${envLabel}
  ${dim('Server')}        ${server}
  ${dim('Key')}           .pointer/credentials.env ${dim('(gitignored)')}
  ${dim('Skills')}        ${skillFiles.length ? skillFiles.join('\n                ') : 'installed'}
${rule}`);

    if (injected) {
        console.log(`
${bold('Next')}  Start your dev server and open the app — the ${product} button should appear.
      ${dim(`Widget mounted in ${filesMod.find((f) => f.endsWith('.html')) ?? 'your HTML'}`)}
      ${dim(`Dashboard: ${branding.urls?.app || server}`)}`);
    } else {
        // The install is NOT finished here, and saying so plainly is the whole point of the block.
        console.log(`
${bold('Next')}  ${cyan(`The widget is not mounted yet — ${appInfo.kind} has no single entry point to inject into.`)}

      Run this in ${tool}:   ${bold('/pointer-init')}
      ${dim('It reads .pointer/config.json, so it will not ask for your key or project again.')}

      ${dim(`Or name the file yourself:  pointer init --html path/to/index.html`)}
      ${dim(`Dashboard: ${branding.urls?.app || server}`)}`);
    }

    const mcpConfigPaths: Record<string, string> = {
      'claude-code': '~/.claude.json',
      claude: '~/.claude.json',
      cursor: '~/.cursor/mcp.json (or Cursor Settings > MCP)',
      windsurf: '~/.codeium/windsurf/mcp_config.json',
      opencode: '~/.config/opencode/opencode.json',
    };
    const toolKey = (tool || '').toLowerCase();
    const configPath = mcpConfigPaths[toolKey] || "your tool's user MCP settings";

    // Optional, so it reads as optional. It used to be the last and largest thing printed, which
    // made a 12-line JSON blob look like a required step and pushed the actual next action off
    // screen.
    console.log(`
${dim('─'.repeat(60))}
${dim(`Optional — MCP server, for tool-native access to comments (${tool}):`)}
${dim(`Add to ${configPath}, user-level, do not commit:`)}
${dim('  { "mcpServers": { "pointer": { "command": "npx", "args": ["-y", "pointer-feedback", "mcp"] } } }')}`);

    process.exit(0);
}
