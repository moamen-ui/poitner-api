// Enum values confirmed against Domain/Enums (EnvironmentTag.cs, CommentStatus.cs) and
// docs/E2E_TEST_PLAN.md. Keep these two files as the single source of truth for the scenario.
export const Environment = { Local: 1, Staging: 2, Production: 3 };
export const Status = { Open: 1, ReadyToApply: 2, Applied: 3 };

// Seeded super-admin, deterministic on a fresh `docker compose down -v` (see .env: ADMIN__EMAIL/
// ADMIN__PASSWORD, read by API/Seed/AdminSeeder.cs). This account can log in and manage tenants,
// but is structurally forbidden from creating projects/comments (CommentService/ProjectService
// both reject IsSuperAdmin callers) — it is never used as a comment author below.
export const SUPER_ADMIN = {
  email: process.env.ADMIN__EMAIL || 'admin@pointer.local',
  password: process.env.ADMIN__PASSWORD || 'ChangeMe123!',
};

// The tenant this whole scenario lives under. Created fresh by seed.mjs via POST /api/admin/tenants
// — that call itself creates the tenant's "Workspace Admin" owner user with these credentials.
export const TENANT_OWNER = {
  email: 'e2e-owner@example.com',
  password: 'E2eOwnerPass1!',
  displayName: 'E2E Workspace Admin',
};

// The 4 additionally-created users, one per creatable role. NOTE: the seeded "Admin" role is the
// literal super-admin (singleton, cannot author comments) — there is no second "Admin" account.
// "Workspace Admin Deputy" is the closest creatable admin-tier (GrantsAdmin=true, not
// IsSuperAdmin) role, and stands in for docs/E2E_TEST_PLAN.md's "Admin"-authored comment (C3) —
// see seed.mjs for where this substitution is made.
export const USERS = {
  deputy: { email: 'deputy@example.com', password: 'DeputyPass1!', displayName: 'E2E Admin Deputy', roleName: 'Workspace Admin Deputy' },
  developer: { email: 'dev@example.com', password: 'DevPass1!', displayName: 'E2E Developer', roleName: 'Developer' },
  pm: { email: 'pm@example.com', password: 'PmPass1!', displayName: 'E2E PM', roleName: 'PM' },
  tester: { email: 'tester@example.com', password: 'TesterPass1!', displayName: 'E2E Tester', roleName: 'Tester' },
};

// The Client (QuickAccess) user, created via the invite flow and scoped to e2e-alpha.
export const CLIENT = { email: 'client@example.com', password: 'ClientPass1!', displayName: 'E2E Client' };

// A SECOND tenant, with its own owner and project. Every cross-tenant negative assertion needs a
// real foreign tenant to be denied against: asserting isolation with only one tenant in the
// database proves nothing, because there is nothing to leak from.
export const TENANT_B_OWNER = {
  email: 'e2e-b-owner@example.com',
  password: 'E2eBOwnerPass1!',
  displayName: 'E2E Tenant B Owner',
};

// Sole author of the rate-limit burst scenario, and used for nothing else. The comment rate limit
// is a per-user sliding window, so any account that burns its budget here would carry that
// exhaustion into later scenarios — an isolated persona keeps the burn contained.
export const FLOOD = {
  email: 'flood@example.com',
  password: 'FloodPass1!',
  displayName: 'E2E Flood',
  roleName: 'Tester',
};

export const PROJECTS = {
  alpha: { key: 'e2e-alpha', name: 'E2E Alpha', appUrl: 'https://e2e-alpha.example.test' },
  beta: { key: 'e2e-beta', name: 'E2E Beta', appUrl: 'https://e2e-beta.example.test' },
  // Tenant B's project — the target of cross-tenant reads that must fail.
  gamma: { key: 'e2e-gamma', name: 'E2E Gamma', appUrl: 'https://e2e-gamma.example.test' },
};

// Single source of truth for every port the suite binds. Fixture servers, Playwright baseURL and
// the docs all read from here: a port hard-coded in two places is a phase that passes alone and
// fails in a full run, which is expensive to diagnose and trivial to prevent.
export const PORTS = {
  smoke: 4173,
  fresh: 4174,
  viteReact: 4175,
  cspNonce: 4176,
  pinnedTamper: 4177,
  privacy: 4178,
  recorder: 4179,
  pinned: 4180,
  alpha: 4181,
  beta: 4182,
  caddy: 8443,
  registry: 4873,
  landing: 8099,
};

// The Developer account doubles as the documented automation account every AI-under-test
// invocation uses (skill.md's recommended convention — "world (a)" in docs/E2E_TEST_PLAN.md).
export const AUTOMATION_ACCOUNT = USERS.developer;
