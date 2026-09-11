# R1-08 — Tenant invitation by email (§50 · Release 1 · **CRITICAL** · 2–3 days)

## Goal
A super admin onboards a new workspace by typing an **email address** — never a password. The server
creates a single-use, expiring, email-locked invitation, emails a link, and the invitee opens it and
sets their **own** password, which mints the workspace and signs them in. The super admin can see
pending invitations, copy the link, resend and revoke. Creating a workspace by directly choosing
someone else's password remains possible but becomes an explicitly-labelled secondary path.

**Most of this already exists.** `CreateInviteRequest.CreateNewWorkspace` (super-admin only) already
produces a null-owner `Invite` whose accept mints a brand-new self-owned tenant with the invitee's own
password (`InviteService.CreateAsync:61-100,140-180`, `AcceptCreateNewWorkspaceAsync:445-500`), emails
a link with no plaintext password (`BuildInviteEmailHtml:688-700`), and supports TTL, revoke, and an
atomic single-use claim (`AtomicClaimInviteSlotAsync`). This doc is therefore **surface + harden +
prefill**, not a new subsystem: make it the primary, discoverable path in the Tenants area, force
single-use and email-lock, carry display name/plan/demo through, add resend, and demote direct create.

## Out of scope
- Removing or breaking `POST /api/admin/tenants` (the direct path) — `e2e/scripts/seed.mjs:50-55`
  depends on it and so do self-hosted/air-gapped bootstraps. It stays byte-compatible.
- Hashing `Invite.Code` at rest (see Decision 3 — a cross-cutting change to every invite type; new
  hold-list item).
- Self-service signup (`register-admin` / `scoped_admin_signup_enabled`) — unrelated path, untouched.
- Quick-access client magic links (R2-05) and the plaintext-password email that flow still sends —
  R2-05 owns that fix.
- Un-holding the notification email channel. R1-08 needs **one** transactional message, which the
  existing invite mail already is.

## Prerequisites
- **None inside R1** — this doc is independent of R1-01…R1-07 and can be implemented in parallel.
- Facts (verified 2026-09-11):
  - A "tenant" is **not a separate entity**: it is a `User` row that owns itself (`OwnerId == PublicId`)
    with the global role `"Workspace Admin"` (`TenantService.CreateAsync:169-183`).
  - `POST /api/admin/tenants` takes `CreateTenantRequest { Email, Password, DisplayName }` and hashes
    the caller-chosen password (`TenantService.CreateAsync:129-199`); gated `Policies.SuperAdmin`
    (`API/Controllers/Admin/TenantsController.cs:12`).
  - `Invite` (`Domain/Entity/Invite.cs`) already documents the null-owner "new workspace" case; fields
    `OwnerId?`, `Code`, `RoleId?`, `ProjectId?`, `Email?`, `ExpiresAt`, `MaxUses?`, `Uses`, `RevokedAt?`.
  - `Invite.Code` = 128-bit base64url, unique index, looked up as a DB row (`GenerateCode:670-675`).
  - Join URL = `{app_base_url}/join?code=…` (`BuildJoinUrl:684-686`); `app_base_url` is a DB setting
    (`ISettingsService.AppBaseUrl`, default `https://app.pointer.moamen.work`).
  - Accept = anonymous `POST /api/auth/register-invite` with `AcceptInviteRequest { Code, Email,
    Password, DisplayName, RoleId? }`, rate-limited `signup` (`AuthController.cs:113-125`); returns
    `LoginResponse` (auto sign-in).
  - Anonymous preview = `GET /api/invites/{code}` → `InvitePreviewResponse { IsNewWorkspace,
    WorkspaceName, RoleName?, EmailLocked }`, rate-limited `signup`.
  - Admin invite CRUD = `GET/POST /api/admin/invites`, `DELETE /api/admin/invites/{id}`, gated
    `Policies.Admin` (not SuperAdmin) (`API/Controllers/Admin/InvitesController.cs`).
  - `ListAsync` hides revoked/expired/used-up invites and is tenant-filtered; **a super admin sees
    every tenant's invites** (`AppDbContext.cs:81` filter bypass) with no way to select only workspace
    invites.
  - Email is gated by the **DB** setting `email_enabled` (`EmailService.SendAsync:19-22`), plus
    `email_from_email` and a daily cap (`email_daily_cap`, default 250); the `Email__Enabled` env var
    is read by nothing. Settings are written by the replace-all `PUT /api/admin/settings`
    (`API/Controllers/Admin/SettingsController.cs:31-55`).
  - Invite mail is best-effort: a send failure never fails the invite; `InviteResponse.EmailSent`
    reports it (`InviteService.CreateAsync:160-180`).

## Design

### Decisions

**Decision 1 — no tenant row exists until acceptance.** Keep `AcceptCreateNewWorkspaceAsync` as the only
place a workspace is minted. A "pending workspace" is *the invite row itself*, surfaced through a new
read model. Rejected alternative: pre-creating a `User` with `ApprovalStatus.Pending` — it would occupy
the self-owned-email uniqueness slot (`CreateAsync:145-155`), count toward seat/usage queries, appear in
the Tenants list as a real workspace with no owner able to sign in, and need a cleanup job on expiry.
Consequence: **expiry or revoke leaves nothing behind** — the invite simply stops resolving
(`ResolveValidInviteAsync:608`), and no tenant, user, subscription or project was ever created.

**Decision 2 — pending state is additive and needs no new column on `User`.** `ApprovalStatus`
(`Approved|Pending|Rejected`) and `IsActive` keep their current meanings for real tenants. "Pending
invitation" is derived: a non-deleted, non-revoked, unexpired `Invite` with `OwnerId == null`.

**Decision 3 — reuse `Invite.Code` as the token; do not introduce a second token type.** 128 random
bits, base64url, unique-indexed, looked up as a DB row — the same shape R2-05 specifies for its magic
link, minus the SHA-256-at-rest storage. Storing the hash instead is **not** done here because
`GetPreviewAsync`/`ResolveValidInviteAsync` look invites up by raw code today and every invite type
(staff, quick-access, workspace) shares the column — hashing is a separate cross-cutting change.
**Follow-up (hold list): hash `Invite.Code` at rest across all invite types, alongside NEW-5's key
hashing.** Expiry/uses semantics *are* aligned with R2-05 (see Decisions 4–5).

**Decision 4 — workspace invites are always single-use.** `MaxUses` is forced to `1` server-side and the
request field is ignored for `CreateNewWorkspace`. Today `MaxUses = null` means unlimited within the
TTL, so one leaked link could mint N workspaces. (R2-05's unlimited-within-TTL choice is correct for a
link bound to one already-provisioned low-privilege account; a workspace invite *creates* accounts, so
the opposite default applies.)

**Decision 5 — email is required and the invite is always email-locked.** `Email` is mandatory for a
workspace invite (400 otherwise); `Invite.Email` is set, so only that address can accept
(`AcceptAsync:337-339`). TTL default stays the existing 7 days (`DefaultTtlDays:36`), overridable via
`expiresInDays` (1–30, validated).

**Decision 6 — resend re-sends the same code and extends the expiry; rotation is opt-in.**
`POST /api/admin/tenants/invites/{id}/resend` re-sends the existing `Code` and sets
`ExpiresAt = UtcNow + ttl`, so a link the invitee already has keeps working (they may simply not have
opened it yet). `?rotate=true` generates a new `Code` first — the "the link leaked" case — which
immediately invalidates the old one. Both are super-admin only and both return the fresh `Url`.

**Decision 7 — the direct path stays byte-compatible and is demoted by labelling, not by a guard.**
`POST /api/admin/tenants` keeps its exact request/response shape (the E2E seed and self-hosted
bootstraps depend on it). It gains: an XML-doc/Swagger summary marking it the secondary path, a
`UsageEvent` (`Type: "tenant_created_direct"`) when R1-02's events table exists, and a dashboard UI that
hides it behind a disclosure. No new required field, no 403.

**Decision 8 — plan and demo flags are carried on the invite and applied at accept.** Two additive
nullable columns on `Invite`: `PlanId` (int?) and `DisplayName` (string?). `IsDemo` is **not** carried —
demo tenants are minted by the separate demo flow (`DemoService`) and mixing the two would need
`ExpiresAt`/`DemoTtlHours` semantics on an invite; a super admin who wants a demo tenant uses the demo
flow. (Stated so an implementer does not invent it.)

**Decision 9 — the link-copy fallback is the documented behaviour when mail is off.** When
`email_enabled` is false (or the daily cap is hit, or the send throws), the invite is still created and
`EmailSent` is `false`; `Url` is always returned. The dashboard always shows **Copy link** and, when
`EmailSent == false`, a warning line: "Email is disabled — send this link yourself." Identical to
R2-05's link-copy delivery.

### Data

Migration `AddTenantInviteFields` (additive only):

| Column | Type | Notes |
|---|---|---|
| `Invite.PlanId` | `int?` | FK-less reference to `Plan.Id`; null = the default plan resolution at accept (unchanged behaviour) |
| `Invite.DisplayName` | `string?` (120) | Prefilled workspace/owner display name; the accept form shows it and the invitee may change it |

No change to `User`, no change to the `Invite` query filter bucket (strict-own; super admin bypasses).

### Endpoints (all new ones **super-admin only**, `Policies.SuperAdmin`)

Placed under `api/admin/tenants/invites` — not `api/admin/invites` — so the Tenants screen owns the
whole workspace-onboarding surface and tenant admins (who hold `Policies.Admin`) can never reach it.

| Verb & route | Request | Response | Notes |
|---|---|---|---|
| `POST /api/admin/tenants/invites` | `CreateTenantInviteRequest { Email (required), DisplayName?, PlanId?, ExpiresInDays? }` | `TenantInviteResponse` | Delegates to `IInviteService` with `CreateNewWorkspace = true`, `MaxUses = 1`, email-locked |
| `GET /api/admin/tenants/invites` | — | `List<TenantInviteResponse>` | Pending only: `OwnerId == null && RevokedAt == null && ExpiresAt > now && Uses < MaxUses` |
| `POST /api/admin/tenants/invites/{id}/resend?rotate=false` | — | `TenantInviteResponse` | Decision 6 |
| `DELETE /api/admin/tenants/invites/{id}` | — | `Result` | Revoke (sets `RevokedAt`) |

`Application/DTOs/Tenant/TenantInviteResponse.cs`:
```csharp
public class TenantInviteResponse
{
    public int Id { get; set; }
    public string Email { get; set; } = string.Empty;   // always set (Decision 5)
    public string? DisplayName { get; set; }
    public int? PlanId { get; set; }
    public string? PlanName { get; set; }
    public string Url { get; set; } = string.Empty;     // {app_base_url}/join?code=…
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool EmailSent { get; set; }                 // false ⇒ show the copy-link warning
}
```

Unchanged and reused: `GET /api/invites/{code}` (anonymous preview, `IsNewWorkspace: true`) and
`POST /api/auth/register-invite` (anonymous accept). The accept response is the existing `LoginResponse`
with a token, so the invitee lands signed in.

### Service

`ITenantService` gains `CreateInviteAsync`, `ListInvitesAsync`, `ResendInviteAsync(int id, bool rotate)`,
`RevokeInviteAsync(int id)`; the implementation composes `IInviteService` (it already owns code
generation, mail and the atomic claim) rather than duplicating it. `IInviteService.CreateAsync` changes
in exactly two ways inside its `CreateNewWorkspace` branch: `Email` becomes required (400
`MessageKeys.User.EmailRequired`) and `MaxUses` is forced to `1`.

`AcceptCreateNewWorkspaceAsync` gains: after the user is saved, if `invite.PlanId` is set, create the
`Subscription` for the new tenant with that plan (reuse `TenantService.ChangePlanAsync`'s body — extract
it to a private `AssignPlanAsync(Guid ownerId, int planId)` used by both), and if `request.DisplayName`
is blank, fall back to `invite.DisplayName`.

### Email

Subject and body reuse `BuildInviteEmailHtml` (`InviteService.cs:688-700`) with a workspace-specific
heading: subject `You're invited to {productName}`, body = product name, "You've been invited to create
a workspace", the `Accept invite →` button linking to `{app}/join?code=…`, and the expiry line. **Rule:
the body must never contain a password, a generated credential, or the word `Password:`** — asserted in
tests. Send is best-effort and never fails the invite (existing behaviour).

## Tasks
1. Migration + entity: `Invite.PlanId`, `Invite.DisplayName`; `Infrastructure/Mappings/InviteMapping.cs`
   (max length 120 on `DisplayName`); `just migrate name="AddTenantInviteFields"`.
2. `Application/DTOs/Tenant/CreateTenantInviteRequest.cs` + `TenantInviteResponse.cs`;
   `Application/Validators/CreateTenantInviteValidator.cs` (email format required; `ExpiresInDays` 1–30
   when present; `DisplayName` ≤ 120).
3. `InviteService.CreateAsync` — in the `CreateNewWorkspace` branch only: require `Email`, force
   `MaxUses = 1`, persist `PlanId`/`DisplayName` from the request.
4. `InviteService.AcceptCreateNewWorkspaceAsync` — apply `invite.PlanId` via the extracted
   `AssignPlanAsync`; fall back to `invite.DisplayName` when the accept body omits one.
5. `ITenantService`/`TenantService` — the four invite methods (compose `IInviteService`); `ListInvitesAsync`
   filters `OwnerId == null` and resolves `PlanName`; `ResendInviteAsync` per Decision 6.
6. `API/Controllers/Admin/TenantsController.cs` — the four routes above with
   `[ProducesResponseType(typeof(Inner), 200)]` and the existing `[Produces("application/json")]`;
   `Policies.SuperAdmin` is already class-level.
7. `POST /api/admin/tenants` — add the XML doc marking it the secondary path ("sets the password
   directly; prefer `POST /api/admin/tenants/invites`") and the `tenant_created_direct` usage event
   (guarded so it compiles before R1-02 lands, or skipped with a `// TODO(R1-02)` line).
8. Tests (below).
9. Docs: `AGENTS.md` — one line under the tenant/onboarding notes; `DEPLOY.md` — a note that workspace
   invitations need `email_enabled` on (or the super admin copies the link).

## Dashboard tasks
- Regenerate services: `CreateTenantInviteRequest`, `TenantInviteResponse`, and the four
  `api/admin/tenants/invites*` operations.
- Tenants screen: **"Invite workspace"** becomes the primary button — form = email (required), display
  name, plan, expiry days. On success show the link with a **Copy link** button and, when
  `emailSent === false`, the warning from Decision 9.
- A **Pending invitations** section above the tenants table: email, display name, plan, expires-in,
  Resend (with a "rotate link" checkbox), Revoke. Empty state hidden.
- The existing create-with-password form moves behind a disclosure labelled "Create directly (sets the
  password yourself)" with a one-line hint that the invitation is preferred.
- Nothing in the UI may display a password for another account.

## Tests
- **Unit** (`Tests/`): `TenantInviteServiceTests.cs` — create forces `MaxUses = 1` and requires an email;
  `PlanId`/`DisplayName` persist; list returns only null-owner pending rows and excludes
  revoked/expired/used; resend keeps the code and extends expiry; `resend?rotate=true` changes the code
  and the old one no longer resolves; revoke makes accept fail; a non-super-admin caller is forbidden;
  accept applies the invited plan and the fallback display name; a second accept on the same code fails
  (atomic claim); cross-tenant: a tenant admin's `GET /api/admin/invites` never returns a null-owner row.
  Extend `Tests/InviteServiceTests.cs` only where the existing `CreateNewWorkspace` behaviour changes.
- **E2E**: `docs/roadmap/testing/R1-08-tests.md` — scenarios `R1-08-01 … R1-08-11`.

## Acceptance criteria
- [ ] A super admin can create a workspace from an email address alone; no password field is involved
      anywhere in the primary flow.
- [ ] The invitation email contains the join link and **no password** (no `Password:`, no generated
      credential string).
- [ ] Opening the link shows the accept form (`IsNewWorkspace: true`); setting a password mints the
      workspace, signs the invitee in, and they can immediately create a project.
- [ ] A second accept with the same code fails; an expired or revoked link fails; another email address
      cannot accept an email-locked invite.
- [ ] Resend re-sends the same working link and extends the expiry; `rotate=true` invalidates the old link.
- [ ] Pending invitations are listed with email, plan and expiry, and disappear after acceptance.
- [ ] A non-super-admin (workspace admin) gets 403 on every `api/admin/tenants/invites` route and never
      sees a null-owner invite in `GET /api/admin/invites`.
- [ ] With `email_enabled = false`: the invite is still created, `emailSent` is `false`, no mail is sent,
      and the returned `Url` works.
- [ ] `POST /api/admin/tenants` still works exactly as before (`e2e/scripts/seed.mjs` unchanged and green).
- [ ] An invite carrying `PlanId` produces a workspace on that plan.
- [ ] `just fmt`, `dotnet build`, `just test` green.

## Rollout / compatibility
Additive migration (two nullable columns). No endpoint removed or changed in shape. Existing staff and
quick-access invites are untouched — only the `CreateNewWorkspace` branch gains the email requirement
and the forced `MaxUses = 1`; any in-flight null-owner invite created before this change keeps working
(its `MaxUses` stays as it was). Self-hosters with `email_enabled = false` are unaffected: the link-copy
path is the documented fallback. Dashboard and API can deploy independently — the new routes are additive
and the old create form keeps working until the dashboard ships.

## Report template
Branch · files changed (api / tests / docs) · `just test` summary · curl transcript for: create invite →
email captured (or `emailSent:false`) → preview → accept → project created → second accept rejected →
resend (both modes) → revoke → 403 as a workspace admin · confirmation that `node e2e/scripts/seed.mjs`
still succeeds · dashboard follow-ups with exact DTO names.
