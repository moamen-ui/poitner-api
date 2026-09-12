// Playwright widget spec for R1-09 Nightly tier.
// Scenario implemented:
// - R1-09-07 ⛓ — disabled environment: extension lookup misses, widget gate stays active
// Contract: docs/roadmap/testing/R1-09-tests.md
import { test, expect } from '@playwright/test';
import { spawn, type ChildProcess } from 'node:child_process';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { raw, login, postRaw, patchRaw, putRaw, delRaw } from '../scripts/lib/api.mjs';
import { SUPER_ADMIN, TENANT_OWNER, USERS, PORTS } from '../scripts/lib/constants.mjs';
import { preAuthWidget } from './lib/auth';
import { record } from '../scripts/lib/report.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const STATE_DIR = join(here, '..', 'state');
const credPath = join(STATE_DIR, 'credentials.json');
const credentials = existsSync(credPath)
  ? JSON.parse(readFileSync(credPath, 'utf8'))
  : {
      superAdmin: SUPER_ADMIN,
      wsAdmin: TENANT_OWNER,
      tester: USERS.tester,
    };

const RUN_ID = Math.random().toString(36).substring(2, 7);

// Port and URL for the beta fixture
const BETA_FIXTURE_PORT = PORTS.beta || 4182;
const BETA_FIXTURE_URL = `http://localhost:${BETA_FIXTURE_PORT}/`;

let fixtureServer: ChildProcess | null = null;

test.beforeAll(async () => {
  // Ensure the beta fixture server is reachable on port 4182
  const isUp = await fetch(BETA_FIXTURE_URL).then((r) => r.ok).catch(() => false);
  if (!isUp) {
    const serveScript = join(here, '..', 'fixture-app', 'serve.mjs');
    fixtureServer = spawn('node', [serveScript, 'beta', String(BETA_FIXTURE_PORT)], {
      stdio: 'ignore',
    });

    const deadline = Date.now() + 10_000;
    let ready = false;
    while (Date.now() < deadline) {
      ready = await fetch(BETA_FIXTURE_URL).then((r) => r.ok).catch(() => false);
      if (ready) break;
      await new Promise((r) => setTimeout(r, 200));
    }
    // Fail here, not later. Proceeding without a fixture turns "the server never started" into a
    // 30s timeout on a missing <pointer-feedback>, which reads like a product bug.
    if (!ready) {
      throw new Error(`beta fixture never came up on ${BETA_FIXTURE_URL} — is port ${BETA_FIXTURE_PORT} already in use?`);
    }
  }
});

test.afterAll(() => {
  if (fixtureServer) {
    fixtureServer.kill();
    fixtureServer = null;
  }
});

// R1-09-07 is a NIGHTLY scenario (marked ⛓ in R1-09-tests.md: it is chained, depending on state
// earlier nightly scenarios establish). Running it in the PR tier exercises it without its chain,
// which proves nothing and fails for the wrong reason.
//
// Tier is the right signal HERE — unlike the destructive guards, which key off their phase flag.
// The question is "should this scenario run at all", not "is it safe to run in this phase".
// KNOWN FAILING (nightly only). The widget never renders #pf-add for the per-run project this
// scenario builds, so the assertion that the gate "stays active" cannot be checked. Ruled out so
// far: the beta fixture itself (loads fine standalone — tag present, custom element defined), the
// route-interception headers (fixed below), and the fixture failing to start (now fails loudly).
// What remains is a product question the contract answers one way and the code may answer another:
// whether a project whose AppEnvironment is disabled should still initialise the widget.
//
// Left running and red rather than skipped: a scenario nobody can see is how this suite got into
// the state it was in.
test('R1-09-07 ⛓ — disabled environment: extension lookup misses, widget gate stays active', async ({ page }) => {
  test.skip(process.env.TIER !== 'nightly', 'chained nightly scenario — needs the nightly phase order');
  const start = Date.now();
  const wsAdminCreds = credentials.wsAdmin || TENANT_OWNER;
  const testerCreds = credentials.tester || USERS.tester;
  const wsAdmin = await login(wsAdminCreds.email, wsAdminCreds.password);
  const tester = await login(testerCreds.email, testerCreds.password);

  const projectWKey = `e2e-r109-w-${RUN_ID}`;
  let projectWId: number | null = null;
  let extEnvId: number | null = null;
  let localId: number | null = null;

  try {
    // ─── API HALF ─────────────────────────────────────────────────────────────

    // 1. WA creates e2e-r109-w-<runId> with appUrl: 'http://localhost:<PORTS.beta>' (no env id → local)
    const createProjRes = await postRaw(
      '/api/admin/projects',
      {
        key: projectWKey,
        name: 'R109 Widget Gate',
        appUrl: `http://localhost:${BETA_FIXTURE_PORT}`,
      },
      { token: wsAdmin.token },
    );
    expect(createProjRes.status).toBe(200);
    projectWId = createProjRes.data.id;
    localId = createProjRes.data.appUrls?.[0]?.appEnvironmentId ?? null;

    // 2. WA POST /api/admin/environments { name: 'r109-<runId>-ext' } → extId;
    // put …/app-urls/{extId} { url: 'https://r109-ext.test' }
    const createEnvRes = await postRaw(
      '/api/admin/environments',
      { name: `r109-${RUN_ID}-ext` },
      { token: wsAdmin.token },
    );
    expect(createEnvRes.status).toBe(200);
    extEnvId = createEnvRes.data.id;

    const putExtUrlRes = await putRaw(
      `/api/admin/projects/${projectWId}/app-urls/${extEnvId}`,
      { url: 'https://r109-ext.test', isActive: true },
      { token: wsAdmin.token },
    );
    expect(putExtUrlRes.status).toBe(200);

    // 3. WA GET /api/extension/project-for-origin?origin=https://r109-ext.test
    // (resolved from API/Controllers/ExtensionController.cs ProjectForOrigin route)
    const extLookup1 = await raw('GET', `/api/extension/project-for-origin?origin=${encodeURIComponent('https://r109-ext.test')}`, {
      token: wsAdmin.token,
    });
    // 3 → 200, key === 'e2e-r109-w-<runId>'
    expect(extLookup1.status).toBe(200);
    expect(extLookup1.data?.key).toBe(projectWKey);

    // 4. WA PATCH /api/admin/environments/{extId} { isEnabled: false }; repeat step 3
    const disableExtRes = await patchRaw(
      `/api/admin/environments/${extEnvId}`,
      { isEnabled: false },
      { token: wsAdmin.token },
    );
    expect(disableExtRes.status).toBe(200);
    expect(disableExtRes.data.isEnabled).toBe(false);

    const extLookup2 = await raw('GET', `/api/extension/project-for-origin?origin=${encodeURIComponent('https://r109-ext.test')}`, {
      token: wsAdmin.token,
    });
    // 4 → 404 Project.NoneForOrigin: URL on a disabled environment no longer resolves
    expect(extLookup2.status).toBe(404);

    // ─── WIDGET HALF ──────────────────────────────────────────────────────────

    // 6. Delete local's mapping on projectW so the fixture origin is described solely by ext
    if (localId) {
      await delRaw(`/api/admin/projects/${projectWId}/app-urls/${localId}`, { token: wsAdmin.token });
    }

    // Put ext URL for http://localhost:<PORTS.beta> with isActive: false (while still disabled)
    // Re-enable ext first to allow the write
    await patchRaw(`/api/admin/environments/${extEnvId}`, { isEnabled: true }, { token: wsAdmin.token });
    const putBetaUrl = await putRaw(
      `/api/admin/projects/${projectWId}/app-urls/${extEnvId}`,
      { url: `http://localhost:${BETA_FIXTURE_PORT}`, isActive: false },
      { token: wsAdmin.token },
    );
    expect(putBetaUrl.status).toBe(200);

    // 7. With ext enabled and its row isActive: false → GET …/widget-status?origin=…
    const status7 = await raw(
      'GET',
      `/api/public/projects/${projectWKey}/widget-status?origin=${encodeURIComponent(`http://localhost:${BETA_FIXTURE_PORT}`)}`,
    );
    // 7 → active: false — explicit per-mapping isActive: false on an enabled environment still blocks
    expect(status7.status).toBe(200);
    expect(status7.data?.active).toBe(false);

    // 8. With ext disabled (row still isActive: false) → same call
    const disableExtAgain = await patchRaw(
      `/api/admin/environments/${extEnvId}`,
      { isEnabled: false },
      { token: wsAdmin.token },
    );
    expect(disableExtAgain.status).toBe(200);

    const status8 = await raw(
      'GET',
      `/api/public/projects/${projectWKey}/widget-status?origin=${encodeURIComponent(`http://localhost:${BETA_FIXTURE_PORT}`)}`,
    );
    // 8 → active: true — row whose environment is disabled is treated as absent, not as a block
    expect(status8.status).toBe(200);
    expect(status8.data?.active).toBe(true);

    // Browser verification: prove the widget gate stays active and renders the widget toolbar
    await preAuthWidget(page, tester.token, tester.user);

    // Intercept fixture HTML to inline the test project key
    await page.route(BETA_FIXTURE_URL, async (route) => {
      const response = await route.fetch();
      let text = await response.text();
      text = text.replace('project="e2e-beta"', `project="${projectWKey}"`);
      // Do NOT spread the original headers. They carry the ORIGINAL content-length, and the
      // rewritten body is a different size — the browser then truncates the document, nothing
      // parses, and the failure surfaces 10s later as a missing <pointer-feedback> that looks
      // like a widget bug.
      await route.fulfill({
        body: text,
        contentType: 'text/html; charset=utf-8',
        status: response.status(),
      });
    });

    const captureConfigLoaded = page.waitForResponse((r) => r.url().includes('/capture-config'));
    await page.goto(BETA_FIXTURE_URL);

    const widget = page.locator('pointer-feedback');
    await expect(widget.locator('#pf-add')).toBeVisible({ timeout: 10_000 });
    await captureConfigLoaded;

    record({
      id: 'R1-09-07',
      tier: 'nightly',
      layer: 'api + widget',
      role: 'WA, TESTER',
      result: 'PASS',
      ms: Date.now() - start,
      detail: `status7=${JSON.stringify(status7.data)}, status8=${JSON.stringify(status8.data)}`,
    });
  } finally {
    // Teardown created project and environment
    if (projectWId) {
      await delRaw(`/api/admin/projects/${projectWId}`, { token: wsAdmin.token });
    }
    if (extEnvId) {
      await delRaw(`/api/admin/environments/${extEnvId}`, { token: wsAdmin.token });
    }
  }
});
