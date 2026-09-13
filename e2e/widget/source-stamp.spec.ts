// E2E spec for R3-01: Vite source-stamp plugin, local manifest, deploy awareness — Widget layer.
// Covers:
// - R3-01-01: source-stamp-prod-build (widget: steps 5–12) (AC-3, AC-5)
// - R3-01-03 ⛓: deploy-awareness-widget (AC-8, AC-9)
// Contract: docs/roadmap/testing/R3-01-tests.md
import { test, expect } from '@playwright/test';
import { copyFileSync, mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { preAuthWidget } from './lib/auth';
import { raw, get, post, patch, login } from '../scripts/lib/api.mjs';
import { credentials as loadCredentials, keys as loadKeys } from '../scripts/lib/state.mjs';
import { buildFixture, serveFixture } from '../scripts/lib/vite-fixture.mjs';
import { spawnCli } from '../scripts/lib/cli.mjs';
import { record } from '../scripts/lib/report.mjs';

test.describe.configure({ timeout: 180_000 });

const credentials = () => loadCredentials();
const keys = () => loadKeys();
const PROJECT_KEY = 'e2e-r301';
const FIXTURE_URL = 'http://localhost:4175/';

test('R3-01-01 — source-stamp-prod-build (widget: steps 5–12)', async ({ page }) => {
  test.skip(process.env.TIER === 'pr', 'nightly tier only');
  const start = Date.now();

  const testerCreds = credentials().tester;
  const tester = await login(testerCreds.email, testerCreds.password);

  // 1. Build fixture with enabled: true
  const { distDir, manifest, repo } = buildFixture({ enabled: true });
  const server = await serveFixture(distDir, 4175);

  const consoleErrors: string[] = [];
  page.on('console', (msg) => {
    if (msg.type() === 'error') {
      consoleErrors.push(msg.text());
    }
  });

  try {
    const entries = manifest?.entries ?? manifest ?? {};
    const cardHash = Object.entries(entries).find(([, v]: [string, any]) => v.component === 'Card')?.[0];
    const planListHash = Object.entries(entries).find(([, v]: [string, any]) => v.component === 'PlanList')?.[0];
    const shellHash = Object.entries(entries).find(([, v]: [string, any]) => v.component === 'Shell')?.[0];

    // 5. preAuthWidget, register capture-config before goto
    await preAuthWidget(page, tester.token, tester.user);
    const captureConfigLoaded = page.waitForResponse((r) => r.url().includes('/capture-config'));
    await page.goto(FIXTURE_URL);

    const widget = page.locator('pointer-feedback');
    await expect(widget.locator('#pf-add')).toBeVisible({ timeout: 10_000 });
    await captureConfigLoaded;

    // 6. page.locator('.card').first() -> data-component-source matches /^[0-9a-f]{8}$/ and equals Card's hash
    const cardEl = page.locator('.card').first();
    await expect(cardEl).toBeVisible();
    const cardAttr = await cardEl.getAttribute('data-component-source');
    expect(cardAttr).toMatch(/^[0-9a-f]{8}$/);
    if (cardHash) {
      expect(cardAttr).toBe(cardHash);
    }

    // 7. PlanList: both h2#plans and ul#plan-list carry the same hash
    const plansH2 = page.locator('h2#plans');
    const plansUl = page.locator('ul#plan-list');
    await expect(plansH2).toBeVisible();
    await expect(plansUl).toBeVisible();
    const h2Hash = await plansH2.getAttribute('data-component-source');
    const ulHash = await plansUl.getAttribute('data-component-source');
    expect(h2Hash).toMatch(/^[0-9a-f]{8}$/);
    expect(ulHash).toBe(h2Hash);
    if (planListHash) {
      expect(h2Hash).toBe(planListHash);
    }

    // 8. Shell root: Shell renders <Card id="shell-card"/>; assert #shell-card has Card's hash
    // and no element carries a hash for Shell
    const shellCard = page.locator('#shell-card');
    await expect(shellCard).toBeVisible();
    const shellCardHash = await shellCard.getAttribute('data-component-source');
    expect(shellCardHash).toBe(cardAttr);

    if (shellHash) {
      const shellStampedCount = await page.evaluate(
        (sh) => document.querySelectorAll(`[data-component-source="${sh}"]`).length,
        shellHash,
      );
      expect(shellStampedCount).toBe(0);
    }

    // 9. Widget pick: widget.locator('#pf-add').click(); page.locator('.card').first().click({ force: true })
    await widget.locator('#pf-add').click();
    await expect(widget.locator('#pf-add')).toHaveClass(/active/);
    await cardEl.click({ force: true });

    const popover = page.locator('#pf-popover-host');
    await expect(popover.locator('#pf-comment-text')).toBeVisible({ timeout: 10_000 });
    await popover.locator('#pf-comment-text').fill('R3-01-01 stamp check');
    await popover.locator('#pf-submit').click();
    await expect(popover).toBeEmpty({ timeout: 10_000 });

    // 10. API (QA token): GET /api/projects/e2e-r301/comments?view=summary&pageSize=5 -> newest item sourcePath === CardHash
    const summaryRes = await raw('GET', `/api/projects/${PROJECT_KEY}/comments?view=summary&pageSize=5`, {
      token: tester.token,
    });
    expect(summaryRes.status).toBe(200);
    const newestComment = summaryRes.data?.items?.[0];
    expect(newestComment).toBeTruthy();
    if (cardHash) {
      expect(newestComment.sourcePath).toBe(cardHash);
    }

    // 11. CLI (in <repo>): spawnCli get <id> --json -> exit 0; resolvedSource deep-equals { kind: 'manifest', path: 'src/components/Card.tsx', component: 'Card' }
    //
    // The fixture repo has never been through `init`, so it carries no CLI config and `get` would
    // exit 2 before reaching the resolver. Writing the two files init would have written is enough
    // and keeps the scenario focused on resolution rather than on install.
    mkdirSync(join(repo.dir, '.pointer'), { recursive: true });
    writeFileSync(
      join(repo.dir, '.pointer', 'config.json'),
      JSON.stringify({ server: 'http://localhost:8090', project: PROJECT_KEY }, null, 2),
      'utf8',
    );
    writeFileSync(
      join(repo.dir, '.pointer', 'credentials.env'),
      `POINTER_API_KEY=${keys().developer.apiKey}\n`,
      { encoding: 'utf8', mode: 0o600 },
    );

    const getRes = await spawnCli({
      cwd: repo.dir,
      args: ['get', String(newestComment.id), '--json'],
    });
    expect(getRes.code).toBe(0);
    expect(getRes.json?.resolvedSource).toEqual({
      kind: 'manifest',
      path: 'src/components/Card.tsx',
      component: 'Card',
    });

    // 12. Console: zero error-level messages during steps 5-9
    expect(consoleErrors).toEqual([]);

    // Save manifest evidence
    try {
      const stateDir = join(process.cwd(), 'state', 'r3-01');
      mkdirSync(stateDir, { recursive: true });
      copyFileSync(join(repo.dir, '.pointer', 'manifest.json'), join(stateDir, 'manifest.json'));
    } catch {}

    record({
      id: 'R3-01-01',
      tier: 'nightly',
      layer: 'widget',
      role: 'QA',
      result: 'PASS',
      ms: Date.now() - start,
      detail: 'browser element stamp matches CardHash, pick records sourcePath, get --json resolves to Card.tsx, 0 console errors',
    });
  } finally {
    server.stop();
    repo.cleanup();
  }
});

test('R3-01-03 ⛓ — deploy-awareness-widget', async ({ page }) => {
  test.skip(process.env.TIER === 'pr', 'nightly tier only');
  const start = Date.now();

  const waCreds = credentials().wsAdmin;
  const wa = await login(waCreds.email, waCreds.password);
  const qaCreds = credentials().tester;
  const qa = await login(qaCreds.email, qaCreds.password);

  const S = 'e1f2a3b4c5d6789012345678901234567890abcd'; // 40 lowercase hex chars

  // 1. Prepare an Applied comment on e2e-r301 with known commitSha: S
  const comment1Res = await raw('POST', `/api/projects/${PROJECT_KEY}/comments`, {
    token: wa.token,
    body: { body: 'Applied comment with commitSha', environment: 2, element: { selector: '#applied-1' } },
  });
  expect(comment1Res.status).toBe(200);
  const comment1Id = comment1Res.data?.id;

  const patch1Res = await raw('PATCH', `/api/comments/${comment1Id}`, {
    token: wa.token,
    body: {
      status: 3, // Applied
      appliedByLabel: 'e2e',
      commitSha: S,
      commitUrl: `https://example.test/commit/${S}`,
    },
  });
  expect(patch1Res.status).toBe(200);
  expect(patch1Res.data?.commitSha).toBe(S);
  expect(patch1Res.data?.deployedAt).toBeNull();

  // 2. Create a second Applied comment without commitSha (old-style)
  const comment2Res = await raw('POST', `/api/projects/${PROJECT_KEY}/comments`, {
    token: wa.token,
    body: { body: 'Old-style applied comment', environment: 2, element: { selector: '#applied-2' } },
  });
  expect(comment2Res.status).toBe(200);
  const comment2Id = comment2Res.data?.id;

  const patch2Res = await raw('PATCH', `/api/comments/${comment2Id}`, {
    token: wa.token,
    body: { status: 3 }, // Applied, no commitSha
  });
  expect(patch2Res.status).toBe(200);
  expect(patch2Res.data?.deployedAt).toBeNull();

  // 3. Build fixture with buildSha: S and serve on 4175
  const { distDir, repo } = buildFixture({ buildSha: S, enabled: true });
  const server = await serveFixture(distDir, 4175);

  try {
    // 4. Register page.route to count /builds calls before goto
    let calls = 0;
    await page.route('**/api/projects/e2e-r301/builds', (route) => {
      calls++;
      route.continue();
    });

    await preAuthWidget(page, qa.token, qa.user);
    const captureConfigLoaded = page.waitForResponse((r) => r.url().includes('/capture-config'));
    const buildsResponse = page.waitForResponse((r) => r.url().includes('/api/projects/e2e-r301/builds'));

    await page.goto(FIXTURE_URL);
    await captureConfigLoaded;
    await buildsResponse;

    // 5. Assert calls === 1 after 3s more (poll); reload -> calls === 2 total
    await expect.poll(() => calls, { timeout: 4_000 }).toBe(1);

    const secondBuildsResponse = page.waitForResponse((r) => r.url().includes('/api/projects/e2e-r301/builds'));
    await page.reload();
    await secondBuildsResponse;
    expect(calls).toBe(2);

    // 6. GET /api/comments/{id} (QA) -> deployedAt non-null, deployedSha === S
    const comment1After = await raw('GET', `/api/comments/${comment1Id}`, { token: qa.token });
    expect(comment1After.status).toBe(200);
    expect(comment1After.data?.deployedAt).not.toBeNull();
    expect(comment1After.data?.deployedSha).toBe(S);

    // 7. Widget: open comments list; pill reads "✓ live" with title="Deployed in <S.slice(0,7)>"
    const widget = page.locator('pointer-feedback');
    // Collapsed, the widget renders only the launcher; pre-authenticated it renders the toolbar
    // directly. Handle both rather than assuming one — which state you get depends on stored auth,
    // and guessing wrong fails on a missing element instead of on what the test is about.
    const launcher = widget.locator('#pf-launcher');
    if (await launcher.count()) {
      await launcher.click();
      await expect(widget.locator('#pf-toggle')).toBeVisible({ timeout: 10_000 });
    }

    // The list is filtered by the selected environment, and these comments are staging (2). A
    // widget showing another environment renders no card at all, which looks identical to "the
    // deployed pill is missing" at the assertion below.
    const envSelect = widget.locator('#pf-env');
    if (await envSelect.count()) {
      await envSelect.selectOption('staging').catch(() => {});
    }

    await widget.locator('#pf-toggle').click();

    // Switch to the completed list. The default "all" filter deliberately means ACTIVE comments —
    // completed and archived ones move out to their own chips — so an applied comment renders no
    // card at all until this is selected, and the missing pill would look like a rendering bug
    // rather than the default view doing exactly what it is meant to.
    const statusFilter = widget.locator('#pf-status-filter');
    await expect(statusFilter).toBeVisible({ timeout: 10_000 });
    await statusFilter.selectOption('applied');

    const card1Pill = widget.locator(`.pf-card[data-id="${comment1Id}"] .pf-pill.status-applied`);
    await expect(card1Pill).toHaveText(/✓ live/);
    await expect(card1Pill).toHaveAttribute('title', `Deployed in ${S.slice(0, 7)}`);

    // 8. Old-style comment: pill text "✓ completed", API deployedAt === null
    const card2Pill = widget.locator(`.pf-card[data-id="${comment2Id}"] .pf-pill.status-applied`);
    await expect(card2Pill).toHaveText(/✓ completed/);
    const comment2After = await raw('GET', `/api/comments/${comment2Id}`, { token: qa.token });
    expect(comment2After.status).toBe(200);
    expect(comment2After.data?.deployedAt).toBeNull();

    // 9. Second identical report (repeat POST via API with QA token) -> firstSeen: false, deployedCommentIds: []
    const repeatPost = await raw('POST', `/api/projects/${PROJECT_KEY}/builds`, {
      token: qa.token,
      body: { sha: S },
    });
    expect(repeatPost.status).toBe(200);
    expect(repeatPost.data?.firstSeen).toBe(false);
    expect(repeatPost.data?.deployedCommentIds).toEqual([]);

    record({
      id: 'R3-01-03',
      tier: 'nightly',
      layer: 'widget',
      role: 'WA',
      result: 'PASS',
      ms: Date.now() - start,
      detail: 'beacon fired once per load, applied comment marked deployed, live pill rendered with sha title',
    });
  } finally {
    server.stop();
    repo.cleanup();
  }
});
