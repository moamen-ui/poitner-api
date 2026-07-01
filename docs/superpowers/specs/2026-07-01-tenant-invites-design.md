# Design + Plan: Tenant invite links / codes

**Status:** Draft for review. **Date:** 2026-07-01.

## Context
Today a person can only become a teammate of a tenant through the **stakeholder signup**
(`POST /api/auth/register`, `AuthService.cs:143`): they submit a **`projectKey`**, the server resolves
the project's `OwnerId`, stamps the new user with it (`ApprovalStatus.Pending`, `IsActive=false`), and a
super-admin/tenant later approves them. That works for widget users (the key is embedded in
`<pointer-feedback project="…">`) but has two gaps:

- **No self-serve join from the dashboard** — there is no embedded project key there, so a teammate has
  no handle to say *which* workspace to join.
- **Approval-queue spam / no access control** — anyone who knows a project key can queue a request.

An **invite link/code** is the industry-standard fix (Slack/Notion/Linear): a tenant admin generates a
link; whoever opens it can create an account that is **pre-authorized** (skips the approval queue) and
**pre-scoped** to that tenant (and optionally a role). No typed workspace name (collision/typo/guess-spam
prone), no exposed tenant GUID.

## Goals
- A tenant admin (scoped-admin, or super-admin acting for a tenant) can **create, list, and revoke**
  invites.
- An invitee **accepts** via a link (`/join?code=…`) and lands as an **active, approved** member of that
  tenant — no approval step.
- Invites are **unguessable, expiring**, optionally **email-locked** and **usage-capped**.
- Consistent with the current model: an invited member is **tenant-scoped** (gets all that tenant's
  projects), matching stakeholder behavior.

## Non-goals (v1)
- Per-project membership (members see all of the tenant's projects — unchanged from today).
- Cross-tenant single identity (email is still one account per person; see the existing limitation).
- SSO / domain-based auto-join.

## Data model
New entity `Invite : BaseEntity` (`Domain/Entity/Invite.cs`), mapping `invites`:
```
Invite {
  Guid   OwnerId          // tenant this invite joins — NOT null (isolation boundary)
  string Code             // unguessable token (URL-safe), UNIQUE index
  int?   RoleId           // optional pinned non-admin role; null = invitee picks a tenant role
  string? Email           // optional lock: only this email may accept; null = anyone with the link
  DateTime ExpiresAt      // required TTL (default from a setting, e.g. 7 days)
  int?   MaxUses          // null = unlimited within TTL; else cap
  int    Uses             // incremented per successful accept
  DateTime? RevokedAt     // soft-revoke; a revoked/expired/used-up invite cannot be accepted
}
```
- **Query filter:** strict-own like `Project`/`Comment` (`superAdmin || OwnerId == currentUser.TenantId`).
  `OwnerId` is always set (invites are always tenant-scoped) — no null-owner branch. (Contrast the
  predefined-actions nullable-owner case; invites don't need it.)
- **Indexes:** unique on `Code`; `HasIndex(OwnerId)`.
- Reuse the crypto-random token approach already in the codebase (see `ResetTokenService` /
  `UploadSigner` in `Infrastructure/`); the code is a random 128-bit URL-safe string (not signed — it's a
  DB row we look up), generated server-side.

## API
Admin (auth, tenant-scoped) — new `API/Controllers/Admin/InvitesController.cs`, `[Route("api/admin/invites")]`,
`[Authorize(Policy = Policies.Admin)]`, `[Tags("Invites")]` (add `Invites` to `orval.config.ts` filters):
- `GET  /api/admin/invites` → list this tenant's active invites (never returns others').
- `POST /api/admin/invites` → `{ roleId?, email?, expiresInDays?, maxUses? }` → creates, returns
  `{ code, url, expiresAt, ... }` where `url = {app}/join?code=…`.
- `DELETE /api/admin/invites/{id}` → revoke (sets `RevokedAt`); scoped like the tenant-wide predefined
  CRUD (`LoadOwn…` pattern — explicit own-owner match, never reachable cross-tenant).

Anonymous accept — extend `AuthController` / `AuthService`:
- `GET  /api/invites/{code}` → validates (exists, not revoked/expired, uses remaining, email-lock note)
  and returns a **safe preview**: tenant display name + the role name (NO tenant GUID, NO prompts/secrets).
  Returns NotFound for invalid/expired so codes can't be probed for validity beyond existence.
- `POST /api/auth/register-invite` → `{ code, email, password, displayName, roleId? }`:
  1. Resolve the invite (IgnoreQueryFilters — anonymous path, like `RegisterAsync`); reject if
     invalid/expired/revoked/used-up, or if `Email` is set and ≠ submitted email.
  2. Resolve the role: the invite's `RoleId` if pinned, else validate `roleId` is a **non-admin** role of
     the invite's tenant or global (mirror `RegisterAsync.cs:162-176`).
  3. Create the user: `OwnerId = invite.OwnerId`, `ApprovalStatus = Approved`, `IsActive = true`
     (invite = authorization; **skip the Pending queue**). If the email already exists → same
     "account exists" handling as `RegisterAsync`.
  4. Increment `invite.Uses`; if `MaxUses` reached, it naturally stops accepting.
  5. Return a login token (auto-signin) like the normal login response.
- Rate-limit `register-invite` + `GET /api/invites/{code}` with the existing `"signup"` rate-limit policy.

## Dashboard (×3: Angular / React / Vue)
- **Admin — "Invite teammates"**: a section (candidate home: the existing Settings page, or the Tenants/
  team area) to generate an invite (optional role, optional email lock, expiry), copy the link, and
  list/revoke active invites. Uses the generated `InvitesService` / `usePostApiAdminInvites` hooks after a
  client republish.
- **Accept — `/join?code=…` page** (anonymous route): fetches the preview (`GET /api/invites/{code}`),
  shows "Join {workspace} as {role}", collects email/password/display name (email prefilled + locked if the
  invite is email-locked), submits `register-invite`, then signs in with the returned token.
- i18n en + ar for all new strings.

## Email (optional, ties into the demo-email note)
When an admin creates an email-locked invite, optionally email the link via the existing
`IEmailService.SendAsync` (best-effort, quota-guarded). NOTE: prod email currently fails because the VM IP
isn't in Brevo's Authorised-IPs list — unrelated to this feature, fix in Brevo. Copy-link works regardless.

## Reuse (don't reinvent)
- Register/role-resolution/`account exists` logic — mirror `AuthService.RegisterAsync` (`AuthService.cs:143`).
- Tenant-scoped CRUD loader that can't be reached cross-tenant — mirror
  `PredefinedActionService.LoadOwnTenantWideAsync`.
- Token/random-string generation — `Infrastructure/…/ResetTokenService` / `UploadSigner`.
- `MessageKeys.*` for all user strings; `[ProducesResponseType(typeof(Inner))]` + `[Produces("application/json")]`
  so orval types cleanly; add `Invites` to `orval.config.ts` `filters.tags`.

## Migration & rollout
- One migration: new `invites` table (additive). Then the standard coordinated deploy: API → publish
  clients (adds `Invites` hooks) → bump dashboards → build/deploy; verify accept flow end-to-end.

## Verification
- Admin creates an invite → gets a `/join?code=…` link. `GET /api/invites/{code}` returns the workspace +
  role preview.
- Accept with a fresh email → user is created **Approved + active**, owned by the tenant, auto-signed-in;
  can immediately comment on that tenant's projects.
- Negative: expired / revoked / used-up / wrong-email (when locked) invite → rejected. A tenant-B admin
  cannot list or revoke tenant-A's invites (isolation test, like the predefined-action isolation test).
- Rate-limit on repeated `register-invite` / code probes.

## Open questions
1. Invite default TTL + default `MaxUses` (proposed: 7 days, unlimited-within-TTL; both admin-overridable).
2. Auto-approve on accept (proposed: yes — the invite is the authorization) vs. still queue for approval.
3. Home for the admin invite UI: Settings page section vs. a dedicated "Team" page.
