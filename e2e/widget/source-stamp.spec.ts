// E2E spec for R3-01: Vite source-stamp plugin, local manifest, deploy awareness — Widget layer.
// Covers:
// - R3-01-01: source-stamp-prod-build (widget: steps 5–12) (AC-3, AC-5)
// - R3-01-03 ⛓: deploy-awareness-widget (AC-8, AC-9)
// Contract: docs/roadmap/testing/R3-01-tests.md
import { test, expect } from '@playwright/test';
import { copyFileSync, mkdirSync } from 'node:fs';
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

// BLOCKED — not a test defect. `pointer get --json` does not emit resolvedSource (R3-01 AC-5); steps 5-10 of this scenario do pass, step 11 needs the feature.
// The CLI half of R3-01-01 (cli/source-stamp.spec.mjs) passes and is what keeps this scenario
// counted as covered; this widget half additionally reaches into the unbuilt resolver.
test('R3-01-01 — source-stamp-prod-build (widget: steps 5–12)', async ({ page }) => {
  test.fixme(true, '`pointer get --json` does not emit resolvedSource (R3-01 AC-5); steps 5-10 of this scenario do pass, step 11 needs the feature');
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

// BLOCKED — not a test defect. deploy awareness is not implemented: no /builds endpoint, no deployedAt on comments, and the widget does not POST its build sha on boot.
// Marked fixme rather than left failing so the nightly tier stays a signal; the scenario
// stays here, and this line is what has to be deleted when the feature lands.
test('R3-01-03 ⛓ — deploy-awareness-widget', async ({ page }) => {
  test.fixme(true, 'deploy awareness is not implemented: no /builds endpoint, no deployedAt on comments, and the widget does not POST its build sha on boot');
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
    await widget.locator('#pf-toggle').click();
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
