import { expect, type Page } from '@playwright/test';

/**
 * Signs a user into the widget by pre-seeding the storage keys element.ts reads at connect time,
 * and reveals the collapsed launcher.
 *
 * Driving the real login modal would be more faithful, but every widget scenario would then pay
 * for it in setup time and share a single point of flake. The keys below are the widget's own
 * confirmed mechanism, not a test-only backdoor: `pointer_token` / `pointer_user` in localStorage
 * and `pointer_visible` in sessionStorage.
 *
 * Must be called BEFORE page.goto — addInitScript only applies to subsequent navigations.
 */
export async function preAuthWidget(page: Page, token: string, user: unknown): Promise<void> {
  await page.addInitScript(
    ([t, u]) => {
      window.localStorage.setItem('pointer_token', t as string);
      window.localStorage.setItem('pointer_user', JSON.stringify(u));
      window.sessionStorage.setItem('pointer_visible', '1');
    },
    [token, user],
  );
}

/**
 * Waits for the widget to be interactive AND for page-context capture to have started.
 *
 * element.ts's init() renders the toolbar (#fbk-add) BEFORE awaiting fetchCaptureConfig(), and it
 * is that fetch which calls startPageContextCapture() to patch console.error / window.fetch. So
 * "#fbk-add is visible" does NOT imply capture is running: a test that triggers its error as soon
 * as the button appears races the patch and PageContextSnapshot silently stays null.
 *
 * Pass the promise returned by page.waitForResponse(...) for /capture-config, created BEFORE
 * page.goto, as `captureConfigLoaded`.
 */
export async function waitForWidgetReady(
  page: Page,
  captureConfigLoaded?: Promise<unknown>,
): Promise<void> {
  const widget = page.locator('pointer-feedback');
  await widget.locator('#fbk-add').waitFor({ state: 'visible', timeout: 10_000 });
  if (captureConfigLoaded) await captureConfigLoaded;
}

/**
 * Enters element-pick mode and selects `targetSelector`.
 *
 * Two non-obvious requirements, both of which have already produced silent failures:
 *
 * 1. Wait for #fbk-add to flip to `aria-pressed="true"` before clicking the target. That
 *    attribute and the document-level click listener are installed by the same startPicking()
 *    call, so clicking earlier lands on the page as an ordinary click and no pick happens.
 *    (startPicking() no longer toggles a CSS class — element.ts sets aria-pressed and swaps the
 *    button's icon/title instead; see element.ts's startPicking()/stopPicking().)
 * 2. Click the target with `force`. In pick mode the widget draws a hover highlight over the
 *    element; Playwright's actionability check sees the element as obscured and would wait
 *    forever.
 */
export async function pickElement(page: Page, targetSelector: string): Promise<void> {
  const widget = page.locator('pointer-feedback');
  await widget.locator('#fbk-add').click();
  await expect(widget.locator('#fbk-add')).toHaveAttribute('aria-pressed', 'true');
  await page.locator(targetSelector).click({ force: true });
  await page.locator('#fbk-popover-host').locator('#fbk-comment-text').waitFor({ timeout: 10_000 });
}

/**
 * Switches the widget's environment via `#fbk-env`.
 *
 * `#fbk-env` has NOT lived in the toolbar for a while: eb2220f ("environment filter moved beside
 * status") moved it into the sidebar's own filter row, and 26b4364 ("collapsible filter bar")
 * then made that row start collapsed behind `#fbk-filters-toggle`. So the select is present in
 * the DOM as soon as the widget mounts (renderSidebar() runs once at init regardless of whether
 * anything is open) but stays invisible — off-screen inside the closed `.fbk-sidebar`, and then
 * behind `.fbk-hidden` on `#fbk-filters` — until both are opened. Several specs were still
 * driving it as if it were a toolbar-level control, which made `selectOption` hang for the full
 * test timeout waiting on an element that was never going to become visible/enabled.
 *
 * Opens whichever of the sidebar / filter row was closed, switches the environment, then closes
 * back only what it opened — so this is a drop-in replacement for the old bare
 * `widget.locator('#fbk-env').selectOption(value)` regardless of what state the caller left the
 * sidebar in.
 */
export async function switchEnvironment(page: Page, value: string): Promise<void> {
  const widget = page.locator('pointer-feedback');
  const sidebar = widget.locator('#fbk-sidebar');
  const sidebarWasOpen = await sidebar.evaluate((el) => el.classList.contains('open')).catch(() => false);
  if (!sidebarWasOpen) await widget.locator('#fbk-toggle').click();

  const filters = widget.locator('#fbk-filters');
  const filtersWereOpen = await filters
    .evaluate((el) => !el.classList.contains('fbk-hidden'))
    .catch(() => false);
  if (!filtersWereOpen) await widget.locator('#fbk-filters-toggle').click();

  await widget.locator('#fbk-env').selectOption(value);

  if (!filtersWereOpen) await widget.locator('#fbk-filters-toggle').click();
  if (!sidebarWasOpen) await widget.locator('#fbk-toggle').click();
}
