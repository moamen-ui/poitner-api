// Playwright API specs for R1-09: Project URLs belong to enabled workspace environments.
// Scenarios implemented (PR tier):
// - R1-09-01 ⛓ — fresh seed has local, not default
// - R1-09-03 — create with a URL and no environment id → local
// - R1-09-04 — create with an explicit / disabled / foreign environment id
// - R1-09-05 ⛓ — PUT …/app-urls/{envId} respects the enabled flag
// - R1-09-06 ⛓ — disabling an environment with URLs, and re-enabling
// - R1-09-09 ⛓ — a tenant-defined environment works end-to-end
// - R1-09-10 ⛓ — cross-tenant isolation
// Contract: docs/roadmap/testing/R1-09-tests.md
import { test, expect } from '@playwright/test';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { raw, get, login, getRaw, postRaw, patchRaw, putRaw, delRaw } from '../scripts/lib/api.mjs';
import { SUPER_ADMIN, TENANT_OWNER, USERS, TENANT_B_OWNER } from '../scripts/lib/constants.mjs';
import { record } from '../scripts/lib/report.mjs';

test.describe.configure({ mode: 'serial' });

const here = dirname(fileURLToPath(import.meta.url));
const STATE_DIR = join(here, '..', 'state');
const credPath = join(STATE_DIR, 'credentials.json');
const credentials = existsSync(credPath)
  ? JSON.parse(readFileSync(credPath, 'utf8'))
  : {
      superAdmin: SUPER_ADMIN,
      wsAdmin: TENANT_OWNER,
      developer: USERS.developer,
      tenantBOwner: TENANT_B_OWNER,
    };

// Unique run ID so concurrent or re-run artifacts never collide
const RUN_ID = Math.random().toString(36).substring(2, 7);

// Shared state across the sequential scenarios in this file
const PROJECT_A_KEY = `e2e-r109-a-${RUN_ID}`;
let projectA = null;
let qaEnvId = null;
let localEnvId = null;

async function getTenantBToken(superAdminToken) {
  const tbCreds = credentials.tenantBOwner || TENANT_B_OWNER;
  try {
    const tb = await login(tbCreds.email, tbCreds.password);
    return tb.token;
  } catch {
    // If Tenant B is not seeded yet, create it via superAdmin
    await postRaw(
      '/api/admin/tenants',
      {
        email: tbCreds.email,
        password: tbCreds.password,
        displayName: tbCreds.displayName,
      },
      { token: superAdminToken },
    );
    const tb = await login(tbCreds.email, tbCreds.password);
    return tb.token;
  }
}

async function ensureProjectA(wsAdminToken) {
  if (projectA) return projectA;
  const listRes = await raw('GET', '/api/admin/projects', { token: wsAdminToken });
  if (listRes.ok && Array.isArray(listRes.data)) {
    const found = listRes.data.find((p) => p.key === PROJECT_A_KEY);
    if (found) {
      projectA = found;
      return projectA;
    }
  }
  const createRes = await postRaw(
    '/api/admin/projects',
    {
      key: PROJECT_A_KEY,
      name: 'R109 A',
      appUrl: 'https://r109-a.test',
    },
    { token: wsAdminToken },
  );
  if (createRes.status === 200) {
    projectA = createRes.data;
  }
  return projectA;
}

test.afterAll(async () => {
  // Teardown shared projectA and test environments created by this suite
  try {
    const wsAdminCreds = credentials.wsAdmin || TENANT_OWNER;
    const wsAdmin = await login(wsAdminCreds.email, wsAdminCreds.password);
    if (projectA?.id) {
      await delRaw(`/api/admin/projects/${projectA.id}`, { token: wsAdmin.token });
    }
    if (qaEnvId) {
      await delRaw(`/api/admin/environments/${qaEnvId}`, { token: wsAdmin.token });
    }
  } catch {
    // Teardown best-effort
  }
});

test('R1-09-01 ⛓ — fresh seed has local, not default', async () => {
  const start = Date.now();
  const wsAdminCreds = credentials.wsAdmin || TENANT_OWNER;
  const superAdminCreds = credentials.superAdmin || SUPER_ADMIN;
  const wsAdmin = await login(wsAdminCreds.email, wsAdminCreds.password);
  const superAdmin = await login(superAdminCreds.email, superAdminCreds.password);

  // 1. WA GET /api/admin/environments
  const waRes = await raw('GET', '/api/admin/environments', { token: wsAdmin.token });
  expect(waRes.status).toBe(200);

  // 2. Filter isGlobal === true
  const waGlobals = waRes.data.filter((e) => e.isGlobal === true);
  const waNames = waGlobals.map((e) => e.name);

  // Names must include local, prod, staging, testing
  expect(waNames).toContain('local');
  expect(waNames).toContain('prod');
  expect(waNames).toContain('staging');
  expect(waNames).toContain('testing');

  // Cache localId for later scenarios
  const localRow = waGlobals.find((e) => e.name === 'local');
  expect(localRow).toBeTruthy();
  localEnvId = localRow.id;

  // Global default: either absent (fresh DB) or retired/disabled (upgraded DB). Never enabled.
  const defaultRow = waGlobals.find((e) => e.name === 'default');
  if (defaultRow) {
    expect(defaultRow.isEnabled).toBe(false);
    expect(defaultRow.isRetired).toBe(true);
  }

  // Every global row has isEnabled === true except a retired default; canManage === false for WA
  for (const env of waGlobals) {
    if (env.name === 'default') {
      expect(env.isEnabled).toBe(false);
    } else {
      expect(env.isEnabled).toBe(true);
    }
    expect(env.canManage).toBe(false);
  }

  // 3. SA GET /api/admin/environments (filter isGlobal)
  const saRes = await raw('GET', '/api/admin/environments', { token: superAdmin.token });
  expect(saRes.status).toBe(200);
  const saGlobals = saRes.data.filter((e) => e.isGlobal === true);
  const saNames = saGlobals.map((e) => e.name);

  expect(saNames.slice().sort()).toEqual(waNames.slice().sort());
  for (const env of saGlobals) {
    expect(env.canManage).toBe(true);
  }

  record({
    id: 'R1-09-01',
    tier: 'PR',
    layer: 'api',
    role: 'WA, SA',
    result: 'PASS',
    ms: Date.now() - start,
    detail: `globals=[${waNames.join(',')}]`,
  });
});

test('R1-09-03 — create with a URL and no environment id → local', async () => {
  const start = Date.now();
  const wsAdminCreds = credentials.wsAdmin || TENANT_OWNER;
  const wsAdmin = await login(wsAdminCreds.email, wsAdminCreds.password);

  // 1. WA GET /api/admin/environments → localId = id of the global local
  const envRes = await raw('GET', '/api/admin/environments', { token: wsAdmin.token });
  expect(envRes.status).toBe(200);
  const localEnv = envRes.data.find((e) => e.isGlobal === true && e.name === 'local');
  expect(localEnv).toBeTruthy();
  const localId = localEnv.id;
  localEnvId = localId;

  // 2. WA POST /api/admin/projects { key, name, appUrl } (no appEnvironmentId)
  const projRes = await postRaw(
    '/api/admin/projects',
    {
      key: PROJECT_A_KEY,
      name: 'R109 A',
      appUrl: 'https://r109-a.test',
    },
    { token: wsAdmin.token },
  );
  expect(projRes.status).toBe(200);
  projectA = projRes.data;

  // Response appUrls has exactly one row on localId
  expect(projectA.appUrls).toHaveLength(1);
  const urlRow = projectA.appUrls[0];
  expect(urlRow.appEnvironmentId).toBe(localId);
  expect(urlRow.environmentName).toBe('local');
  expect(urlRow.url).toBe('https://r109-a.test');
  expect(urlRow.isActive).toBe(true);
  expect(urlRow.environmentIsEnabled).toBe(true);
  // Deprecated mirror field matches
  expect(projectA.appUrl).toBe('https://r109-a.test');

  // 3. WA GET /api/admin/projects/{id}/app-urls
  const getUrlsRes = await raw('GET', `/api/admin/projects/${projectA.id}/app-urls`, { token: wsAdmin.token });
  expect(getUrlsRes.status).toBe(200);
  expect(getUrlsRes.data).toHaveLength(1);
  expect(getUrlsRes.data[0].appEnvironmentId).toBe(localId);
  expect(getUrlsRes.data[0].environmentName).toBe('local');
  expect(getUrlsRes.data[0].url).toBe('https://r109-a.test');
  expect(getUrlsRes.data[0].isActive).toBe(true);
  expect(getUrlsRes.data[0].environmentIsEnabled).toBe(true);

  record({
    id: 'R1-09-03',
    tier: 'PR',
    layer: 'api',
    role: 'WA',
    result: 'PASS',
    ms: Date.now() - start,
    detail: `projectId=${projectA.id}, localId=${localId}`,
  });
});

test('R1-09-04 — create with an explicit / disabled / foreign environment id', async () => {
  const start = Date.now();
  const wsAdminCreds = credentials.wsAdmin || TENANT_OWNER;
  const superAdminCreds = credentials.superAdmin || SUPER_ADMIN;
  const wsAdmin = await login(wsAdminCreds.email, wsAdminCreds.password);
  const superAdmin = await login(superAdminCreds.email, superAdminCreds.password);
  const tbToken = await getTenantBToken(superAdmin.token);

  let envId = null;
  let tbEnvId = null;
  let projBId = null;
  let projEId = null;

  try {
    // 1. WA POST /api/admin/environments { name: 'r109-<runId>-staging2' } → envId
    const createEnvRes = await postRaw(
      '/api/admin/environments',
      { name: `r109-${RUN_ID}-staging2` },
      { token: wsAdmin.token },
    );
    expect(createEnvRes.status).toBe(200);
    envId = createEnvRes.data.id;
    expect(envId).toBeTruthy();

    // 2. WA postRaw /api/admin/projects { key: 'e2e-r109-b-<runId>', appUrl: 'https://r109-b.test', appEnvironmentId: envId }
    const pBRes = await postRaw(
      '/api/admin/projects',
      {
        key: `e2e-r109-b-${RUN_ID}`,
        name: 'R109 B',
        appUrl: 'https://r109-b.test',
        appEnvironmentId: envId,
      },
      { token: wsAdmin.token },
    );
    expect(pBRes.status).toBe(200);
    projBId = pBRes.data.id;
    expect(pBRes.data.appUrls).toHaveLength(1);
    expect(pBRes.data.appUrls[0].appEnvironmentId).toBe(envId);

    // 3. WA PATCH /api/admin/environments/{envId} { isEnabled: false }
    const disableRes = await patchRaw(
      `/api/admin/environments/${envId}`,
      { isEnabled: false },
      { token: wsAdmin.token },
    );
    expect(disableRes.status).toBe(200);
    expect(disableRes.data.isEnabled).toBe(false);

    // 4. WA postRaw /api/admin/projects { key: 'e2e-r109-c-<runId>', appUrl: 'https://r109-c.test', appEnvironmentId: envId }
    const pCRes = await postRaw(
      '/api/admin/projects',
      {
        key: `e2e-r109-c-${RUN_ID}`,
        name: 'R109 C',
        appUrl: 'https://r109-c.test',
        appEnvironmentId: envId,
      },
      { token: wsAdmin.token },
    );
    // 4 → 400 (AppEnvironment.NotEnabled) and project e2e-r109-c-<runId> must NOT exist
    expect(pCRes.status).toBe(400);
    expect(pCRes.isSuccess).toBe(false);

    const listProjects = await raw('GET', '/api/admin/projects', { token: wsAdmin.token });
    expect(listProjects.status).toBe(200);
    const projCExists = listProjects.data.some((p) => p.key === `e2e-r109-c-${RUN_ID}`);
    expect(projCExists).toBe(false);

    // 5. TB POST /api/admin/environments { name: 'r109-<runId>-tb' } → tbEnvId
    // WA postRaw /api/admin/projects with appEnvironmentId: tbEnvId → 404 (tenant B's env is invisible)
    const tbEnvRes = await postRaw(
      '/api/admin/environments',
      { name: `r109-${RUN_ID}-tb` },
      { token: tbToken },
    );
    expect(tbEnvRes.status).toBe(200);
    tbEnvId = tbEnvRes.data.id;

    const pDRes = await postRaw(
      '/api/admin/projects',
      {
        key: `e2e-r109-d-${RUN_ID}`,
        name: 'R109 D',
        appUrl: 'https://r109-d.test',
        appEnvironmentId: tbEnvId,
      },
      { token: wsAdmin.token },
    );
    expect(pDRes.status).toBe(404);

    // 6. WA postRaw with appEnvironmentId: 999999 → 404
    const pMissingRes = await postRaw(
      '/api/admin/projects',
      {
        key: `e2e-r109-missing-${RUN_ID}`,
        name: 'R109 Missing',
        appUrl: 'https://r109-missing.test',
        appEnvironmentId: 999999,
      },
      { token: wsAdmin.token },
    );
    expect(pMissingRes.status).toBe(404);

    // 7. WA postRaw /api/admin/projects { key: 'e2e-r109-e-<runId>', name: 'R109 E' } (no appUrl, but appEnvironmentId: envId)
    // 7 → 200: no appUrl means appEnvironmentId is ignored entirely (Decision 5), appUrls is empty
    const pERes = await postRaw(
      '/api/admin/projects',
      {
        key: `e2e-r109-e-${RUN_ID}`,
        name: 'R109 E',
        appEnvironmentId: envId,
      },
      { token: wsAdmin.token },
    );
    expect(pERes.status).toBe(200);
    projEId = pERes.data.id;
    expect(pERes.data.appUrls).toHaveLength(0);

    record({
      id: 'R1-09-04',
      tier: 'PR',
      layer: 'api',
      role: 'WA, TB',
      result: 'PASS',
      ms: Date.now() - start,
      detail: `statuses: step2=200, step4=400, step5=404, step6=404, step7=200`,
    });
  } finally {
    // Teardown created resources
    if (projBId) await delRaw(`/api/admin/projects/${projBId}`, { token: wsAdmin.token });
    if (projEId) await delRaw(`/api/admin/projects/${projEId}`, { token: wsAdmin.token });
    if (envId) await delRaw(`/api/admin/environments/${envId}`, { token: wsAdmin.token });
    if (tbEnvId) await delRaw(`/api/admin/environments/${tbEnvId}`, { token: tbToken });
  }
});

test('R1-09-05 ⛓ — PUT …/app-urls/{envId} respects the enabled flag', async () => {
  const start = Date.now();
  const wsAdminCreds = credentials.wsAdmin || TENANT_OWNER;
  const devCreds = credentials.developer || USERS.developer;
  const wsAdmin = await login(wsAdminCreds.email, wsAdminCreds.password);
  const dev = await login(devCreds.email, devCreds.password);

  await ensureProjectA(wsAdmin.token);
  expect(projectA).toBeTruthy();

  // Create fresh env r109-<runId>-qa (qaEnvId)
  if (!qaEnvId) {
    const qaEnvRes = await postRaw(
      '/api/admin/environments',
      { name: `r109-${RUN_ID}-qa` },
      { token: wsAdmin.token },
    );
    expect(qaEnvRes.status).toBe(200);
    qaEnvId = qaEnvRes.data.id;
  }

  // 1. WA put /api/admin/projects/{a.id}/app-urls/{qaId} { url: 'https://r109-qa.test', isActive: true }
  const putRes1 = await putRaw(
    `/api/admin/projects/${projectA.id}/app-urls/${qaEnvId}`,
    { url: 'https://r109-qa.test', isActive: true },
    { token: wsAdmin.token },
  );
  expect(putRes1.status).toBe(200);

  // 2. WA GET …/app-urls → two rows (local, qa), both environmentIsEnabled: true
  const getUrlsRes2 = await raw('GET', `/api/admin/projects/${projectA.id}/app-urls`, { token: wsAdmin.token });
  expect(getUrlsRes2.status).toBe(200);
  expect(getUrlsRes2.data.length).toBeGreaterThanOrEqual(2);

  const localRow = getUrlsRes2.data.find((u) => u.environmentName === 'local');
  const qaRow = getUrlsRes2.data.find((u) => u.appEnvironmentId === qaEnvId);
  expect(localRow).toBeTruthy();
  expect(localRow.environmentIsEnabled).toBe(true);
  expect(qaRow).toBeTruthy();
  expect(qaRow.environmentIsEnabled).toBe(true);

  // 3. WA PATCH /api/admin/environments/{qaId} { isEnabled: false }
  const disableRes = await patchRaw(
    `/api/admin/environments/${qaEnvId}`,
    { isEnabled: false },
    { token: wsAdmin.token },
  );
  expect(disableRes.status).toBe(200);
  expect(disableRes.data.isEnabled).toBe(false);

  // 4. WA putRaw …/app-urls/{qaId} { url: 'https://r109-qa-2.test' } → 400 AppEnvironment.NotEnabled
  const putRes4 = await putRaw(
    `/api/admin/projects/${projectA.id}/app-urls/${qaEnvId}`,
    { url: 'https://r109-qa-2.test' },
    { token: wsAdmin.token },
  );
  expect(putRes4.status).toBe(400);
  expect(putRes4.isSuccess).toBe(false);

  // 5. WA GET …/app-urls → still two rows; qa row present with environmentIsEnabled: false and original url
  const getUrlsRes5 = await raw('GET', `/api/admin/projects/${projectA.id}/app-urls`, { token: wsAdmin.token });
  expect(getUrlsRes5.status).toBe(200);
  const qaRow5 = getUrlsRes5.data.find((u) => u.appEnvironmentId === qaEnvId);
  expect(qaRow5).toBeTruthy();
  expect(qaRow5.environmentIsEnabled).toBe(false);
  expect(qaRow5.url).toBe('https://r109-qa.test');

  // 6. DEV (non-admin, not creator) putRaw …/app-urls/{qaId} { url: 'https://x.test' } → 403
  const devPutRes = await putRaw(
    `/api/admin/projects/${projectA.id}/app-urls/${qaEnvId}`,
    { url: 'https://x.test' },
    { token: dev.token },
  );
  expect(devPutRes.status).toBe(403);

  record({
    id: 'R1-09-05',
    tier: 'PR',
    layer: 'api',
    role: 'WA, DEV',
    result: 'PASS',
    ms: Date.now() - start,
    detail: `qaEnvId=${qaEnvId}, step1=200, step4=400, step6=403`,
  });
});

test('R1-09-06 ⛓ — disabling an environment with URLs, and re-enabling', async () => {
  const start = Date.now();
  const wsAdminCreds = credentials.wsAdmin || TENANT_OWNER;
  const wsAdmin = await login(wsAdminCreds.email, wsAdminCreds.password);

  await ensureProjectA(wsAdmin.token);
  expect(qaEnvId, 'qaEnvId must exist from R1-09-05').toBeTruthy();

  try {
    // 1. WA GET /api/admin/environments → note projectUrlCount for qaId
    const envs1 = await raw('GET', '/api/admin/environments', { token: wsAdmin.token });
    expect(envs1.status).toBe(200);
    const qaEnv1 = envs1.data.find((e) => e.id === qaEnvId);
    expect(qaEnv1).toBeTruthy();
    expect(qaEnv1.projectUrlCount).toBeGreaterThanOrEqual(1);
    const savedCount = qaEnv1.projectUrlCount;

    // 2. WA PATCH /api/admin/environments/{qaId} { isEnabled: true } → re-enable
    const enableRes2 = await patchRaw(
      `/api/admin/environments/${qaEnvId}`,
      { isEnabled: true },
      { token: wsAdmin.token },
    );
    expect(enableRes2.status).toBe(200);
    expect(enableRes2.data.isEnabled).toBe(true);

    // 3. WA put …/app-urls/{qaId} { url: 'https://r109-qa-3.test' } → 200 (re-enabling restores writes)
    const putRes3 = await putRaw(
      `/api/admin/projects/${projectA.id}/app-urls/${qaEnvId}`,
      { url: 'https://r109-qa-3.test' },
      { token: wsAdmin.token },
    );
    expect(putRes3.status).toBe(200);

    // 4. WA PATCH /api/admin/environments/{qaId} { isEnabled: false }
    const disableRes4 = await patchRaw(
      `/api/admin/environments/${qaEnvId}`,
      { isEnabled: false },
      { token: wsAdmin.token },
    );
    expect(disableRes4.status).toBe(200);
    expect(disableRes4.data.isEnabled).toBe(false);

    // 5. WA GET /api/admin/environments → isEnabled: false and the same projectUrlCount
    const envs5 = await raw('GET', '/api/admin/environments', { token: wsAdmin.token });
    expect(envs5.status).toBe(200);
    const qaEnv5 = envs5.data.find((e) => e.id === qaEnvId);
    expect(qaEnv5.isEnabled).toBe(false);
    expect(qaEnv5.projectUrlCount).toBe(savedCount);

    // 6. WA delRaw /api/admin/environments/{qaId} → 409 AppEnvironment.InUse
    const delRes6 = await delRaw(`/api/admin/environments/${qaEnvId}`, { token: wsAdmin.token });
    expect(delRes6.status).toBe(409);

    // 7. WA PATCH /api/admin/environments/{qaId} { isEnabled: true } (restore, for a clean tree)
    const restoreRes7 = await patchRaw(
      `/api/admin/environments/${qaEnvId}`,
      { isEnabled: true },
      { token: wsAdmin.token },
    );
    expect(restoreRes7.status).toBe(200);
    expect(restoreRes7.data.isEnabled).toBe(true);

    record({
      id: 'R1-09-06',
      tier: 'PR',
      layer: 'api',
      role: 'WA',
      result: 'PASS',
      ms: Date.now() - start,
      detail: `projectUrlCount=${savedCount}, step3=200, step6=409, step7=200`,
    });
  } finally {
    // Teardown: ensure qaEnvId is left enabled if anything threw before step 7
    try {
      await patchRaw(
        `/api/admin/environments/${qaEnvId}`,
        { isEnabled: true },
        { token: wsAdmin.token },
      );
    } catch {}
  }
});

test('R1-09-09 ⛓ — a tenant-defined environment works end-to-end', async () => {
  const start = Date.now();
  const wsAdminCreds = credentials.wsAdmin || TENANT_OWNER;
  const wsAdmin = await login(wsAdminCreds.email, wsAdminCreds.password);

  await ensureProjectA(wsAdmin.token);
  let qa2Id = null;

  try {
    // 1. WA POST /api/admin/environments { name: 'r109-<runId>-qa2' }
    const createRes = await postRaw(
      '/api/admin/environments',
      { name: `r109-${RUN_ID}-qa2` },
      { token: wsAdmin.token },
    );
    expect(createRes.status).toBe(200);
    const qa2Env = createRes.data;
    qa2Id = qa2Env.id;
    expect(qa2Env.isGlobal).toBe(false);
    expect(qa2Env.canManage).toBe(true);
    expect(qa2Env.isEnabled).toBe(true);
    expect(qa2Env.isRetired).toBe(false);
    expect(qa2Env.projectUrlCount).toBe(0);

    // 2. WA put /api/admin/projects/{a.id}/app-urls/{id} { url: 'https://r109-qa2.test' }
    const putRes = await putRaw(
      `/api/admin/projects/${projectA.id}/app-urls/${qa2Id}`,
      { url: 'https://r109-qa2.test' },
      { token: wsAdmin.token },
    );
    expect(putRes.status).toBe(200);

    // 3. WA GET …/app-urls → row listed with environmentName === 'r109-<runId>-qa2' and environmentIsEnabled: true
    const getUrlsRes = await raw('GET', `/api/admin/projects/${projectA.id}/app-urls`, { token: wsAdmin.token });
    expect(getUrlsRes.status).toBe(200);
    const row = getUrlsRes.data.find((u) => u.appEnvironmentId === qa2Id);
    expect(row).toBeTruthy();
    expect(row.environmentName).toBe(`r109-${RUN_ID}-qa2`);
    expect(row.environmentIsEnabled).toBe(true);
    expect(row.url).toBe('https://r109-qa2.test');

    // 4. WA GET /api/admin/environments → projectUrlCount === 1, global rows first, then tenant rows, retired last
    const envsRes = await raw('GET', '/api/admin/environments', { token: wsAdmin.token });
    expect(envsRes.status).toBe(200);
    const qa2InList = envsRes.data.find((e) => e.id === qa2Id);
    expect(qa2InList).toBeTruthy();
    expect(qa2InList.projectUrlCount).toBe(1);

    // Assert ordering: globals first, then non-retired tenant environments, retired last
    let seenTenantRow = false;
    let seenRetiredRow = false;
    for (const env of envsRes.data) {
      if (env.isRetired) {
        seenRetiredRow = true;
      } else {
        expect(seenRetiredRow, 'Non-retired row must not appear after a retired row').toBe(false);
        if (!env.isGlobal) {
          seenTenantRow = true;
        } else {
          expect(seenTenantRow, 'Global row must not appear after a tenant row').toBe(false);
        }
      }
    }

    record({
      id: 'R1-09-09',
      tier: 'PR',
      layer: 'api',
      role: 'WA',
      result: 'PASS',
      ms: Date.now() - start,
      detail: `qa2Id=${qa2Id}, projectUrlCount=1`,
    });
  } finally {
    if (qa2Id) {
      await delRaw(`/api/admin/projects/${projectA.id}/app-urls/${qa2Id}`, { token: wsAdmin.token });
      await delRaw(`/api/admin/environments/${qa2Id}`, { token: wsAdmin.token });
    }
  }
});

test('R1-09-10 ⛓ — cross-tenant isolation', async () => {
  const start = Date.now();
  const wsAdminCreds = credentials.wsAdmin || TENANT_OWNER;
  const superAdminCreds = credentials.superAdmin || SUPER_ADMIN;
  const wsAdmin = await login(wsAdminCreds.email, wsAdminCreds.password);
  const superAdmin = await login(superAdminCreds.email, superAdminCreds.password);
  const tbToken = await getTenantBToken(superAdmin.token);

  await ensureProjectA(wsAdmin.token);
  expect(localEnvId, 'localEnvId must be resolved').toBeTruthy();
  expect(qaEnvId, 'qaEnvId must exist from R1-09-05').toBeTruthy();

  // 1. TB GET /api/admin/environments → global set plus only TB's own rows (no r109-<runId>-* of WA)
  const tbEnvsRes = await raw('GET', '/api/admin/environments', { token: tbToken });
  expect(tbEnvsRes.status).toBe(200);
  const waEnvsInTb = tbEnvsRes.data.filter((e) => e.name.startsWith(`r109-${RUN_ID}-`));
  expect(waEnvsInTb).toHaveLength(0);

  // 2. TB getRaw /api/admin/projects/{a.id}/app-urls (WA's project) → 404 (strict-own query filter)
  const tbGetRes = await getRaw(`/api/admin/projects/${projectA.id}/app-urls`, { token: tbToken });
  expect(tbGetRes.status).toBe(404);

  // 3. TB putRaw /api/admin/projects/{a.id}/app-urls/{qaId} { url: 'https://evil.test' } → 404
  const tbPutRes = await putRaw(
    `/api/admin/projects/${projectA.id}/app-urls/${qaEnvId}`,
    { url: 'https://evil.test' },
    { token: tbToken },
  );
  expect(tbPutRes.status).toBe(404);

  // 4. TB patchRaw /api/admin/environments/{qaId} { isEnabled: false } (WA's env) → 404
  const tbPatchQa = await patchRaw(
    `/api/admin/environments/${qaEnvId}`,
    { isEnabled: false },
    { token: tbToken },
  );
  expect(tbPatchQa.status).toBe(404);

  // 5. TB patchRaw /api/admin/environments/{localId} { isEnabled: false } (the global local) → 403 AppEnvironment.NotManageable
  const tbPatchLocal = await patchRaw(
    `/api/admin/environments/${localEnvId}`,
    { isEnabled: false },
    { token: tbToken },
  );
  expect(tbPatchLocal.status).toBe(403);

  // Verify global local row still reads isEnabled: true afterwards
  const waEnvsCheck = await raw('GET', '/api/admin/environments', { token: wsAdmin.token });
  expect(waEnvsCheck.status).toBe(200);
  const localCheck = waEnvsCheck.data.find((e) => e.id === localEnvId);
  expect(localCheck.isEnabled).toBe(true);

  // 6. WA GET …/app-urls for its project → unchanged, nothing TB did mutated WA's data
  const waUrlsAfter = await raw('GET', `/api/admin/projects/${projectA.id}/app-urls`, { token: wsAdmin.token });
  expect(waUrlsAfter.status).toBe(200);
  expect(waUrlsAfter.data.some((u) => u.url === 'https://evil.test')).toBe(false);

  record({
    id: 'R1-09-10',
    tier: 'PR',
    layer: 'api',
    role: 'TB, WA',
    result: 'PASS',
    ms: Date.now() - start,
    detail: `statuses: step2=404, step3=404, step4=404, step5=403`,
  });
});
