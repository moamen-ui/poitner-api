// R2-05: passwordless quick-access invites — widget layer.
// Scenarios implemented (docs/roadmap/testing/R2-05-tests.md):
// - R2-05-01 — quick-access: magic link signs in
// - R2-05-02 — quick-access: url param stripped on success (+ no token persisted; second visit works)
// - R2-05-03 — quick-access: url param stripped on failure (rotated link rejected)
// - R2-05-04 — quick-access: rotated link works
//
// Every scenario provisions its OWN invited client (`qa-cl-<runId>-<n>@example.com`, harness
// Preconditions "one per scenario") and drives it in a fresh browser.newContext(), so no
// pointer_token ever leaks between scenarios and each row is independently runnable — the doc's
// State coupling section lists none of these, and absence is a positive claim.
//
// Widget reveal (contract Preconditions): a fresh context renders ONLY #pf-launcher until
// sessionStorage.pointer_visible is set, so every signed-in assertion clicks the launcher first.
import { test, expect } from '@playwright/test';
import { spawn, type ChildProcess } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { login, get, raw } from '../scripts/lib/api.mjs';
import { credentials as loadCredentials } from '../scripts/lib/state.mjs';
import { record } from '../scripts/lib/report.mjs';
import {
  QA_PROJECT_KEY,
  MAGIC_LINK_RE,
  ensureQaFixture,
  clientRoleId,
  inviteQaClient,
  rotateQuickLink,
  redeemInvite,
  extractInviteToken,
} from '../scripts/lib/quick-access.mjs';

const here = dirname(fileURLToPath(import.meta.url));

// Read lazily: Playwright evaluates this file to DISCOVER tests, so an eager read on an
// unseeded workspace made `playwright test --list` report 0 tests in 0 files.
const credentials = () => loadCredentials();

// Unique per run (Flake notes): re-inviting a used address would 409, and mail rows must never
// match a previous run's message.
const RUN_ID = Math.random().toString(36).substring(2, 7);

// The notice R2-05-03/04 assert on invite failure. The shipped widget renders it as a shadow-root
// toast (element.ts:299 → toast(), 2.2 s lifetime) rather than the contract's Decision locator
// `.pf-modal-notice` — see the SPEC-CONFLICT note in the task report; the assertion targets the
// user-visible outcome (exact text, error styling) either way.
const LINK_INVALID_NOTICE = 'This invite link is invalid or expired — ask for a new one.';

// The smoke fixture on 4173 is the only page honouring ?project= (harness §2). The widget phase's
// runner starts it; when this spec is dispatched alone it starts its own and kills it in afterAll.
const SMOKE_URL = 'http://localhost:4173/';
let fixtureServer: ChildProcess | null = null;

let wa: { token: string };
let qaProject: { id: number };
let roleId: number;

test.beforeAll(async () => {
  wa = await login(credentials().wsAdmin.email, credentials().wsAdmin.password);
  qaProject = await ensureQaFixture(wa.token);
  roleId = await clientRoleId(wa.token);

  const isUp = await fetch(SMOKE_URL).then((r) => r.ok).catch(() => false);
  if (!isUp) {
    fixtureServer = spawn('node', [join(here, '..', 'fixture-app', 'serve.mjs'), 'smoke', '4173'], {
      stdio: 'ignore',
    });
    const deadline = Date.now() + 10_000;
    while (Date.now() < deadline) {
      const ready = await fetch(SMOKE_URL).then((r) => r.ok).catch(() => false);
      if (ready) break;
      await new Promise((r) => setTimeout(r, 200));
    }
  }
});

test.afterAll(() => {
  if (fixtureServer) {
    fixtureServer.kill();
    fixtureServer = null;
  }
});

test('R2-05-01 — quick-access: magic link signs in', async ({ browser }, testInfo) => {
  const start = Date.now();
  const email = `qa-cl-${RUN_ID}-1@example.com`;

  // 1-2. WA invites the client; validate the response fields exactly as the contract spells them.
  const invite = await inviteQaClient(wa.token, { roleId, email, expiresInDays: 14, projectId: qaProject.id });
  expect(invite.status).toBe(200);
  const magicLink = invite.data?.magicLink;
  expect(magicLink, 'magicLink must be the AppUrl with a 43-char pointer_invite appended').toMatch(MAGIC_LINK_RE);
  expect(invite.data?.url, 'url must equal magicLink for a quick-access invite').toBe(magicLink);
  expect(invite.data?.linkExpiresAt).toBeTruthy();
  expect(invite.data?.emailSent, 'no email by default (link-copy delivery)').toBe(false);

  // 3. Fresh browser context (no localStorage). The success path is asserted via capture-config,
  // never by racing the invite exchange (Flake notes) — the waiter is registered BEFORE goto.
  const ctx = await browser.newContext();
  const page = await ctx.newPage();
  try {
    const captureConfigLoaded = page.waitForResponse('**/capture-config');
    await page.goto(magicLink);
    await captureConfigLoaded;

    const widget = page.locator('pointer-feedback');

    // 4. No auth modal ever appeared — the init() branch took over from saveAuth.
    await expect(widget.locator('.pf-modal-overlay')).toHaveCount(0);

    // Reveal the toolbar (fresh context renders only the launcher), then assert the signed-in
    // chrome. DisplayName is the EMAIL LOCAL PART (InviteService), not the full address; the
    // title template is `Signed in as ${displayName} · ${roleLabel}`.
    await widget.locator('#pf-launcher').click();
    await expect(widget.locator('#pf-add')).toBeVisible({ timeout: 10_000 });
    await expect(widget.locator('#pf-user')).toHaveAttribute(
      'title',
      `Signed in as qa-cl-${RUN_ID}-1 · Client`,
    );

    // 5. The session was persisted by saveAuth.
    const storedToken = await page.evaluate(() => localStorage.getItem('pointer_token'));
    expect(storedToken).toBeTruthy();

    // Evidence: a screenshot of the signed-in page.
    //
    // The PAGE, not the `pointer-feedback` locator. The host element is a zero-size box — every
    // pixel it shows lives in its shadow root and is position:fixed — so an element screenshot
    // waits forever for a stable non-zero bounding box and takes the whole test down with it, as
    // a 30s timeout with every assertion already passed. Awaited, so a failure here is reported
    // rather than surfacing later as an unhandled rejection.
    await testInfo.attach('signed-in-widget', {
      body: await page.screenshot(),
      contentType: 'image/png',
    });

    record({
      id: 'R2-05-01', tier: 'PR', layer: 'widget + api', role: 'WA, CL', result: 'PASS',
      ms: Date.now() - start, detail: 'signed in via magic link, no modal, no password step',
    });
  } finally {
    await ctx.close();
  }
});

test('R2-05-02 — quick-access: url param stripped on success', async ({ browser }) => {
  const start = Date.now();
  const email = `qa-cl-${RUN_ID}-2@example.com`;

  const invite = await inviteQaClient(wa.token, { roleId, email, expiresInDays: 14, projectId: qaProject.id });
  expect(invite.status).toBe(200);
  const magicLink = invite.data?.magicLink;
  expect(magicLink).toMatch(MAGIC_LINK_RE);
  const token = extractInviteToken(magicLink);

  // Redeem once via the API to learn the provisioned user's id — the authorId assertion below
  // needs it, and a second redemption is legal (unlimited uses within the TTL).
  const redeemed = await redeemInvite(token);
  expect(redeemed.status).toBe(200);
  expect(redeemed.data?.status).toBe('ok');
  const clientUserId = redeemed.data?.user?.id;
  expect(clientUserId).toBeTruthy();

  const ctx = await browser.newContext();
  const page = await ctx.newPage();
  try {
    const captureConfigLoaded = page.waitForResponse('**/capture-config');
    await page.goto(magicLink);
    await captureConfigLoaded;

    // 1. Only the project override remains in the address bar.
    expect(await page.evaluate(() => location.search)).toBe('?project=e2e-qa-invite');

    const widget = page.locator('pointer-feedback');
    await expect(widget.locator('.pf-modal-overlay')).toHaveCount(0);
    await widget.locator('#pf-launcher').click();
    await expect(widget.locator('#pf-add')).toBeVisible({ timeout: 10_000 });

    // 2. The client posts a comment through the widget on the smoke page's checkout button.
    await widget.locator('#pf-add').click();
    await expect(widget.locator('#pf-add')).toHaveClass(/active/);
    await page.locator('#checkout-btn').click({ force: true });

    const popover = page.locator('#pf-popover-host');
    await expect(popover.locator('#pf-comment-text')).toBeVisible({ timeout: 10_000 });
    await popover.locator('#pf-comment-text').fill('qa-invite comment');
    await popover.locator('#pf-submit').click();
    await expect(popover).toBeEmpty({ timeout: 10_000 });

    // 3. Staff-side check: newest comment is the client's, and the strip happened before any
    // capture — neither element.pageUrl nor element.route may carry the token.
    const res = await get(`/api/projects/${QA_PROJECT_KEY}/comments?pageSize=100`, { token: wa.token });
    const items = res.items as Array<Record<string, any>>;
    const newest = items?.[0];
    expect(newest, 'the widget-posted comment must exist').toBeTruthy();
    expect(newest.authorId, 'attributed to the provisioned quick-access client').toBe(clientUserId);
    expect(String(newest.element?.pageUrl), 'pageUrl must not contain the invite token').not.toContain('pointer_invite');
    expect(String(newest.element?.route), 'route must not contain the invite token').not.toContain('pointer_invite');
    // Evidence: the stored element.pageUrl, pasted.
    console.log(`[R2-05-02] stored element.pageUrl: ${newest.element?.pageUrl}`);

    // 4. Second visit with the SAME link: still signed in, no modal (unlimited uses within TTL —
    // silent re-sign-in). pointer_visible persisted by the first reveal, so the toolbar renders
    // directly this time.
    const captureConfigAgain = page.waitForResponse('**/capture-config');
    await page.goto(magicLink);
    await captureConfigAgain;
    await expect(widget.locator('.pf-modal-overlay')).toHaveCount(0);
    await expect(widget.locator('#pf-add')).toBeVisible({ timeout: 10_000 });
    await expect(widget.locator('#pf-user')).toHaveAttribute(
      'title',
      `Signed in as qa-cl-${RUN_ID}-2 · Client`,
    );

    record({
      id: 'R2-05-02', tier: 'PR', layer: 'widget + api', role: 'CL, WA', result: 'PASS',
      ms: Date.now() - start, detail: 'param stripped; token absent from pageUrl/route; second visit silent',
    });
  } finally {
    await ctx.close();
  }
});

test('R2-05-03 — quick-access: url param stripped on failure', async ({ browser }) => {
  const start = Date.now();
  const email = `qa-cl-${RUN_ID}-3@example.com`;

  const invite = await inviteQaClient(wa.token, { roleId, email, expiresInDays: 14, projectId: qaProject.id });
  expect(invite.status).toBe(200);
  const inviteId = invite.data?.id;
  const oldMagicLink = invite.data?.magicLink;
  expect(oldMagicLink).toMatch(MAGIC_LINK_RE);
  const oldToken = extractInviteToken(oldMagicLink);

  // 1. Rotate: the response carries a new magicLink whose token differs from the old one.
  const rotated = await rotateQuickLink(wa.token, inviteId);
  expect(rotated.status).toBe(200);
  const newMagicLink = rotated.data?.magicLink;
  expect(newMagicLink, 'rotate must return a new magicLink').toBeTruthy();
  expect(newMagicLink).not.toBe(oldMagicLink);
  expect(extractInviteToken(newMagicLink)).not.toBe(oldToken);

  // 2-3. Fresh context; capture the page URL at the moment the redemption request fires.
  const ctx = await browser.newContext();
  const page = await ctx.newPage();
  try {
    let capturedPageUrl: string | null = null;
    await page.route('**/api/auth/login-with-invite', (route) => {
      capturedPageUrl = page.url();
      return route.continue();
    });
    const inviteCall = page.waitForResponse('**/api/auth/login-with-invite');
    await page.goto(oldMagicLink);
    await inviteCall;

    // 5. At the moment the network call fired, the page URL contained NO pointer_invite — the
    // history.replaceState strip happens before the call, on the failure path too. Truthiness
    // first: a null capture (the request never fired) must not pass the substring check vacuously.
    expect(capturedPageUrl, 'the redemption request must have fired and been captured').toBeTruthy();
    expect(capturedPageUrl, 'the URL at request time must already be stripped').not.toContain('pointer_invite');

    const widget = page.locator('pointer-feedback');

    // 4. The invalid/expired notice (a 2.2 s toast — poll before it self-removes) and the normal
    // login still offered: reveal the chrome, act on #pf-add with no token, get the login modal.
    await expect
      .poll(async () => widget.locator('.pf-toast.error').textContent().catch(() => null), {
        message: `Expected the invalid-link notice "${LINK_INVALID_NOTICE}"`,
        intervals: [250],
        timeout: 10_000,
      })
      .toBe(LINK_INVALID_NOTICE);

    await widget.locator('#pf-launcher').click();
    await expect(widget.locator('#pf-add')).toBeVisible({ timeout: 10_000 });
    await widget.locator('#pf-add').click();
    await expect(widget.locator('.pf-modal-overlay')).toBeVisible({ timeout: 10_000 });

    expect(await page.evaluate(() => location.search)).toBe('?project=e2e-qa-invite');

    record({
      id: 'R2-05-03', tier: 'PR', layer: 'widget', role: 'WA, CL', result: 'PASS',
      ms: Date.now() - start, detail: 'strip precedes the call; notice + login modal on rotated link',
    });
  } finally {
    await ctx.close();
  }
});

test('R2-05-04 — quick-access: rotated link works', async ({ browser }) => {
  const start = Date.now();
  const email = `qa-cl-${RUN_ID}-4@example.com`;

  // This row rotates its own invite: the doc's State coupling section is empty (every scenario
  // stands alone), so depending on R2-05-03's rotated link would make it un-retryable in isolation.
  const invite = await inviteQaClient(wa.token, { roleId, email, expiresInDays: 14, projectId: qaProject.id });
  expect(invite.status).toBe(200);
  const inviteId = invite.data?.id;
  const oldMagicLink = invite.data?.magicLink;
  const rotated = await rotateQuickLink(wa.token, inviteId);
  expect(rotated.status).toBe(200);
  const newMagicLink = rotated.data?.magicLink;
  expect(newMagicLink).toBeTruthy();
  expect(newMagicLink).not.toBe(oldMagicLink);

  // 1-2. The rotated (new) link signs the client in — same assertions as R2-05-01 steps 4-5,
  // including the #pf-launcher reveal before #pf-add/#pf-user exist.
  const ctx = await browser.newContext();
  const page = await ctx.newPage();
  try {
    const captureConfigLoaded = page.waitForResponse('**/capture-config');
    await page.goto(newMagicLink);
    await captureConfigLoaded;

    const widget = page.locator('pointer-feedback');
    await expect(widget.locator('.pf-modal-overlay')).toHaveCount(0);
    await widget.locator('#pf-launcher').click();
    await expect(widget.locator('#pf-add')).toBeVisible({ timeout: 10_000 });
    await expect(widget.locator('#pf-user')).toHaveAttribute(
      'title',
      `Signed in as qa-cl-${RUN_ID}-4 · Client`,
    );
    expect(await page.evaluate(() => localStorage.getItem('pointer_token'))).toBeTruthy();
  } finally {
    await ctx.close();
  }

  // 3. Revoke (DELETE the invite) kills the link too: the previously-valid link now yields the
  // same notice + login modal as R2-05-03 — rotate AND revoke both invalidate.
  const del = await raw('DELETE', `/api/admin/invites/${inviteId}`, { token: wa.token });
  expect(del.status).toBe(200);

  const ctx2 = await browser.newContext();
  const page2 = await ctx2.newPage();
  try {
    const inviteCall = page2.waitForResponse('**/api/auth/login-with-invite');
    await page2.goto(newMagicLink);
    await inviteCall;

    const widget = page2.locator('pointer-feedback');
    await expect
      .poll(async () => widget.locator('.pf-toast.error').textContent().catch(() => null), {
        message: `Expected the invalid-link notice after revoke: "${LINK_INVALID_NOTICE}"`,
        intervals: [250],
        timeout: 10_000,
      })
      .toBe(LINK_INVALID_NOTICE);

    await widget.locator('#pf-launcher').click();
    await expect(widget.locator('#pf-add')).toBeVisible({ timeout: 10_000 });
    await widget.locator('#pf-add').click();
    await expect(widget.locator('.pf-modal-overlay')).toBeVisible({ timeout: 10_000 });
    expect(await page2.evaluate(() => location.search)).toBe('?project=e2e-qa-invite');

    record({
      id: 'R2-05-04', tier: 'PR', layer: 'widget + api', role: 'WA, CL', result: 'PASS',
      ms: Date.now() - start, detail: 'rotated link signs in; revoke kills it',
    });
  } finally {
    await ctx2.close();
  }
});
