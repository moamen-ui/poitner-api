# Multi-Tenancy — Phase 2 (Demo Tenants) Design

Date: 2026-06-29
Status: Approved (design settled in brainstorming; builds on Phase 1 tenancy core)

## Summary

A guest can try the full dashboard + widget with **one click**, no install. "Try the
demo" provisions an **ephemeral scoped-admin tenant** (a Phase-1 tenant) **seeded with
its own sample data**, logs the guest straight in, and shows a copy-paste
`<pointer-feedback>` snippet + widget credentials. Each demo tenant is capped at **10
comments**, auto-expires, and is **hard-deleted 24h after creation** by a background job
(reusing Phase 1's `TenantService.HardDeleteAsync`). Abuse is bounded by a per-IP rate
limit and a global active-demo cap. The demo dashboard is served at
`demo.pointer.moamen.work`.

Because each demo tenant gets its **own** seeded data and Phase 1 enforces strict
per-tenant isolation, no shared-seed special-casing is needed — a demo guest sees only
their own world (seed + what they add), exactly like a real tenant.

## Data model

- `User`: add `IsDemo` (bool, default false) + `ExpiresAt` (DateTime?, UTC). Demo tenants
  are scoped-admins (role "Workspace Admin") created **auto-approved + active** with
  `IsDemo = true`, `ExpiresAt = now + 24h`, `OwnerId = self`. Migration `AddDemoColumns`.

## API

- **`POST /api/demo`** — `[AllowAnonymous]`, **rate-limited** (fixed-window per IP). Steps:
  1. Enforce the **global active-demo cap**: if `count(users where IsDemo && ExpiresAt > now && DeletedAt == null) >= DemoMaxActive` (default 100) → return a friendly "demo at capacity, try again shortly" (429/409).
  2. Create the demo scoped-admin user (random email `demo-<short>@demo.pointer`, random password, role "Workspace Admin", `IsDemo`, `ExpiresAt = now+24h`, `OwnerId = self`, approved+active).
  3. **Seed the tenant's own sample data** (owned by it): one demo project (key `demo-<short>`), and ~3 sample comments across statuses so the dashboard/profile and the widget aren't empty.
  4. Issue a JWT and return `DemoSessionResponse { token, email, password, projectKey, expiresAt, serverUrl }`. The dashboard composes the `<pointer-feedback>` snippet from `projectKey` + `serverUrl`.
- **10-comment cap:** in `CommentService.CreateAsync`, after resolving the project's owner, if that owner is a demo tenant and it already owns ≥ 10 comments, reject with a clear message ("Demo limit: 10 comments"). (Look up `User.IsDemo` by `OwnerId`; count comments where `OwnerId == ownerId`.)
- **Settings/limits** via existing `SettingsService`/config: `DemoMaxActive` (default 100), `DemoCommentCap` (default 10), `DemoTtlHours` (default 24), rate-limit window. Keep them as config constants for Phase 2 (super-admin-tunable later).

## Background cleanup

A new in-process `BackgroundService` (`DemoCleanupService`) with an hourly `PeriodicTimer`:
- Resolve a scope; query (via `IgnoreQueryFilters`) demo users with `ExpiresAt < now` (and not already deleted); for each, call `TenantService.HardDeleteAsync(publicId)` — which removes the tenant's projects/comments/replies/users/roles/status-overrides + uploaded files (already proven in Phase 1, FK-safe, transactional). Log counts. Swallow per-tenant errors so one failure doesn't stop the sweep.
- Registered with `AddHostedService`. Uses a DI scope per run.

## Clients

Regenerate after the demo endpoint exists (tag `Demo`); republish at deploy.

## Dashboard

- **"Try the demo"** button on the **login page** (all 3 frameworks): calls `POST /api/demo`, stores the returned token (same storage the normal login uses) + sets the user, navigates into the dashboard, and shows a **Demo panel**: the project key, a copy-paste `<pointer-feedback project="…" server="…">` snippet, the widget login (email + password), and a countdown to `expiresAt`. The guest is a scoped admin, so they see the full (tenant-scoped) dashboard.
- Served at **`demo.pointer.moamen.work`** — the same dashboard build, fronted by Caddy at the new subdomain (DNS already set on GoDaddy). The "Try the demo" button can be shown on all hosts, or specifically surfaced on the demo host; for Phase 2 it's available on the login page.

## Security / abuse

- Per-IP rate limit on `POST /api/demo` (e.g., 3/hour) + global active cap (100) → bounded resource use on the free VM (each demo ≤10 comments ≈ a few MB; 100 active ≈ trivial vs 37 GB free).
- Demo tenants are ordinary scoped admins → Phase 1 isolation applies unchanged (they cannot touch other tenants or prod/global data; the role-escalation + ownership guards hold).
- 24h hard-delete truly erases demo data + uploaded files.

## Testing (local, before deploy)

- Provision a demo via `POST /api/demo`; log into the dashboard + the widget with the returned creds; confirm the guest sees only their seeded tenant data (Phase 1 isolation).
- 10-comment cap: create comments until the 11th is rejected.
- Expiry/cleanup: set a demo tenant's `ExpiresAt` to the past, run the cleanup once, confirm the tenant + all its data + upload files are gone and other tenants are untouched.
- Rate limit + active cap: rapid `POST /api/demo` → throttled; cap reached → friendly rejection.

## Out of scope (Phase 3)

Billing/plans/quotas, email verification, converting a demo tenant into a permanent paid tenant.
