// Playwright API spec for R1-09 Nightly tier.
// Scenario implemented:
// - R1-09-08 ⛓ — quick-access invite still works after the migration
// Contract: docs/roadmap/testing/R1-09-tests.md
import { test, expect } from '@playwright/test';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { raw, login, postRaw, delRaw } from '../scripts/lib/api.mjs';
import { SUPER_ADMIN, TENANT_OWNER } from '../scripts/lib/constants.mjs';
import { record } from '../scripts/lib/report.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const STATE_DIR = join(here, '..', 'state');
const credPath = join(STATE_DIR, 'credentials.json');
const credentials = existsSync(credPath)
  ? JSON.parse(readFileSync(credPath, 'utf8'))
  : {
      superAdmin: SUPER_ADMIN,
      wsAdmin: TENANT_OWNER,
    };

const RUN_ID = Math.random().toString(36).substring(2, 7);

test('R1-09-08 ⛓ — quick-access invite still works after the migration', async () => {
  const start = Date.now();
  const wsAdminCreds = credentials.wsAdmin || TENANT_OWNER;
  const wsAdmin = await login(wsAdminCreds.email, wsAdminCreds.password);

  let projectA = null;
  let createdProjectLocally = false;
  let inviteId = null;

  try {
    // 1. WA GET /api/admin/projects → pick e2e-r109-a-<runId> (its URL arrived via the local default path)
    const listRes = await raw('GET', '/api/admin/projects', { token: wsAdmin.token });
    expect(listRes.status).toBe(200);
    projectA = Array.isArray(listRes.data)
      ? listRes.data.find((p) => p.key.startsWith('e2e-r109-a-'))
      : null;

    if (!projectA) {
      // Standalone execution: create the project with no appEnvironmentId so it lands on local
      const createRes = await postRaw(
        '/api/admin/projects',
        {
          key: `e2e-r109-a-${RUN_ID}`,
          name: 'R109 A',
          appUrl: 'https://r109-a.test',
        },
        { token: wsAdmin.token },
      );
      expect(createRes.status).toBe(200);
      projectA = createRes.data;
      createdProjectLocally = true;
    }

    // 2. Resolve QuickAccess role id from GET /api/admin/roles
    const rolesRes = await raw('GET', '/api/admin/roles', { token: wsAdmin.token });
    expect(rolesRes.status).toBe(200);
    const qaRole = Array.isArray(rolesRes.data)
      ? rolesRes.data.find((r) => r.quickAccess === true || r.name.toLowerCase() === 'client')
      : null;
    expect(qaRole, 'QuickAccess role must exist in roles list').toBeTruthy();
    const qaRoleId = qaRole.id;

    // WA POST /api/admin/invites { projectId: a.id, email: 'r109-cl-<runId>@example.com', roleId, createNewWorkspace: false }
    const inviteEmail = `r109-cl-${RUN_ID}@example.com`;
    const inviteRes = await postRaw(
      '/api/admin/invites',
      {
        projectId: projectA.id,
        email: inviteEmail,
        roleId: qaRoleId,
        createNewWorkspace: false,
      },
      { token: wsAdmin.token },
    );

    // 2 → 200, not Invite.QuickAccessAppUrlRequired
    expect(inviteRes.status).toBe(200);
    inviteId = inviteRes.data?.id;

    // 3. Inspect response: the response's app-url field equals https://r109-a.test
    // R2-05: a quick-access invite returns the MAGIC LINK, not the bare app URL — the app URL with
    // ?pointer_invite=<token> appended. The point of this assertion is that the link is built from
    // the project's migrated app URL, so check the base rather than equality.
    expect(inviteRes.data?.url).toMatch(/^https:\/\/r109-a\.test\/?\?pointer_invite=[A-Za-z0-9_-]{43}$/);

    record({
      id: 'R1-09-08',
      tier: 'nightly',
      layer: 'api',
      role: 'WA',
      result: 'PASS',
      ms: Date.now() - start,
      detail: `projectId=${projectA.id}, url=${inviteRes.data?.url}`,
    });
  } finally {
    // Teardown: revoke invite and clean up local project if created in this test
    if (inviteId) {
      await delRaw(`/api/admin/invites/${inviteId}`, { token: wsAdmin.token });
    }
    if (createdProjectLocally && projectA?.id) {
      await delRaw(`/api/admin/projects/${projectA.id}`, { token: wsAdmin.token });
    }
  }
});
