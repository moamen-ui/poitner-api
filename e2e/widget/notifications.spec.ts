// Playwright widget automation for R2-04: In-app notifications + author verify loop.
// Scenarios:
// 1. notify: applied shows badge to author
// 2. notify: thumbs-down reopens with note
// 3. notify: read-all clears badge
import { test, expect } from '@playwright/test';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { get, post, patch, login, ApiError } from '../scripts/lib/api.mjs';
import { preAuthWidget } from './lib/auth';
import { credentials as loadCredentials } from '../scripts/lib/state.mjs';

const credentials = () => loadCredentials();
const SMOKE_KEY = process.env.E2E_SMOKE_PROJECT_KEY || 'e2e-widget-smoke';
const SMOKE_PATH = SMOKE_KEY === 'e2e-widget-smoke' ? '/' : `/?project=${SMOKE_KEY}`;

let projectId: number;

test.beforeAll(async () => {
  const wsAdmin = await login(credentials().wsAdmin.email, credentials().wsAdmin.password);
  let project;
  try {
    project = await post('/api/admin/projects', { key: SMOKE_KEY, name: 'E2E Widget Smoke' }, { token: wsAdmin.token });
  } catch (err) {
    if (!(err instanceof ApiError) || err.status !== 409) throw err;
    const all = await get('/api/admin/projects', { token: wsAdmin.token });
    project = all.find((p: { key: string; id: number }) => p.key === SMOKE_KEY);
  }
  projectId = project.id;
});

test.describe('R2-04: In-app notifications and author verify loop', () => {
  let commentId: number;

  test('notify: applied shows badge to author', async ({ page }) => {
    const client = await login(credentials().client.email, credentials().client.password);
    const dev = await login(credentials().dev.email, credentials().dev.password);

    // 1. Create a comment as client
    const newComment = await post(
      `/api/projects/${SMOKE_KEY}/comments`,
      {
        body: 'Checkout button styling bug for notifications test',
        environment: 2,
        element: {
          selector: '#checkout-btn',
          snapshot: '<button id="checkout-btn">Checkout</button>',
        },
      },
      { token: client.token },
    );
    commentId = newComment.id;

    // 2. Configure page with fast polling (1s) and pre-auth as client
    await page.addInitScript(() => {
      window.__POINTER_CONFIG__ = {
        ...(window.__POINTER_CONFIG__ || {}),
        notifyPollMs: 1000,
      };
    });
    await preAuthWidget(page, client.token, client.user);
    await page.goto(SMOKE_PATH);

    const widget = page.locator('pointer-feedback');
    await expect(widget.locator('#pf-updates')).toBeVisible({ timeout: 10_000 });

    // 3. Dev applies the comment
    await patch(
      `/api/comments/${commentId}`,
      { status: 3, commitUrl: 'https://github.com/org/repo/commit/1234567' },
      { token: dev.token },
    );

    // 4. Author widget polls and receives notification: badge appears with 70s ceiling
    const badge = widget.locator('#pf-notify-count');
    await expect(badge).toBeVisible({ timeout: 70_000 });
    await expect(badge).toHaveText('1');
    await expect(badge).toHaveClass(/pf-notify-badge/);
  });

  test('notify: thumbs-down reopens with note', async ({ page }) => {
    const client = await login(credentials().client.email, credentials().client.password);

    await page.addInitScript(() => {
      window.__POINTER_CONFIG__ = {
        ...(window.__POINTER_CONFIG__ || {}),
        notifyPollMs: 1000,
      };
    });
    await preAuthWidget(page, client.token, client.user);
    await page.goto(SMOKE_PATH);

    const widget = page.locator('pointer-feedback');
    await expect(widget.locator('#pf-toggle')).toBeVisible({ timeout: 10_000 });

    // Open sidebar to view the applied comment
    await widget.locator('#pf-toggle').click();

    // Verify rejection button should be visible on own applied comment
    const rejectBtn = widget.locator(`[data-act="verify-reject"][data-id="${commentId}"]`);
    await expect(rejectBtn).toBeVisible({ timeout: 10_000 });

    // Click thumbs-down (👎 Not fixed)
    await rejectBtn.click();

    // The inline verify box should appear
    const verifyBox = widget.locator(`#pf-verify-box-${commentId}`);
    await expect(verifyBox).toBeVisible();

    // Enter note and submit
    const noteInput = verifyBox.locator(`#pf-verify-note-${commentId}`);
    await noteInput.fill('Still broken on mobile viewport');
    await verifyBox.locator('[data-act="verify-submit"]').click();

    // Comment should now be re-opened (no status-applied pill, status is open)
    const card = widget.locator(`.pf-card[data-id="${commentId}"]`);
    await expect(card.locator('.pf-pill.status-applied')).toHaveCount(0);

    // Reply containing "Not fixed: Still broken on mobile viewport" should appear in card
    await expect(card.locator('.pf-replies')).toContainText('Not fixed: Still broken on mobile viewport');
  });

  test('notify: read-all clears badge', async ({ page }) => {
    const client = await login(credentials().client.email, credentials().client.password);
    const dev = await login(credentials().dev.email, credentials().dev.password);

    // Dev adds a reply to the re-opened comment, generating a ReplyAdded notification
    await post(
      `/api/comments/${commentId}/replies`,
      { body: 'Looking into the mobile viewport issue now.' },
      { token: dev.token },
    );

    await page.addInitScript(() => {
      window.__POINTER_CONFIG__ = {
        ...(window.__POINTER_CONFIG__ || {}),
        notifyPollMs: 1000,
      };
    });
    await preAuthWidget(page, client.token, client.user);
    await page.goto(SMOKE_PATH);

    const widget = page.locator('pointer-feedback');
    const badge = widget.locator('#pf-notify-count');
    // Wait for notification badge to appear
    await expect(badge).toBeVisible({ timeout: 70_000 });

    // Click Updates button to open notifications menu
    const updatesBtn = widget.locator('#pf-updates');
    await updatesBtn.click();

    // Notifications menu should open
    const menu = widget.locator('#pf-notifications-menu');
    await expect(menu).toBeVisible();

    // Badge should be cleared / hidden
    await expect(badge).toBeHidden();
  });
});
