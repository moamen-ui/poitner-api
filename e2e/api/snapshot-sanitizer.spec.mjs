// R3-04 API half: the server-side snapshot sanitizer.
//
// The widget sanitizes before it posts (widget/privacy-snapshot.spec.ts covers that). This file
// covers the safety net behind it — what the API does with a snapshot that arrives raw, which is
// what any non-widget caller can send: the CLI, a script, a stale widget build, or someone with
// the project's key and curl. The widget's escaping means a malformed tag can only reach the
// server this way, so these are the only tests that can reach the sanitizer's exception path.
//
// Scenarios: R3-04-03 (api half — the captureTextContent toggle and its permission gate) and
// R3-04-04 (create-path sanitizing, including never-throw).
import { expect, test } from '@playwright/test';
import { get, post, raw, login, ApiError } from '../scripts/lib/api.mjs';
import { credentials, expected } from '../scripts/lib/state.mjs';
import { PORTS } from '../scripts/lib/constants.mjs';
import { record } from '../scripts/lib/report.mjs';

const PRIVACY_KEY = 'e2e-privacy';
const PRIVACY_URL = `http://localhost:${PORTS.privacy}/`;

let wa;          // workspace admin — owns the project, authors R3-04-04's comments
let qa;          // tester — non-admin, the 403 half of the gate
let deputy;      // admin-tier non-creator, the 200 half of the gate
let privacyId;

/** Same create-or-find as widget/lib/ensure-project.ts; this file cannot import the .ts helper. */
async function ensureProject(token, key, name) {
  try {
    return await post('/api/admin/projects', { key, name }, { token });
  } catch (err) {
    if (!(err instanceof ApiError) || err.status !== 409) throw err;
    const all = await get('/api/admin/projects', { token });
    const found = (all || []).find((p) => p.key === key);
    if (!found) throw new Error(`${key} conflicted but was not in the list`);
    return found;
  }
}

/** Posts one comment carrying a raw element snapshot and returns what the API stored back. */
async function postSnapshot(snapshot, body) {
  const created = await post(
    `/api/projects/${PRIVACY_KEY}/comments`,
    {
      body,
      environment: 2,
      element: {
        selector: '#legacy-input',
        route: '/',
        snapshot,
        pageUrl: PRIVACY_URL,
        pageTitle: 'e2e-privacy fixture',
      },
    },
    { token: wa.token },
  );

  const list = await get(`/api/projects/${PRIVACY_KEY}/comments?pageSize=50`, { token: wa.token });
  const items = list?.items || list || [];
  const found = items.find((c) => c.id === created.id);
  expect(found, `comment ${created.id} was not returned by the list`).toBeTruthy();
  return found.element?.snapshot;
}

test.beforeAll(async () => {
  const creds = credentials();
  wa = await login(creds.wsAdmin.email, creds.wsAdmin.password);
  qa = await login(creds.tester.email, creds.tester.password);
  // The Workspace Admin Deputy, not the developer: this half of the gate is specifically about
  // an ADMIN-TIER user who did not create the project (constants.mjs — Deputy is the closest
  // creatable GrantsAdmin role, standing in for a second admin).
  deputy = await login(creds.deputy.email, creds.deputy.password);
  const project = await ensureProject(wa.token, PRIVACY_KEY, 'E2E Privacy');
  privacyId = project.id;
});

test('R3-04-03 ⛓ — project-no-text-capture (api half: toggle + gate)', async () => {
  const start = Date.now();
  try {
    // 1. The default is ON. This is an additive column, so an existing project must read `true`
    //    without anyone having set it — a default of false would silently stop capturing text for
    //    every project that predates the migration.
    const before = await get(`/api/projects/${PRIVACY_KEY}/capture-config`, { token: qa.token });
    expect(before?.captureTextContent, 'captureTextContent must default to true').toBe(true);

    // 2. The owner turns it off and the widget's own config endpoint reflects it.
    const off = await raw('PATCH', `/api/admin/projects/${privacyId}`, {
      token: wa.token,
      body: { captureTextContent: false },
    });
    expect(off.status).toBe(200);

    const after = await get(`/api/projects/${PRIVACY_KEY}/capture-config`, { token: qa.token });
    expect(after?.captureTextContent).toBe(false);

    // 5. The gate. A tester cannot change a project setting…
    const byTester = await raw('PATCH', `/api/admin/projects/${privacyId}`, {
      token: qa.token,
      body: { captureTextContent: false },
    });
    expect(byTester.status, 'a non-admin must not be able to change capture settings').toBe(403);

    // …a foreign tenant gets 404 rather than 403, because admitting the project exists is itself
    // a cross-tenant leak.
    //
    // Unconditional on purpose. Guarding this behind `if (foreignId && tenantB)` would make the
    // suite's only cross-tenant assertion here disappear silently the day the seed stops
    // producing either one — the isolation check would stop running and nothing would say so.
    // The seed always writes both; if it ever does not, this should fail loudly.
    const foreignId = expected().projects.alpha.id;
    const tenantB = credentials().tenantBOwner;
    const tb = await login(tenantB.email, tenantB.password);
    const byForeign = await raw('PATCH', `/api/admin/projects/${foreignId}`, {
      token: tb.token,
      body: { captureTextContent: false },
    });
    expect(byForeign.status, 'another tenant must not learn the project exists').toBe(404);

    // …while an admin-tier user who did not create the project still may. Asserting the exact
    // status matters here: accepting "200 or 403" would pass whether the gate admits admin-tier
    // or not, which is the one thing this step exists to pin down.
    const byDeputy = await raw('PATCH', `/api/admin/projects/${privacyId}`, {
      token: deputy.token,
      body: { captureTextContent: false },
    });
    expect(byDeputy.status, 'admin-tier must be admitted even when not the creator').toBe(200);

    record({
      id: 'R3-04-03', tier: 'PR', layer: 'api', role: 'WA, QA, DP, TB', result: 'PASS',
      ms: Date.now() - start,
      detail: 'default true; toggle honoured; tester 403; foreign tenant 404',
    });
  } finally {
    // 6. Restore. A project left with text capture off poisons every later scenario that reads a
    //    snapshot — including this file's own R3-04-04, which expects text to survive.
    await raw('PATCH', `/api/admin/projects/${privacyId}`, {
      token: wa.token,
      body: { captureTextContent: true },
    });
    const restored = await get(`/api/projects/${PRIVACY_KEY}/capture-config`, { token: wa.token });
    expect(restored?.captureTextContent, 'the toggle must be back on for later scenarios').toBe(true);
  }
});

test('R3-04-04 — snapshot sanitized on the create path', async () => {
  const start = Date.now();

  // 1–2. A value attribute is what carries typed input — a password, a card number, an address.
  // It is replaced in place, so the tag's shape and attribute order survive for the developer
  // reading the comment.
  const withValue = await postSnapshot(
    '<input id="legacy-input" type="text" value="x">',
    'R3-04-04 legacy raw post',
  );
  expect(withValue).toBe('<input id="legacy-input" type="text" value="•••">');

  // 3. Identifier-bearing attributes are dropped outright rather than masked: unlike `value`,
  // there is no reason for a developer to know one was present. Text is kept — the project
  // toggle is on here, and R3-04-03 covers the other setting.
  const withTokens = await postSnapshot(
    '<span id="t1" data-token="abc" data-user-id="42">hello</span>',
    'R3-04-04 sensitive attributes',
  );
  expect(withTokens).toBe('<span id="t1">hello</span>');

  // 4. Malformed input must pass through byte-identical. The sanitizer runs on data from outside,
  // so the one thing it must never do is throw — a 500 here would turn a junk snapshot into a
  // failed comment, losing the feedback the user was trying to send.
  const malformed = '<input id="q" value="x"';
  const passedThrough = await postSnapshot(malformed, 'R3-04-04 malformed snapshot');
  expect(passedThrough, 'a snapshot the parser cannot read must be stored untouched').toBe(malformed);

  record({
    id: 'R3-04-04', tier: 'PR', layer: 'api', role: 'WA', result: 'PASS', ms: Date.now() - start,
    detail: 'value masked in place; data-token/data-user-* dropped; malformed passed through',
  });
});
