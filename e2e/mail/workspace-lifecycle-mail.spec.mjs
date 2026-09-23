// Playwright mail-layer spec for DB-18 (workspace self-service pause / e-mail-confirmed delete —
// docs/db/execution/DB-18-workspace-self-service-pause-and-delete.md §6 test 13).
//
// Scenarios (API-level via lib/api.mjs, Mailpit via lib/mail.mjs — no browser, same style as
// tenant-invite-mail.spec.mjs):
//   (a) admin pauses -> a widget write (authenticated comment POST, the same session shape every
//       other spec in this suite calls "the widget") -> 423 + X-Workspace-Paused: true; reads stay
//       200; a key session is refused even on reads (D18.4); resume -> the write works again.
//   (b) request deletion -> Mailpit "Confirm deleting" mail -> extract the link's token -> preview
//       shows counts -> confirm with the wrong password -> refused; confirm with the right password
//       + the exact typed workspace name -> scheduled (workspaceDeletionScheduledFor on /me); writes
//       423 while scheduled; the SAME link is refused a second time (single-use); cancel -> cleared,
//       "was cancelled" mail, writes succeed again.
//   (c) pause-instead from a fresh link -> paused, no schedule; that link is refused a second time
//       too (every workspace-deletion link always spends itself, whichever action redeems it).
//
// Deliberately does NOT drive the grace-period hosted job (memory: don't try to run the 7-day job)
// — (b) only asserts the SCHEDULE is set and then cleared, never waits for WorkspaceDeletionService
// to actually delete anything.
//
// Isolated throwaway tenant: POST /api/admin/tenants (the same mechanism e2e/scripts/seed.mjs and
// lib/tenants.mjs#deleteTenantByEmail use for every other spec's own tenants) — this never touches
// the shared seeded e2e-owner/e2e-alpha tenant other specs in this suite depend on.
import { test, expect } from '@playwright/test';
import { get, getRaw, post, postRaw, put, login } from '../scripts/lib/api.mjs';
import * as mail from '../scripts/lib/mail.mjs';
import { applySettings } from '../scripts/lib/settings.mjs';
import { deleteTenantByEmail } from '../scripts/lib/tenants.mjs';
import { recordMailEvidence } from '../scripts/lib/report.mjs';
import { Environment, SUPER_ADMIN } from '../scripts/lib/constants.mjs';

const RUN_ID = Math.random().toString(36).substring(2, 8);
const ADMIN_EMAIL = `db18-admin-${RUN_ID}@example.com`;
const ADMIN_PASSWORD = 'Db18LifecyclePass1!';
const WORKSPACE_NAME = `DB18 Lifecycle ${RUN_ID}`;
const PROJECT_KEY = `db18-life-${RUN_ID}`;

let superAdmin;
let admin; // { token, user }

function commentBody(note) {
  return {
    body: `DB-18 lifecycle probe (${note})`,
    environment: Environment.Local,
    isPrivate: false,
    element: {
      selector: '#db18-probe',
      route: '/',
      pageUrl: 'https://example.test/',
      pageTitle: 'DB-18 lifecycle fixture',
    },
  };
}

// The confirm-deletion link is `{app}/confirm-workspace-deletion?token=<escaped scoped token>`
// (§3.4) — the dashboard route reads the query param; there is no API redemption "by URL", only by
// the token it carries.
function extractToken(link) {
  const token = new URL(link).searchParams.get('token');
  expect(token, `token query param missing from link: ${link}`).toBeTruthy();
  return token;
}

async function loginWithKey(apiKey) {
  const res = await postRaw('/api/auth/login-with-key', { apiKey });
  expect(res.status, 'login-with-key must succeed for a freshly-minted key').toBe(200);
  expect(res.data?.status).toBe('ok');
  expect(res.data?.token).toBeTruthy();
  return res.data.token;
}

test.beforeAll(async () => {
  superAdmin = await login(SUPER_ADMIN.email, SUPER_ADMIN.password);
  // Mail-phase precedent (tenant-invite-mail.spec.mjs): this file's process may run before or
  // after that one within the same "mail" phase, so re-assert rather than assume it already ran.
  await applySettings({ emailEnabled: true, emailFromEmail: 'noreply@e2e.local' }, superAdmin.token);

  // A fresh, isolated tenant + its Workspace Admin owner — never the shared e2e-owner/e2e-alpha
  // tenant. POST /api/admin/tenants auto-verifies the owner's e-mail (TenantService.CreateAsync),
  // so no verify-email step is needed before using admin-only lifecycle routes.
  await post(
    '/api/admin/tenants',
    { email: ADMIN_EMAIL, password: ADMIN_PASSWORD, displayName: 'DB18 Lifecycle Admin' },
    { token: superAdmin.token },
  );

  admin = await login(ADMIN_EMAIL, ADMIN_PASSWORD);

  // A deterministic, typeable name — the confirm step requires the caller to type the workspace's
  // OWN name exactly (normalised compare, §3.4); a random/placeholder name would work too, but a
  // name we chose keeps the assertion below trivial to read.
  await put('/api/admin/workspace/name', { name: WORKSPACE_NAME }, { token: admin.token });

  await post('/api/admin/projects', { key: PROJECT_KEY, name: 'DB18 Lifecycle Project' }, { token: admin.token });
});

test.afterAll(async () => {
  if (!superAdmin) return;
  // Hard-deletes the whole throwaway workspace regardless of what state (paused/scheduled) the
  // scenarios above left it in — TenantsController's operator delete is not blocked by any of them.
  await deleteTenantByEmail(ADMIN_EMAIL, superAdmin.token).catch(() => {});
});

test('DB18-a — pause freezes writes (423 + header) and key sessions (D18.4); reads stay 200; resume restores writes', async () => {
  const pauseRes = await post('/api/admin/workspace/pause', {}, { token: admin.token });
  expect(pauseRes.pausedAt, 'PUT pause must set PausedAt').toBeTruthy();
  expect(pauseRes.pausedByOperator, 'a self-pause is not an operator pause').toBe(false);

  const writeWhilePaused = await postRaw(`/api/projects/${PROJECT_KEY}/comments`, commentBody('paused'), {
    token: admin.token,
  });
  expect(writeWhilePaused.status, 'a write on a paused workspace must be 423 Locked').toBe(423);
  expect(
    writeWhilePaused.headers.get('x-workspace-paused'),
    'the freeze filter must set X-Workspace-Paused: true',
  ).toBe('true');

  const readWhilePaused = await getRaw(`/api/projects/${PROJECT_KEY}/comments`, { token: admin.token });
  expect(readWhilePaused.status, 'reads stay available while paused (D18.3)').toBe(200);

  // A key session (CLI/MCP/apply) is refused entirely while frozen, reads included (D18.4) —
  // unlike the JWT session above, whose GET just worked.
  const apiKeyRes = await get('/api/me/api-key', { token: admin.token });
  const keyToken = await loginWithKey(apiKeyRes.apiKey);
  const keyReadWhilePaused = await getRaw(`/api/projects/${PROJECT_KEY}/comments`, { token: keyToken });
  expect(
    keyReadWhilePaused.status,
    'a key session GET must also be 423 while frozen (D18.4 — reads too)',
  ).toBe(423);

  // GET /api/auth/me is one of the two key-session exemptions (§3.5) — `pointer whoami`/`apply`
  // must still be able to read and explain the frozen state.
  const meViaKey = await get('/api/auth/me', { token: keyToken });
  expect(meViaKey.workspacePausedAt, 'MeResponse must carry the freeze state for a key session too').toBeTruthy();

  const resumeRes = await post('/api/admin/workspace/resume', {}, { token: admin.token });
  expect(resumeRes.pausedAt, 'resume must clear PausedAt').toBeFalsy();

  const writeAfterResume = await postRaw(`/api/projects/${PROJECT_KEY}/comments`, commentBody('resumed'), {
    token: admin.token,
  });
  expect(writeAfterResume.status, 'a write must succeed again once resumed').toBe(200);
});

test('DB18-b — e-mail-confirmed deletion: preview, wrong/right password, schedule, 423 writes, single-use link, cancel clears', async () => {
  test.setTimeout(90_000); // up to three separate Mailpit waits (request/scheduled/cancelled mail)

  await mail.clear();

  const requestRes = await post('/api/admin/workspace/deletion/request', {}, { token: admin.token });
  expect(requestRes.expiresAt, 'request must return the link expiry (30 min, §3.4)').toBeTruthy();

  const requestMessage = await mail.awaitMessage({
    to: ADMIN_EMAIL,
    subjectIncludes: 'Confirm deleting',
    timeoutMs: 15_000,
  });
  recordMailEvidence({ to: ADMIN_EMAIL, subject: requestMessage.subject, scenarioId: 'DB18-b-request' });
  // D18.2 — only the requesting admin gets the confirm link; nothing else to assert here without a
  // second admin persona, which this isolated single-admin workspace deliberately does not have.

  const link = mail.extractLink(requestMessage.html, '/confirm-workspace-deletion');
  const token = extractToken(link);

  const previewRes = await postRaw('/api/auth/workspace-deletion/preview', { token });
  expect(previewRes.status, 'preview must accept a fresh token').toBe(200);
  expect(previewRes.data.workspaceName).toBe(WORKSPACE_NAME);
  expect(previewRes.data.projectCount, 'the DB18 Lifecycle Project must be counted').toBeGreaterThanOrEqual(1);
  expect(previewRes.data.requiresPassword, 'a password account requires the password step').toBe(true);
  expect(previewRes.data.isPaused).toBe(false);
  expect(previewRes.data.graceDays).toBe(7);

  const wrongPasswordRes = await postRaw('/api/auth/workspace-deletion/confirm', {
    token,
    password: 'DefinitelyWrongPass1!',
    workspaceName: WORKSPACE_NAME,
  });
  expect(
    wrongPasswordRes.status,
    'the wrong password must refuse and must not schedule anything',
  ).toBeGreaterThanOrEqual(400);
  expect(wrongPasswordRes.status).toBeLessThan(500);

  const meAfterWrongPassword = await get('/api/auth/me', { token: admin.token });
  expect(meAfterWrongPassword.workspaceDeletionScheduledFor, 'a wrong password must not schedule a delete').toBeFalsy();

  const confirmRes = await postRaw('/api/auth/workspace-deletion/confirm', {
    token,
    password: ADMIN_PASSWORD,
    workspaceName: WORKSPACE_NAME,
  });
  expect(confirmRes.status, 'the right password + exact workspace name must schedule the deletion').toBe(200);
  expect(confirmRes.data.workspaceId).toBeTruthy();

  const scheduledForMs = new Date(confirmRes.data.deletionScheduledFor).getTime();
  const graceMs = 7 * 24 * 60 * 60 * 1000; // API/appsettings.json WorkspaceDeletion:GraceDays = 7
  const toleranceMs = 5 * 60_000;
  expect(scheduledForMs, 'DeletionScheduledFor must be ~now + GraceDays (7d)').toBeGreaterThan(
    Date.now() + graceMs - toleranceMs,
  );
  expect(scheduledForMs).toBeLessThan(Date.now() + graceMs + toleranceMs);

  const meAfterConfirm = await get('/api/auth/me', { token: admin.token });
  expect(
    meAfterConfirm.workspaceDeletionScheduledFor,
    '/me must carry the schedule (dashboard banner source)',
  ).toBeTruthy();

  const writeWhileScheduled = await postRaw(`/api/projects/${PROJECT_KEY}/comments`, commentBody('scheduled'), {
    token: admin.token,
  });
  expect(writeWhileScheduled.status, 'a scheduled deletion freezes writes just like a pause').toBe(423);

  // Single-use: the very same link, same password, same name — must be refused now that it has
  // already been redeemed once (ValidateTokenAsync: DeletionScheduledFor != null => dead).
  const reuseRes = await postRaw('/api/auth/workspace-deletion/confirm', {
    token,
    password: ADMIN_PASSWORD,
    workspaceName: WORKSPACE_NAME,
  });
  expect(reuseRes.status, 'a confirmed link must not be redeemable a second time').toBeGreaterThanOrEqual(400);
  expect(reuseRes.status).toBeLessThan(500);

  const scheduledMessage = await mail.awaitMessage({
    to: ADMIN_EMAIL,
    subjectIncludes: 'will be deleted',
    timeoutMs: 15_000,
  });
  recordMailEvidence({ to: ADMIN_EMAIL, subject: scheduledMessage.subject, scenarioId: 'DB18-b-scheduled' });

  await mail.clear();
  const cancelRes = await post('/api/admin/workspace/deletion/cancel', {}, { token: admin.token });
  expect(cancelRes.deletionScheduledFor, 'cancel must clear the schedule').toBeFalsy();

  const meAfterCancel = await get('/api/auth/me', { token: admin.token });
  expect(meAfterCancel.workspaceDeletionScheduledFor, '/me must reflect the cleared schedule').toBeFalsy();

  const cancelledMessage = await mail.awaitMessage({
    to: ADMIN_EMAIL,
    subjectIncludes: 'cancelled',
    timeoutMs: 15_000,
  });
  recordMailEvidence({ to: ADMIN_EMAIL, subject: cancelledMessage.subject, scenarioId: 'DB18-b-cancelled' });

  const writeAfterCancel = await postRaw(`/api/projects/${PROJECT_KEY}/comments`, commentBody('cancelled'), {
    token: admin.token,
  });
  expect(writeAfterCancel.status, 'a write must succeed again once the schedule is cancelled').toBe(200);
});

test('DB18-c — pause-instead from the confirm-deletion link pauses without scheduling; the link is single-use', async () => {
  test.setTimeout(60_000);

  await mail.clear();

  // A fresh request is allowed here: the prior test's cancel cleared DeletionRequestedAt back to
  // null, so the 5-minute cooldown (which compares against that column) does not apply.
  const requestRes = await post('/api/admin/workspace/deletion/request', {}, { token: admin.token });
  expect(requestRes.expiresAt).toBeTruthy();

  const message = await mail.awaitMessage({
    to: ADMIN_EMAIL,
    subjectIncludes: 'Confirm deleting',
    timeoutMs: 15_000,
  });
  recordMailEvidence({ to: ADMIN_EMAIL, subject: message.subject, scenarioId: 'DB18-c-request' });
  const token = extractToken(mail.extractLink(message.html, '/confirm-workspace-deletion'));

  const pauseInsteadRes = await postRaw('/api/auth/workspace-deletion/pause-instead', { token });
  expect(pauseInsteadRes.status, 'pause-instead must succeed on a fresh link').toBe(200);

  const workspaceAfter = await get('/api/admin/workspace', { token: admin.token });
  expect(workspaceAfter.pausedAt, 'pause-instead must pause the workspace').toBeTruthy();
  expect(workspaceAfter.deletionScheduledFor, 'pause-instead must never schedule a deletion').toBeFalsy();
  expect(workspaceAfter.deletionRequestedAt, 'pause-instead always spends the pending request too').toBeFalsy();

  const writeWhilePaused = await postRaw(`/api/projects/${PROJECT_KEY}/comments`, commentBody('pause-instead'), {
    token: admin.token,
  });
  expect(writeWhilePaused.status).toBe(423);

  // The link always spends itself, whichever action redeems it (§3.4) — reusing it here, even for
  // the same action, must be refused.
  const reuseRes = await postRaw('/api/auth/workspace-deletion/pause-instead', { token });
  expect(reuseRes.status, 'a spent link must not be redeemable a second time').toBeGreaterThanOrEqual(400);
  expect(reuseRes.status).toBeLessThan(500);

  // Restore for a clean teardown (afterAll hard-deletes regardless, but leaves nothing paused for
  // whatever runs next against this persona in the meantime).
  await post('/api/admin/workspace/resume', {}, { token: admin.token });
});
