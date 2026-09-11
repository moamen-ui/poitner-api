# R1-08 — Tenant invitation by email (§50 · Release 1 · **CRITICAL** · 2–3 days)

## Goal
A super admin onboards a new workspace by typing an **email address** — never a password. The server
creates a single-use, expiring, email-locked invitation, emails a link, and the invitee opens it and
sets their **own** password, which mints the workspace and signs them in. The super admin can see
pending invitations, copy the link, resend and revoke. Creating a workspace by directly choosing
someone else's password remains possible but becomes an explicitly-labelled secondary path.

**Most of this already exists.** `CreateInviteRequest.CreateNewWorkspace` (super-admin only, gated at
`InviteService.cs:66-76` / `:107-108`) already produces a null-owner `Invite` whose accept mints a
brand-new self-owned tenant with the invitee's own password (`AcceptCreateNewWorkspaceAsync:445-501`,
hash at `:474`), emails a link with no plaintext password (`BuildInviteEmailHtml:688-699`), and supports
TTL and revoke (`:151`, `RevokeAsync:222-233`).

**What does *not* exist, and is the actual work here:** *single use* (`CreateAsync:143` leaves
`MaxUses = null`, and `ResolveValidInviteAsync:626-627` treats null as unlimited-within-TTL — the
**atomic claim** ships, the **cap** does not); a required email lock; resend; plan/display-name on the
invite; a pending-invitations read model; workspace-specific email copy (`roleLine` is empty when
`roleName == null`, `:690-692`, so a workspace invite today is heading + button + expiry only); a
the Tenants-area UI. (The writable `app_base_url`, once part of this item, shipped separately — Decision 10.) So this doc is **surface + harden +
prefill**, not a new subsystem.

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
- **The server work is independent of R1-01…R1-07** and can be implemented in parallel.
- **R1-07 is required for the mail E2E scenarios only** (mailpit service + `lib/mail.mjs`): AC-2 and the
  resend/no-mail criteria are unprovable end-to-end until it lands. The unit tests and every non-mail
  scenario are unaffected.
- Facts (verified 2026-09-11):
  - A "tenant" is **not a separate entity**: it is a `User` row that owns itself (`OwnerId == PublicId`)
    with the global role `"Workspace Admin"` (`TenantService.CreateAsync:169-182`).
  - `POST /api/admin/tenants` takes `CreateTenantRequest { Email, Password, DisplayName }` and hashes
    the caller-chosen password (`TenantService.CreateAsync:129-199`); gated `Policies.SuperAdmin`
    (`API/Controllers/Admin/TenantsController.cs:12`).
  - `Invite` (`Domain/Entity/Invite.cs`) already documents the null-owner "new workspace" case; fields
    `OwnerId?`, `Code`, `RoleId?`, `ProjectId?`, `Email?`, `ExpiresAt`, `MaxUses?`, `Uses`, `RevokedAt?`.
  - `Invite.Code` = 128-bit base64url, unique index, looked up as a DB row (`GenerateCode:670-674`).
  - Join URL = `{base}/join?code=…` (`BuildJoinUrl`), where `{base}` resolves
    `app_base_url` → `brand_url_app` → the compiled default (`GetAppBaseUrlAsync`). Writable at
    `PUT /api/admin/settings`; `SettingsResponse.EffectiveAppBaseUrl` reports the resolved value.
    **Shipped on `main` in `42e534e`** — see Decision 10.
  - Accept = anonymous `POST /api/auth/register-invite` with `AcceptInviteRequest { Code, Email,
    Password, DisplayName, RoleId? }`, rate-limited `signup` (`AuthController.cs:113-125`); returns
    `LoginResponse` (auto sign-in).
  - Anonymous preview = `GET /api/invites/{code}` → `InvitePreviewResponse { IsNewWorkspace,
    WorkspaceName, RoleName?, EmailLocked }`, rate-limited `signup` (`InvitesController.cs:23`). For a
    null-owner invite it returns only `IsNewWorkspace`/`EmailLocked`; `WorkspaceName` stays `""`
    (`GetPreviewAsync:269-277`) and there is no display-name/plan field — added here (Task 6).
  - The `signup` limiter is **5 requests / hour / IP** and is shared by **six** endpoints — `register`,
    `register-admin`, `register-invite`, `forgot-password`, `reset-password` and the anonymous invite
    **preview** (`RateLimitingExtensions.cs:30-38`; `AuthController.cs:113`, `InvitesController.cs:23`).
    Every preview *and* every accept spends a token; the E2E budget depends on it.
  - Admin invite CRUD = `GET/POST /api/admin/invites`, `DELETE /api/admin/invites/{id}`, gated
    `Policies.Admin` (not SuperAdmin) (`API/Controllers/Admin/InvitesController.cs`).
  - `ListAsync` hides revoked/expired/used-up invites and is tenant-filtered; **a super admin sees
    every tenant's invites** (`AppDbContext.cs:81` filter bypass) with no way to select only workspace
    invites.
  - Email is gated by the **DB** setting `email_enabled` (`EmailService.SendAsync:19-22`) and a daily
    cap (`email_daily_cap`, default 250); the `Email__Enabled` env var is read by nothing.
    `email_from_email` is **not** required — `EmailService.cs:35-41` passes it through and
    `SmtpEmailSender` falls back to `dev@pointer.local`. Settings are written by the replace-all
    `PUT /api/admin/settings` (`API/Controllers/Admin/SettingsController.cs:32-55`), which covers
    signup/email/demo/extension only — **branding is a different writer** (`PUT /api/admin/branding`,
    `BrandingService.cs:46`).
  - Invite mail is best-effort: a send failure never fails the invite; `InviteResponse.EmailSent`
    reports it (`InviteService.CreateAsync:160-180`).

## Design

### Decisions

**Decision 1 — no tenant row exists until acceptance.** Keep `AcceptCreateNewWorkspaceAsync` as the only
place a workspace is minted. A "pending workspace" is *the invite row itself*, surfaced through a new
read model. Rejected alternative: pre-creating a `User` with `ApprovalStatus.Pending` — it would occupy
the self-owned-email uniqueness slot (`InviteService.cs:448-455`, mirroring
`TenantService.cs:144-153`), count toward seat/usage queries, appear in
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
**The legacy route stays reachable:** `POST /api/admin/invites { createNewWorkspace: true }`
(`Policies.Admin` at the controller, super-admin-only inside `CreateAsync:107-108`) hits the same branch
and is therefore **also** forced to `MaxUses = 1` + required email. A supplied `maxUses` other than 1 is
**silently rewritten, not rejected** — rejecting would break existing callers and
`Tests/InviteServiceTests.cs:374-391`. The new `CreateTenantInviteRequest` has no `maxUses` field at all,
so there is nothing to reject there. Note in the XML docs that this legacy route is a second, unlabelled
entry point to the same flow.

**Decision 5 — email is required and the invite is always email-locked.** `Email` is mandatory for a
workspace invite (400 otherwise); `Invite.Email` is set, so only that address can accept
(`AcceptAsync:337-339`). TTL default stays the existing 7 days (`DefaultTtlDays:36`), overridable via
`expiresInDays`. **Bound the range inside the `CreateNewWorkspace` service branch (1–30), not only in
the new validator** — the shared `CreateInviteRequestValidator.cs:20-22` allows 1–365 into the same
branch via the legacy route, so a validator-only bound is bypassable.

**Decision 6 — resend re-sends the same code and extends the expiry; rotation is opt-in.**
`POST /api/admin/tenants/invites/{id}/resend` re-sends the existing `Code` and sets
`ExpiresAt = UtcNow + ttl`, where **`ttl` = the invite's original span (`ExpiresAt − CreatedAt`, rounded
to whole days), falling back to `DefaultTtlDays` when that is ≤ 0** — so a link the invitee already has
keeps working (they may simply not have
opened it yet). `?rotate=true` generates a new `Code` first — the "the link leaked" case — which
immediately invalidates the old one. Both are super-admin only and both return the fresh `Url`.

**Decision 7 — the direct path stays byte-compatible and is demoted by labelling, not by a guard.**
`POST /api/admin/tenants` keeps its exact request/response shape (the E2E seed and self-hosted
bootstraps depend on it). It gains: an XML-doc/Swagger summary marking it the secondary path, a
`UsageEvent` (`Type: "tenant_created_direct"`) and a dashboard UI that hides it behind a disclosure. No
new required field, no 403. **Decision on ordering: the usage-event call is omitted entirely with a
`// TODO(R1-02): emit tenant_created_direct`** — not written defensively against a type that does not
exist yet. R1-02 wires all three events (this one plus the two in Decision 11) when it lands.

**Decision 8 — plan and demo flags are carried on the invite and applied at accept.** Two additive
nullable columns on `Invite`: `PlanId` (int?) and `DisplayName` (string?). `IsDemo` is **not** carried —
demo tenants are minted by the separate demo flow (`DemoService`) and mixing the two would need
`ExpiresAt`/`DemoTtlHours` semantics on an invite; a super admin who wants a demo tenant uses the demo
flow. (Stated so an implementer does not invent it.)

**Decision 8a — the super-admin invitation IS the activation: the invited plan goes `Active`.** On
acceptance the invitee's `Subscription` is created with `Status = SubscriptionStatus.Active` — the
`ChangePlanAsync:300-354` semantics, **not** `RegisterAdminAsync`'s. The owner user is
`ApprovalStatus.Approved` + `IsActive = true`, exactly as `AcceptCreateNewWorkspaceAsync:470-481`
already writes it, so the workspace is usable immediately with no second approval step.

> **This deliberately differs from self-serve signup and is not a bug.**
> `AuthService.RegisterAdminAsync:395-417` parks a paid plan in `PendingActivation` because anyone can
> submit that form. A workspace invitation is issued by a super admin *by hand*, which is a stronger
> authorization signal than a signup — parking the new workspace to wait on the same super admin who
> just invited it would be circular. Anyone tempted to "fix" this to `PendingActivation` should read
> this paragraph first.

**Consequence, stated plainly:** an invited **paid** plan becomes `Active` with **no payment collected**.
That is intentional for super-admin-issued invitations (comped, sales-led, migrated workspaces). The
billing provider is `Noop` today, so nothing is bypassed in practice.
**Hold-list follow-up (do not build now):** when real billing lands (§20), invited paid workspaces need
either an explicit "comped" marker on the subscription or a billing-provider call at acceptance.

**Decision 8b — the plan write is inline in `InviteService`; do not extract a shared helper.** The
obvious-looking `AssignPlanAsync(Guid ownerId, int planId)` shared with `TenantService` is a **DI cycle**:
§Service already has `TenantService` composing `IInviteService`, so `InviteService` cannot depend on
`ITenantService`. `InviteService` needs only `Repository<Plan>` and `Repository<Subscription>` — write
the rows there (a third `ISubscriptionProvisioner` service is available if the duplication ever grows,
but is not justified by ~15 lines).
**Do not call `IBillingProvider`** from the accept path: `InviteService`'s ctor (`:39-57`) has no billing
dependency and acceptance is an anonymous request; `ChangePlanAsync`'s `_billing.ChangePlanAsync` call is
deliberately **not** ported.

**Decision 8c — stamp `Subscription.OwnerId` explicitly and read with `IgnoreQueryFilters()`.**
`Subscription.OwnerId` is non-null strict-own (`Subscription.cs:13-14`, `AppDbContext.cs:122`), but
acceptance runs anonymously (`TenantId == null`), where `TenantStamp.OwnerFor` returns **null**
(`TenantStamp.cs:11`). 01-OVERVIEW's default "stamp via `TenantStamp`" rule would therefore write a null
and violate the entity's own invariant. Set `OwnerId = publicId` (the new tenant's own `PublicId`) by
hand, and read `Plan`/`Subscription` with `IgnoreQueryFilters()` on this path, as every other anonymous
query in `InviteService` already does.

**Decision 9 — the link-copy fallback is the documented behaviour when mail is off.** When
`email_enabled` is false (or the daily cap is hit, or the send throws), the invite is still created and
`EmailSent` is `false`; `Url` is always returned. The dashboard always shows **Copy link** and, when
`EmailSent == false`, a warning line: "Email is disabled — send this link yourself." Identical to
R2-05's link-copy delivery.
**`EmailSent` is meaningful only on create/resend responses.** It is a one-time side effect of sending,
never a persisted invite property (`InviteResponse.cs:28-34`; `ListAsync` never sets it, `:212-217`), so
on **list** rows it is `null` — `TenantInviteResponse.EmailSent` is `bool?` and the dashboard's warning
fires only when it is exactly `false`. A non-nullable field reused on list rows would show the warning on
every pending invitation.

**Decision 10 — the join-link base URL. ✅ SHIPPED on `main` in commit `42e534e`, before this doc is
implemented.** `app_base_url` was read at `GetAppBaseUrlAsync` and **nothing anywhere wrote it** — not
`UpdateSettingsRequest`, not `SettingsController`, not `AdminSeeder` — so every invitation link resolved
to the compiled `https://app.pointer.moamen.work`, which broke exactly the self-hosted installs this doc
protects. That was a live bug affecting the staff and quick-access invitations that already ship, so it
was fixed separately rather than waiting for R1-08. What landed:
- `GetAppBaseUrlAsync` resolves **`app_base_url` → `brand_url_app` → compiled default** — a
  white-labelled install that has only set `urls.app` (`PUT /api/admin/branding`, `BrandingService.cs:46`)
  now produces correct links with no extra configuration.
- `AppBaseUrl` is writable at `PUT /api/admin/settings` as an explicit override (trimmed, trailing slash
  removed, empty ⇒ fall through to branding), for the rare install whose `/join` page is not on the
  dashboard origin.
- `SettingsResponse` gained `AppBaseUrl` (raw override) **and** `EffectiveAppBaseUrl` (where links will
  actually point after the fallback), so a super admin can see the resolved value.
- Covered by `Tests/InviteJoinUrlBaseTests.cs` (5 tests: branding fallback, override precedence, compiled
  default, trailing-slash trim, whitespace-only override).
**Consequence for this doc:** Task 7 is done; the remaining obligation is only that the Tenants UI
surfaces `effectiveAppBaseUrl` (Dashboard tasks) and that the acceptance criterion below is re-verified
against the shipped behaviour rather than implemented.

**Decision 11 — usage events.** `tenant_invited` (on create) and `tenant_invite_accepted` (on accept),
alongside Decision 7's `tenant_created_direct`. All three are `// TODO(R1-02)` comments in this release —
R1-02 owns the events table and wires them when it lands.

### Data

Migration `AddTenantInviteFields` (additive only):

| Column | Type | Mapping | Notes |
|---|---|---|---|
| `Invite.PlanId` | `int?` | `HasColumnName("plan_id")` | FK-less reference to `Plan.Id`; null = the default plan resolution at accept (unchanged behaviour) |
| `Invite.DisplayName` | `string?` (120) | `HasColumnName("display_name").HasMaxLength(120)` | Prefilled workspace/owner display name; the accept form shows it and the invitee may change it |

**Column names must be given explicitly.** There is no global snake_case convention — every column in
`Infrastructure/Mappings/InviteMapping.cs:14-33` is named by hand, so omitting `HasColumnName` produces
PascalCase columns inconsistent with the rest of the table.

No change to `User`, no change to the `Invite` query filter bucket (strict-own; super admin bypasses).

### Endpoints (all new ones **super-admin only**, `Policies.SuperAdmin`)

Placed under `api/admin/tenants/invites` — not `api/admin/invites` — so the Tenants screen owns the
whole workspace-onboarding surface and tenant admins (who hold `Policies.Admin`) can never reach it.

| Verb & route | Request | Response | Notes |
|---|---|---|---|
| `POST /api/admin/tenants/invites` | `CreateTenantInviteRequest { Email (required), DisplayName?, PlanId?, ExpiresInDays? }` | `TenantInviteResponse` | Delegates to `IInviteService` with `CreateNewWorkspace = true`, `MaxUses = 1`, email-locked |
| `GET /api/admin/tenants/invites` | — | `List<TenantInviteResponse>` | Pending only: `OwnerId == null && DeletedAt == null && RevokedAt == null && ExpiresAt > now && (MaxUses == null \|\| Uses < MaxUses)` — see the null-`MaxUses` note below |
| `POST /api/admin/tenants/invites/{id}/resend?rotate=false` | — | `TenantInviteResponse` | Decision 6 |
| `DELETE /api/admin/tenants/invites/{id}` | — | `Result` | Revoke (sets `RevokedAt`) |

**Two rules every one of the four routes must follow:**

1. **`MaxUses == null` must be treated as "uses remaining".** In SQL `Uses < MaxUses` is `NULL` (→ false)
   when `MaxUses IS NULL`, so a naive filter hides every pre-change null-owner invite — which the Rollout
   section promises keeps working — while it is still perfectly acceptable. Use `ListAsync`'s existing
   form (`InviteService.cs:196`): `(i.MaxUses == null || i.Uses < i.MaxUses)`.
2. **Every id-addressed route re-checks `OwnerId == null`, returning 404 otherwise.**
   `IInviteService.RevokeAsync` loads through `LoadOwnAsync:239-249`, which **bypasses tenant scoping
   entirely for a super admin** — so without this guard `DELETE /api/admin/tenants/invites/{id}` would
   happily revoke another tenant's staff or quick-access invite through the Tenants surface. The guard
   applies to resend and revoke (and the list filter above covers list).

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
    public bool? EmailSent { get; set; }                // create/resend only; null on list rows (Decision 9)
}
```

Reused, with one additive change: `GET /api/invites/{code}` (anonymous preview, `IsNewWorkspace: true`)
gains **`DisplayName` (string?)** and **`PlanName` (string?)** on `InvitePreviewResponse`. Today it
returns only `IsNewWorkspace`/`EmailLocked` for a null-owner invite and leaves `WorkspaceName` empty
(`GetPreviewAsync:269-277`, `InvitePreviewResponse.cs:17`) — so without this the Data table's promise
that "the accept form shows it" is undeliverable: no endpoint can supply the prefilled name. Both fields
are safe to expose anonymously (they are what the super admin typed for this invitee); the tenant GUID,
invite id and code are still never returned.

`POST /api/auth/register-invite` (anonymous accept) is unchanged. The accept response is the existing
`LoginResponse` with a token, so the invitee lands signed in.

**Invitee's address already owns a workspace.** `AcceptCreateNewWorkspaceAsync:448-455` returns 409
`AccountExists` — *after* the invite was created and mailed, and *before* the atomic claim, so the invite
is never consumed and the pending row never clears. Handle it at both ends: **(a)** `POST
/api/admin/tenants/invites` pre-checks the same condition and returns **409** with a clear message
("that address already owns a workspace") so the super admin finds out immediately instead of via a
stranded invitation; **(b)** the accept-time 409 stays as the race guard, and the pending row simply
expires — the super admin can revoke it. No cleanup job.

### Service

`ITenantService` gains `CreateInviteAsync`, `ListInvitesAsync`, `ResendInviteAsync(int id, bool rotate)`,
`RevokeInviteAsync(int id)`; the implementation composes `IInviteService` (it already owns code
generation, mail and the atomic claim) rather than duplicating it. `IInviteService.CreateAsync` changes
in exactly two ways inside its `CreateNewWorkspace` branch: `Email` becomes required (400
`MessageKeys.User.EmailRequired`) and `MaxUses` is forced to `1`.

`AcceptCreateNewWorkspaceAsync` gains, after the user row is saved: if `invite.PlanId` is set, load the
plan (`IgnoreQueryFilters()`, must be non-deleted + `IsActive`) and add
`Subscription { OwnerId = publicId, PlanId = plan.Id, Status = SubscriptionStatus.Active }` **inline**
(Decisions 8a–8c: Active, no `IBillingProvider`, explicit owner). Skip the write when the plan resolves
to `slug == "free"` or is missing — a tenant with no subscription already reports `Free`
(`TenantService.cs:96-102`), so writing a Free row adds nothing. If `request.DisplayName` is blank, fall
back to `invite.DisplayName`.

### Email

`BuildInviteEmailHtml` (`InviteService.cs:688-699`) needs a real change, not just reuse: its only
variable line is `roleLine`, which is **empty when `roleName == null`** (`:690-692`) — precisely the
workspace-invite case — so a workspace invitation today is heading + button + expiry with no indication
of what is being accepted. Add a `isNewWorkspace` branch producing the workspace copy. Subject `You're invited to {productName}`, body = product name, "You've been invited to create
a workspace", the `Accept invite →` button linking to `{app}/join?code=…`, and the expiry line. **Rule:
the body must never contain a password, a generated credential, or the word `Password:`** — asserted in
tests. Send is best-effort and never fails the invite (existing behaviour).

## Tasks
1. Migration + entity: `Invite.PlanId`, `Invite.DisplayName`; `Infrastructure/Mappings/InviteMapping.cs`
   with **explicit** `HasColumnName("plan_id")` / `HasColumnName("display_name").HasMaxLength(120)`;
   `just migrate name="AddTenantInviteFields"`.
2. `Application/DTOs/Tenant/CreateTenantInviteRequest.cs` + `TenantInviteResponse.cs` (`EmailSent` is
   `bool?`); `Application/Validators/CreateTenantInviteValidator.cs` (email required + format;
   `ExpiresInDays` 1–30 when present; `DisplayName` ≤ 120).
3. `InviteService.CreateAsync` — in the `CreateNewWorkspace` branch only: require `Email` (400
   `MessageKeys.User.EmailRequired`), force `MaxUses = 1` (silently, per Decision 4), clamp
   `ExpiresInDays` to 1–30 **in the branch** (not only in the new validator — the legacy route's
   `CreateInviteRequestValidator.cs:20-22` allows 1–365 into the same code), persist
   `PlanId`/`DisplayName`, and pre-check "address already owns a workspace" → 409.
4. `InviteService.AcceptCreateNewWorkspaceAsync` — apply `invite.PlanId` **inline** per Decisions 8a–8c
   (`Status = Active`, `OwnerId = publicId`, `IgnoreQueryFilters()`, no `IBillingProvider`, skip Free);
   fall back to `invite.DisplayName` when the accept body omits one.
5. `InviteService` email builder — add the `isNewWorkspace` branch to `BuildInviteEmailHtml` ("You've
   been invited to create a workspace"); assert in tests that the body never contains `Password:`.
6. `InvitePreviewResponse` — add `DisplayName` and `PlanName`; populate them in `GetPreviewAsync`'s
   null-owner branch (resolve the plan name when `PlanId` is set).
7. ~~**`app_base_url` writer** (Decision 10)~~ — **already done on `main`, commit `42e534e`.** Verify
   only: `GetAppBaseUrlAsync` chains `app_base_url → brand_url_app → default`, `AppBaseUrl` round-trips
   through `PUT /api/admin/settings`, and `SettingsResponse.EffectiveAppBaseUrl` reports the resolved
   value. Do **not** re-implement; if the Tenants UI needs the value it reads `effectiveAppBaseUrl`.
8. `ITenantService`/`TenantService` — the four invite methods (compose `IInviteService`);
   `ListInvitesAsync` filters `OwnerId == null && DeletedAt == null && RevokedAt == null &&
   ExpiresAt > now && (MaxUses == null || Uses < MaxUses)` and resolves `PlanName`; `ResendInviteAsync`
   per Decision 6; **every id-addressed method re-checks `OwnerId == null` → 404** (Endpoints, rule 2).
9. `API/Controllers/Admin/TenantsController.cs` — the four routes with
   `[ProducesResponseType(typeof(Inner), 200)]`, and **add `[Produces("application/json")]` to the
   class** (it has `[ApiController]`, `[Route]`, `[Authorize(Policy = Policies.SuperAdmin)]` and
   `[Tags("Tenants")]` at `:10-14`, but **no `[Produces]`** — unlike `Admin/InvitesController.cs:16`).
10. `MessageKeys` — add the invite-not-found / already-owns-a-workspace / resend keys next to the
    existing invite block (`MessageKeys.cs:135-146`); reuse `User.EmailRequired:22`.
11. `POST /api/admin/tenants` — XML doc marking it the secondary path ("sets the password directly;
    prefer `POST /api/admin/tenants/invites`"), plus `// TODO(R1-02): emit tenant_created_direct`.
    Add the same TODO for `tenant_invited` / `tenant_invite_accepted` (Decision 11).
12. XML doc on the legacy `POST /api/admin/invites` noting that `createNewWorkspace: true` is a second,
    unlabelled entry point to this flow and is subject to the same forced single-use + email rules.
13. Tests (below), including the **swagger contract guard** (00-HARNESS §10 layer 1) covering the four
    new operations and `CreateTenantInviteRequest` / `TenantInviteResponse`.
14. Docs: `AGENTS.md` — one line under the tenant/onboarding notes; `DEPLOY.md` — workspace invitations
    need `email_enabled` on (or the super admin copies the link) **and** `app_base_url` set, or links
    point at the default host.

## Dashboard tasks
- Regenerate services: `CreateTenantInviteRequest`, `TenantInviteResponse`, the `InvitePreviewResponse`
  additions, the `SettingsResponse` (`appBaseUrl`, `effectiveAppBaseUrl`) / `UpdateSettingsRequest` (`appBaseUrl`) fields already shipped in `42e534e`, and the four
  `api/admin/tenants/invites*` operations. The new routes inherit **`[Tags("Tenants")]`**
  (`TenantsController.cs:13`), so the Orval tag filter must include `Tenants` (cf. commit `992b63f`,
  which added `AppEnvironments` to that filter).
- Settings screen: an **App base URL** override field plus a read-only "Invitation links will use: `{effectiveAppBaseUrl}`" line. The API side already ships (`42e534e`) — this is regeneration + UI only.
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
  Accept applies the invited plan as **`Active`** (not `PendingActivation`) and skips the write for Free.
  **`Tests/InviteServiceTests.cs:374-391`
  (`SuperAdmin_Create_NewWorkspaceInvite_IgnoresTargetOwnerIdAndRole`) will fail as written** — it creates
  a `CreateNewWorkspace` invite with **no email** and asserts success, which Task 3 turns into a 400.
  Update it to pass an email and to assert `MaxUses == 1`.
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
      sees a null-owner invite in `GET /api/admin/invites`. (Holds because the strict-own filter excludes
      null-owner rows from any caller **with** a tenant claim. Note the other branch of
      `AppDbContext.cs:81`: a principal with *no* tenant claim sees null-owner rows when
      `Tenancy:StrictNullTenantIsolation` is false — its default, `:19-20`. Every real workspace admin
      carries a tenant claim, so this is not a hole, but do not "simplify" the filter.)
- [ ] With `email_enabled = false`: the invite is still created, `emailSent` is `false`, no mail is sent,
      and the returned `Url` works.
- [ ] `POST /api/admin/tenants` still works exactly as before (`e2e/scripts/seed.mjs` unchanged and green).
- [ ] An invite carrying `PlanId` for a **non-free** plan produces a workspace whose subscription is
      `Active` (Decision 8a — *not* `PendingActivation`), with no billing call, and the owner can use the
      workspace immediately with no approval step (`approvalStatus: Approved`, `isActive: true`).
- [ ] An id-addressed invite route (`resend`, `DELETE`) returns **404** for an invite whose `OwnerId` is
      not null — a super admin cannot reach another tenant's staff/quick-access invite through Tenants.
- [ ] A pending invitation created **before** this change (null `MaxUses`) still appears in the pending
      list and still accepts.
- [ ] Inviting an address that already owns a workspace returns 409 at create time.
- [ ] *(Re-verify, already shipped in `42e534e`)* With `app_base_url` set to a self-hosted origin, the
      returned `Url` and the emailed link both use it; with it empty, the link falls back to
      `brand_url_app`, then the compiled default.
- [ ] `just fmt`, `dotnet build`, `just test` green.

## Rollout / compatibility
Additive migration (two nullable columns) plus one additive settings field. No endpoint removed or
changed in shape; `InvitePreviewResponse` and `SettingsResponse` gain fields (additive, safe for Orval).
Existing staff and quick-access invites are untouched — only the `CreateNewWorkspace` branch gains the
email requirement and the forced `MaxUses = 1`; any in-flight null-owner invite created before this
change keeps working (its `MaxUses` stays `null`, which the pending-list filter treats as "uses
remaining" — see Endpoints rule 1; getting that filter wrong is the one way to silently break them).
**One existing unit test changes** (`Tests/InviteServiceTests.cs:374-391`) because the email requirement
is new. **Invited paid plans become `Active` without payment** (Decision 8a) — intentional, and inert
while the billing provider is `Noop`; revisit with §20. Self-hosters with `email_enabled = false` are unaffected: the link-copy
path is the documented fallback. Dashboard and API can deploy independently — the new routes are additive
and the old create form keeps working until the dashboard ships.

## Report template
Branch · files changed (api / tests / docs) · `just test` summary · curl transcript for: create invite →
email captured (or `emailSent:false`) → preview → accept → project created → second accept rejected →
resend (both modes) → revoke → 403 as a workspace admin · confirmation that `node e2e/scripts/seed.mjs`
still succeeds · dashboard follow-ups with exact DTO names.
