// Playwright widget spec for R3-04: DOM-snapshot privacy.
// Scenarios (contract: docs/roadmap/testing/R3-04-tests.md):
// - R3-04-01 — snapshot-no-form-values (typed form values never leave the browser)
// - R3-04-02 — snapshot-mask-attribute (data-snapshot-mask masks text + attribute VALUES)
// - R3-04-03 ⛓ — project-no-text-capture, WIDGET half (CaptureTextContent=false end-to-end;
//   the api half — default, toggle write, admin/creator gate matrix — lives in
//   e2e/api/snapshot-sanitizer.spec.mjs, which runs in the earlier api phase)
//
// Every snapshot assertion below compares against a byte-exact literal (or anchored regex) built
// from the fixture markup in fixture-app/privacy/index.html — attribute ORDER included, because
// shallowSnapshot (web-component/src/capture.ts) emits attributes in DOM order and the server
// sanitizer preserves them. The three-bullet mask is the literal '•••' (U+2022 ×3), never a
// regex placeholder.
import { test, expect, type Page } from '@playwright/test';
import { spawn, type ChildProcess } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { raw, post, login } from '../scripts/lib/api.mjs';
import { PORTS } from '../scripts/lib/constants.mjs';
import { preAuthWidget, waitForWidgetReady, pickElement } from './lib/auth';
import { ensureProject } from './lib/ensure-project';
import { credentials as loadCredentials, loginClient } from '../scripts/lib/state.mjs';
import { record } from '../scripts/lib/report.mjs';

const here = dirname(fileURLToPath(import.meta.url));
// Read lazily: Playwright evaluates this file to DISCOVER tests, so an eager read on an
// unseeded workspace made `playwright test --list` report 0 tests in 0 files.
const credentials = () => loadCredentials();

// The ONLY commenting surface for these scenario ids — never e2e-alpha/e2e-beta (ground truth +
// R1-05 fixture), per R3-04-tests.md §Flake notes.
const PRIVACY_KEY = 'e2e-privacy';
const PRIVACY_URL = `http://localhost:${PORTS.privacy}/`;

let fixtureServer: ChildProcess | null = null;
let privacyProjectId: number;

test.beforeAll(async () => {
  // Serve the privacy fixture with serve-dir.mjs (the contract's chosen server), reusing an
  // already-running instance so this file coexists with any phase runner that started it.
  // Fail HERE, with the reason, rather than 30 s later on a missing <pointer-feedback> that
  // reads like a widget bug (same rationale as widget/project-env-urls.spec.ts).
  const isUp = await fetch(PRIVACY_URL).then((r) => r.ok).catch(() => false);
  if (!isUp) {
    fixtureServer = spawn(
      'node',
      [join(here, '..', 'scripts', 'serve-dir.mjs'), join(here, '..', 'fixture-app', 'privacy'), String(PORTS.privacy)],
      { stdio: 'ignore' },
    );
    const deadline = Date.now() + 10_000;
    let ready = false;
    while (Date.now() < deadline) {
      ready = await fetch(PRIVACY_URL).then((r) => r.ok).catch(() => false);
      if (ready) break;
      await new Promise((r) => setTimeout(r, 200));
    }
    if (!ready) {
      throw new Error(`privacy fixture never came up on ${PRIVACY_URL} — is port ${PORTS.privacy} already in use?`);
    }
  }

  // Ensure e2e-privacy exists (idempotent; created here rather than in seed.mjs per the
  // contract's Preconditions — in a full run the api phase's spec has usually created it
  // already, and the 409-tolerant path reuses that row).
  const wsAdmin = await login(credentials().wsAdmin.email, credentials().wsAdmin.password);
  const project = await ensureProject(wsAdmin.token, PRIVACY_KEY, 'E2E Privacy');
  privacyProjectId = project.id;
});

test.afterAll(() => {
  if (fixtureServer) {
    fixtureServer.kill();
    fixtureServer = null;
  }
});

// Headroom for pickAndComment's one rate-limit wait (62s) plus the work either side of it. The
// 30s default cannot survive that wait, so a budget spent by an earlier spec would fail the
// scenario for a reason that has nothing to do with what it tests.
test.describe.configure({ timeout: 180_000 });

/**
 * Opens the popover on `targetSelector`, submits `body`, and waits for the popover to clear.
 *
 * It watches the POST rather than only the popover. Comment creation is rate-limited to 30/min
 * per USER, and this file authors four comments as the tester — who also authors most of the rest
 * of the widget phase. When that budget runs out the submit 429s, the popover stays open, and the
 * only symptom is `toBeEmpty` timing out on `#fbk-popover-host`: a message that points at the
 * widget and says nothing about the limit. Reading the response turns that into either a clean
 * wait-and-retry (the limit is real product behaviour, not something to switch off for tests) or
 * a failure that names the actual status.
 */
async function pickAndComment(page: Page, targetSelector: string, body: string): Promise<void> {
  const submitOnce = async (): Promise<number> => {
    await pickElement(page, targetSelector);
    const popover = page.locator('#fbk-popover-host');
    await popover.locator('#fbk-comment-text').fill(body);

    const posted = page.waitForResponse(
      (r) => r.request().method() === 'POST' && /\/comments$/.test(new URL(r.url()).pathname),
      { timeout: 15_000 },
    );
    await popover.locator('#fbk-submit').click();
    return (await posted).status();
  };

  let status = await submitOnce();
  if (status === 429) {
    // The sliding window is one minute; wait it out once rather than failing the scenario over a
    // budget another spec spent.
    console.log(`[R3-04] comment rate limit hit posting "${body}" — waiting 62s for the window`);
    await page.waitForTimeout(62_000);
    status = await submitOnce();
  }

  expect(status, `submitting "${body}" must be accepted`).toBeLessThan(400);
  await expect(page.locator('#fbk-popover-host')).toBeEmpty({ timeout: 10_000 });
}

interface ListResult {
  status: number;
  text: string;
  items: Array<Record<string, any>>;
}

/** Staff (workspace-admin) read of the project's comments, as the raw envelope + parsed items. */
async function staffList(waToken: string): Promise<ListResult> {
  const res = await raw('GET', `/api/projects/${PRIVACY_KEY}/comments?pageSize=50`, { token: waToken });
  expect(res.status, 'staff list read must be 200').toBe(200);
  return { status: res.status, text: res.text, items: (res.data?.items ?? []) as Array<Record<string, any>> };
}

/**
 * Finds a comment by its exact body. The contract says "newest item" — the list is ordered
 * CreatedAt-descending so this IS the newest — but matching on the unique body also makes the
 * assertion immune to a same-microsecond CreatedAt tie with a sibling scenario's comment.
 */
function findByBody(list: ListResult, body: string): Record<string, any> {
  const hit = list.items.find((c) => c.body === body);
  expect(hit, `comment "${body}" must exist and be visible to staff`).toBeTruthy();
  return hit;
}

test('R3-04-01 — snapshot-no-form-values', async ({ page }) => {
  const start = Date.now();
  // CL persona. QuickAccess accounts are passwordless (R2-05): loginClient redeems the seeded
  // magic link — password login refuses the account outright.
  const client = await loginClient({ post, login });
  const wa = await login(credentials().wsAdmin.email, credentials().wsAdmin.password);

  // 2. Pre-auth, then register the capture-config waiter BEFORE goto (the flag is read once at
  // boot) and only then navigate.
  await preAuthWidget(page, client.token, client.user);
  const captureConfigLoaded = page.waitForResponse((r) => r.url().includes('/capture-config'));
  await page.goto(PRIVACY_URL);
  await waitForWidgetReady(page, captureConfigLoaded);

  // 3. Typed values via Playwright fill() — it sets the value PROPERTY only, never the
  // attribute, which is exactly the typed-value case rule A.1 targets. Replacing this with
  // setAttribute('value', …) would flip the scenario to the attribute branch and silently
  // weaken step 5 (R3-04-tests.md §Flake notes).
  await page.locator('#signup-email').fill('client@example.com');
  await page.locator('#password').fill('Sup3rSecret!');

  // 4. Pick the typed email input and submit.
  await pickAndComment(page, '#signup-email', 'R3-04-01 form values must not leak');

  // 5-6. Staff read: exact snapshot (value="•••" appended last — the fixture input carries no
  // value attribute), plus the strongest assertion on the RAW response text: neither typed
  // secret appears anywhere in the JSON. (AuthorName is a display name — the client's EMAIL is
  // not serialized on this DTO, so a leak here can only come from the snapshot.)
  const list = await staffList(wa.token);
  const formComment = findByBody(list, 'R3-04-01 form values must not leak');
  expect(formComment.element?.snapshot).toBe(
    '<input id="signup-email" type="email" name="email" placeholder="Work email" value="•••"/>',
  );
  expect(list.text, 'the typed email must not appear anywhere in the response').not.toContain('client@example.com');
  expect(list.text, 'the typed password must not appear anywhere in the response').not.toContain('Sup3rSecret');
  // Idempotency: the server sanitizer must not re-rewrite the widget's already-masked value —
  // a double rewrite would leave six bullets ("••••••") in the stored string.
  expect((String(formComment.element.snapshot).match(/•••/g) ?? []).length).toBe(1);

  // 7. Untyped regression: #phone was never typed — empty DOM value ⇒ no value attribute at all.
  await pickAndComment(page, '#phone', 'R3-04-01 untyped phone stays valueless');
  const phoneComment = findByBody(await staffList(wa.token), 'R3-04-01 untyped phone stays valueless');
  expect(phoneComment.element?.snapshot).toBe('<input id="phone" type="tel" name="phone"/>');

  // 8. Non-form regression: a button's visible text survives — the rules touch form values only.
  await pickAndComment(page, '#cta', 'R3-04-01 plain text button');
  const ctaComment = findByBody(await staffList(wa.token), 'R3-04-01 plain text button');
  expect(ctaComment.element?.snapshot).toBe('<button id="cta" type="submit">Request invite</button>');

  record({
    id: 'R3-04-01',
    tier: 'PR',
    layer: 'widget',
    role: 'CL',
    result: 'PASS',
    ms: Date.now() - start,
    detail: 'typed value → ••• exactly once; untyped input stays valueless; button text kept',
  });
});

test('R3-04-02 — snapshot-mask-attribute', async ({ page }) => {
  const start = Date.now();
  const qa = await login(credentials().tester.email, credentials().tester.password);
  const wa = await login(credentials().wsAdmin.email, credentials().wsAdmin.password);

  // 1. QA login + pre-auth; capture-config waiter registered before goto.
  await preAuthWidget(page, qa.token, qa.user);
  const captureConfigLoaded = page.waitForResponse((r) => r.url().includes('/capture-config'));
  await page.goto(PRIVACY_URL);
  await waitForWidgetReady(page, captureConfigLoaded);

  // 2. Pick the cell inside <table data-snapshot-mask>.
  await pickAndComment(page, '#cust-name-cell', 'R3-04-02 mask');

  // 5+6 (the pick): the unmasked control, #tagline, in the SAME page load — step 5 needs its
  // dedicated capture fields for the shape comparison and step 6 asserts its escaping
  // round-trip; one pick serves both.
  await pickAndComment(page, '#tagline', 'R3-04-02 unmasked control');

  // 3-6. Staff read of both comments.
  const list = await staffList(wa.token);
  const masked = findByBody(list, 'R3-04-02 mask');
  const control = findByBody(list, 'R3-04-02 unmasked control');

  // 3. Attribute NAMES kept, data-customer-name value masked (data-* values are masked inside a
  // masked subtree), data-email dropped (sensitive-name list), id/role kept (structural), text
  // masked via the ancestor's data-snapshot-mask.
  expect(masked.element?.snapshot).toMatch(
    /^<td id="cust-name-cell" data-customer-name="•••" role="cell">•••<\/td>$/,
  );

  // 4. The raw response text carries neither the customer's name nor her email.
  expect(list.text, 'masked text must not leak into the response').not.toContain('Jane Doe');
  expect(list.text, 'sensitive attribute value must not leak into the response').not.toContain('jane@example.test');

  // 5. Selector generation is untouched by masking, and the dedicated capture fields keep the
  // same SHAPE as the control's — class/style are excluded from the snapshot precisely because
  // they travel in these fields (capture.ts), so masking the snapshot must never touch them.
  expect(masked.element?.selector).toMatch(/cust-name-cell/);
  expect(typeof masked.element?.classes, 'classes must stay a JSON string').toBe(typeof control.element?.classes);
  expect(JSON.parse(masked.element.classes)).toEqual(JSON.parse(control.element.classes));
  const maskedStyles = JSON.parse(masked.element.computedStyles);
  const controlStyles = JSON.parse(control.element.computedStyles);
  expect(Object.keys(maskedStyles).length, 'computedStyles must stay a populated JSON object').toBeGreaterThan(0);
  expect(Object.keys(controlStyles).length, 'the control computedStyles must be populated too').toBeGreaterThan(0);
  // A plain static page offers no data-component-source and no framework internals: both are
  // null, and masking must not have changed that.
  expect(masked.element?.sourcePath).toEqual(control.element?.sourcePath);

  // 6. Escaping round-trip (AC-3, unmasked element): a " in an attribute value round-trips as
  // &quot; and the server sanitizer still parsed the tag — the stored string is a well-formed
  // single tag, not a mangled fragment.
  expect(control.element?.snapshot).toBe('<p id="tagline" data-note="Say &quot;hi&quot;">Fast feedback</p>');

  // 7. The mask covers descendants: the plan cell has no own attributes to mask, its TEXT is
  // masked because its ancestor table carries the attribute.
  await pickAndComment(page, '#cust-plan-cell', 'R3-04-02 descendant cell');
  const planComment = findByBody(await staffList(wa.token), 'R3-04-02 descendant cell');
  expect(planComment.element?.snapshot).toBe('<td id="cust-plan-cell">•••</td>');

  record({
    id: 'R3-04-02',
    tier: 'PR',
    layer: 'widget',
    role: 'QA',
    result: 'PASS',
    ms: Date.now() - start,
    detail: 'mask: attr names kept, values •••, data-email dropped, selector survives, &quot; round-trips',
  });
});

// ⛓ per R3-04-tests.md §State coupling: it PATCHes a project toggle, and a leaked `false`
// breaks sibling scenarios — never retried alone (the runner refuses it via the doc's coupling
// section). The widget half is independently runnable: it flips the toggle itself instead of
// trusting the api half, because the api half restores `true` in its own finally.
test('R3-04-03 ⛓ — project-no-text-capture (widget half: no-text comment)', async ({ page }) => {
  const start = Date.now();
  const qa = await login(credentials().tester.email, credentials().tester.password);
  const wa = await login(credentials().wsAdmin.email, credentials().wsAdmin.password);

  await preAuthWidget(page, qa.token, qa.user);
  const captureConfigLoaded = page.waitForResponse((r) => r.url().includes('/capture-config'));
  await page.goto(PRIVACY_URL);
  await waitForWidgetReady(page, captureConfigLoaded);

  try {
    // Toggle off (WA). Steps 1-2-5 of this scenario — the default read, the toggle write and
    // the gate matrix — are the api half's; here the toggle is only the precondition for the
    // widget behaviour.
    const off = await raw('PATCH', `/api/admin/projects/${privacyProjectId}`, {
      token: wa.token,
      body: { captureTextContent: false },
    });
    expect(off.status, 'WA must be able to disable text capture').toBe(200);

    // 3. capture-config is cached at boot — reload and re-wait before commenting, or the
    // widget still holds the old flag.
    const reloaded = page.waitForResponse((r) => r.url().includes('/capture-config'));
    await page.reload();
    await waitForWidgetReady(page, reloaded);

    await pickAndComment(page, '#cta', 'R3-04-03 no text');

    // 4. Staff read: text replaced by ••• (matching the §C sanitizer's normalised form so
    // client and server agree deterministically), pageTitle masked, selector and attributes
    // still captured under §A.
    const list = await staffList(wa.token);
    const noText = findByBody(list, 'R3-04-03 no text');
    expect(noText.element?.snapshot).toBe('<button id="cta" type="submit">•••</button>');
    expect(noText.element?.pageTitle).toBe('•••');
    expect(noText.element?.selector).toMatch(/cta/);
  } finally {
    // 6. Restore is part of PASS criteria: a leaked `false` breaks R3-04-04's text-kept
    // expectation on the next run and H-02's two-run determinism.
    const restore = await raw('PATCH', `/api/admin/projects/${privacyProjectId}`, {
      token: wa.token,
      body: { captureTextContent: true },
    });
    expect(restore.status, 'restoring captureTextContent=true must succeed').toBe(200);
    const cfg = await raw('GET', `/api/projects/${PRIVACY_KEY}/capture-config`, { token: qa.token });
    expect(cfg.status).toBe(200);
    expect(cfg.data?.captureTextContent, 'the toggle must read true again after restore').toBe(true);
  }

  // Recorded after the finally: a failed restore must not be reportable as PASS.
  record({
    id: 'R3-04-03',
    tier: 'PR',
    layer: 'widget',
    role: 'QA, WA',
    result: 'PASS',
    ms: Date.now() - start,
    detail: 'widget half — no-text comment: text → •••, pageTitle •••, selector/attrs intact, toggle restored',
  });
});
