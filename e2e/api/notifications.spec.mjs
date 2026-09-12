// Playwright API-layer specs for R2-04: In-app notifications + author verify loop.
// Scenarios, in contract order (docs/roadmap/testing/R2-04-tests.md):
// - R2-04-04    — cross-user 404 + MarkAllRead scoped + unread-count   (PR tier)
// - R2-04-05 ⛓ — verify rules matrix                                   (PR tier; ⛓ reset — this
//   scenario stamps verifiedAt on seeded C8, which nothing later in the run may re-verify)
//
// The contract's "## Spec files" section calls this a "node `node:test`" file. That is the same
// conflict e2e/apply/apply.spec.mjs already hit and documented: the api phase runs through
// scripts/pw.sh → `npx playwright test "(^|/)api/"`, and run-e2e.sh --only dispatches single
// scenarios with `npx playwright test -g <id>` — a node:test file is invisible to both (0 tests
// collected ⇒ the phase reports "no tests found" and FAILs). The intent the contract is
// protecting — API-layer assertions, no browser — is preserved by writing @playwright/test blocks
// with no page fixture. Reported as SPEC-CONFLICT in the task report.
//
// Status matrices go through raw() — never a try/catch around a throwing verb: a catch that
// swallows the wrong error passes silently, which is the failure mode this suite exists to
// prevent. Assertions are on statuses and structured fields only; MessageKeys are English
// literals, not localisation keys, so no message text is asserted.
import { test, expect } from '@playwright/test';
import { raw, post, login } from '../scripts/lib/api.mjs';
import { credentials as loadCredentials, expected as loadExpected, loginClient } from '../scripts/lib/state.mjs';
import { PROJECTS } from '../scripts/lib/constants.mjs';
import { record } from '../scripts/lib/report.mjs';

// Read lazily: Playwright evaluates this file to DISCOVER tests, so an eager read on an
// unseeded workspace made `playwright test --list` report 0 tests in 0 files.
const credentials = () => loadCredentials();
const expected = () => loadExpected();

const ALPHA = PROJECTS.alpha.key;

test('R2-04-04 — cross-user 404 + MarkAllRead scoped + unread-count', async () => {
  const start = Date.now();
  const qa = await login(credentials().tester.email, credentials().tester.password);
  const dev = await login(credentials().developer.email, credentials().developer.password);
  const tb = await login(credentials().tenantBOwner.email, credentials().tenantBOwner.password);

  // 1. QA authors a comment; DEV replies → QA (the comment author, replier ≠ author) gains a
  //    ReplyAdded row. `N` is that row, identified by commentId+type.
  const created = await raw('POST', `/api/projects/${ALPHA}/comments`, {
    token: qa.token,
    body: {
      body: 'reply-target for isolation',
      environment: 1,
      element: { selector: '.x', route: '/' },
    },
  });
  expect(created.status).toBe(200);
  const commentId = created.data.id;

  const reply = await raw('POST', `/api/comments/${commentId}/replies`, {
    token: dev.token,
    body: { body: 'dev reply' },
  });
  expect(reply.status).toBe(200);

  const qaUnread = await raw('GET', '/api/me/notifications?unread=true', { token: qa.token });
  expect(qaUnread.status).toBe(200);
  const rowN = qaUnread.data.items.find((n) => n.commentId === commentId && n.type === 3);
  expect(rowN, 'QA must hold a ReplyAdded (type 3) row for the new reply').toBeTruthy();

  // 2. Row N is absent from DEV's list AND from DEV's count — the count endpoint is a second
  //    leak surface, not a restatement of the first.
  const devList = await raw('GET', '/api/me/notifications?pageSize=100', { token: dev.token });
  expect(devList.status).toBe(200);
  expect(
    devList.data.items.some((n) => n.commentId === commentId),
    "QA's notification row must never appear in DEV's list",
  ).toBe(false);

  const devCount = await raw('GET', '/api/me/notifications/unread-count', { token: dev.token });
  expect(devCount.status).toBe(200);
  expect(Object.keys(devCount.data).sort()).toEqual(['count']);
  expect(
    devCount.data.count,
    'unread-count must equal the unread rows of DEV\u2019s own list — no room for QA\u2019s row to hide',
  ).toBe(devList.data.items.filter((n) => n.readAt === null).length);

  // 3. DEV marks QA's row read → 404, not 403: revealing that the id exists (403) would leak
  //    QA's notification ids to a same-tenant colleague.
  const markRead = await raw('PATCH', `/api/me/notifications/${rowN.id}/read`, { token: dev.token });
  expect(markRead.status).toBe(404);

  // 4. DEV's read-all must touch none of QA's rows: QA's count after is EXACTLY the value it had
  //    before (the contract demands byte-equal, not merely "≥ 1").
  const qaBefore = await raw('GET', '/api/me/notifications/unread-count', { token: qa.token });
  expect(qaBefore.status).toBe(200);
  const qaBeforeCount = qaBefore.data.count;

  const readAll = await raw('POST', '/api/me/notifications/read-all', { token: dev.token });
  expect(readAll.status).toBe(200);
  expect(Object.keys(readAll.data).sort()).toEqual(['marked']);
  expect(readAll.data.marked, 'read-all marked exactly DEV\u2019s own unread rows').toBe(devCount.data.count);

  const qaAfter = await raw('GET', '/api/me/notifications/unread-count', { token: qa.token });
  expect(qaAfter.status).toBe(200);
  expect(qaAfter.data.count, "DEV's read-all must leave QA's unread count byte-equal").toBe(qaBeforeCount);
  expect(qaAfter.data.count, 'the surviving row is the point — QA\u2019s ReplyAdded is still unread').toBeGreaterThan(0);

  // 5. Tenant B sees none of tenant A's activity, despite all of it being in one database.
  const tbUnread = await raw('GET', '/api/me/notifications?unread=true', { token: tb.token });
  expect(tbUnread.status).toBe(200);
  expect(tbUnread.data.items).toHaveLength(0);

  const tbCount = await raw('GET', '/api/me/notifications/unread-count', { token: tb.token });
  expect(tbCount.status).toBe(200);
  expect(tbCount.data.count).toBe(0);

  record({
    id: 'R2-04-04',
    tier: 'PR',
    layer: 'api',
    role: 'QA, DEV, TB',
    result: 'PASS',
    ms: Date.now() - start,
    detail: 'cross-user 404; read-all scoped per recipient; tenant B isolated',
  });
});

test('R2-04-05 ⛓ — verify rules matrix', async () => {
  const start = Date.now();
  const qa = await login(credentials().tester.email, credentials().tester.password);
  const wa = await login(credentials().wsAdmin.email, credentials().wsAdmin.password);
  const dev = await login(credentials().developer.email, credentials().developer.password);
  // Quick-access accounts are passwordless — sign in by redeeming the seeded magic link.
  const cl = await loginClient({ post, login });

  const c5 = expected().comments.c5.id; // seeded Open comment, authored by QA (tester)
  const c8 = expected().comments.c8.id; // seeded Applied comment, authored by CL (client)

  // 1. QA authors; WA (the applier persona) applies; QA verifies 👍.
  const created = await raw('POST', `/api/projects/${ALPHA}/comments`, {
    token: qa.token,
    body: {
      body: 'verify-matrix target: applied then verified by its author',
      environment: 1,
      element: { selector: '.verify-matrix', route: '/' },
    },
  });
  expect(created.status).toBe(200);
  const commentId = created.data.id;

  const applied = await raw('PATCH', `/api/comments/${commentId}`, {
    token: wa.token,
    body: { status: 3 },
  });
  expect(applied.status).toBe(200);
  expect(applied.data?.status).toBe(3);

  const v1 = await raw('POST', `/api/comments/${commentId}/verify`, {
    token: qa.token,
    body: { ok: true },
  });
  expect(v1.status).toBe(200);
  expect(v1.data?.verifiedAt, '👍 must stamp VerifiedAt').toBeTruthy();
  const replies = v1.data?.replies ?? [];
  expect(replies[replies.length - 1]?.body ?? '', '👍 adds the "Verified ✓" reply').toContain('Verified ✓');

  // 2. 👎 without a note → 400 (the note is the reply that tells the applier what is still wrong).
  const v2 = await raw('POST', `/api/comments/${commentId}/verify`, {
    token: qa.token,
    body: { ok: false },
  });
  expect(v2.status).toBe(400);

  // 3. DEV (non-author, non-admin) on QA's comment → 403.
  const v3 = await raw('POST', `/api/comments/${commentId}/verify`, {
    token: dev.token,
    body: { ok: true },
  });
  expect(v3.status).toBe(403);

  // 4. Verify on an Open comment → 400 (Comment.VerifyRequiresApplied). Seeded c5 is Open and
  //    authored by QA — the call is refused before any mutation, so c5 is left untouched.
  const v4 = await raw('POST', `/api/comments/${c5}/verify`, {
    token: qa.token,
    body: { ok: true },
  });
  expect(v4.status).toBe(400);

  // 5. Quick-access author CAN verify — the one lifecycle action a Client legitimately owns.
  //    ⛓ reset: this stamps verifiedAt on seeded C8; nothing later in the run may re-verify it.
  const v5 = await raw('POST', `/api/comments/${c8}/verify`, {
    token: cl.token,
    body: { ok: true },
  });
  expect(v5.status).toBe(200);
  expect(v5.data?.verifiedAt).toBeTruthy();

  // 6. The existing guard is intact: CL still cannot PATCH status — verify did not become a
  //    side door into lifecycle management.
  const v6 = await raw('PATCH', `/api/comments/${c8}`, {
    token: cl.token,
    body: { status: 3 },
  });
  expect(v6.status).toBe(403);

  record({
    id: 'R2-04-05',
    tier: 'PR',
    layer: 'api',
    role: 'QA, CL, DEV, WA',
    result: 'PASS',
    ms: Date.now() - start,
    detail: 'verify matrix statuses exactly 200/400/403/400/200/403',
  });
});
