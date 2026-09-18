// Playwright E2E spec for fresh-app scenarios:
// - R2-00-01 — fresh-app: vite
// - R2-00-02 — fresh-app: static
// - R2-00-03 — fresh-app: angular (skill-routed)
// - R2-00-04 — fresh-app: next (handoff)
// - R2-00-05 — whitelabel: cli-output has no brand leak
// - R2-00-06 ⛓ — whitelabel: widget text has no brand leak
// - R2-00-07 ⛓ — branding restored after whitelabel run
// - R2-00-08 ⛓ — whitelabel: widget title/aria-label have no brand leak
// Contract: docs/roadmap/testing/R2-00-tests.md
import { test, expect, type Page } from '@playwright/test';
import { readFileSync, existsSync, writeFileSync, mkdirSync, readdirSync } from 'node:fs';
import { readFile, stat } from 'node:fs/promises';
import { join, dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { createHash } from 'node:crypto';
import { raw, login } from '../scripts/lib/api.mjs';
import { spawnCli } from '../scripts/lib/cli.mjs';
import { TENANT_OWNER, USERS, SUPER_ADMIN, PORTS } from '../scripts/lib/constants.mjs';
import { record } from '../scripts/lib/report.mjs';
import { preAuthWidget } from '../widget/lib/auth';
import { setBranding } from '../scripts/set-branding.mjs';
import { resetBranding } from '../scripts/reset-branding.mjs';
import { assertBrandingDefault } from '../scripts/assert-branding-default.mjs';
import {
  scaffoldStatic,
  scaffoldVite,
  scaffoldAngular,
  scaffoldNext,
  startStaticServer,
  startVitePreview,
} from './run.mjs';

const execFileAsync = promisify(execFile);

const here = dirname(fileURLToPath(import.meta.url));
const e2eRoot = resolve(here, '..');
const repoRoot = resolve(e2eRoot, '..');
const STATE_DIR = resolve(here, '../state');
const SERVER = process.env.E2E_API_URL || 'http://localhost:8090';
const PREVIEW_PORT = PORTS.freshPreview || PORTS.fresh || 4174;
const PREVIEW_URL = `http://localhost:${PREVIEW_PORT}`;

function sha256(content: string | Buffer): string {
  return createHash('sha256').update(content).digest('hex');
}

type CredentialUser = {
  email: string;
  password: string;
};

function getCredentials() {
  const credPath = join(STATE_DIR, 'credentials.json');
  if (existsSync(credPath)) {
    try {
      return JSON.parse(readFileSync(credPath, 'utf8'));
    } catch {}
  }
  return {
    wsAdmin: TENANT_OWNER,
    developer: USERS.developer,
    superAdmin: SUPER_ADMIN,
  };
}

function getKeys() {
  const keysPath = join(STATE_DIR, 'keys.json');
  if (existsSync(keysPath)) {
    try {
      return JSON.parse(readFileSync(keysPath, 'utf8'));
    } catch {}
  }
  return {
    developer: { apiKey: 'ptr_dev_key_placeholder' },
  };
}

/**
 * Performs deferred login and comment creation following the real widget interaction:
 * element.ts:161-163, 264-268, 649-655.
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

  // Fresh context is collapsed by default (element.ts:161-163, 649-655). Reveal toolbar first.
  const launcher = widget.locator('#pf-launcher');
  if (await launcher.isVisible()) {
    await launcher.click();
  }

  // Click #pf-toggle to open login modal
  await widget.locator('#pf-toggle').click();
  const modalOverlay = widget.locator('.pf-modal-overlay');
  await expect(modalOverlay).toBeVisible({ timeout: 10_000 });

  // Register capture-config response waiter BEFORE submitting login (element.ts:673)
  const cfg = page.waitForResponse((r) => r.url().includes('/capture-config'));

  const emailInput = widget.locator('#pf-email');
  const passwordInput = widget.locator('#pf-password');
  await emailInput.fill(email);
  await passwordInput.fill(pass);

  // Submit login
  await widget.locator('#pf-login-submit').click();
  await cfg;

  // Modal closes
  await expect(modalOverlay).not.toBeVisible({ timeout: 10_000 });

  // Click #pf-add to enter pick mode
  await widget.locator('#pf-add').click();
  await page.locator('h1').first().click({ force: true });

  // Fill comment text and submit
  const popover = page.locator('#pf-popover-host');
  await expect(popover.locator('#pf-comment-text')).toBeVisible({ timeout: 10_000 });
  await popover.locator('#pf-comment-text').fill(commentText);
  await popover.locator('#pf-submit').click();
  await expect(popover).toBeEmpty({ timeout: 10_000 });
}

test('R2-00-01 — fresh-app: vite', async ({ page }) => {
  // Respect tier: nightly only, skipped in PR tier
  test.skip(process.env.TIER === 'pr', 'nightly tier only — skipped during PR tier');
  test.setTimeout(360_000);

  const start = Date.now();
  const runId = Math.random().toString(36).substring(2, 7);
  const creds = getCredentials();
  const devKey = getKeys().developer?.apiKey;
  const wsAdmin = await login(creds.wsAdmin.email, creds.wsAdmin.password);

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

    // 4. Assert files in app
    const indexHtml = await readFile(join(appDir, 'index.html'), 'utf8');
    const startMarkers = indexHtml.match(/<!-- pointer-feedback:start -->/g) || [];
    const endMarkers = indexHtml.match(/<!-- pointer-feedback:end -->/g) || [];
    expect(startMarkers.length).toBe(1);
    expect(endMarkers.length).toBe(1);

    const envContent = await readFile(join(appDir, '.env'), 'utf8');
    expect(envContent).not.toContain('VITE_POINTER_ENABLED');
    expect(envContent).toContain(`VITE_POINTER_SERVER=${SERVER}`);
    expect(envContent).toContain(`VITE_POINTER_PROJECT=${createdProjectKey}`);
    // No environment in .env: the server resolves it from the page origin (only `--environment` pins one).
    expect(envContent).not.toContain('VITE_POINTER_ENV');

    // Run doctor --json
    const docRes = await spawnCli({
      cwd: appDir,
      args: ['doctor', '--json'],
    });
    expect(docRes.code).toBe(0);
    expect(docRes.json?.ok).toBe(true);

    // 5. Build and preview on port 4174
    serverProcess = await startVitePreview(appDir, PREVIEW_PORT);

    // 6. Playwright browser flow
    const freshUrl = process.env.FRESH_URL || PREVIEW_URL;
    const origin = new URL(freshUrl).origin;

    // Gate check
    const gateRes = await raw(
      'GET',
      `/api/public/projects/${createdProjectKey}/widget-status?origin=${encodeURIComponent(origin)}`,
    );
    expect(gateRes.status).toBe(200);
    expect(gateRes.data?.active).toBe(true);

    await performDeferredLoginCommentFlow(
      page,
      freshUrl,
      creds.developer.email,
      creds.developer.password,
      'E2E fresh comment',
    );

    // 7. API assertion as developer
    const devAuth = await login(creds.developer.email, creds.developer.password);
    const commentsRes = await raw(
      'GET',
      `/api/projects/${createdProjectKey}/comments?view=summary&pageSize=10`,
      { token: devAuth.token },
    );
    expect(commentsRes.status).toBe(200);
    const items = commentsRes.data?.items || commentsRes.data || [];
    expect(items.length).toBe(1);
    expect(items[0]?.body).toBe('E2E fresh comment');

    // 8. Wall-clock budget <= 300 s
    const durationMs = Date.now() - start;
    console.log(`[R2-00-01] completed in ${Math.round(durationMs / 1000)}s`);
    expect(durationMs).toBeLessThanOrEqual(300_000);

    record({
      id: 'R2-00-01',
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

test('R2-00-02 — fresh-app: static', async ({ page }) => {
  test.setTimeout(360_000);

  const start = Date.now();
  const runId = Math.random().toString(36).substring(2, 7);
  const creds = getCredentials();
  const devKey = getKeys().developer?.apiKey;
  const wsAdmin = await login(creds.wsAdmin.email, creds.wsAdmin.password);

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

    createdProjectKey = initRes.json?.project?.key;

    // 3. Assert index.html contains script and pointer-feedback before </body>
    const indexHtml = await readFile(join(appDir, 'index.html'), 'utf8');
    const startMarkers = indexHtml.match(/<!-- pointer-feedback:start -->/g) || [];
    const endMarkers = indexHtml.match(/<!-- pointer-feedback:end -->/g) || [];
    expect(startMarkers.length).toBe(1);
    expect(endMarkers.length).toBe(1);
    expect(indexHtml).toContain(`<script src="${SERVER}/widget.js" defer></script>`);
    expect(indexHtml).toContain(`<pointer-feedback project="${createdProjectKey}"`);

    // Doctor check
    const docRes = await spawnCli({
      cwd: appDir,
      args: ['doctor', '--json'],
    });
    expect(docRes.code).toBe(0);
    expect(docRes.json?.ok).toBe(true);

    // 4. Start serve-dir on PREVIEW_PORT and drive deferred-login -> comment flow
    serverProcess = await startStaticServer(appDir, PREVIEW_PORT);
    const freshUrl = process.env.FRESH_URL || PREVIEW_URL;
    const origin = new URL(freshUrl).origin;

    const gateRes = await raw(
      'GET',
      `/api/public/projects/${createdProjectKey}/widget-status?origin=${encodeURIComponent(origin)}`,
    );
    expect(gateRes.status).toBe(200);
    expect(gateRes.data?.active).toBe(true);

    await performDeferredLoginCommentFlow(
      page,
      freshUrl,
      creds.developer.email,
      creds.developer.password,
      'E2E fresh comment',
    );

    // 5. API assert comment created
    const devAuth = await login(creds.developer.email, creds.developer.password);
    const commentsRes = await raw(
      'GET',
      `/api/projects/${createdProjectKey}/comments?view=summary&pageSize=10`,
      { token: devAuth.token },
    );
    expect(commentsRes.status).toBe(200);
    const items = commentsRes.data?.items || commentsRes.data || [];
    expect(items.length).toBe(1);
    expect(items[0]?.body).toBe('E2E fresh comment');

    // 6. Wall-clock budget <= 300 s
    const durationMs = Date.now() - start;
    console.log(`[R2-00-02] completed in ${Math.round(durationMs / 1000)}s`);
    expect(durationMs).toBeLessThanOrEqual(300_000);

    record({
      id: 'R2-00-02',
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

test('R2-00-03 — fresh-app: angular (skill-routed)', async () => {
  test.skip(process.env.TIER === 'pr', 'nightly tier only — skipped during PR tier');

  const start = Date.now();
  const runId = Math.random().toString(36).substring(2, 7);
  const creds = getCredentials();
  const devKey = getKeys().developer?.apiKey;
  const wsAdmin = await login(creds.wsAdmin.email, creds.wsAdmin.password);

  let createdProjectKey = '';

  try {
    // 1. Scaffold Angular app
    const appDir = await scaffoldAngular();

    // 2. Snapshot file list and hash of src/index.html
    const indexPath = join(appDir, 'src', 'index.html');
    const indexBefore = existsSync(indexPath) ? await readFile(indexPath, 'utf8') : '';
    const hashBefore = sha256(indexBefore);

    // 3. init with same flags
    const initRes = await spawnCli({
      cwd: appDir,
      args: [
        'init',
        '--server',
        SERVER,
        '--key',
        devKey || '',
        '--create',
        `Fresh angular ${runId}`,
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

    createdProjectKey = initRes.json?.project?.key;

    // Doctor check
    const docRes = await spawnCli({
      cwd: appDir,
      args: ['doctor', '--json'],
    });
    expect(docRes.code).toBe(0);
    expect(docRes.json?.ok).toBe(true);

    // Check stdout verbatim handoff message via non-json init re-run
    const humanRes = await spawnCli({
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

    // Assert verbatim: /^ℹ angular detected — automatic injection isn't supported for this stack yet\.$/m
    expect(humanRes.stdout).toMatch(/^ℹ angular detected — automatic injection isn't supported for this stack yet\.$/m);
    expect(humanRes.stdout).toContain('.agents/pointer-init');

    // 5. Diff file snapshot; src/index.html byte-identical
    const indexAfter = existsSync(indexPath) ? await readFile(indexPath, 'utf8') : '';
    const hashAfter = sha256(indexAfter);
    expect(hashAfter).toBe(hashBefore);

    const durationMs = Date.now() - start;
    record({
      id: 'R2-00-03',
      tier: 'nightly',
      layer: 'cli',
      role: 'developer',
      result: 'PASS',
      ms: durationMs,
      detail: `project=${createdProjectKey}, hashMatched=true`,
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

test('R2-00-04 — fresh-app: next (handoff)', async () => {
  test.skip(process.env.TIER === 'pr', 'nightly tier only — skipped during PR tier');
  // This test had no timeout at all, so it inherited playwright.config.ts's 30s and could not
  // pass on any nightly run: scaffolding alone took longer than that. scaffoldNext now copies the
  // committed fixture instead, but the budget stays generous — with E2E_REAL_NEXT_GENERATOR=1 it
  // goes back to create-next-app and its full install.
  test.setTimeout(600_000);

  const start = Date.now();
  const runId = Math.random().toString(36).substring(2, 7);
  const creds = getCredentials();
  const devKey = getKeys().developer?.apiKey;
  const wsAdmin = await login(creds.wsAdmin.email, creds.wsAdmin.password);

  let createdProjectKey = '';

  try {
    // 1. Scaffold Next.js app
    const appDir = await scaffoldNext();

    // 2. Snapshot files + sha256 of app/layout.tsx and app/page.tsx
    const layoutPath = join(appDir, 'app', 'layout.tsx');
    const pagePath = join(appDir, 'app', 'page.tsx');
    const layoutBefore = existsSync(layoutPath) ? await readFile(layoutPath, 'utf8') : '';
    const pageBefore = existsSync(pagePath) ? await readFile(pagePath, 'utf8') : '';
    const layoutHashBefore = sha256(layoutBefore);
    const pageHashBefore = sha256(pageBefore);

    // 3. CLI init with --json
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

    createdProjectKey = initRes.json?.project?.key;

    // 4. Git diff check: diff = only .pointer/** + .agents/** + .gitignore
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

    // Both hashes byte-identical
    const layoutAfter = existsSync(layoutPath) ? await readFile(layoutPath, 'utf8') : '';
    const pageAfter = existsSync(pagePath) ? await readFile(pagePath, 'utf8') : '';
    expect(sha256(layoutAfter)).toBe(layoutHashBefore);
    expect(sha256(pageAfter)).toBe(pageHashBefore);

    // Human mode init: verify verbatim message
    const humanRes = await spawnCli({
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

    expect(humanRes.stdout).toMatch(/^ℹ next detected — automatic injection isn't supported for this stack yet\.$/m);

    const durationMs = Date.now() - start;
    record({
      id: 'R2-00-04',
      tier: 'nightly',
      layer: 'cli',
      role: 'developer',
      result: 'PASS',
      ms: durationMs,
      detail: `layoutHash=${layoutHashBefore.slice(0, 8)}, pageHash=${pageHashBefore.slice(0, 8)}`,
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

test('R2-00-05 — whitelabel: cli-output has no brand leak', async () => {
  test.skip(process.env.TIER === 'pr', 'nightly tier only — skipped during PR tier');

  const start = Date.now();
  const runId = Math.random().toString(36).substring(2, 7);
  const creds = getCredentials();
  const devKey = getKeys().developer?.apiKey;
  const saCreds = creds.superAdmin || SUPER_ADMIN;
  const saAuth = await login(saCreds.email, saCreds.password);

  let appDir = '';
  let createdProjectKey = '';

  try {
    // 1. SA PUT /api/admin/branding
    const brandPayload = {
      productName: 'Acme Review',
      tagline: 'Review anything',
      urls: { app: 'https://app.acme.test' },
    };
    const brandPutRes = await raw('PUT', '/api/admin/branding', {
      token: saAuth.token,
      body: brandPayload,
    });
    expect(brandPutRes.status).toBe(200);

    const brandingCheck = await raw('GET', '/api/branding');
    expect(brandingCheck.status).toBe(200);
    expect(brandingCheck.data?.productName).toBe('Acme Review');

    // 2. Scaffold static app
    appDir = await scaffoldStatic();

    // init with --json
    const init1 = await spawnCli({
      cwd: appDir,
      args: [
        'init',
        '--server',
        SERVER,
        '--key',
        devKey || '',
        '--create',
        `Fresh brand ${runId}`,
        '--environment',
        'local',
        '--tool',
        'other',
        '--yes',
        '--json',
      ],
    });
    expect(init1.code).toBe(0);
    expect(init1.json?.product).toBe('Acme Review');
    createdProjectKey = init1.json?.project?.key;

    // init without --json, using --project
    const init2 = await spawnCli({
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

    // doctor in human mode
    const docRes = await spawnCli({
      cwd: appDir,
      args: ['doctor'],
    });

    const wlDir = join(STATE_DIR, 'whitelabel');
    if (!existsSync(wlDir)) mkdirSync(wlDir, { recursive: true });
    const combinedOutput = [
      '=== INIT HUMAN OUTPUT ===',
      init2.stdout,
      init2.stderr,
      '=== DOCTOR HUMAN OUTPUT ===',
      docRes.stdout,
      docRes.stderr,
    ].join('\n');

    const outPath = join(wlDir, 'cli-output.txt');
    writeFileSync(outPath, combinedOutput, 'utf8');

    // 3. Assert file contains Acme Review; count matches of leak regex
    expect(combinedOutput).toContain('Acme Review');
    const leakRegex = /(?<![-\w])Pointer(?![-\w])/g;
    const leakMatches = combinedOutput.match(leakRegex) || [];
    expect(leakMatches.length, `Expected 0 brand leak matches in CLI output, found: ${leakMatches.join(', ')}`).toBe(0);

    // 4. GET /skill.md and /pointer-init.md raw bodies contain server origin
    const skillRes = await fetch(`${SERVER}/skill.md`);
    expect(skillRes.ok).toBe(true);
    const skillText = await skillRes.text();
    expect(skillText).toContain(SERVER);

    const initMdRes = await fetch(`${SERVER}/pointer-init.md`);
    expect(initMdRes.ok).toBe(true);
    const initMdText = await initMdRes.text();
    expect(initMdText).toContain(SERVER);

    const durationMs = Date.now() - start;
    record({
      id: 'R2-00-05',
      tier: 'nightly',
      layer: 'cli',
      role: 'SA, DEV',
      result: 'PASS',
      ms: durationMs,
      detail: `leakMatches=${leakMatches.length}, file=${outPath}`,
    });
  } finally {
    await resetBranding({ token: saAuth.token }).catch(() => {});
    if (createdProjectKey) {
      const wsAdminAuth = await login(creds.wsAdmin.email, creds.wsAdmin.password).catch(() => null);
      if (wsAdminAuth) {
        const allProjects = await raw('GET', '/api/admin/projects', { token: wsAdminAuth.token });
        const found = (allProjects.data || []).find((p: any) => p.key === createdProjectKey);
        if (found) {
          await raw('DELETE', `/api/admin/projects/${found.id}`, { token: wsAdminAuth.token });
        }
      }
    }
  }
});

test('R2-00-06 ⛓ — whitelabel: widget text has no brand leak', async ({ page, browser }) => {
  test.skip(process.env.TIER === 'pr', 'nightly tier only — skipped during PR tier');
  // This one scaffolds an app, runs init against the live API, starts a server and opens a second
  // browser context — the 30s default is a budget for a normal test, not for this.
  test.setTimeout(120_000);

  // KNOWN FAILING (nightly only) — times out in its second browser context.
  //
  // Ruled out: the app IS instrumented (init writes .pointer/config.json and injects the widget —
  // verified on disk after a run), and the brand-leak guarantee this scenario exists to prove is
  // covered by R2-00-08, which passes: the launcher's title and aria-label carry the configured
  // product name and no "Pointer" literal. What remains is this scenario's own sequencing across
  // two contexts and a mid-test re-brand.
  //
  // Left running and red rather than skipped: the white-label promise is load-bearing for this
  // product, and a scenario nobody can see is how the suite got into the state it was found in.

  const start = Date.now();
  const creds = getCredentials();
  const saCreds = creds.superAdmin || SUPER_ADMIN;
  const saAuth = await login(saCreds.email, saCreds.password);
  const devAuth = await login(creds.developer.email, creds.developer.password);

  let serverProcess: { stop: () => Promise<void> } | null = null;
  let secondContext = null;

  try {
    // 1. Establish branding Acme Review
    await setBranding({
      productName: 'Acme Review',
      tagline: 'Review anything',
      urls: { app: 'https://app.acme.test' },
    }, { token: saAuth.token });

    // Serve static app on 4174
    const appDir = await scaffoldStatic();
    // The static template is deliberately un-instrumented — scaffolding alone leaves a page with
    // no widget on it. These scenarios assert what the widget RENDERS, so the app has to be
    // instrumented the way a real user would: by running init, which is the thing under test.
    const initRes = await spawnCli({
      cwd: appDir,
      args: [
        'init', '--server', SERVER, '--key', getKeys().developer?.apiKey || '',
        '--create', `Whitelabel ${Date.now()}`, '--environment', 'local', '--tool', 'other', '--yes', '--json',
      ],
    });
    expect(initRes.code, `init failed: ${initRes.stderr}`).toBe(0);

    serverProcess = await startStaticServer(appDir, PREVIEW_PORT);

    // Browser context created AFTER branding PUT (element.ts:263)
    await preAuthWidget(page, devAuth.token, devAuth.user);
    const cfg = page.waitForResponse((r) => r.url().includes('/capture-config'));
    await page.goto(PREVIEW_URL);
    await cfg;

    // 2. Shadow text: innerText does not match leak regex
    const shadowText = await page.locator('pointer-feedback').innerText();
    const leakRegex = /(?<![-\w])Pointer(?![-\w])/g;
    const leakMatches = shadowText.match(leakRegex) || [];
    expect(leakMatches.length, `Shadow DOM innerText leak count: ${leakMatches.join(', ')}`).toBe(0);

    // 3. Login modal title: second, non-pre-authed context
    secondContext = await browser.newContext();
    const page2 = await secondContext.newPage();
    await page2.goto(PREVIEW_URL);

    const widget2 = page2.locator('pointer-feedback');
    await expect(widget2).toBeAttached({ timeout: 10_000 });
    const launcher2 = widget2.locator('#pf-launcher');
    if (await launcher2.isVisible()) {
      await launcher2.click();
    }
    await widget2.locator('#pf-toggle').click();
    const modalH2 = widget2.locator('.pf-modal h2');
    await expect(modalH2).toBeVisible({ timeout: 10_000 });
    const modalTitle = (await modalH2.innerText()).trim();
    expect(modalTitle).toBe('Acme Review');

    // 4. Toasts carry the brand too. Trigger the REAL one — clicking hide emits
    // `${brand} hidden — click the button to reopen` (element.ts) — rather than poking a method to
    // produce a synthetic toast, which would assert only that the test can write the brand itself.
    await widget2.locator('#pf-hide').click();
    const toastLocator = widget2.locator('.pf-toast');
    await expect(toastLocator).toHaveText(/Acme Review/, { timeout: 5000 });
    await expect(toastLocator).not.toHaveText(/Pointer/);

    const durationMs = Date.now() - start;
    record({
      id: 'R2-00-06',
      tier: 'nightly',
      layer: 'widget',
      role: 'SA, DEV',
      result: 'PASS',
      ms: durationMs,
      detail: `modalTitle=${modalTitle}`,
    });
  } finally {
    if (secondContext) await secondContext.close().catch(() => {});
    if (serverProcess) await serverProcess.stop();
    await resetBranding({ token: saAuth.token }).catch(() => {});
  }
});

test('R2-00-07 ⛓ — branding restored after whitelabel run', async () => {
  const res = await assertBrandingDefault();
  expect(res.public.productName).toBe('Pointer');
  expect(res.admin.urls.app).toBe('https://app.pointer.moamen.work');
});

test('R2-00-08 ⛓ — whitelabel: widget title/aria-label have no brand leak', async ({ page, browser }) => {
  test.skip(process.env.TIER === 'pr', 'nightly tier only — skipped during PR tier');

  const start = Date.now();
  const creds = getCredentials();
  const saCreds = creds.superAdmin || SUPER_ADMIN;
  const saAuth = await login(saCreds.email, saCreds.password);
  const devAuth = await login(creds.developer.email, creds.developer.password);

  let serverProcess: { stop: () => Promise<void> } | null = null;
  let preAuthCtx = null;

  try {
    // Branding PUT: Acme Review
    await setBranding({
      productName: 'Acme Review',
      tagline: 'Review anything',
      urls: { app: 'https://app.acme.test' },
    }, { token: saAuth.token });

    const appDir = await scaffoldStatic();
    // The static template is deliberately un-instrumented — scaffolding alone leaves a page with
    // no widget on it. These scenarios assert what the widget RENDERS, so the app has to be
    // instrumented the way a real user would: by running init, which is the thing under test.
    const initRes = await spawnCli({
      cwd: appDir,
      args: [
        'init', '--server', SERVER, '--key', getKeys().developer?.apiKey || '',
        '--create', `Whitelabel ${Date.now()}`, '--environment', 'local', '--tool', 'other', '--yes', '--json',
      ],
    });
    expect(initRes.code, `init failed: ${initRes.stderr}`).toBe(0);

    serverProcess = await startStaticServer(appDir, PREVIEW_PORT);

    // 1. Collapsed context (no sessionStorage pointer_visible)
    await page.goto(PREVIEW_URL);
    const widget = page.locator('pointer-feedback');
    await expect(widget).toBeAttached({ timeout: 10_000 });
    const launcher = widget.locator('#pf-launcher');
    await expect(launcher).toBeVisible({ timeout: 10_000 });

    const titleAttr = await launcher.getAttribute('title');
    const ariaAttr = await launcher.getAttribute('aria-label');
    expect(titleAttr).toBe('Open Acme Review feedback');
    expect(ariaAttr).toBe('Open Acme Review feedback');

    // 2. Expanded context (preAuthWidget sets pointer_visible)
    preAuthCtx = await browser.newContext();
    const page2 = await preAuthCtx.newPage();
    await preAuthWidget(page2, devAuth.token, devAuth.user);
    const cfg = page2.waitForResponse((r) => r.url().includes('/capture-config'));
    await page2.goto(PREVIEW_URL);
    await cfg;

    // Evaluate every element in shadow root with title or aria-label
    const leakedAttrs = await page2.evaluate(() => {
      const widgetEl = document.querySelector('pointer-feedback');
      if (!widgetEl?.shadowRoot) return [];
      const elements = widgetEl.shadowRoot.querySelectorAll('[title], [aria-label]');
      const leaks: string[] = [];
      const regex = /(?<![-\w])Pointer(?![-\w])/g;
      elements.forEach((el) => {
        const t = el.getAttribute('title') || '';
        const a = el.getAttribute('aria-label') || '';
        if (regex.test(t)) leaks.push(`title: ${t}`);
        regex.lastIndex = 0;
        if (regex.test(a)) leaks.push(`aria-label: ${a}`);
        regex.lastIndex = 0;
      });
      return leaks;
    });
    expect(leakedAttrs.length, `Leaked attributes found: ${leakedAttrs.join(', ')}`).toBe(0);

    // 3. Source check: grep web-component/src/templates.ts for "Open Pointer feedback"
    const templatesPath = join(repoRoot, 'web-component', 'src', 'templates.ts');
    if (existsSync(templatesPath)) {
      const tplSource = await readFile(templatesPath, 'utf8');
      const matches = tplSource.match(/"Open Pointer feedback"/g) || [];
      expect(matches.length, 'templates.ts must not contain literal "Open Pointer feedback"').toBe(0);
    }

    const durationMs = Date.now() - start;
    record({
      id: 'R2-00-08',
      tier: 'nightly',
      layer: 'widget',
      role: 'SA, DEV',
      result: 'PASS',
      ms: durationMs,
      detail: `launcherTitle="${titleAttr}"`,
    });
  } finally {
    if (preAuthCtx) await preAuthCtx.close().catch(() => {});
    if (serverProcess) await serverProcess.stop();
    await resetBranding({ token: saAuth.token }).catch(() => {});
  }
});

/**
 * R1-02-02 — the static injection contract, asserted exactly.
 *
 * R2-00-02 already drives the whole static path end to end (scaffold → init → serve → browser
 * login → comment → API). This scenario owns something narrower that the end-to-end run only
 * checks loosely: what the injected block CONTAINS, character for character, and what init must
 * NOT leave behind.
 *
 * It deliberately re-scaffolds rather than chaining onto R2-00-02's directory. Scaffolding a
 * static app is a file copy and init is a single CLI call, so a private fixture costs a few
 * seconds — far less than the ordering constraint of sharing one, and it keeps this scenario
 * runnable on its own with `-g R1-02-02`.
 */
test('R1-02-02 — init-static-no-ai', async () => {
  test.setTimeout(120_000);

  const start = Date.now();
  const runId = Math.random().toString(36).substring(2, 7);
  const creds = getCredentials();
  const devKey = getKeys().developer?.apiKey;
  const wsAdmin = await login(creds.wsAdmin.email, creds.wsAdmin.password);

  let createdProjectKey = '';
  try {
    const appDir = await scaffoldStatic();

    const initRes = await spawnCli({
      cwd: appDir,
      args: [
        'init', '--server', SERVER, '--key', devKey || '',
        '--create', `Fresh static ${runId}`,
        '--environment', 'local', '--tool', 'other', '--yes', '--json',
      ],
    });

    expect(initRes.code).toBe(0);
    expect(initRes.json?.ok).toBe(true);
    expect(initRes.json?.stack?.kind).toBe('static');
    // "no-ai": a static page needs no skill hand-off, so init must do the work itself.
    expect(initRes.json?.routedToSkill).toBe(false);
    expect(initRes.json?.injected).toBe(true);
    createdProjectKey = initRes.json?.project?.key;
    expect(createdProjectKey).toMatch(/^fresh-static-/);

    // The marker pair must wrap EXACTLY the two documented lines. Comparing the slice between the
    // markers — rather than asserting the file merely contains each line — is what catches an
    // injector that also writes something undocumented in there.
    const indexHtml = await readFile(join(appDir, 'index.html'), 'utf8');
    const block = indexHtml.match(
      /<!-- pointer-feedback:start -->([\s\S]*?)<!-- pointer-feedback:end -->/,
    );
    expect(block, 'no marker pair found in index.html').not.toBeNull();

    const injected = block![1].trim();
    const expected = [
      `<script src="${SERVER}/widget.js" defer></script>`,
      `<pointer-feedback project="${createdProjectKey}" server="${SERVER}" environment="local"></pointer-feedback>`,
    ].join('\n');
    expect(injected.replace(/\n\s+/g, '\n')).toBe(expected);

    // Static injection carries its config in the element's attributes, so there is nothing for a
    // .env to hold. Writing one anyway would be a stray file in the user's project root.
    expect(existsSync(join(appDir, '.env')), '.env must not be created for a static stack').toBe(false);
    expect(existsSync(join(appDir, '.env.local')), '.env.local must not be created either').toBe(false);

    const ms = Date.now() - start;
    record({
      id: 'R1-02-02', tier: 'PR', layer: 'cli', role: 'developer', result: 'PASS', ms,
      detail: `project=${createdProjectKey}; marker block exact; no .env written`,
    });
  } finally {
    if (createdProjectKey) {
      const allProjects = await raw('GET', '/api/admin/projects', { token: wsAdmin.token });
      const found = (allProjects.data || []).find((p: any) => p.key === createdProjectKey);
      if (found) await raw('DELETE', `/api/admin/projects/${found.id}`, { token: wsAdmin.token });
    }
  }
});

/**
 * R1-02-01 — what `init` writes into a Vite app, down to the file modes.
 *
 * R2-00-01 proves the vite path works end to end (build + preview + a real comment). This owns
 * the part an end-to-end run cannot see: that the env file has exactly one of each key, that the
 * credentials file is 0600 and the helper script 0755, that the frozen .gitignore block is
 * present with its negations, and that the skill files land for `--tool other`.
 *
 * Nightly, because scaffolding through the pinned generator hits the network.
 */
test('R1-02-01 — init-vite-no-ai', async () => {
  test.skip(process.env.TIER === 'pr', 'nightly tier only — the pinned generator hits the network');
  test.setTimeout(300_000);

  const start = Date.now();
  const runId = Math.random().toString(36).substring(2, 7);
  const creds = getCredentials();
  const devKey = getKeys().developer?.apiKey;
  const wsAdmin = await login(creds.wsAdmin.email, creds.wsAdmin.password);

  let createdProjectKey = '';
  try {
    const appDir = await scaffoldVite();

    const initRes = await spawnCli({
      cwd: appDir,
      args: [
        'init', '--server', SERVER, '--key', devKey || '',
        '--create', `Fresh vite ${runId}`,
        '--environment', 'local', '--tool', 'other', '--yes', '--json',
      ],
    });

    expect(initRes.code).toBe(0);
    expect(initRes.json?.ok).toBe(true);
    expect(initRes.json?.project?.created).toBe(true);
    expect(initRes.json?.injected).toBe(true);
    expect(initRes.json?.routedToSkill).toBe(false);
    expect(initRes.json?.product).toBe('Pointer');
    expect(String(initRes.json?.cliVersion || '')).not.toBe('');
    createdProjectKey = initRes.json?.project?.key;
    expect(createdProjectKey).toMatch(/^fresh-vite-/);

    // index.html: exactly one marker pair, wrapping the env-guarded snippet.
    const indexHtml = await readFile(join(appDir, 'index.html'), 'utf8');
    expect((indexHtml.match(/<!-- pointer-feedback:start -->/g) || []).length).toBe(1);
    expect((indexHtml.match(/<!-- pointer-feedback:end -->/g) || []).length).toBe(1);
    const block = indexHtml.match(
      /<!-- pointer-feedback:start -->([\s\S]*?)<!-- pointer-feedback:end -->/,
    )![1];
    expect(block).toContain("'%VITE_POINTER_SERVER%'.indexOf('http') === 0");
    expect(block).toContain("document.createElement('pointer-feedback')");
    expect(block).toContain('data-component-source');

    // .env: exactly ONE of each key. A second occurrence is the failure mode that matters here —
    // re-running init must not append a duplicate the bundler then resolves unpredictably.
    const env = await readFile(join(appDir, '.env'), 'utf8');
    for (const [key, value] of [
      ['VITE_POINTER_SERVER', SERVER],
      ['VITE_POINTER_PROJECT', createdProjectKey],
    ]) {
      const hits = env.split('\n').filter((l) => l.trim().startsWith(`${key}=`));
      expect(hits, `${key} must appear exactly once in .env`).toHaveLength(1);
      expect(hits[0].trim()).toBe(`${key}=${value}`);
    }

    // Modes. The credentials file holds an API key, so 0600 is the whole point of writing it to
    // disk rather than leaving it in the shell history; the helper must stay executable.
    const credPath = join(appDir, '.pointer', 'credentials.env');
    const credStat = await stat(credPath);
    expect(credStat.mode & 0o777, '.pointer/credentials.env must be 0600').toBe(0o600);
    expect(await readFile(credPath, 'utf8')).toContain(`POINTER_API_KEY=${devKey}`);
    const shStat = await stat(join(appDir, '.pointer', 'pointer.sh'));
    expect(shStat.mode & 0o777, '.pointer/pointer.sh must be 0755').toBe(0o755);

    // --tool other writes both skills for the agent to find.
    expect(existsSync(join(appDir, '.agents', 'pointer-init', 'SKILL.md'))).toBe(true);
    expect(existsSync(join(appDir, '.agents', 'pointer-feedback', 'SKILL.md'))).toBe(true);
    expect(existsSync(join(appDir, '.pointer', 'stack.json'))).toBe(true);

    // The frozen .gitignore block: the negations are what keep the shareable config in the repo
    // while the credentials stay out.
    const gitignore = await readFile(join(appDir, '.gitignore'), 'utf8');
    expect(gitignore).toContain('!.pointer/config.json');
    expect(gitignore).toContain('!.pointer/stack.json');

    const ms = Date.now() - start;
    record({
      id: 'R1-02-01', tier: 'nightly', layer: 'cli', role: 'developer', result: 'PASS', ms,
      detail: `project=${createdProjectKey}; env keys unique; credentials 0600; pointer.sh 0755`,
    });
  } finally {
    if (createdProjectKey) {
      const allProjects = await raw('GET', '/api/admin/projects', { token: wsAdmin.token });
      const found = (allProjects.data || []).find((p: any) => p.key === createdProjectKey);
      if (found) await raw('DELETE', `/api/admin/projects/${found.id}`, { token: wsAdmin.token });
    }
  }
});

/**
 * R1-02-03 — Next.js hands off, and touches nothing it does not own.
 *
 * The handoff itself is R2-00-04's. What this adds is the proof that a stack init cannot inject
 * still leaves the app exactly as it found it: git says the only new paths are .pointer/,
 * .agents/ and .gitignore. Nothing under app/, no package.json, no next.config.
 */
test('R1-02-03 — init-next-handoff', async () => {
  test.skip(process.env.TIER === 'pr', 'nightly tier only — create-next-app hits the network');
  test.setTimeout(600_000);

  const start = Date.now();
  const runId = Math.random().toString(36).substring(2, 7);
  const creds = getCredentials();
  const devKey = getKeys().developer?.apiKey;
  const wsAdmin = await login(creds.wsAdmin.email, creds.wsAdmin.password);

  let createdProjectKey = '';
  try {
    // scaffoldNext already establishes the clean git baseline the contract asks for (rm -rf .git,
    // git init, identity, add -A, commit) — which is what makes the porcelain check below exact.
    const appDir = await scaffoldNext();

    const args = [
      'init', '--server', SERVER, '--key', devKey || '',
      '--create', `Fresh next ${runId}`,
      '--environment', 'local', '--tool', 'other', '--yes',
    ];

    const initRes = await spawnCli({ cwd: appDir, args: [...args, '--json'] });
    expect(initRes.code).toBe(0);
    expect(initRes.json?.ok).toBe(true);
    expect(initRes.json?.injected).toBe(false);
    expect(initRes.json?.routedToSkill).toBe(true);
    expect(initRes.json?.stack?.kind).toBe('next');
    createdProjectKey = initRes.json?.project?.key;

    // Porcelain lines look like "?? .pointer/" — strip the 2-char status and the space before
    // comparing, or every path fails the startsWith check for the wrong reason.
    const { stdout } = await execFileAsync('git', ['status', '--porcelain'], { cwd: appDir });
    const paths = stdout.split('\n').map((l) => l.slice(3)).filter(Boolean);
    expect(paths.length, 'init must change something').toBeGreaterThan(0);
    for (const p of paths) {
      const owned = p.startsWith('.pointer/') || p.startsWith('.agents/') || p === '.gitignore';
      expect(owned, `init touched a path it does not own: ${p}`).toBe(true);
    }

    // The human-facing half of the handoff: run again without --json and read the message a
    // developer actually sees.
    //
    // --project, not --create. Re-running with --create asks the server to make the same project a
    // second time and exits 3 ("Key already exists, choose another") — which is correct behaviour,
    // just not what re-running init looks like for a developer who already has one.
    const rerun = [
      'init', '--server', SERVER, '--key', devKey || '',
      '--project', createdProjectKey,
      '--environment', 'local', '--tool', 'other', '--yes',
    ];
    const plain = await spawnCli({ cwd: appDir, args: rerun });
    expect(plain.code, plain.stderr).toBe(0);
    const out = `${plain.stdout || ''}${plain.stderr || ''}`;
    expect(out).toContain("next detected — automatic injection isn't supported for this stack yet.");
    expect(out).toContain('pointer-init');

    const ms = Date.now() - start;
    record({
      id: 'R1-02-03', tier: 'nightly', layer: 'cli', role: 'developer', result: 'PASS', ms,
      detail: `project=${createdProjectKey}; ${paths.length} path(s) changed, all owned by init`,
    });
  } finally {
    if (createdProjectKey) {
      const allProjects = await raw('GET', '/api/admin/projects', { token: wsAdmin.token });
      const found = (allProjects.data || []).find((p: any) => p.key === createdProjectKey);
      if (found) await raw('DELETE', `/api/admin/projects/${found.id}`, { token: wsAdmin.token });
    }
  }
});
