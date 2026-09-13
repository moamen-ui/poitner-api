import { promises as fs } from 'node:fs';
import { join } from 'node:path';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { readConfig, type PointerConfig } from './config.js';
import { api, ApiError } from './api.js';
import { detectStack } from './detect.js';
import { SKILL_FILES } from './skills.js';
import { readStamp } from './lib/skill-stamp.js';
import { skillFilesFor } from './lib/skill-paths.js';

const execFileAsync = promisify(execFile);

export interface CheckResult {
  id: string;
  status: 'ok' | 'warn' | 'error';
  message: string;
  hint?: string;
  /** Set by a check that --fix can repair. */
  fixable?: boolean;
}

export interface MetaResponse {
  version?: string;
  apiVersion?: number;
  minCliVersion?: string;
  skillVersion?: string | null;
  productName?: string;
  serverTime?: string;
}

/**
 * Compares two semver-ish strings. Returns <0, 0, >0 like a comparator.
 *
 * Hand-rolled because the CLI ships with zero runtime dependencies — a version comparison is not
 * worth an install on a `npx -y` cold start. Pre-release suffixes sort BELOW their release
 * (1.0.0-beta < 1.0.0), which is what semver requires and what makes "is my CLI new enough?"
 * answer correctly for a pre-release build.
 */
/**
 * The one place the "your CLI is too old" wording lives.
 *
 * Every caller must include the upgrade command. Saying only what is wrong leaves the user at a
 * dead end — the CLI is the thing that is out of date, so it is the only party that knows what to
 * run. doctor carried the hint; init, apply and mcp each said only that the version was wrong.
 */
export function tooOldMessage(cliVersion: string, min: string): string {
  return `CLI ${cliVersion} is older than the server requires (${min}). Upgrade with: npx -y pointer-feedback@latest`;
}

export const UPGRADE_HINT = 'Run `npx -y pointer-feedback@latest doctor`';

export function compareSemver(a: string, b: string): number {
  const parse = (v: string) => {
    const [core, pre] = String(v ?? '0.0.0').trim().replace(/^v/, '').split('-');
    const nums = core.split('.').map((n) => parseInt(n, 10) || 0);
    return { nums: [nums[0] ?? 0, nums[1] ?? 0, nums[2] ?? 0], pre: pre ?? null };
  };
  const x = parse(a);
  const y = parse(b);
  for (let i = 0; i < 3; i++) {
    if (x.nums[i] !== y.nums[i]) return x.nums[i] - y.nums[i];
  }
  if (x.pre === y.pre) return 0;
  if (x.pre === null) return 1;   // release > pre-release
  if (y.pre === null) return -1;
  return x.pre < y.pre ? -1 : 1;
}

/** A fetch with a deadline — an unreachable server must fail fast, not hang the whole run. */
async function fetchWithTimeout(url: string, ms: number, init?: RequestInit): Promise<Response> {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), ms);
  try {
    return await fetch(url, { ...init, signal: controller.signal });
  } finally {
    clearTimeout(timer);
  }
}

async function readCredentialsKey(cwd: string): Promise<string | undefined> {
  try {
    const raw = await fs.readFile(join(cwd, '.pointer/credentials.env'), 'utf8');
    const match = raw.match(/^POINTER_API_KEY=(.*)$/m);
    return match?.[1]?.trim() || undefined;
  } catch {
    return undefined;
  }
}

/**
 * Runs the install diagnostics. Order matters: `doctor` applies exit-code precedence to the
 * results, and a caller that stops early (CLI too old) must not have run the later checks, whose
 * answers would be meaningless against a newer server.
 *
 * Never throws — a check that cannot run reports `error` with its reason. A diagnostic tool that
 * crashes on a broken install is useless precisely when it is needed.
 */
export async function runInitChecks(
  cwd: string,
  overrides: { server?: string; project?: string } = {},
  cliVersion = '0.0.0',
): Promise<CheckResult[]> {
  const checks: CheckResult[] = [];
  const config: PointerConfig = await readConfig(cwd);
  const server = (overrides.server || config.server || '').replace(/\/$/, '');
  const project = overrides.project || config.project || '';
  const environment = config.environment || 'local';

  // config ------------------------------------------------------------------
  if (server && project) {
    checks.push({ id: 'config', status: 'ok', message: `${project} @ ${server} (${environment})` });
  } else {
    checks.push({
      id: 'config',
      status: 'error',
      message: 'No .pointer/config.json',
      hint: 'Run `npx -y pointer-feedback init`',
    });
    // Everything below needs a server; there is nothing further to say.
    return checks;
  }

  // server ------------------------------------------------------------------
  let serverReachable = false;
  try {
    const res = await fetchWithTimeout(`${server}/api/branding`, 3000);
    serverReachable = res.ok;
    checks.push(
      res.ok
        ? { id: 'server', status: 'ok', message: `Reached ${server}` }
        : { id: 'server', status: 'error', message: `Cannot reach ${server} (HTTP ${res.status})` },
    );
  } catch {
    checks.push({ id: 'server', status: 'error', message: `Cannot reach ${server}` });
  }

  // meta --------------------------------------------------------------------
  let meta: MetaResponse | null = null;
  if (serverReachable) {
    try {
      meta = await api<MetaResponse>(server, '/api/meta');
      const min = meta?.minCliVersion || '0.0.0';
      if (compareSemver(cliVersion, min) < 0) {
        checks.push({
          id: 'meta',
          status: 'error',
          message: tooOldMessage(cliVersion, min),
          hint: UPGRADE_HINT,
        });
        // Precedence rule 1: stop here. Later checks may be meaningless against a newer server.
        return checks;
      }
      checks.push({ id: 'meta', status: 'ok', message: `Server ${meta?.version ?? 'unknown'} (api v${meta?.apiVersion ?? '?'})` });
    } catch (err: any) {
      checks.push(
        err instanceof ApiError && err.code === 404
          ? { id: 'meta', status: 'warn', message: 'Server predates /api/meta' }
          : { id: 'meta', status: 'warn', message: `Could not read /api/meta: ${err?.message ?? err}` },
      );
    }
  }

  // clock -------------------------------------------------------------------
  if (meta?.serverTime) {
    const skewMs = Math.abs(Date.now() - new Date(meta.serverTime).getTime());
    const skewSeconds = Math.round(skewMs / 1000);
    // The endpoint is cached for 60s, so serverTime can legitimately be that stale. The threshold
    // must stay well above it — never tighten below 2 minutes.
    checks.push(
      skewMs < 5 * 60_000
        ? { id: 'clock', status: 'ok', message: `Clock within ${skewSeconds}s of the server` }
        : { id: 'clock', status: 'warn', message: `Clock skew ${skewSeconds}s — logins may fail` },
    );
  }

  // key ---------------------------------------------------------------------
  const apiKey = await readCredentialsKey(cwd);
  let token: string | undefined;
  if (!apiKey) {
    checks.push({
      id: 'key',
      status: 'error',
      message: 'No POINTER_API_KEY in .pointer/credentials.env',
      hint: 'Copy it from Profile → API key',
    });
  } else if (!serverReachable) {
    checks.push({ id: 'key', status: 'warn', message: 'Server unreachable — key not verified' });
  } else {
    try {
      const login = await api<{ status?: string; token?: string }>(server, '/api/auth/login-with-key', {
        method: 'POST',
        body: { apiKey },
      });
      if (login?.status === 'ok' && login.token) {
        token = login.token;
        checks.push({ id: 'key', status: 'ok', message: 'API key accepted' });
      } else {
        checks.push({ id: 'key', status: 'error', message: 'API key rejected', hint: 'Regenerate it in Profile → API key' });
      }
    } catch {
      checks.push({ id: 'key', status: 'error', message: 'API key invalid', hint: 'Regenerate it in Profile → API key' });
    }
  }

  // project -----------------------------------------------------------------
  if (token) {
    try {
      const projects = await api<any[]>(server, '/api/admin/projects', { token });
      const found = projects.find((p) => p.key === project);
      if (!found) {
        checks.push({ id: 'project', status: 'error', message: `Project ${project} not found in this workspace` });
      } else {
        const activeField =
          environment === 'production' ? 'isActiveProduction' : environment === 'staging' ? 'isActiveStaging' : 'isActiveLocal';
        checks.push(
          found[activeField] === false
            ? { id: 'project', status: 'warn', message: `Project inactive for ${environment}` }
            : { id: 'project', status: 'ok', message: `Project ${project} active for ${environment}` },
        );
      }
    } catch (err: any) {
      checks.push({ id: 'project', status: 'warn', message: `Could not list projects: ${err?.message ?? err}` });
    }
  }

  // widget ------------------------------------------------------------------
  checks.push(await widgetCheck(cwd, config));

  // widget-served -----------------------------------------------------------
  if (serverReachable) {
    try {
      const res = await fetchWithTimeout(`${server}/pointer.js`, 3000);
      const type = res.headers.get('content-type') || '';
      checks.push(
        res.ok && type.includes('javascript')
          ? { id: 'widget-served', status: 'ok', message: 'Widget script served' }
          : { id: 'widget-served', status: 'error', message: `Widget script not served (HTTP ${res.status})` },
      );
    } catch {
      checks.push({ id: 'widget-served', status: 'error', message: 'Widget script not served' });
    }
  }

  // skills ------------------------------------------------------------------
  checks.push(await skillsCheck(cwd, config));

  // stale ---------------------------------------------------------------------
  // A skill file installed months ago is frozen prose describing an API that has moved on. The
  // developer has no way to notice; the server stamps a version into every served copy so this
  // check can say so. Warning, not error: a stale skill still works, it is just behind.
  if (meta?.skillVersion) {
    const stale: string[] = [];
    for (const rel of skillFilesFor(config)) {
      const abs = join(cwd, rel);
      try {
        await fs.access(abs);
      } catch {
        continue; // not installed for this tool
      }
      if ((await readStamp(abs)) !== meta.skillVersion) stale.push(rel);
    }
    checks.push(
      stale.length === 0
        ? { id: 'stale', status: 'ok', message: `Skills match the server (${meta.skillVersion})` }
        : {
            id: 'stale',
            status: 'warn',
            message: `${stale.length} file${stale.length === 1 ? '' : 's'} behind the server (${meta.skillVersion}): ${stale.join(', ')}`,
            hint: 'Run `npx -y pointer-feedback update`',
          },
    );
  }

  // gitignore ---------------------------------------------------------------
  checks.push(...(await gitignoreChecks(cwd)));

  // stack -------------------------------------------------------------------
  try {
    await fs.access(join(cwd, '.pointer/stack.json'));
    checks.push({ id: 'stack', status: 'ok', message: 'Stack registered' });
  } catch {
    checks.push({ id: 'stack', status: 'warn', message: 'Stack not registered', fixable: true });
  }

  return checks;
}

/**
 * Warn, never error: a Next or Angular install mounts the widget from a component file this
 * scan does not read, so "not found" is genuinely inconclusive.
 */
async function widgetCheck(cwd: string, config: PointerConfig = {}): Promise<CheckResult> {
  const detection = await detectStack(cwd).catch(() => null);
  // The recorded path first: init knows where it wrote, and in a monorepo no amount of convention
  // guessing will find apps/<app>/src/index.html.
  const candidates = [config.htmlPath, detection?.htmlPath, 'index.html', 'public/index.html', 'src/index.html'].filter(Boolean) as string[];

  for (const rel of candidates) {
    try {
      const html = await fs.readFile(join(cwd, rel), 'utf8');
      if (html.includes('<!-- pointer-feedback:start -->') || html.includes('<pointer-feedback')) {
        return { id: 'widget', status: 'ok', message: `Widget found in ${rel}` };
      }
    } catch {
      // Missing candidate file is expected — keep looking.
    }
  }

  for (const envFile of ['.env', '.env.local', '.env.development']) {
    try {
      const env = await fs.readFile(join(cwd, envFile), 'utf8');
      if (/^VITE_POINTER_PROJECT=/m.test(env)) {
        return { id: 'widget', status: 'ok', message: `Widget env configured in ${envFile}` };
      }
    } catch {
      // Same — absence is not evidence.
    }
  }

  return {
    id: 'widget',
    status: 'warn',
    message: 'Widget not found in this app',
    hint: 'Run `init`, or the pointer-init skill for framework installs',
  };
}

async function skillsCheck(cwd: string, config: PointerConfig): Promise<CheckResult> {
  const tool = config.aiTool;
  if (!tool) return { id: 'skills', status: 'warn', message: 'No AI tool configured' };

  const expected = SKILL_FILES[tool] ?? SKILL_FILES.other;

  const missing: string[] = [];
  for (const rel of expected) {
    try {
      await fs.access(join(cwd, config.skillsDir ?? '', rel));
    } catch {
      missing.push(rel);
    }
  }

  return missing.length === 0
    ? { id: 'skills', status: 'ok', message: `Skills installed for ${tool}` }
    : { id: 'skills', status: 'warn', message: `Skills missing for ${tool}: ${missing.join(', ')}`, fixable: true };
}

/**
 * Two separate concerns under one id in the contract, and only one of them is an error:
 * missing ignore lines are a warning, but credentials.env actually being TRACKED means the key is
 * in git history and is the single most serious thing doctor can find.
 */
async function gitignoreChecks(cwd: string): Promise<CheckResult[]> {
  const results: CheckResult[] = [];

  let tracked = false;
  try {
    await execFileAsync('git', ['ls-files', '--error-unmatch', '.pointer/credentials.env'], { cwd });
    tracked = true; // exits 0 only when the file IS tracked
  } catch {
    tracked = false; // non-zero: untracked, or not a git repo at all
  }

  if (tracked) {
    results.push({
      id: 'gitignore',
      status: 'error',
      message: '.pointer/credentials.env is tracked by git!',
      hint: 'Run `git rm --cached .pointer/credentials.env`, then rotate the key — it is in your history',
    });
    return results;
  }

  try {
    const ignore = await fs.readFile(join(cwd, '.gitignore'), 'utf8');
    results.push(
      ignore.includes('.pointer/')
        ? { id: 'gitignore', status: 'ok', message: 'Credentials ignored by git' }
        : { id: 'gitignore', status: 'warn', message: '.gitignore is missing the .pointer/ entries', fixable: true },
    );
  } catch {
    results.push({ id: 'gitignore', status: 'warn', message: 'No .gitignore found', fixable: true });
  }

  return results;
}
