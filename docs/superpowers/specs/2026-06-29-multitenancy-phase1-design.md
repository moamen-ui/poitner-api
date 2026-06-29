# Multi-Tenancy — Phase 1 (Tenancy Core) Design

Date: 2026-06-29
Status: Approved (user waived spec-review gate — proceed to plan + build)

## Summary

Turn Pointer into a multi-tenant system. Two admin tiers:

- **Super admin** — the operator (today's `Admin`). Sees and manages everything across all tenants. Behavior unchanged.
- **Scoped admin** — a **tenant**. Sees and manages only the data its tenant owns. New.

Tenancy is enforced **server-side, default-deny**. A scoped admin physically cannot read or mutate another tenant's data. This is the foundation for **Phase 2 (demo tenants)** and any future SaaS productization (Phase 3).

This phase is security-critical: a scoping bug is a cross-tenant data leak. Cross-tenant isolation tests are a first-class deliverable.

## Concepts

- **Tenant** = one scoped-admin user plus everything owned beneath it (its projects, the comments/replies on them, the stakeholder users it created, its roles, its status overrides).
- **TenantId** = the scoped-admin user's `PublicId` (the tenant root). Stored as `OwnerId` on owned entities and on member users. Super-admin-owned/global data has `OwnerId = null`.

## Data model

- `Role`: add `IsSuperAdmin` (bool, default false). Seeded `Admin` role → `IsSuperAdmin = true` (+ existing `GrantsAdmin = true`). New seeded **`Workspace Admin`** role → `GrantsAdmin = true`, `IsSuperAdmin = false` (this is the scoped-admin role). `Role` also gains `OwnerId` (Guid?, null = system/global role; T = a tenant's own role).
- `User`: add `OwnerId` (Guid?) = the tenant the user belongs to. Super admin → null. A scoped admin → **its own PublicId** (self-owned tenant root). A stakeholder created within tenant T → T. (Reuses existing `ApprovalStatus` + `IsActive`.)
- `Project`: add `OwnerId` (Guid?). null = legacy/super-admin-global; T = tenant-owned.
- `StatusPresentation`: add `OwnerId` (Guid?). null = global override (super-admin-managed — the existing catalog feature); T = a tenant's own override.
- New `AppSetting` entity (key/value, super-admin-managed) for runtime flags. First key: `scoped_admin_signup_enabled` ("true"/"false").
- `Comment`/`Reply`: **no new column** — they scope through their project's `OwnerId`.

Migration: existing prod rows have `OwnerId = null` → treated as super-admin-global (visible only to the super admin). Prod behaviour for the operator is unchanged.

## Authorization

- JWT adds `is_super_admin` (bool) and `tenant` (the caller's `OwnerId`, omitted/empty for super admin). Keeps `is_admin` (from `GrantsAdmin`).
- `ICurrentUser` adds `bool IsSuperAdmin` and `Guid? TenantId` (the caller's owner scope).
- Policies: keep `Admin` (`is_admin`). Add **`SuperAdmin`** (`is_super_admin`) for operator-only endpoints (tenant management, global settings, global status defaults, cross-tenant views).
- `MeResponse` adds `isSuperAdmin` so dashboards can show/hide super-admin-only UI.

## Scoping rules (core — default-deny)

A request's **scope** = `currentUser.TenantId`. Super admin has no scope (unfiltered — today's behaviour).

- **Reads** (scoped admin): filter to `OwnerId == scope`. Comments/Replies filter via `Project.OwnerId == scope`.
- **Writes** (scoped admin): stamp `OwnerId = scope` on create; on update/delete, load the target and require `target.OwnerId == scope`, else return **NotFound** (don't leak existence). Never trust an `OwnerId` from the request body.
- Applied in every admin service: Projects, Users, Roles, Stats, Comments, Statuses. Use a single shared scoping helper (e.g. an `IQueryable` extension `ScopedTo(currentUser)` + a guard `EnsureOwned(entity, currentUser)`) so the rule is defined once and reused.
- A scoped admin's `is_admin` is true (so it reaches admin endpoints), but the service scopes results. Super-admin-only endpoints additionally require the `SuperAdmin` policy.

## Per-tenant status catalog

Merge order for the effective catalog: **code `Defaults` → global overrides (`OwnerId = null`) → tenant overrides (`OwnerId = scope`)**.

- `GET /api/statuses` (no params): resolves by the authenticated caller's tenant. Anonymous → code defaults + global overrides only.
- Admin status endpoints: super admin edits **global** overrides (`OwnerId = null`); a scoped admin edits **its own** (`OwnerId = scope`). Same endpoints, scoped by caller.

## Tenant lifecycle (super-admin only)

- `GET /api/admin/tenants` — list scoped admins (+ status, created, counts).
- `POST /api/admin/tenants` — super admin creates a scoped admin (active, approved).
- `PATCH /api/admin/tenants/{id}` — approve / disable / enable (reuses `ApprovalStatus` + `IsActive`).
- `DELETE /api/admin/tenants/{id}` — **cascade hard-delete** (below).
- **Self-signup**: `POST /api/auth/register-admin` creates a **Pending** scoped-admin tenant; gated by the `scoped_admin_signup_enabled` setting (403 when disabled). The existing login gate already blocks pending/inactive users until the super admin approves.
- **Settings**: `GET/PUT /api/admin/settings` (super admin) to read/flip the toggle; `GET /api/auth/signup-enabled` (anonymous) so the public signup page knows whether to render.

## Cascade hard-delete

Reusable `ITenantService.HardDeleteAsync(Guid tenantId)` (callable by super admin now, by the Phase-2 cron later). Hard delete = rows actually removed (not soft `DeletedAt`), so the data is truly erased:

1. Collect the tenant's projects (`OwnerId = tenantId`) and their comments → delete the comments' uploaded screenshot files from disk via `IFileStorage`.
2. Hard-delete replies → comments → projects of the tenant.
3. Hard-delete the tenant's status overrides, roles, and users (stakeholders + the scoped-admin user).
4. Order respects FK constraints; run in a transaction.

## Dashboards (Angular, React, Vue)

- Scoping is server-side, so super admin and scoped admin use the **same existing dashboard** — a scoped admin transparently sees only its tenant. No per-view rewrites.
- New **super-admin-only "Tenants"** management page (list / add / approve / enable / disable / delete) — built in all three frameworks.
- **Settings** toggle UI (super-admin-only) for self-signup.
- Hide super-admin-only nav (Tenants, Settings) when `!isSuperAdmin`.
- Public **self-signup page** for scoped admins, shown only when `signup-enabled` is true.

## Security / testing (first-class)

- Cross-tenant isolation: as tenant A, every attempt to read/list/update/delete tenant B's project / user / role / comment / status returns 404/403. As a scoped admin, every super-admin-only endpoint returns 403. Anonymous `GET /api/statuses` returns defaults + global only (no tenant data).
- Verify the cascade hard-delete removes every owned row **and** the screenshot files, and touches no other tenant.
- This is the highest-risk area — verify exhaustively (curl matrices + a focused isolation test pass).

## Back-compat

- Existing prod projects/comments/users (`OwnerId = null`) remain super-admin-global; the operator sees them exactly as before. The widget keeps working. Real tenants are additive.

## Out of scope (later phases)

- **Phase 2 — demo tenants:** "Try the demo" one-click ephemeral scoped-admin tenant, **seeded with its own sample data** (so strict tenancy needs no shared-seed special case), `IsDemo` + `ExpiresAt`, 10-comment cap, 24h cleanup cron (reuses `HardDeleteAsync`), abuse caps, demo dashboard subdomain.
- **Phase 3:** billing / plans / quotas, email verification, richer signup.
