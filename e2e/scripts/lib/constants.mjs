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

export const PROJECTS = {
  alpha: { key: 'e2e-alpha', name: 'E2E Alpha', appUrl: 'https://e2e-alpha.example.test' },
  beta: { key: 'e2e-beta', name: 'E2E Beta', appUrl: 'https://e2e-beta.example.test' },
};

// The Developer account doubles as the documented automation account every AI-under-test
// invocation uses (skill.md's recommended convention — "world (a)" in docs/E2E_TEST_PLAN.md).
export const AUTOMATION_ACCOUNT = USERS.developer;
