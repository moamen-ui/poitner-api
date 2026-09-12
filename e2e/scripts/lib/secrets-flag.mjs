// Shared fixture for the R2-06 secrets/payload-flag scenarios (docs/roadmap/testing/R2-06-tests.md).
//
// The scenarios are declared ⛓ state-coupled to R2-06-01, but the harness phase order is
// api → cli → widget (00-HARNESS §8), i.e. the widget phase that owns R2-06-01 runs LAST — so
// every half must be able to bring comment F into existence itself. `ensureFlaggedComment`
// therefore finds F by its unique element selector ('.token' — nothing seeded uses it, and it
// survives body edits) and creates it exactly as R2-06-01 step 1 specifies when missing. In the
// canonical run order only the first call creates; the rest are no-ops.
import { get, post, put } from './api.mjs';
import { Environment } from './constants.mjs';

// Deterministic FAKE canaries (R2-06-tests Preconditions: never a real credential). Both are
// ghp_ + exactly 36 chars of [A-Za-z0-9] so the github_token detector pattern matches, and they
// are distinct so the re-add edit in R2-06-04 provably recomputes rather than reusing state.
const canary1 = 'e2eAlphaPayloadCanary'.repeat(2).slice(0, 36);
const canary2 = 'e2eRotatePayloadCanary'.repeat(2).slice(0, 36);
if (canary1.length !== 36 || canary2.length !== 36) {
  throw new Error(`canary length drift: ${canary1.length}/${canary2.length} — must stay 36`);
}

export const GHP_CANARY = `ghp_${canary1}`;
export const GHP_CANARY_2 = `ghp_${canary2}`;
export const FLAGGED_BODY = `leaked key ${GHP_CANARY} please rotate`;
export const CLEAN_BODY = 'safe text now, token removed';
export const ROTATE_BODY = `rotate again ${GHP_CANARY_2}`;
export const SCRIPT_REPLY = '<script>alert(1)</script>';
export const FLAG_ELEMENT = { selector: '.token', route: '/' };
export const PROJECT_KEY = 'e2e-alpha';

/** Case-sensitive substring count — the doc's `grep -c` semantics. */
export function countPayloadFlag(text) {
  return String(text ?? '').split('payloadFlag').length - 1;
}

/**
 * Returns the id of the scenario's flagged comment F on e2e-alpha, creating it (QA token) as
 * R2-06-01 step 1 specifies when it does not exist yet.
 *
 * `ensureFlagged: true` (default) additionally re-applies FLAGGED_BODY when a previous half of
 * the suite left F edited clean — an idempotent no-op in the canonical order, and what lets the
 * api/cli phases run before the widget phase that "owns" the creation step.
 */
export async function ensureFlaggedComment(qaToken, { ensureFlagged = true } = {}) {
  const list = await get(`/api/projects/${PROJECT_KEY}/comments?pageSize=100`, { token: qaToken });
  const existing = (list.items || []).find((c) => c.element?.selector === FLAG_ELEMENT.selector);
  if (!existing) {
    const created = await post(
      `/api/projects/${PROJECT_KEY}/comments`,
      { body: FLAGGED_BODY, environment: Environment.Local, element: FLAG_ELEMENT },
      { token: qaToken },
    );
    return created.id;
  }
  if (ensureFlagged && existing.body !== FLAGGED_BODY) {
    await put(`/api/comments/${existing.id}`, { body: FLAGGED_BODY }, { token: qaToken });
  }
  return existing.id;
}
