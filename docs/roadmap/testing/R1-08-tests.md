# R1-08-tests — Tenant invitation by email

Harness: [`00-HARNESS.md`](00-HARNESS.md). Execution doc: [`../execution/R1-08-tenant-invitation.md`](../execution/R1-08-tenant-invitation.md).

Release 1 (R1.8), **CRITICAL**. The execution doc depends on nothing else in R1, but this suite does:
R1-08 is the one item that needs transactional email for a single message, so every `mail`-layer
scenario here requires the mail harness — the `mailpit` service (00-HARNESS §2, added by R1-07) **and**
the `email_enabled` **DB** toggle (`EmailService.SendAsync:19-22`; the `Email__Enabled` env var is read
by nothing). The non-mail scenarios run without either.

## Covers

- AC-1 (workspace created from an email alone; no password anywhere in the primary flow) → **R1-08-01**.
- AC-2 (email carries the link and **no** password) → **R1-08-02**.
- AC-3 (link → accept form → own password → workspace minted, signed in, can create a project) → **R1-08-01**.
- AC-4 (second accept fails; expired fails; revoked fails; wrong email fails) → **R1-08-04**, **R1-08-05**,
  **R1-08-06**, **R1-08-07**.
- AC-5 (resend re-sends the same working link and extends expiry; `rotate=true` invalidates the old) → **R1-08-08**.
- AC-6 (pending list shows email/plan/expiry and disappears after acceptance) → **R1-08-01** step 8 + **R1-08-03**.
- AC-7 (non-super-admin 403 on every invite route; never sees a null-owner invite) → **R1-08-09**.
- AC-8 (`email_enabled=false` → invite still created, `emailSent:false`, no mail, `Url` works) → **R1-08-10**.
- AC-9 (`POST /api/admin/tenants` unchanged; the seed still works) → **R1-08-11** + every run's `seed` phase.
- AC-10 (an invite carrying `PlanId` produces a workspace on that plan) → **R1-08-03**.
- AC-11 (`just fmt`/`build`/`test`) — unit-level, see *Not covered here*.
- Endpoints rule 2 / "already owns a workspace" → **R1-08-12**; required email → **R1-08-13**; the legacy
  route inheriting the same rules → **R1-08-14**; the swagger contract guard (00-HARNESS §10 layer 1) →
  **R1-08-15**; resend under `email_enabled:false` → **R1-08-16**.

## Preconditions

- Seed complete (`state/credentials.json`, `state/keys.json`); personas `SUPER_ADMIN` (the only caller
  allowed on these routes) and `TENANT_OWNER` (the 403 case). No new persona is needed — every invitee
  in this suite is created **by the scenario** with a unique address, never seeded.
- **Prerequisite doc merged: R1-07** (mailpit service + `lib/mail.mjs`) for the `mail`-layer scenarios
  (R1-08-02, R1-08-08 step 4, R1-08-10). Without it those rows report **SKIP**, never PASS.
- **Email must be enabled in the DB, not the environment.** As `superAdmin`, before any mail scenario:
  `GET /api/admin/settings` → mutate the returned object (`emailEnabled: true`,
  `emailFromEmail: 'noreply@e2e.local'`, keep every other field verbatim) → `PUT /api/admin/settings`
  with the **full** body. `PUT` is a replace-all writer (`SettingsController.cs:32-55`), so a partial
  body silently clears the signup/email/demo/extension settings. (Branding is **not** written here — it
  has its own writer, `PUT /api/admin/branding`.) Restore the captured original in the phase's `finally`.
- `app_base_url` must point at something the test can parse. Scenarios never open the app — they extract
  the `code` query parameter from `Url` and call the API directly — so any absolute URL works. Note that
  until R1-08 Task 7 lands there is **no writer** for this setting (`GetAppBaseUrlAsync:676-679` is
  read-only), so the value is whatever the compiled default is; after Task 7 the suite may set it via
  `PUT /api/admin/settings` like any other setting.
- Unique addresses per run: `inv-<runId>-<n>@example.com`. Never reuse an address across scenarios —
  `AcceptCreateNewWorkspaceAsync:448-455` conflicts (409) on an existing self-owned account.
- **Every accepted invite mints a real tenant that outlives the scenario.** Record each minted
  `PublicId`; the suite's `finally` hard-deletes them via `DELETE /api/admin/tenants/{id}` so repeated
  local runs without `down -v` stay clean.
- **`signup` rate limit budget: 5 requests / hour / IP** (`RateLimitingExtensions.cs:30-38`), shared by
  **six** endpoints — `register`, `register-admin`, `register-invite`, `forgot-password`,
  `reset-password` and the anonymous `GET /api/invites/{code}` **preview** (`AuthController.cs:113`,
  `InvitesController.cs:23`). **Every preview and every accept spends one token**, so the naive reading
  of these scenarios costs far more than 5:

  | scenario | previews | accepts | tokens |
  |---|---|---|---|
  | R1-08-01 | 1 | 1 | 2 |
  | R1-08-03 | 0 | 1 | 1 |
  | R1-08-04 | 0 | 2 | 2 |
  | R1-08-05 | 0 (see below) | 1 | 1 |
  | R1-08-06 | 0 (see below) | 1 | 1 |
  | R1-08-07 | 0 | 2 | 2 |
  | R1-08-08 | 0 (see below) | 1 | 1 |
  | R1-08-10 | 0 (see below) | 0 | 0 |
  | **PR tier total** | | | **7** |
  | **full nightly total** | | | **10** |

  **Two mitigations, both required.** (1) The redundant previews in R1-08-05/06/08/10 are dropped — each
  asserts invalidity through the **accept** (404) instead, which it already does; only R1-08-01 keeps a
  preview, because the preview *is* its point (the accept form's data). (2) The whole suite runs in the
  isolated compose project **`docker compose -p e2e-429`** (00-HARNESS §2) so its IP bucket is its own
  and cannot poison H-04, R2-05 or any other scenario — and is not poisoned by them. Declare this spend
  in 00-HARNESS §9 as that section requires.

## Scenarios

| id | intent | tier | layer | role | steps | expected | evidence |
|---|---|---|---|---|---|---|---|
| R1-08-01 | invite → accept → workspace usable | PR | api | superAdmin → invitee | 1. `POST /api/admin/tenants/invites { email:'inv-<runId>-1@example.com', displayName:'Acme Co', expiresInDays:7 }` 2. capture `id`, `url`, `expiresAt`, `emailSent`; parse `code` from `url` (`new URL(url).searchParams.get('code')`) 3. `GET /api/admin/tenants/invites` 4. `GET /api/invites/<code>` (anonymous, no token) 5. `POST /api/auth/register-invite { code, email:'inv-<runId>-1@example.com', password:'InviteePass1!', displayName:'Acme Co' }` 6. `GET /api/auth/me` with the returned token 7. `POST /api/admin/projects { key:'r108-<runId>', name:'R108' }` with that token 8. `GET /api/admin/tenants/invites` again 9. `GET /api/admin/tenants` as superAdmin | 1 → 200, `url` matches `/\/join\?code=/`, `expiresAt` ≈ now + 7 d 1 → also `emailSent === true` (mail is enabled by the phase precondition) and the created invite is single-use: `GET /api/admin/tenants/invites` row (step 3) carries `maxUses === 1` **or**, if the DTO omits it, assert single-use behaviourally via R1-08-04 3 → the row is present with `email`, `displayName`, `expiresAt` 4 → 200, `isNewWorkspace === true`, `emailLocked === true`, `displayName === 'Acme Co'` (the prefill the accept form renders); body contains no tenant GUID 5 → 200, `status === 'ok'`, `token` present 6 → `roleName === 'Workspace Admin'`, `isAdmin === true`, `isSuperAdmin === false` 7 → 200 (the new owner can immediately create a project) 8 → the row is **gone** (used up) 9 → a tenant with that email exists, **`approvalStatus === 'Approved'` and `isActive === true` immediately after acceptance** — there is no second approval step (execution doc Decision 8a); step 7 succeeding is the behavioural proof of the same thing | report row + the minted `publicId` for teardown |
| R1-08-02 | invitation email carries a link and **no** password | PR | mail | superAdmin | 1. `mail.clear()` 2. create an invite for `inv-<runId>-2@example.com` 3. `mail.awaitMessage({ to:'inv-<runId>-2@example.com', subjectIncludes:'invited' })` 4. inspect `html` | 2 → `emailSent === true` 3 → one message within 10 s; subject contains the product name from `GET /api/branding` 4 → `html` contains the exact `url` from step 2, **and does not match `/password\s*:/i`**. Nothing stronger: "must not contain the word `password`" would break the moment the copy says "set your password", and "no credential-looking token" has no implementable oracle — the criterion is *no credential is transmitted*, and the `Password:` label is how this codebase emits one (`InviteService.cs:715`, the quick-access path R2-05 removes) | Mail evidence row (to, subject) |
| R1-08-03 | invited plan is applied **and active** on the minted workspace | nightly | api | superAdmin → invitee | **The scenario must create its own plan.** Only `free` (Visible) and `legacy` (Hidden) are seeded (`AdminSeeder.cs:179-231`), and a tenant with **no** subscription already reports `planName: 'Free'` (`TenantService.cs:96-102`) — so "pick a visible plan" picks Free and the assertion passes even if `planId` is ignored entirely. `GET /api/plans` is also useless here: `PlanPublicResponse` has no `id` (`PlanPublicResponse.cs:11-18`). 1. `POST /api/admin/plans { name:'E2E Invited <runId>', slug:'e2e-invited-<runId>', priceMonthly: 19, currency:'USD', isActive:true, displayState: Visible }` → capture `planId` (teardown deletes it) 2. `POST /api/admin/tenants/invites { email:'inv-<runId>-3@example.com', displayName:'Plan Co', planId }` 3. `GET /api/admin/tenants/invites` 4. accept with a fresh password, **omitting `displayName`** 5. `GET /api/admin/tenants` as superAdmin 6. with the invitee's token: `POST /api/admin/projects { key:'r108p-<runId>', name:'Plan Co App' }` | 3 → the pending row echoes `planId` and `planName === 'E2E Invited <runId>'` 4 → 200; the created user's `displayName` falls back to the invite's `'Plan Co'` 5 → the new tenant's `planName` equals the invited plan **and `subscriptionStatus === 'Active'`** — not `PendingActivation`, and not the missing-row `Free`/`null` default (execution doc Decision 8a: the super-admin invitation *is* the activation; contrast `RegisterAdminAsync:395-417`, which parks self-serve paid signups). `subscriptionStatus` is the field that distinguishes a real subscription row from the default 6 → 200 — the workspace is usable immediately, with no approval step between acceptance and use | report row + the created `planId` for teardown |
| R1-08-04 | single use — a second accept is rejected | PR | api | invitee | 1. create an invite for `inv-<runId>-4@example.com` 2. accept it (`register-invite`) 3. accept **the same code** again **with the same address** and a different password | 2 → 200 3 → **404** (`MessageKeys.Invite.NotFound`). The path matters: `ResolveValidInviteAsync:626-627` finds `Uses >= MaxUses` and returns null, so `AcceptAsync:333-334` 404s — that is the **usage cap**, which is what this scenario exists to prove. Retrying with a *different* address would instead trip the email lock at `:337-338` (400 `EmailMismatch`) before the usage check ever runs, duplicating R1-08-07 and proving nothing about single use. Also: no second tenant for the address in `GET /api/admin/tenants` | report row |
| R1-08-05 | revoked link cannot be accepted | PR | api | superAdmin → invitee | 1. create an invite for `inv-<runId>-5@example.com`, capture `id` + `code` 2. `DELETE /api/admin/tenants/invites/<id>` 3. `POST /api/auth/register-invite` with that code 4. `GET /api/admin/tenants/invites` | 2 → 200 3 → **404**, no tenant created (revoked codes are indistinguishable from unknown ones) 4 → the row is absent. (**No preview call** — it would cost a `signup` token to re-prove what the accept already proves; see Preconditions.) | report row |
| R1-08-06 | expired link cannot be accepted | nightly | api | superAdmin → invitee | 1. create an invite with `expiresInDays: 1` 2. expire it directly: `docker compose exec -T db psql -U pointer -d pointer -c "update invites set expires_at = now() - interval '1 day' where code = '<code>'"` (**Decision:** direct SQL — no API can backdate an invite, and waiting a day is not a test) 3. `POST /api/auth/register-invite` 4. `GET /api/admin/tenants/invites` | 3 → **404**, no tenant created 4 → the row is absent (expired rows are filtered out). (No preview call — `signup` budget.) | report row + the psql statement |
| R1-08-07 | email lock — another address cannot accept | PR | api | invitee | 1. create an invite for `inv-<runId>-7@example.com` 2. `POST /api/auth/register-invite { code, email:'someone-else-<runId>@example.com', password:'Other1234!', displayName:'X' }` 3. accept correctly with the locked address | 2 → 400 with the email-mismatch message; no tenant for the wrong address 3 → 200 (the invite was not consumed by the failed attempt) | report row |
| R1-08-08 | resend keeps the link working; rotate invalidates it | nightly | api + mail | superAdmin | 1. create an invite for `inv-<runId>-8@example.com` with `expiresInDays: 7`, capture `code1`, `expiresAt1`, `id` 2. `POST /api/admin/tenants/invites/<id>/resend` 3. `mail.clear()` then `POST /api/admin/tenants/invites/<id>/resend?rotate=true` → capture `code2` 4. `mail.awaitMessage({ to:'inv-<runId>-8@example.com' })` 5. accept with `code2`; then accept with `code1` | 2 → 200, `url` still contains `code1`, and `expiresAt ≈ now + 7 d` — **strictly greater than `expiresAt1`** because resend re-bases the invite's *original span* (execution doc Decision 6: `ExpiresAt − CreatedAt`, rounded to whole days, falling back to `DefaultTtlDays`); asserting only `>` without knowing that rule is unverifiable, so assert the 7-day window explicitly (±1 min) 3 → 200, `code2 !== code1` 4 → the newest message contains `code2` and **not** `code1` 5 → `code2` → 200; `code1` → 404 (rotation invalidated it — proven through the accept, not a preview, per the budget) | report row + mail evidence |
| R1-08-09 | only a super admin can invite workspaces | PR | api | wsAdmin | 1. as `wsAdmin`: `POST /api/admin/tenants/invites { email:'nope-<runId>@example.com' }` 2. `GET /api/admin/tenants/invites` 3. `DELETE /api/admin/tenants/invites/1` 4. as `superAdmin` create a workspace invite (capture `id`); then as `wsAdmin` `GET /api/admin/invites` 5. as `wsAdmin` `DELETE /api/admin/invites/<id>` (the **legacy** route, `Policies.Admin` — reachable) | 1–3 → **403** each (class-level `Policies.SuperAdmin`), not 404/400. Step 3 needs a **`delRaw`** helper: `lib/api.mjs:43`'s `del` throws on non-2xx and 00-HARNESS §4 adds only `getRaw`/`postRaw`/`patchRaw` 4 → the returned list contains **no** row whose `url` matches the super admin's workspace invite `code` 5 → **404** — `LoadOwnAsync:251-258` scopes a non-super-admin to `OwnerId == own tenant`, so a null-owner invite is unreachable even on the route a tenant admin *can* call. **Assumption pinned:** this holds because every real workspace admin carries a `tenant` claim; `AppDbContext.cs:81`'s other branch would expose null-owner rows to a principal with **no** tenant claim while `Tenancy:StrictNullTenantIsolation` is false (its default, `:19-20`) — do not "simplify" that filter | report row |
| R1-08-10 | mail disabled → link-copy fallback | nightly | api + mail | superAdmin | 1. capture settings; `PUT /api/admin/settings` with the full body and `emailEnabled:false` 2. `mail.clear()` 3. create an invite for `inv-<runId>-10@example.com` 4. `mail.assertNoMail({ to:'inv-<runId>-10@example.com', withinMs:3000 })` 5. accept with `<code>` 6. restore the captured settings (`finally`) | 3 → 200, `emailSent === false`, `url` non-empty 4 → no message 5 → **200** — the link works regardless of mail, which is the point of the fallback (asserted through the accept rather than a preview, per the budget) | report row + "no mail" evidence |
| R1-08-11 | the direct path still works (seed depends on it) | PR | api | superAdmin | 1. `POST /api/admin/tenants { email:'direct-<runId>@example.com', password:'DirectPass1!', displayName:'Direct Co' }` 2. `POST /api/auth/login { email, password }` 3. `GET /api/admin/tenants` | 1 → 200; assert these fields as a **superset**, not an exact shape — the real `TenantResponse` also carries `projects`, `comments`, `planName`, `subscriptionStatus` and the demo fields (`TenantResponse.cs:5-31`), so an exact-shape assertion would fail on an unrelated addition: `id`, `publicId`, `ownerId`, `email`, `displayName`, `approvalStatus:'Approved'`, `isActive:true` 2 → `status === 'ok'` 3 → the tenant is listed | report row |
| R1-08-12 | invitee address already owns a workspace | PR | api | superAdmin → invitee | 1. `POST /api/admin/tenants { email:'owner-<runId>@example.com', password:'DirectPass1!', displayName:'Existing Co' }` (direct path — cheapest way to create a self-owned account) 2. `POST /api/admin/tenants/invites { email:'owner-<runId>@example.com' }` 3. if step 2 returned 200, accept with that code | 2 → **409** (execution doc Endpoints, "Invitee's address already owns a workspace": the create-time pre-check) so the super admin learns immediately instead of mailing a link that can never be accepted 3 → only reachable if the pre-check is absent; then the accept must 409 (`AcceptCreateNewWorkspaceAsync:448-455`) **and** the invite must remain unconsumed (the 409 precedes the atomic claim at `:466`), leaving a stranded pending row that the super admin can revoke | report row |
| R1-08-13 | create without an email is rejected | PR | api | superAdmin | 1. `POST /api/admin/tenants/invites { displayName:'No Email Co' }` (no `email`) 2. `POST /api/admin/tenants/invites { email:'not-an-email', displayName:'Bad' }` 3. `GET /api/admin/tenants/invites` | 1 → **400** (`MessageKeys.User.EmailRequired`) — email is mandatory for a workspace invite (Decision 5), which is also why `Tests/InviteServiceTests.cs:374-391` changes 2 → 400 (format) 3 → neither attempt created a row | report row |
| R1-08-14 | the legacy invite route obeys the same rules | PR | api | superAdmin | The shared-column change (forced `MaxUses = 1` + required email in the `CreateNewWorkspace` branch) is reachable through the **old** route too, and has no other E2E: 1. `POST /api/admin/invites { createNewWorkspace: true }` (no email) 2. `POST /api/admin/invites { createNewWorkspace: true, email:'legacy-<runId>@example.com', maxUses: 5 }` 3. accept it twice (same address) | 1 → **400** (the branch requires an email regardless of route) 2 → 200 and the stored invite is single-use — `maxUses: 5` is **silently rewritten to 1**, not rejected (Decision 4: rejecting would break existing callers) 3 → first accept 200, second **404** | report row |
| R1-08-15 | swagger contract guard | PR | api | — | 00-HARNESS §10 layer 1. `GET /swagger/v1/swagger.json`: 1. the four operations exist: `post /api/admin/tenants/invites`, `get /api/admin/tenants/invites`, `post /api/admin/tenants/invites/{id}/resend`, `delete /api/admin/tenants/invites/{id}` 2. each 200 response resolves a schema (`schema.$ref ?? schema.items?.$ref`) 3. `CreateTenantInviteRequest` and `TenantInviteResponse` are present in `components.schemas`, and `TenantInviteResponse` has no `code` property 4. every operation carries the `Tenants` tag | 1–4 → all pass. 3's negative is load-bearing: the invite **code** must never be in a listed DTO the dashboard renders by default. 4 is what the dashboard's Orval tag filter keys on (`TenantsController.cs:13`) | report row |
| R1-08-16 | resend while mail is disabled | nightly | api + mail | superAdmin | 1. create an invite for `inv-<runId>-16@example.com` (mail on) 2. capture settings; `PUT /api/admin/settings` full body with `emailEnabled:false` 3. `mail.clear()` 4. `POST /api/admin/tenants/invites/<id>/resend` 5. `mail.assertNoMail({ to:'inv-<runId>-16@example.com', withinMs:3000 })` 6. restore settings (`finally`) | 4 → **200** with `emailSent === false` and a non-empty `url` — resend must degrade exactly like create (Decision 9), never fail 5 → no message | report row + "no mail" evidence |


## Spec files

- `e2e/api/tenant-invite.spec.mjs` — R1-08-01, 04, 05, 07, 09, 11 (PR tier; `lib/api.mjs`, `lib/report.mjs`).
- `e2e/mail/tenant-invite-mail.spec.mjs` — R1-08-02, 08, 10 (needs `lib/mail.mjs`).
- `e2e/api/tenant-invite-nightly.spec.mjs` — R1-08-03, 06.
- New helpers, all additive to 00-HARNESS §4:
  - `lib/api.mjs` — `getRaw`/`postRaw`/`patchRaw` (00-HARNESS §4) **plus `delRaw`** (R1-08-09 step 3
    asserts a 403 on a DELETE, and `del` at `lib/api.mjs:43` throws on non-2xx) **and a `put` primitive**
    (none exists — `lib/api.mjs:40-43` exports only `get`/`post`/`patch`/`del`), which
    `lib/settings.mjs` needs for the replace-all settings write.
  - `lib/settings.mjs` — `captureSettings()` / `applySettings(patch)` / `restoreSettings(original)`
    wrapping that `PUT`.
  - `lib/tenants.mjs` — `deleteTenantByEmail()` for the suite teardown (also deletes the plan created by
    R1-08-03).
- `e2e/run-e2e.sh` — the invite mail scenarios join the `--mail` phase **added by R1-07** (no such phase
  exists today). R1-08-06 needs the `db` container, so it stays out of any phase that runs against a
  remote API. The whole suite runs under the isolated `-p e2e-429` compose project (Preconditions).

## Not covered here

- Validator behaviour (missing email → 400, `expiresInDays` out of 1–30, `displayName` > 120) —
  unit-level, `Tests/TenantInviteServiceTests.cs`.
- The atomic single-use claim under genuine concurrency — unit-level (`AtomicClaimInviteSlotAsync`);
  R1-08-04 proves the sequential case only.
- The dashboard UI (invite form, pending list, copy-link, resend/revoke, the demoted direct form) —
  lives in the `pointer-dashboard` repo with `DASH-` ids per 00-HARNESS §10; reported SKIP here when
  `DASHBOARD_DIR` is unset.
- Hashing `Invite.Code` at rest — explicitly out of scope (execution doc Decision 3, hold-list item).
- Demo-tenant provisioning — a separate flow (`DemoService`), not carried on invites (Decision 8).

## Flake notes

- **`signup` budget**: see the table in *Preconditions* — 7 tokens (PR) / 10 (nightly) against 5 per
  hour per IP, which is why the suite runs in the isolated `-p e2e-429` compose project and why only
  R1-08-01 previews. Even isolated, do not run it twice inside an hour without recreating that project's
  containers. If a 429 appears on `register-invite`, that is the budget, not a product defect — report it
  as such.
- R1-08-06 mutates the database directly; it must run after every scenario that lists pending invites,
  and its invite must be created inside the scenario so nothing else observes the backdated row.
- R1-08-10 mutates global settings — it must own the mail phase's tail and restore in `finally`; a
  crashed run leaves `emailEnabled:false` and every later mail scenario silently reports "no mail".
  Start the mail phase by asserting `emailEnabled === true` and fail loudly if not.
- R1-08-08 asserts on "the newest message"; sort Mailpit results by `Created` descending rather than
  assuming inbox order, and `mail.clear()` immediately before the rotating resend (step 3) — the
  non-rotating resend in step 2 also sends a message, and without the clear the assertion could match it.
- R1-08-03 creates a plan and R1-08-12 creates a tenant through the direct path; both are global rows.
  Teardown must delete the plan and both tenants, and neither may reuse an id/slug across runs (`runId`
  suffix).
- R1-08-16 and R1-08-10 both flip `emailEnabled` off. They must not interleave: run them back-to-back at
  the tail of the mail phase, each restoring in its own `finally`, and assert `emailEnabled === true` at
  the phase start (a crashed earlier run otherwise makes every mail scenario silently report "no mail").
- Accepted invites leave real tenants behind. Teardown deletes them; if a run is interrupted, the next
  run's addresses differ by `runId`, so a stale tenant never collides — but the Tenants list grows until
  the next `down -v`.
