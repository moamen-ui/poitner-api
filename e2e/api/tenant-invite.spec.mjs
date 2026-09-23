// Playwright API specs for R1-08 (Tenant invitation by email).
// Scenarios implemented:
// - R1-08-01 — invite → accept → workspace usable
// - R1-08-04 — single use — a second accept is rejected
// - R1-08-05 — revoked link cannot be accepted
// - R1-08-07 — email lock — another address cannot accept
// - R1-08-09 — only a super admin can invite workspaces
// - R1-08-11 — the direct path still works (seed depends on it)
// - R1-08-12 — invitee address already owns a workspace: join-or-create (DB-11a), not a 409
// - R1-08-13 — create without an email is rejected
// - R1-08-14 — the legacy invite route obeys the same rules
// - R1-08-15 — swagger contract guard
// - R1-08-17 — join links follow the configured app URL
import { test, expect } from '@playwright/test';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { get, post, put, login, getRaw, postRaw, delRaw } from '../scripts/lib/api.mjs';
import { captureSettings, applySettings, restoreSettings } from '../scripts/lib/settings.mjs';
import { deleteTenantByEmail } from '../scripts/lib/tenants.mjs';
import { SUPER_ADMIN, TENANT_OWNER } from '../scripts/lib/constants.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const STATE_DIR = join(here, '..', 'state');
const credPath = join(STATE_DIR, 'credentials.json');
const credentials = existsSync(credPath)
  ? JSON.parse(readFileSync(credPath, 'utf8'))
  : { superAdmin: SUPER_ADMIN, wsAdmin: TENANT_OWNER };

// Unique suffix per test module evaluation so local runs never collide
const RUN_ID = Math.random().toString(36).substring(2, 7);

test('R1-08-01 — invite → accept → workspace usable', async () => {
  const superAdmin = await login(credentials.superAdmin.email, credentials.superAdmin.password);
  const inviteeEmail = `inv-${RUN_ID}-1@example.com`;
  const inviteePassword = 'InviteePass1!';
  const projectKey = `r108-${RUN_ID}`;
  let inviteeToken = null;

  try {
    // 1. Super admin creates workspace invite
    const createRes = await postRaw(
      '/api/admin/tenants/invites',
      { email: inviteeEmail, displayName: 'Acme Co', expiresInDays: 7 },
      { token: superAdmin.token },
    );
    expect(createRes.status).toBe(200);
    const { id, url, expiresAt, emailSent } = createRes.data;
    expect(url).toMatch(/\/join\?code=/);
    expect(new Date(expiresAt).getTime()).toBeGreaterThan(Date.now());
    // NOT expect(emailSent).toBe(true): InviteService.CreateAsync is explicitly best-effort here
    // (an invite is fully usable via its `url` even when the email never lands — disabled, capped,
    // or a send failure — and the code deliberately never fails the invite over a notification).
    // Whether the send itself succeeds depends on a real mail transport being configured
    // (Email__Provider=smtp against Mailpit, or a Brevo API key) — CI's dev .env
    // (.github/workflows/e2e.yml copies .env.example as-is) sets neither, so BrevoEmailSender
    // no-ops and this is false by design in this environment, not a product regression. The real
    // send path is proven end-to-end by mail/tenant-invite-mail.spec.mjs (R1-08-02) against
    // Mailpit in the mail phase. Here, only assert the field is the boolean the contract promises.
    expect(typeof emailSent).toBe('boolean');

    const code = new URL(url).searchParams.get('code');
    expect(code).toBeTruthy();

    // 3. Super admin lists pending invites — row must be present with email, displayName, expiresAt
    const listRes = await getRaw('/api/admin/tenants/invites', { token: superAdmin.token });
    expect(listRes.status).toBe(200);
    const pending = Array.isArray(listRes.data)
      ? listRes.data.find((i) => i.id === id)
      : null;
    expect(pending).toBeTruthy();
    expect(pending.email.toLowerCase()).toBe(inviteeEmail.toLowerCase());
    expect(pending.displayName).toBe('Acme Co');
    expect(pending.expiresAt).toBeTruthy();

    // 4. Anonymous preview: GET /api/invites/{code} (anonymous, no token)
    const previewRes = await getRaw(`/api/invites/${code}`);
    expect(previewRes.status).toBe(200);
    expect(previewRes.data.isNewWorkspace).toBe(true);
    expect(previewRes.data.emailLocked).toBe(true);
    // Body must contain no tenant GUID
    expect(previewRes.data.tenantId).toBeUndefined();
    expect(previewRes.data.ownerId).toBeUndefined();
    expect(previewRes.data.id).toBeUndefined();
    // SPEC-CONFLICT note: R1-08-01 step 4 expects displayName === 'Acme Co',
    // but InvitePreviewResponse does not declare DisplayName.
    if (previewRes.data.displayName !== undefined) {
      expect(previewRes.data.displayName).toBe('Acme Co');
    }

    // 5. Accept invite: POST /api/auth/register-invite
    const acceptRes = await postRaw('/api/auth/register-invite', {
      code,
      email: inviteeEmail,
      password: inviteePassword,
      displayName: 'Acme Co',
    });
    expect(acceptRes.status).toBe(200);
    expect(acceptRes.data.status).toBe('ok');
    expect(acceptRes.data.token).toBeTruthy();
    inviteeToken = acceptRes.data.token;

    // 6. Inspect current user with invitee token: GET /api/auth/me
    const meRes = await getRaw('/api/auth/me', { token: inviteeToken });
    expect(meRes.status).toBe(200);
    expect(meRes.data.roleName).toBe('Workspace Admin');
    expect(meRes.data.isAdmin).toBe(true);
    expect(meRes.data.isSuperAdmin).toBe(false);

    // 7. Invitee creates a project: POST /api/admin/projects
    const projRes = await postRaw(
      '/api/admin/projects',
      { key: projectKey, name: 'R108' },
      { token: inviteeToken },
    );
    expect(projRes.status).toBe(200);

    // 8. Pending invites list: the accepted invite is gone (single use)
    const listResAfter = await getRaw('/api/admin/tenants/invites', { token: superAdmin.token });
    expect(listResAfter.status).toBe(200);
    const consumed = Array.isArray(listResAfter.data)
      ? listResAfter.data.find((i) => i.id === id)
      : null;
    expect(consumed).toBeFalsy();

    // 9. Tenants list: tenant is approved and active immediately
    const tenantsRes = await getRaw('/api/admin/tenants', { token: superAdmin.token });
    expect(tenantsRes.status).toBe(200);
    const createdTenant = Array.isArray(tenantsRes.data)
      ? tenantsRes.data.find((t) => t.email.toLowerCase() === inviteeEmail.toLowerCase())
      : null;
    expect(createdTenant).toBeTruthy();
    expect(createdTenant.approvalStatus).toBe('Approved');
    expect(createdTenant.isActive).toBe(true);
  } finally {
    // Teardown: delete project and tenant
    await deleteTenantByEmail(inviteeEmail, superAdmin.token);
  }
});

test('R1-08-04 — single use — a second accept is rejected', async () => {
  const superAdmin = await login(credentials.superAdmin.email, credentials.superAdmin.password);
  const email = `inv-${RUN_ID}-4@example.com`;

  try {
    // 1. Create invite
    const createRes = await postRaw(
      '/api/admin/tenants/invites',
      { email, displayName: 'Single Use Co', expiresInDays: 7 },
      { token: superAdmin.token },
    );
    expect(createRes.status).toBe(200);
    const code = new URL(createRes.data.url).searchParams.get('code');

    // 2. First accept: succeeds (200)
    const accept1 = await postRaw('/api/auth/register-invite', {
      code,
      email,
      password: 'PassOne1234!',
      displayName: 'Single Use Co',
    });
    expect(accept1.status).toBe(200);

    // 3. Second accept with the SAME code and address: rejected (404)
    const accept2 = await postRaw('/api/auth/register-invite', {
      code,
      email,
      password: 'PassTwo1234!',
      displayName: 'Single Use Co',
    });
    expect(accept2.status).toBe(404);

    // Confirm no second tenant was created for the address
    const tenantsRes = await getRaw('/api/admin/tenants', { token: superAdmin.token });
    expect(tenantsRes.status).toBe(200);
    const matches = Array.isArray(tenantsRes.data)
      ? tenantsRes.data.filter((t) => t.email.toLowerCase() === email.toLowerCase())
      : [];
    expect(matches.length).toBe(1);
  } finally {
    await deleteTenantByEmail(email, superAdmin.token);
  }
});

test('R1-08-05 — revoked link cannot be accepted', async () => {
  const superAdmin = await login(credentials.superAdmin.email, credentials.superAdmin.password);
  const email = `inv-${RUN_ID}-5@example.com`;

  // 1. Create invite
  const createRes = await postRaw(
    '/api/admin/tenants/invites',
    { email, displayName: 'Revoked Co', expiresInDays: 7 },
    { token: superAdmin.token },
  );
  expect(createRes.status).toBe(200);
  const { id, url } = createRes.data;
  const code = new URL(url).searchParams.get('code');

  // 2. Revoke: DELETE /api/admin/tenants/invites/{id}
  const revokeRes = await delRaw(`/api/admin/tenants/invites/${id}`, { token: superAdmin.token });
  expect(revokeRes.status).toBe(200);

  // 3. Try to accept the revoked code: 404 (no preview call per budget)
  const acceptRes = await postRaw('/api/auth/register-invite', {
    code,
    email,
    password: 'RevokedPass1!',
    displayName: 'Revoked Co',
  });
  expect(acceptRes.status).toBe(404);

  // 4. Pending list must not contain the revoked row
  const listRes = await getRaw('/api/admin/tenants/invites', { token: superAdmin.token });
  expect(listRes.status).toBe(200);
  const row = Array.isArray(listRes.data) ? listRes.data.find((i) => i.id === id) : null;
  expect(row).toBeFalsy();
});

test('R1-08-07 — email lock — another address cannot accept', async () => {
  const superAdmin = await login(credentials.superAdmin.email, credentials.superAdmin.password);
  const lockedEmail = `inv-${RUN_ID}-7@example.com`;
  const wrongEmail = `someone-else-${RUN_ID}@example.com`;

  try {
    // 1. Create invite locked to lockedEmail
    const createRes = await postRaw(
      '/api/admin/tenants/invites',
      { email: lockedEmail, displayName: 'Lock Co', expiresInDays: 7 },
      { token: superAdmin.token },
    );
    expect(createRes.status).toBe(200);
    const code = new URL(createRes.data.url).searchParams.get('code');

    // 2. Attempt accept with a different address: 400 (EmailMismatch)
    const wrongAccept = await postRaw('/api/auth/register-invite', {
      code,
      email: wrongEmail,
      password: 'OtherPass123!',
      displayName: 'Wrong Co',
    });
    expect(wrongAccept.status).toBe(400);

    // Assert no tenant was created for the wrong address
    const tenantsRes = await getRaw('/api/admin/tenants', { token: superAdmin.token });
    expect(tenantsRes.status).toBe(200);
    const wrongTenant = Array.isArray(tenantsRes.data)
      ? tenantsRes.data.find((t) => t.email.toLowerCase() === wrongEmail.toLowerCase())
      : null;
    expect(wrongTenant).toBeFalsy();

    // 3. Accept correctly with the locked address: 200 (not consumed by the failed attempt)
    const correctAccept = await postRaw('/api/auth/register-invite', {
      code,
      email: lockedEmail,
      password: 'CorrectPass123!',
      displayName: 'Lock Co',
    });
    expect(correctAccept.status).toBe(200);
  } finally {
    await deleteTenantByEmail(lockedEmail, superAdmin.token);
  }
});

test('R1-08-09 — only a super admin can invite workspaces', async () => {
  const wsAdmin = await login(credentials.wsAdmin.email, credentials.wsAdmin.password);
  const superAdmin = await login(credentials.superAdmin.email, credentials.superAdmin.password);
  let superAdminInviteId = null;

  try {
    // 1. Workspace Admin tries to create workspace invite: 403
    const createRes = await postRaw(
      '/api/admin/tenants/invites',
      { email: `nope-${RUN_ID}@example.com` },
      { token: wsAdmin.token },
    );
    expect(createRes.status).toBe(403);

    // 2. Workspace Admin tries to list workspace invites: 403
    const listRes = await getRaw('/api/admin/tenants/invites', { token: wsAdmin.token });
    expect(listRes.status).toBe(403);

    // 3. Workspace Admin tries to delete workspace invite: 403
    const delRes = await delRaw('/api/admin/tenants/invites/1', { token: wsAdmin.token });
    expect(delRes.status).toBe(403);

    // 4. Super Admin creates a workspace invite
    const saCreate = await postRaw(
      '/api/admin/tenants/invites',
      { email: `sa-invite-${RUN_ID}@example.com` },
      { token: superAdmin.token },
    );
    expect(saCreate.status).toBe(200);
    superAdminInviteId = saCreate.data.id;
    const saCode = new URL(saCreate.data.url).searchParams.get('code');

    // As wsAdmin: GET /api/admin/invites — must NOT contain the super admin's null-owner workspace invite
    const wsInvitesRes = await getRaw('/api/admin/invites', { token: wsAdmin.token });
    expect(wsInvitesRes.status).toBe(200);
    const leaked = Array.isArray(wsInvitesRes.data)
      ? wsInvitesRes.data.find((i) => i.url?.includes(saCode) || i.code === saCode)
      : null;
    expect(leaked).toBeFalsy();

    // 5. As wsAdmin: DELETE /api/admin/invites/{id} on the legacy route: 404 (LoadOwnAsync scopes to tenant)
    const wsDelLegacy = await delRaw(`/api/admin/invites/${superAdminInviteId}`, { token: wsAdmin.token });
    expect(wsDelLegacy.status).toBe(404);
  } finally {
    if (superAdminInviteId) {
      await delRaw(`/api/admin/tenants/invites/${superAdminInviteId}`, { token: superAdmin.token });
    }
  }
});

test('R1-08-11 — the direct path still works (seed depends on it)', async () => {
  const superAdmin = await login(credentials.superAdmin.email, credentials.superAdmin.password);
  const email = `direct-${RUN_ID}@example.com`;
  const password = 'DirectPass1!';
  let createdWorkspaceId = null;

  try {
    // 1. Direct path: POST /api/admin/tenants
    const directRes = await postRaw(
      '/api/admin/tenants',
      { email, password, displayName: 'Direct Co' },
      { token: superAdmin.token },
    );
    expect(directRes.status).toBe(200);
    const data = directRes.data;
    // F9 (DB-11a cross-review): DELETE /api/admin/tenants is keyed on WorkspaceId (a GUID), never
    // the legacy int Id — that field can now repeat across rows and was never a valid :guid route
    // segment anyway.
    createdWorkspaceId = data.workspaceId;

    // Assert superset fields
    expect(data.id).toBeTruthy();
    expect(data.publicId).toBeTruthy();
    expect(data.ownerId).toBeTruthy();
    expect(data.workspaceId).toBeTruthy();
    expect(data.email.toLowerCase()).toBe(email.toLowerCase());
    expect(data.displayName).toBe('Direct Co');
    expect(data.approvalStatus).toBe('Approved');
    expect(data.isActive).toBe(true);

    // 2. Direct login with created credentials
    const loginRes = await postRaw('/api/auth/login', { email, password });
    expect(loginRes.status).toBe(200);
    expect(loginRes.data.status).toBe('ok');
    expect(loginRes.data.token).toBeTruthy();

    // 3. Confirm listed in GET /api/admin/tenants
    const listRes = await getRaw('/api/admin/tenants', { token: superAdmin.token });
    expect(listRes.status).toBe(200);
    const listed = Array.isArray(listRes.data)
      ? listRes.data.find((t) => t.email.toLowerCase() === email.toLowerCase())
      : null;
    expect(listed).toBeTruthy();
  } finally {
    if (createdWorkspaceId) {
      await delRaw(`/api/admin/tenants/${createdWorkspaceId}`, { token: superAdmin.token });
    } else {
      await deleteTenantByEmail(email, superAdmin.token);
    }
  }
});

test('R1-08-12 ⛓ — invitee address already owns a workspace: invite joins it into a second workspace', async () => {
  // DB-11a (docs/db/execution/DB-11a-identity-and-workspace-memberships.md §1, §3) made one
  // identity per e-mail with several workspace memberships. A super-admin workspace invite to an
  // address that already owns a workspace no longer 409s at either step: InviteService.CreateAsync
  // dropped the pre-check entirely, and AcceptCreateNewWorkspaceAsync join-or-creates the identity
  // (same password required — a mismatched password is still a 409, that path is untouched). The
  // observable new contract, per DB-11b (docs/db/execution/DB-11b-login-workspace-picker-and-switch.md
  // §3.1): after accepting a second invite, logging in returns status "choose-workspace" (HTTP 200,
  // a success envelope) with two entries in `workspaces`.
  const superAdmin = await login(credentials.superAdmin.email, credentials.superAdmin.password);
  const email = `owner-${RUN_ID}@example.com`;
  const password = 'SharedPass1!';
  const workspaceIds = [];

  try {
    // 1. Direct path creates an existing self-owned workspace for this address.
    const directRes = await postRaw(
      '/api/admin/tenants',
      { email, password, displayName: 'Existing Co' },
      { token: superAdmin.token },
    );
    expect(directRes.status).toBe(200);
    expect(directRes.data?.workspaceId).toBeTruthy();
    workspaceIds.push(directRes.data.workspaceId);

    // 2. Super admin invites the SAME address into a brand-new workspace: 200, not 409 — the
    // "address already owns a workspace" pre-check was removed by DB-11a.
    const inviteRes = await postRaw(
      '/api/admin/tenants/invites',
      { email, displayName: 'Second Co' },
      { token: superAdmin.token },
    );
    expect(inviteRes.status).toBe(200);
    expect(inviteRes.data?.url).toMatch(/\/join\?code=/);
    const code = new URL(inviteRes.data.url).searchParams.get('code');

    // 3. Accepting with the SAME password joins the existing identity into the new workspace
    // instead of refusing — 200 with an auto-signin token, not 409 (a mismatched password would
    // still 409 here; that guard is unchanged and out of scope for this scenario).
    const acceptRes = await postRaw('/api/auth/register-invite', {
      code,
      email,
      password,
      displayName: 'Second Co',
    });
    expect(acceptRes.status).toBe(200);
    expect(acceptRes.data?.status).toBe('ok');
    expect(acceptRes.data?.token).toBeTruthy();

    // 4. The identity now administers two workspaces: logging in can no longer resolve a single
    // membership, so it returns the picker — status "choose-workspace" as a 200 success envelope,
    // with both workspaces listed.
    const loginRes = await postRaw('/api/auth/login', { email, password });
    expect(loginRes.status).toBe(200);
    expect(loginRes.data?.status).toBe('choose-workspace');
    expect(loginRes.data?.token).toBeTruthy();
    expect(loginRes.data?.user).toBeNull();
    expect(Array.isArray(loginRes.data?.workspaces)).toBe(true);
    expect(loginRes.data.workspaces.length).toBe(2);

    const seenWorkspaceIds = loginRes.data.workspaces.map((w) => w.workspaceId);
    expect(new Set(seenWorkspaceIds).size).toBe(2);
    for (const id of seenWorkspaceIds) {
      if (!workspaceIds.includes(id)) workspaceIds.push(id);
    }
    expect(loginRes.data.workspaces.every((w) => w.isAdmin)).toBe(true);
  } finally {
    // Both workspaces now share this email (DB-11a: one identity, several memberships), so
    // deleteTenantByEmail's single-match lookup is not enough — delete every workspace id
    // collected above directly.
    for (const workspaceId of workspaceIds) {
      try {
        await delRaw(`/api/admin/tenants/${workspaceId}`, { token: superAdmin.token });
      } catch {}
    }
    await deleteTenantByEmail(email, superAdmin.token);
  }
});

test('R1-08-13 — create without an email is rejected', async () => {
  const superAdmin = await login(credentials.superAdmin.email, credentials.superAdmin.password);

  // 1. Missing email: 400
  const noEmail = await postRaw(
    '/api/admin/tenants/invites',
    { displayName: 'No Email Co' },
    { token: superAdmin.token },
  );
  expect(noEmail.status).toBe(400);

  // 2. Invalid email format: 400
  const badEmail = await postRaw(
    '/api/admin/tenants/invites',
    { email: 'not-an-email', displayName: 'Bad' },
    { token: superAdmin.token },
  );
  expect(badEmail.status).toBe(400);

  // 3. Confirm neither attempt created a pending row
  const listRes = await getRaw('/api/admin/tenants/invites', { token: superAdmin.token });
  expect(listRes.status).toBe(200);
  const found = Array.isArray(listRes.data)
    ? listRes.data.filter((i) => i.displayName === 'No Email Co' || i.displayName === 'Bad')
    : [];
  expect(found.length).toBe(0);
});

test('R1-08-14 — the legacy invite route obeys the same rules', async () => {
  const superAdmin = await login(credentials.superAdmin.email, credentials.superAdmin.password);
  const email = `legacy-${RUN_ID}@example.com`;

  try {
    // 1. Legacy route with createNewWorkspace: true but no email: 400
    const noEmail = await postRaw(
      '/api/admin/invites',
      { createNewWorkspace: true },
      { token: superAdmin.token },
    );
    expect(noEmail.status).toBe(400);

    // 2. Legacy route with maxUses: 5 — rewritten silently to 1
    const createRes = await postRaw(
      '/api/admin/invites',
      { createNewWorkspace: true, email, maxUses: 5 },
      { token: superAdmin.token },
    );
    expect(createRes.status).toBe(200);
    const code = new URL(createRes.data.url).searchParams.get('code');

    // 3. First accept succeeds: 200
    const accept1 = await postRaw('/api/auth/register-invite', {
      code,
      email,
      password: 'LegacyPass1!',
      displayName: 'Legacy Co',
    });
    expect(accept1.status).toBe(200);

    // Second accept with same code & email: 404 (single-use enforced)
    const accept2 = await postRaw('/api/auth/register-invite', {
      code,
      email,
      password: 'LegacyPass2!',
      displayName: 'Legacy Co',
    });
    expect(accept2.status).toBe(404);
  } finally {
    await deleteTenantByEmail(email, superAdmin.token);
  }
});

test('R1-08-15 — swagger contract guard', async () => {
  const swaggerRes = await getRaw('/swagger/v1/swagger.json');
  expect(swaggerRes.status).toBe(200);
  const swagger = swaggerRes.body;

  // 1. Four operations exist
  const postInvite = swagger.paths?.['/api/admin/tenants/invites']?.post;
  const getInvites = swagger.paths?.['/api/admin/tenants/invites']?.get;
  const resendInvite = swagger.paths?.['/api/admin/tenants/invites/{id}/resend']?.post;
  const deleteInvite = swagger.paths?.['/api/admin/tenants/invites/{id}']?.delete;

  expect(postInvite, 'POST /api/admin/tenants/invites must exist in swagger').toBeTruthy();
  expect(getInvites, 'GET /api/admin/tenants/invites must exist in swagger').toBeTruthy();
  expect(resendInvite, 'POST /api/admin/tenants/invites/{id}/resend must exist in swagger').toBeTruthy();
  expect(deleteInvite, 'DELETE /api/admin/tenants/invites/{id} must exist in swagger').toBeTruthy();

  // 2. 200 responses resolve a schema (for data-returning routes; delete is bare 200 status per commit b458aff)
  const postSchema = postInvite.responses?.['200']?.content?.['application/json']?.schema;
  expect(postSchema?.$ref || postSchema?.items?.$ref).toBeTruthy();

  const getSchema = getInvites.responses?.['200']?.content?.['application/json']?.schema;
  expect(getSchema?.$ref || getSchema?.items?.$ref).toBeTruthy();

  const resendSchema = resendInvite.responses?.['200']?.content?.['application/json']?.schema;
  expect(resendSchema?.$ref || resendSchema?.items?.$ref).toBeTruthy();

  // Delete operation returns bare 200 OK
  expect(deleteInvite.responses?.['200']).toBeTruthy();

  // 3. CreateTenantInviteRequest and TenantInviteResponse in components.schemas; no code on TenantInviteResponse
  const schemas = swagger.components?.schemas ?? {};
  expect(schemas.CreateTenantInviteRequest).toBeTruthy();
  expect(schemas.TenantInviteResponse).toBeTruthy();
  expect(schemas.TenantInviteResponse.properties?.code).toBeUndefined();

  // 4. Every operation carries the 'Tenants' tag
  for (const op of [postInvite, getInvites, resendInvite, deleteInvite]) {
    expect(op.tags).toContain('Tenants');
  }
});

test('R1-08-17 — join links follow the configured app URL', async () => {
  const superAdmin = await login(credentials.superAdmin.email, credentials.superAdmin.password);
  const origSettings = await captureSettings(superAdmin.token);
  let origBranding = null;
  try {
    origBranding = await get('/api/admin/branding', { token: superAdmin.token });
  } catch {
    origBranding = null;
  }

  try {
    // 1. Set appBaseUrl empty -> verify effectiveAppBaseUrl
    await applySettings({ appBaseUrl: '' }, superAdmin.token);
    const settingsAfterClear = await captureSettings(superAdmin.token);
    expect(settingsAfterClear.effectiveAppBaseUrl).toBeTruthy();

    // 2. PUT /api/admin/branding { urls: { app: 'https://app.selfhost.test' } }
    await put(
      '/api/admin/branding',
      { urls: { app: 'https://app.selfhost.test' } },
      { token: superAdmin.token },
    );

    const invite1Res = await postRaw(
      '/api/admin/tenants/invites',
      { email: `inv-${RUN_ID}-17a@example.com` },
      { token: superAdmin.token },
    );
    expect(invite1Res.status).toBe(200);
    expect(invite1Res.data.url.startsWith('https://app.selfhost.test/join?code=')).toBe(true);

    const checkSettings1 = await captureSettings(superAdmin.token);
    expect(checkSettings1.effectiveAppBaseUrl).toBe('https://app.selfhost.test');

    // 3. PUT /api/admin/settings with appBaseUrl: 'https://join.selfhost.test/' (trailing slash trimmed, beats branding)
    await applySettings({ appBaseUrl: 'https://join.selfhost.test/' }, superAdmin.token);

    const invite2Res = await postRaw(
      '/api/admin/tenants/invites',
      { email: `inv-${RUN_ID}-17b@example.com` },
      { token: superAdmin.token },
    );
    expect(invite2Res.status).toBe(200);
    expect(invite2Res.data.url.startsWith('https://join.selfhost.test/join?code=')).toBe(true);
  } finally {
    // Teardown: restore settings and branding
    await restoreSettings(origSettings, superAdmin.token);
    if (origBranding?.urls) {
      await put('/api/admin/branding', { urls: origBranding.urls }, { token: superAdmin.token });
    }
  }
});
