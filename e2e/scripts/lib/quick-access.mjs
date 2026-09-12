// Shared fixture + invite helpers for the R2-05 quick-access scenarios
// (docs/roadmap/testing/R2-05-tests.md).
//
// Lives in scripts/lib/ rather than inside e2e/api/quick-access.spec.mjs (which the contract's
// "Spec files" section names as its home) because a spec file's import would re-register that
// spec's tests inside the importing file — Playwright evaluates each test file in its own module
// registry, so `import { inviteQaClient } from '../api/quick-access.spec.mjs'` from the widget
// spec would run R2-05-05/08 twice, once inside the widget phase. A plain lib module is the only
// shape both an .mjs and a .ts spec can share safely.
//
// Every function here returns the raw() outcome for anything whose STATUS is an assertion of the
// contract — the caller asserts with expect(res.status), never a try/catch (the matrix rule).
import { raw, get } from './api.mjs';

// The quick-access fixture project (R2-05-tests Preconditions). The smoke page honours ?project=
// (fixture-app/smoke/index.html), so the magic link keeps the project override as an ordinary
// query param and the API appends pointer_invite with '&'.
export const QA_PROJECT_KEY = 'e2e-qa-invite';
export const QA_PROJECT_NAME = 'E2E QA Invite';
export const SMOKE_ORIGIN = 'http://localhost:4173';
export const QA_APP_URL = `${SMOKE_ORIGIN}/?project=${QA_PROJECT_KEY}`;

// R2-05-01 step 2's exact expected shape: <AppUrl>?project=…&pointer_invite=<43-char token>.
// If the project override ever moves out of the URL, this regex is what the Flake notes say to
// update.
export const MAGIC_LINK_RE = /^http:\/\/localhost:4173\/\?project=e2e-qa-invite&pointer_invite=[A-Za-z0-9_-]{43}$/;

// The widget-status invariant is asserted once per process (the api, widget, mail and 429 phases
// are separate node processes against one shared run) — a module flag keeps repeat ensureQaFixture
// calls from re-asserting it inside the same file.
let widgetStatusAsserted = false;

/**
 * Creates (or reuses, on 409) the e2e-qa-invite project and pins its AppUrl to the smoke fixture
 * with the ?project= override. Idempotent by design: the api phase runs before the widget phase,
 * so whichever spec file runs first materialises the fixture and the later ones reuse it.
 */
export async function ensureQaFixture(waToken) {
  let project;
  const create = await raw('POST', '/api/admin/projects', {
    token: waToken,
    body: { key: QA_PROJECT_KEY, name: QA_PROJECT_NAME },
  });
  if (create.status === 200) {
    project = create.data;
  } else if (create.status === 409) {
    const all = await get('/api/admin/projects', { token: waToken });
    project = all.find((p) => p.key === QA_PROJECT_KEY);
    if (!project) {
      throw new Error(`${QA_PROJECT_KEY} conflicted on create but was not found via list — ${create.text}`);
    }
  } else {
    throw new Error(`creating ${QA_PROJECT_KEY} failed: ${create.status} ${create.text}`);
  }

  const pin = await raw('PATCH', `/api/admin/projects/${project.id}`, {
    token: waToken,
    body: { appUrl: QA_APP_URL },
  });
  if (pin.status !== 200) {
    throw new Error(`pinning ${QA_PROJECT_KEY} appUrl failed: ${pin.status} ${pin.text}`);
  }

  // Preconditions: assert widget-status active for the smoke origin once — if this is false the
  // widget never renders and every scenario below fails with an unrelated timeout, so fail here
  // with the reason instead.
  if (!widgetStatusAsserted) {
    widgetStatusAsserted = true;
    const status = await raw(
      'GET',
      `/api/public/projects/${QA_PROJECT_KEY}/widget-status?origin=${encodeURIComponent(SMOKE_ORIGIN)}`,
    );
    if (status.status !== 200 || status.data?.active !== true) {
      throw new Error(
        `widget-status for ${QA_PROJECT_KEY} @ ${SMOKE_ORIGIN} is not active: ${status.status} ${status.text}`,
      );
    }
  }

  return project;
}

/** The Client (QuickAccess) role id — same resolution as seed.mjs's roleId('Client'). */
export async function clientRoleId(waToken) {
  const roles = await get('/api/admin/roles', { token: waToken });
  const role = roles.find((r) => r.name === 'Client');
  if (!role) {
    throw new Error(`Client role not found (available: ${roles.map((r) => r.name).join(', ')})`);
  }
  return role.id;
}

/** POST /api/admin/invites for a quick-access client on the fixture project. Raw outcome — the caller asserts the status. */
export function inviteQaClient(waToken, { roleId, email, expiresInDays = 14, projectId }) {
  return raw('POST', '/api/admin/invites', {
    token: waToken,
    body: { roleId, email, expiresInDays, projectId },
  });
}

/** POST /api/admin/invites/{id}/quick-link/rotate — raw outcome; R2-05-03/04 assert 200 + new link. */
export function rotateQuickLink(waToken, inviteId) {
  return raw('POST', `/api/admin/invites/${inviteId}/quick-link/rotate`, { token: waToken, body: {} });
}

/** POST /api/auth/login-with-invite — raw outcome, so 200/400/429 are all assertable by the caller. */
export function redeemInvite(token) {
  return raw('POST', '/api/auth/login-with-invite', { body: { token } });
}

/** The raw 43-char token out of a magic link. */
export function extractInviteToken(magicLink) {
  const token = new URL(magicLink).searchParams.get('pointer_invite');
  if (!token) throw new Error(`no pointer_invite param in magic link: ${magicLink}`);
  return token;
}
