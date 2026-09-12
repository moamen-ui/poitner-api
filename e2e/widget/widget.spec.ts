// Real Playwright browser automation against the e2e-widget-smoke project ONLY — kept fully
// separate from e2e-alpha's AI-facing ground truth (see docs/E2E_TEST_PLAN.md, "Architecture").
// Proves the widget->API sync pipeline and that real browser-triggered console/network errors
// land in PageContextSnapshot in the same shape seed.mjs produces synthetically.
import { test, expect } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { get, post, patch, login, ApiError } from '../scripts/lib/api.mjs';
import { preAuthWidget } from './lib/auth';

const here = dirname(fileURLToPath(import.meta.url));
const STATE_DIR = join(here, '..', 'state');
const credentials = JSON.parse(readFileSync(join(STATE_DIR, 'credentials.json'), 'utf8'));

// Overridable so a one-off manual validation run (against a live, non-reset dev DB) can pick a
// fresh key rather than colliding with a soft-deleted row of a prior debug run — the real suite
// always starts from a fully wiped DB via scripts/reset.sh, so this never matters there. The
// fixture page reads the same override via its own ?project= query param (see smoke/index.html).
const SMOKE_KEY = process.env.E2E_SMOKE_PROJECT_KEY || 'e2e-widget-smoke';
const SMOKE_PATH = SMOKE_KEY === 'e2e-widget-smoke' ? '/' : `/?project=${SMOKE_KEY}`;

test.beforeAll(async () => {
  // Reuses seed.mjs's tenant/users (must have already run) — only creates the one extra project.
  // Idempotent: a re-run (or a Playwright worker retry) reuses the project if it already exists.
  const wsAdmin = await login(credentials.wsAdmin.email, credentials.wsAdmin.password);
  let project;
  try {
    project = await post('/api/admin/projects', { key: SMOKE_KEY, name: 'E2E Widget Smoke' }, { token: wsAdmin.token });
  } catch (err) {
    if (!(err instanceof ApiError) || err.status !== 409) throw err;
    const all = await get('/api/admin/projects', { token: wsAdmin.token });
    project = all.find((p) => p.key === SMOKE_KEY);
    if (!project) throw new Error(`e2e-widget-smoke conflicted but wasn't found via list — ${JSON.stringify(err.body)}`);
  }
  await patch(`/api/admin/projects/${project.id}`, { pageContextCaptureEnabled: true }, { token: wsAdmin.token });
});

test('Tester creates a staging bug report by clicking the real broken checkout button', async ({ page }) => {
  const tester = await login(credentials.tester.email, credentials.tester.password);
  await preAuthWidget(page, tester.token, tester.user);

  // element.ts's init() renders the toolbar (#pf-add) BEFORE awaiting fetchCaptureConfig(), which
  // is what actually calls startPageContextCapture() (pagecontext.ts) to patch console.error/
  // window.fetch — so #pf-add being visible does NOT mean capture has started yet. Wait for the
  // specific capture-config response before triggering the error below, or it races ahead of the
  // patch and PageContextSnapshot silently stays null.
  const captureConfigLoaded = page.waitForResponse((r) => r.url().includes('/capture-config'));
  await page.goto(SMOKE_PATH);

  const widget = page.locator('pointer-feedback');
  await expect(widget.locator('#pf-add')).toBeVisible({ timeout: 10_000 });
  await captureConfigLoaded;

  // Trigger the REAL bug with a normal click BEFORE entering pick mode — not after. The widget's
  // picker listens at `document` in the CAPTURE phase and calls stopPropagation() on a pick click,
  // which prevents the underlying page's own click handler (checkout.js) from ever firing. So
  // picking first and clicking the button "through" the picker never actually runs checkout.js —
  // exactly matching how a real user encounters a bug (by using the page) before opening Pointer
  // to report it, not the other way around.
  await page.locator('#checkout-btn').click();

  // Switch environment to Staging before picking (env select only renders when not fixed).
  await widget.locator('#pf-env').selectOption('staging');
  await widget.locator('#pf-add').click();
  await page.locator('#checkout-btn').click({ force: true }); // now just identifies the target element

  const popover = page.locator('#pf-popover-host');
  await expect(popover.locator('#pf-comment-text')).toBeVisible();
  await popover.locator('#pf-comment-text').fill('Confirmed via widget: checkout throws and the quote request fails.');
  await popover.locator('#pf-comment-bug').check();
  await popover.locator('#pf-submit').click();
  await expect(popover).toBeEmpty({ timeout: 10_000 });
});

test('Client creates a comment without an environment switcher, and it syncs correctly to the API', async ({ page }) => {
  const client = await login(credentials.client.email, credentials.client.password);
  await preAuthWidget(page, client.token, client.user);
  await page.goto(SMOKE_PATH);

  const widget = page.locator('pointer-feedback');
  await expect(widget.locator('#pf-add')).toBeVisible({ timeout: 10_000 });

  // A Client (QuickAccess) account deliberately does NOT get the environment switcher: with no
  // explicit EnvironmentSelectorRoleIds the rule is "everyone except Client"
  // (ProjectService.ShowEnvironmentSelectorFor). This test used to call selectOption('production')
  // here, which asked the Client to do something the product forbids — it hung for 30s waiting for
  // a control that is correctly absent. Assert the absence instead, and let the comment take the
  // page's own environment.
  await expect(widget.locator('#pf-env')).toHaveCount(0);

  await widget.locator('#pf-add').click();
  // Picking is active once #pf-add flips to `active` — the same toggle that installs the
  // document-level click listener. The previously-removed selectOption call was providing this
  // wait by accident.
  await expect(widget.locator('#pf-add')).toHaveClass(/active/);
  await page.locator('#join-btn').click({ force: true });

  const popover = page.locator('#pf-popover-host');
  await expect(popover.locator('#pf-comment-text')).toBeVisible();
  await popover.locator('#pf-comment-text').fill('Widget-created client comment for sync verification.');
  await popover.locator('#pf-submit').click();
  await expect(popover).toBeEmpty({ timeout: 10_000 });

  // Sync verification via a staff token: the Client (QuickAccess) only ever sees its OWN
  // comments (confirmed product behavior, not a bug — CommentService.ListAsync scopes
  // IsQuickAccess callers to AuthorId == callerId), so the Tester's bug report is checked here,
  // not through the Client's own fetch.
  const wsAdmin = await login(credentials.wsAdmin.email, credentials.wsAdmin.password);
  const res = await get(`/api/projects/${SMOKE_KEY}/comments?pageSize=100`, { token: wsAdmin.token });
  const items = res.items as Array<Record<string, any>>;

  const bugReport = items.find((c) => c.isBugReport === true);
  expect(bugReport, "the Tester's bug-report comment must exist and be visible to staff").toBeTruthy();
  expect(bugReport.element?.selector).toContain('checkout-btn');
  // pageContexts is a separate dictionary on the paged response, keyed by pageContextId (not
  // nested inside each comment item) — see CommentService.ListAsync / PagedData.
  expect(bugReport.pageContextId, 'the bug report must reference a PageContextSnapshot').toBeTruthy();
  const snapshot = res.pageContexts?.[bugReport.pageContextId];
  expect(snapshot, 'PageContextSnapshot must be populated from the real browser error').toBeTruthy();

  // Identify the Client's comment by its anchor, not by environment: with no switcher offered to a
  // Client the comment takes whatever environment the page resolves to, so asserting `=== 3`
  // (Production) only ever passed when the test was wrongly driving a control Clients never see.
  const clientComment = items.find((c) => c.element?.selector?.includes('join-btn'));
  expect(clientComment, "the Client's comment must exist and be visible to staff").toBeTruthy();
  expect(clientComment.authorId, "it must be attributed to the Client").toBe(client.user.id);

  // And confirm the Client's own fetch is correctly scoped to just their own comment.
  const clientView = await get(`/api/projects/${SMOKE_KEY}/comments?pageSize=100`, { token: client.token });
  const clientItems = clientView.items as Array<Record<string, any>>;
  expect(clientItems.every((c) => c.authorId === client.user.id), 'Client fetch must only ever contain their own comments').toBe(true);
});
