// Playwright E2E spec for fresh-app scenarios:
// - R1-02-01 — init-vite-no-ai
// - R1-02-02 — init-static-no-ai
// - R1-02-03 — init-next-handoff
// Contract: docs/roadmap/testing/R1-02-tests.md
import { test, expect, type Page } from '@playwright/test';
import { readFileSync, existsSync } from 'node:fs';
import { readFile, stat } from 'node:fs/promises';
import { join, dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { raw, login } from '../scripts/lib/api.mjs';
import { spawnCli } from '../scripts/lib/cli.mjs';
import { TENANT_OWNER, USERS } from '../scripts/lib/constants.mjs';
import { record } from '../scripts/lib/report.mjs';
import {
  scaffoldStatic,
  scaffoldVite,
  scaffoldNext,
  startStaticServer,
  startVitePreview,
} from './run.mjs';

const execFileAsync = promisify(execFile);

const here = dirname(fileURLToPath(import.meta.url));
const STATE_DIR = resolve(here, '../state');
const SERVER = process.env.E2E_API_URL || 'http://localhost:8090';

type CredentialUser = {
  email: string;
  password: string;
};

type CredentialsMap = {
  wsAdmin: CredentialUser;
  developer: CredentialUser;
};

type KeyEntry = {
  apiKey: string;
};

type KeysMap = {
  developer?: KeyEntry;
  superAdmin?: KeyEntry;
};

const credPath = join(STATE_DIR, 'credentials.json');
const credentials: CredentialsMap = existsSync(credPath)
  ? JSON.parse(readFileSync(credPath, 'utf8'))
  : {
      wsAdmin: TENANT_OWNER,
      developer: USERS.developer,
    };

const keysPath = join(STATE_DIR, 'keys.json');
const keys: KeysMap = existsSync(keysPath)
  ? JSON.parse(readFileSync(keysPath, 'utf8'))
  : {
      developer: { apiKey: 'ptr_dev_key_placeholder' },
    };

/**
 * Performs the real deferred-login and comment flow per web-component/src/element.ts.
 * element.ts:264-268, 344-354.
 */
async function performDeferredLoginCommentFlow(
  page: Page,
  url: string,
  email: string,
  pass: string,
  commentText = 'E2E fresh comment',
) {
  await page.goto(url);

  const widget = page.locator('pointer-feedback');
  await expect(widget).toBeAttached({ timeout: 15_000 });

  // If collapsed, click #pf-launcher to expand
  const launcher = widget.locator('#pf-launcher');
  if (await launcher.isVisible()) {
    await launcher.click();
  }

  // Click #pf-add: with no token -> activateAddComment() -> showLoginModal()
  await widget.locator('#pf-add').click();

  // Fill credentials
  const emailInput = widget.locator('#pf-email');
  const passwordInput = widget.locator('#pf-password');
  await expect(emailInput).toBeVisible({ timeout: 10_000 });
  await emailInput.fill(email);
  await passwordInput.fill(pass);

  // Submit login modal
  await widget.locator('#pf-login-submit').click();

  // Picking is now active: afterLogin runs init() then togglePicking().
  // Do NOT click #pf-add again!
  // Click <h1> to pick target
  await page.locator('h1').first().click({ force: true });

  // Fill comment text and submit
  const popover = page.locator('#pf-popover-host');
  await expect(popover.locator('#pf-comment-text')).toBeVisible({ timeout: 10_000 });
  await popover.locator('#pf-comment-text').fill(commentText);
  await popover.locator('#pf-submit').click();
  await expect(popover).toBeEmpty({ timeout: 10_000 });
}

test('R1-02-01 — init-vite-no-ai', async ({ page }) => {
  // Respect tier: nightly only, skipped in PR tier
  test.skip(process.env.TIER === 'pr', 'nightly tier only — skipped during PR tier');
  test.setTimeout(360_000);

  const start = Date.now();
  const runId = Math.random().toString(36).substring(2, 7);
  const devKey = keys.developer?.apiKey;
  const wsAdmin = await login(credentials.wsAdmin.email, credentials.wsAdmin.password);

  let serverProcess: { stop: () => Promise<void> } | null = null;
  let createdProjectKey = '';

  try {
    // 1. Scaffold Vite app
    const appDir = await scaffoldVite();

    // 2. & 3. CLI init with --yes --json
    const initRes = await spawnCli({
      cwd: appDir,
      args: [
        'init',
        '--server',
        SERVER,
        '--key',
        devKey || '',
        '--create',
        `Fresh vite ${runId}`,
        '--environment',
        'local',
        '--tool',
        'other',
        '--yes',
        '--json',
      ],
    });

    expect(initRes.code).toBe(0);
    expect(initRes.json?.ok).toBe(true);
    expect(initRes.json?.project?.created).toBe(true);
    expect(initRes.json?.project?.key).toMatch(/^fresh-vite-/);
    expect(initRes.json?.injected).toBe(true);
    expect(initRes.json?.routedToSkill).toBe(false);
    expect(initRes.json?.product).toBe('Pointer');
    expect(initRes.json?.cliVersion).toBeTruthy();

    createdProjectKey = initRes.json?.project?.key;

    const files = initRes.json?.files || [];
    for (const reqFile of ['.env', 'index.html', '.pointer/config.json', '.pointer/credentials.env', '.gitignore']) {
      expect(files).toContain(reqFile);
    }

    // 4. Assert files in <app>
    const indexHtml = await readFile(join(appDir, 'index.html'), 'utf8');
    const startMarkers = indexHtml.match(/<!-- pointer-feedback:start -->/g) || [];
    const endMarkers = indexHtml.match(/<!-- pointer-feedback:end -->/g) || [];
    expect(startMarkers.length).toBe(1);
    expect(endMarkers.length).toBe(1);
    expect(indexHtml).toContain("'%VITE_POINTER_ENABLED%' === 'true'");
    expect(indexHtml).toContain("document.createElement('pointer-feedback')");
    expect(indexHtml).toContain('data-component-source');

    const envContent = await readFile(join(appDir, '.env'), 'utf8');
    const envLines = envContent.split('\n');
    const countVar = (prefix: string) => envLines.filter((l) => l.startsWith(prefix)).length;
    expect(countVar('VITE_POINTER_ENABLED=true')).toBe(1);
    expect(countVar(`VITE_POINTER_SERVER=${SERVER}`)).toBe(1);
    expect(countVar(`VITE_POINTER_PROJECT=${createdProjectKey}`)).toBe(1);
    expect(countVar('VITE_POINTER_ENV=local')).toBe(1);

    const credStat = await stat(join(appDir, '.pointer/credentials.env'));
    expect(credStat.mode & 0o777).toBe(0o600);
    const credContent = await readFile(join(appDir, '.pointer/credentials.env'), 'utf8');
    expect(credContent).toContain(`POINTER_API_KEY=${devKey}`);

    const shStat = await stat(join(appDir, '.pointer/pointer.sh'));
    expect(shStat.mode & 0o777).toBe(0o755);

    expect(existsSync(join(appDir, '.agents/pointer-init/SKILL.md'))).toBe(true);
    expect(existsSync(join(appDir, '.agents/pointer-feedback/SKILL.md'))).toBe(true);
    expect(existsSync(join(appDir, '.pointer/stack.json'))).toBe(true);

    const gitignore = await readFile(join(appDir, '.gitignore'), 'utf8');
    expect(gitignore).toContain('!.pointer/config.json');
    expect(gitignore).toContain('!.pointer/stack.json');

    // 5. Build and preview on port 4174
    serverProcess = await startVitePreview(appDir, 4174);

    // 6. Deferred-login browser flow
    const freshUrl = process.env.FRESH_URL || 'http://localhost:4174';
    await performDeferredLoginCommentFlow(
      page,
      freshUrl,
      credentials.developer.email,
      credentials.developer.password,
      'E2E fresh comment',
    );

    // 7. API assertion as developer
    const devAuth = await login(credentials.developer.email, credentials.developer.password);
    const commentsRes = await raw(
      'GET',
      `/api/projects/${createdProjectKey}/comments?view=summary`,
      { token: devAuth.token },
    );
    expect(commentsRes.status).toBe(200);
    const items = commentsRes.data?.items || commentsRes.data || [];
    expect(items.length).toBe(1);
    expect(items[0]?.body).toBe('E2E fresh comment');

    // 8. Wall-clock budget <= 300 s
    const durationMs = Date.now() - start;
    expect(durationMs).toBeLessThanOrEqual(300_000);

    record({
      id: 'R1-02-01',
      tier: 'nightly',
      layer: 'cli+widget',
      role: 'developer',
      result: 'PASS',
      ms: durationMs,
      detail: `project=${createdProjectKey}, duration=${Math.round(durationMs / 1000)}s`,
    });
  } finally {
    if (serverProcess) await serverProcess.stop();
    if (createdProjectKey) {
      const allProjects = await raw('GET', '/api/admin/projects', { token: wsAdmin.token });
      const found = (allProjects.data || []).find((p: any) => p.key === createdProjectKey);
      if (found) {
        await raw('DELETE', `/api/admin/projects/${found.id}`, { token: wsAdmin.token });
      }
    }
  }
});

test('R1-02-02 — init-static-no-ai', async ({ page }) => {
  test.setTimeout(360_000);

  const start = Date.now();
  const runId = Math.random().toString(36).substring(2, 7);
  const devKey = keys.developer?.apiKey;
  const wsAdmin = await login(credentials.wsAdmin.email, credentials.wsAdmin.password);

  let serverProcess: { stop: () => Promise<void> } | null = null;
  let createdProjectKey = '';

  try {
    // 1. Scaffold static app
    const appDir = await scaffoldStatic();

    // 2. CLI init with --create 'Fresh static <runId>'
    const initRes = await spawnCli({
      cwd: appDir,
      args: [
        'init',
        '--server',
        SERVER,
        '--key',
        devKey || '',
        '--create',
        `Fresh static ${runId}`,
        '--environment',
        'local',
        '--tool',
        'other',
        '--yes',
        '--json',
      ],
    });

    expect(initRes.code).toBe(0);
    expect(initRes.json?.ok).toBe(true);
    expect(initRes.json?.project?.created).toBe(true);
    expect(initRes.json?.project?.key).toMatch(/^fresh-static-/);
    expect(initRes.json?.injected).toBe(true);
    expect(initRes.json?.routedToSkill).toBe(false);
    expect(initRes.json?.stack?.kind).toBe('static');

    createdProjectKey = initRes.json?.project?.key;

    // 3. Assert index.html contains exactly one marker pair wrapping static script and pointer-feedback
    const indexHtml = await readFile(join(appDir, 'index.html'), 'utf8');
    const startMarkers = indexHtml.match(/<!-- pointer-feedback:start -->/g) || [];
    const endMarkers = indexHtml.match(/<!-- pointer-feedback:end -->/g) || [];
    expect(startMarkers.length).toBe(1);
    expect(endMarkers.length).toBe(1);
    expect(indexHtml).toContain(`<script src="${SERVER}/pointer.js" defer></script>`);
    expect(indexHtml).toContain(
      `<pointer-feedback project="${createdProjectKey}" server="${SERVER}" environment="local" source-attr="data-component-source"></pointer-feedback>`,
    );

    // Decision: assert no .env file was created (static injection writes none)
    expect(existsSync(join(appDir, '.env'))).toBe(false);

    // 4. Start serve-dir on 4174 and drive widget login -> comment flow
    serverProcess = await startStaticServer(appDir, 4174);
    const freshUrl = process.env.FRESH_URL || 'http://localhost:4174';

    await performDeferredLoginCommentFlow(
      page,
      freshUrl,
      credentials.developer.email,
      credentials.developer.password,
      'E2E fresh comment',
    );

    // 5. API assert comment created
    const devAuth = await login(credentials.developer.email, credentials.developer.password);
    const commentsRes = await raw(
      'GET',
      `/api/projects/${createdProjectKey}/comments?view=summary`,
      { token: devAuth.token },
    );
    expect(commentsRes.status).toBe(200);
    const items = commentsRes.data?.items || commentsRes.data || [];
    expect(items.length).toBe(1);
    expect(items[0]?.body).toBe('E2E fresh comment');

    // 6. Wall-clock budget <= 300 s
    const durationMs = Date.now() - start;
    expect(durationMs).toBeLessThanOrEqual(300_000);

    record({
      id: 'R1-02-02',
      tier: 'PR',
      layer: 'cli+widget',
      role: 'developer',
      result: 'PASS',
      ms: durationMs,
      detail: `project=${createdProjectKey}, duration=${Math.round(durationMs / 1000)}s`,
    });
  } finally {
    if (serverProcess) await serverProcess.stop();
    if (createdProjectKey) {
      const allProjects = await raw('GET', '/api/admin/projects', { token: wsAdmin.token });
      const found = (allProjects.data || []).find((p: any) => p.key === createdProjectKey);
      if (found) {
        await raw('DELETE', `/api/admin/projects/${found.id}`, { token: wsAdmin.token });
      }
    }
  }
});

test('R1-02-03 — init-next-handoff', async () => {
  // Respect tier: nightly only, skipped in PR tier
  test.skip(process.env.TIER === 'pr', 'nightly tier only — skipped during PR tier');

  const start = Date.now();
  const runId = Math.random().toString(36).substring(2, 7);
  const devKey = keys.developer?.apiKey;
  const wsAdmin = await login(credentials.wsAdmin.email, credentials.wsAdmin.password);

  let createdProjectKey = '';

  try {
    // 1. Scaffold Next.js app
    const appDir = await scaffoldNext();

    // 2. spawnCli init with --json
    const initRes = await spawnCli({
      cwd: appDir,
      args: [
        'init',
        '--server',
        SERVER,
        '--key',
        devKey || '',
        '--create',
        `Fresh next ${runId}`,
        '--environment',
        'local',
        '--tool',
        'other',
        '--yes',
        '--json',
      ],
    });

    expect(initRes.code).toBe(0);
    expect(initRes.json?.ok).toBe(true);
    expect(initRes.json?.injected).toBe(false);
    expect(initRes.json?.routedToSkill).toBe(true);
    expect(initRes.json?.stack?.kind).toBe('next');

    createdProjectKey = initRes.json?.project?.key;

    // 3. git status --porcelain: every path starts with .pointer/, .agents/, or is .gitignore
    const { stdout: gitStatus } = await execFileAsync('git', ['status', '--porcelain'], {
      cwd: appDir,
    });

    const lines = gitStatus.trim().split('\n').filter(Boolean);
    for (const line of lines) {
      const relPath = line.slice(3).trim();
      const isAllowed =
        relPath.startsWith('.pointer/') ||
        relPath.startsWith('.agents/') ||
        relPath === '.gitignore';
      expect(isAllowed, `Path ${relPath} must not be modified outside allowed set`).toBe(true);
      expect(relPath.startsWith('app/')).toBe(false);
      expect(relPath).not.toBe('package.json');
      expect(relPath.startsWith('next.config.')).toBe(false);
    }

    // 4. spawnCli init again without --json -> stdout contains hand-off message and pointer-init
    const initHuman = await spawnCli({
      cwd: appDir,
      args: [
        'init',
        '--server',
        SERVER,
        '--key',
        devKey || '',
        '--project',
        createdProjectKey,
        '--environment',
        'local',
        '--tool',
        'other',
        '--yes',
      ],
    });

    expect(initHuman.code).toBe(0);
    expect(initHuman.stdout).toContain(
      "ℹ next detected — automatic injection isn't supported for this stack yet.",
    );
    expect(initHuman.stdout).toContain('pointer-init');

    const durationMs = Date.now() - start;
    record({
      id: 'R1-02-03',
      tier: 'nightly',
      layer: 'cli',
      role: 'developer',
      result: 'PASS',
      ms: durationMs,
      detail: `gitStatusLines=${lines.length}`,
    });
  } finally {
    if (createdProjectKey) {
      const allProjects = await raw('GET', '/api/admin/projects', { token: wsAdmin.token });
      const found = (allProjects.data || []).find((p: any) => p.key === createdProjectKey);
      if (found) {
        await raw('DELETE', `/api/admin/projects/${found.id}`, { token: wsAdmin.token });
      }
    }
  }
});
