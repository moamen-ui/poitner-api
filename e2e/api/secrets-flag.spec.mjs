// E2E spec for R2-06: Secrets / payload advisory flag — API layer.
// Covers:
// - R2-06-03 ⛓: header gate both ways + zero-flag AI surfaces (summary, apply-queue, MCP)
// - R2-06-04 ⛓ (api half, steps 1–2 and 4): edit removes the secret → flag cleared; re-add → returns
// Contract: docs/roadmap/testing/R2-06-tests.md
//
// Substring semantics: every `payloadFlag` count runs on the RAW response text (res.text), never
// on the envelope-parsed object — api.mjs returns json.data and would hide key-absence, which is
// the exact silent pass this suite exists to prevent (R2-06-tests Flake notes).
import { test, expect } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { raw, login, BASE_URL } from '../scripts/lib/api.mjs';
import { credentials as loadCredentials, keys as loadKeys } from '../scripts/lib/state.mjs';
import { tempRepo } from '../scripts/lib/git.mjs';
import { connectMcp } from '../scripts/lib/mcp.mjs';
import {
  CLEAN_BODY,
  FLAGGED_BODY,
  PROJECT_KEY,
  ROTATE_BODY,
  countPayloadFlag,
  ensureFlaggedComment,
} from '../scripts/lib/secrets-flag.mjs';

// Read lazily: Playwright evaluates this file to DISCOVER tests, so an eager read on an
// unseeded workspace made `playwright test --list` report 0 tests in 0 files.
const credentials = () => loadCredentials();
const keys = () => loadKeys();

// The caller-kind header that opts a surface into the advisory flags (R2-06 execution doc:
// "Decision — X-Pointer-Client header gate"). Forgeable by design — what is asserted is that the
// DOCUMENTED AI paths (summary, apply-queue, CLI, MCP, legacy pointer.sh) never send it.
const WIDGET_HEADERS = { 'X-Pointer-Client': 'widget' };

test('R2-06-03 — header gate both ways + zero-flag AI surfaces (summary, apply-queue, MCP)', async () => {
  const qa = await login(credentials().tester.email, credentials().tester.password);
  const wa = await login(credentials().wsAdmin.email, credentials().wsAdmin.password);

  // F: the flagged comment (created here when the widget phase has not run yet — see
  // secrets-flag.mjs for why find-or-create is required by the harness phase order).
  const F = await ensureFlaggedComment(qa.token);

  // 1. Detail WITH the widget header → flags present (true + pattern name).
  const withHeader = await raw('GET', `/api/comments/${F}`, { token: qa.token, headers: WIDGET_HEADERS });
  expect(withHeader.status).toBe(200);
  expect(withHeader.text, 'gated detail must carry "hasPayloadFlag":true').toContain('"hasPayloadFlag":true');
  expect(withHeader.text, 'gated detail must name the matched pattern').toContain('github_token');

  // 2. Same URL WITHOUT the header → zero `payloadFlag` substrings. WhenWritingNull means the
  // keys are ABSENT, not null-valued — the raw-text count is the only assertion that proves it.
  const noHeader = await raw('GET', `/api/comments/${F}`, { token: qa.token });
  expect(noHeader.status).toBe(200);
  expect(countPayloadFlag(noHeader.text), 'header-less detail must not contain payloadFlag at all').toBe(0);

  // 3. Summary view (AI/CLI surface) never carries the fields — with AND without the header:
  // DTO-shape guarantee #1 (CommentSummaryDto has no such properties to gate).
  for (const headers of [WIDGET_HEADERS, undefined]) {
    const summary = await raw('GET', `/api/projects/${PROJECT_KEY}/comments?view=summary`, {
      token: qa.token,
      headers,
    });
    expect(summary.status).toBe(200);
    expect(countPayloadFlag(summary.text), `summary (headers=${JSON.stringify(headers)}) must be flag-free`).toBe(0);
  }

  // 4. Apply-queue (admin-gated, [Authorize(Policy="Admin")]). The queue only ever returns
  // status=2 items and F was created Open, so make F queue-visible first.
  const patchRes = await raw('PATCH', `/api/comments/${F}`, { token: wa.token, body: { status: 2 } });
  expect(patchRes.status).toBe(200);

  const queue = await raw('GET', `/api/admin/projects/${PROJECT_KEY}/apply-queue?status=2`, { token: wa.token });
  expect(queue.status).toBe(200);
  const queueItems = queue.data?.items ?? [];
  expect(
    queueItems.some((i) => i.id === F),
    'F must be present in the queue — otherwise the zero-count below proves nothing',
  ).toBe(true);
  expect(countPayloadFlag(queue.text), 'apply-queue must be flag-free').toBe(0);

  // Embedded replies are ApplyReplyDto { authorName, body, createdAt } — a reply-bearing queued
  // item must serialize flag-free too (seeded c1/c4-family comments supply the replies).
  const replyBearing = queueItems.filter((i) => (i.replies || []).length > 0);
  expect(replyBearing.length, 'queue must contain at least one reply-bearing item').toBeGreaterThan(0);
  for (const item of replyBearing) {
    expect(countPayloadFlag(JSON.stringify(item)), 'queued item with replies must be flag-free').toBe(0);
  }

  // 5. List DTO gate: CommentListItemDto honours the same header gate as the detail DTO.
  const list = await raw('GET', `/api/projects/${PROJECT_KEY}/comments`, { token: qa.token, headers: WIDGET_HEADERS });
  expect(list.status).toBe(200);
  const item = (list.data?.items || []).find((c) => c.id === F);
  expect(item, 'F must appear in the widget-facing list').toBeTruthy();
  expect(JSON.stringify(item), 'the flagged list item must carry "hasPayloadFlag":true').toContain('"hasPayloadFlag":true');

  // 6. MCP: the whitelisted AiCommentView projection never carries the fields. DEV persona per
  // the doc — a non-admin automation account, driven through the real stdio server.
  const mcpRepo = tempRepo();
  try {
    mkdirSync(join(mcpRepo.dir, '.pointer'), { recursive: true });
    writeFileSync(
      join(mcpRepo.dir, '.pointer', 'config.json'),
      JSON.stringify({ project: PROJECT_KEY, server: BASE_URL }, null, 2),
    );
    writeFileSync(join(mcpRepo.dir, '.pointer', 'credentials.env'), `POINTER_API_KEY=${keys().developer.apiKey}\n`);

    const mcp = await connectMcp({ cwd: mcpRepo.dir });
    try {
      const res = await mcp.callTool('pointer_get_comment', { id: F });
      expect(res.isError, 'pointer_get_comment must succeed').toBe(false);
      expect(countPayloadFlag(JSON.stringify(res.result)), 'MCP result must be flag-free').toBe(0);
    } finally {
      await mcp.close();
      await mcp.exited;
    }
  } finally {
    mcpRepo.cleanup();
  }
});

test('R2-06-04 — flag: edit removes secret → flag cleared on reload (api half: steps 1–2, 4)', async () => {
  const qa = await login(credentials().tester.email, credentials().tester.password);
  const F = await ensureFlaggedComment(qa.token);

  // 1. Author edit replaces the body with clean text → 200 (edit recomputes the flag).
  const clean = await raw('PUT', `/api/comments/${F}`, { token: qa.token, body: { body: CLEAN_BODY } });
  expect(clean.status).toBe(200);

  // 2. Detail WITH the widget header → hasPayloadFlag is now FALSE, and no pattern is named.
  //
  // False, not absent. For a human surface the server is answering the question — "we looked, it is
  // clean" — and that is a different statement from the key being missing, which is what a
  // non-human caller gets and means "we are not telling you". Asserting absence here would demand
  // the widget lose the ability to distinguish a checked-clean comment from an unchecked one.
  const afterClean = await raw('GET', `/api/comments/${F}`, { token: qa.token, headers: WIDGET_HEADERS });
  expect(afterClean.status).toBe(200);
  expect(afterClean.data?.hasPayloadFlag, 'a cleaned comment reads as checked-and-clean').toBe(false);
  expect(afterClean.data?.payloadFlags ?? [], 'no patterns remain').toHaveLength(0);
  expect(afterClean.text, 'cleaned comment must not name the pattern').not.toContain('github_token');

  // 4. Re-add via edit (a NEW canary) → the flag returns — detection is not create-only.
  const readd = await raw('PUT', `/api/comments/${F}`, { token: qa.token, body: { body: ROTATE_BODY } });
  expect(readd.status).toBe(200);
  const afterReadd = await raw('GET', `/api/comments/${F}`, { token: qa.token, headers: WIDGET_HEADERS });
  expect(afterReadd.status).toBe(200);
  expect(afterReadd.text, 're-edit must flag the new token').toContain('"hasPayloadFlag":true');

  // Sanity for the fixture itself: the flagged body constant still matches the detector.
  expect(FLAGGED_BODY).toContain('ghp_');
});
