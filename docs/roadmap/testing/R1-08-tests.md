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

## Preconditions

- Seed complete (`state/credentials.json`, `state/keys.json`); personas `SUPER_ADMIN` (the only caller
  allowed on these routes) and `TENANT_OWNER` (the 403 case). No new persona is needed — every invitee
  in this suite is created **by the scenario** with a unique address, never seeded.
- **Prerequisite doc merged: R1-07** (mailpit service + `lib/mail.mjs`) for the `mail`-layer scenarios
  (R1-08-02, R1-08-08 step 4, R1-08-10). Without it those rows report **SKIP**, never PASS.
- **Email must be enabled in the DB, not the environment.** As `superAdmin`, before any mail scenario:
  `GET /api/admin/settings` → mutate the returned object (`emailEnabled: true`,
  `emailFromEmail: 'noreply@e2e.local'`, keep every other field verbatim) → `PUT /api/admin/settings`
  with the **full** body. `PUT` is a replace-all writer (`SettingsController.cs:31-55`), so a partial
  body silently clears the brand/demo/extension settings. Restore the captured original in the phase's
  `finally`.
- `app_base_url` must point at something the test can parse; the default
  `https://app.pointer.moamen.work` is fine — scenarios never open the app, they extract the `code`
  query parameter from `Url` and call the API directly.
- Unique addresses per run: `inv-<runId>-<n>@example.com`. Never reuse an address across scenarios —
  `AcceptCreateNewWorkspaceAsync:449-456` conflicts (409) on an existing self-owned account.
- **Every accepted invite mints a real tenant that outlives the scenario.** Record each minted
  `PublicId`; the suite's `finally` hard-deletes them via `DELETE /api/admin/tenants/{id}` so repeated
  local runs without `down -v` stay clean.
- **`signup` rate limit budget: 5 requests / hour / IP** (`RateLimitingExtensions.cs:30-38`) shared by
  `register-invite`, `register-admin`, `forgot-password`, `reset-password` and the anonymous
  `GET /api/invites/{code}` preview. This suite spends **at most 4** (R1-08-01, R1-08-04 ×2,
  R1-08-07) and must run in a phase that does not also run H-04 (password reset, 2 tokens). See *Flake notes*.

## Scenarios

| id | intent | tier | layer | role | steps | expected | evidence |
|---|---|---|---|---|---|---|---|
| R1-08-01 | invite → accept → workspace usable | PR | api | superAdmin → invitee | 1. `POST /api/admin/tenants/invites { email:'inv-<runId>-1@example.com', displayName:'Acme Co', expiresInDays:7 }` 2. capture `id`, `url`, `expiresAt`, `emailSent`; parse `code` from `url` (`new URL(url).searchParams.get('code')`) 3. `GET /api/admin/tenants/invites` 4. `GET /api/invites/<code>` (anonymous, no token) 5. `POST /api/auth/register-invite { code, email:'inv-<runId>-1@example.com', password:'InviteePass1!', displayName:'Acme Co' }` 6. `GET /api/auth/me` with the returned token 7. `POST /api/admin/projects { key:'r108-<runId>', name:'R108' }` with that token 8. `GET /api/admin/tenants/invites` again 9. `GET /api/admin/tenants` as superAdmin | 1 → 200, `url` matches `/\/join\?code=/`, `expiresAt` ≈ now + 7 d 3 → the row is present with `email`, `displayName`, `expiresAt` 4 → 200, `isNewWorkspace === true`, `emailLocked === true`; body contains no tenant GUID 5 → 200, `status === 'ok'`, `token` present 6 → `roleName === 'Workspace Admin'`, `isAdmin === true`, `isSuperAdmin === false` 7 → 200 (the new owner can immediately create a project) 8 → the row is **gone** (used up) 9 → a tenant with that email exists, `approvalStatus === 'Approved'`, `isActive === true` | report row + the minted `publicId` for teardown |
| R1-08-02 | invitation email carries a link and **no** password | PR | mail | superAdmin | 1. `mail.clear()` 2. create an invite for `inv-<runId>-2@example.com` 3. `mail.awaitMessage({ to:'inv-<runId>-2@example.com', subjectIncludes:'invited' })` 4. inspect `html` | 2 → `emailSent === true` 3 → one message within 10 s; subject contains the product name from `GET /api/branding` 4 → `html` contains the exact `url` from step 2; `html` does **not** match `/Password\s*:/i`; does not contain the string `password`; contains no 8+-char credential-looking token other than the invite `code` | Mail evidence row (to, subject) |
| R1-08-03 | invited plan is applied to the minted workspace | nightly | api | superAdmin → invitee | 1. `GET /api/plans` → pick a plan whose `displayState` is visible; resolve its `id` via `GET /api/admin/plans` 2. `POST /api/admin/tenants/invites { email:'inv-<runId>-3@example.com', displayName:'Plan Co', planId:<id> }` 3. `GET /api/admin/tenants/invites` 4. accept with a fresh password, omitting `displayName` 5. `GET /api/admin/tenants` as superAdmin | 3 → the pending row echoes `planId` and a non-empty `planName` 4 → 200; the created user's `displayName` falls back to the invite's `'Plan Co'` 5 → the new tenant's `planName` equals the invited plan | report row |
| R1-08-04 | single use — a second accept is rejected | PR | api | invitee | 1. create an invite for `inv-<runId>-4@example.com` 2. accept it (`register-invite`) 3. accept **the same code** again with a different email + password | 2 → 200 3 → 404 or 409 (never 200); no second tenant with either address in `GET /api/admin/tenants` | report row |
| R1-08-05 | revoked link cannot be accepted | PR | api | superAdmin → invitee | 1. create an invite for `inv-<runId>-5@example.com`, capture `id` + `code` 2. `DELETE /api/admin/tenants/invites/<id>` 3. `GET /api/invites/<code>` 4. `POST /api/auth/register-invite` with that code 5. `GET /api/admin/tenants/invites` | 2 → 200 3 → 404 (revoked codes are indistinguishable from unknown ones) 4 → 404, no tenant created 5 → the row is absent | report row |
| R1-08-06 | expired link cannot be accepted | nightly | api | superAdmin → invitee | 1. create an invite with `expiresInDays: 1` 2. expire it directly: `docker compose exec -T db psql -U postgres -d pointer -c "update invites set expires_at = now() - interval '1 day' where code = '<code>'"` (**Decision:** direct SQL — no API can backdate an invite, and waiting a day is not a test) 3. `GET /api/invites/<code>` 4. `POST /api/auth/register-invite` 5. `GET /api/admin/tenants/invites` | 3 → 404 4 → 404, no tenant created 5 → the row is absent (expired rows are filtered out) | report row + the psql statement |
| R1-08-07 | email lock — another address cannot accept | PR | api | invitee | 1. create an invite for `inv-<runId>-7@example.com` 2. `POST /api/auth/register-invite { code, email:'someone-else-<runId>@example.com', password:'Other1234!', displayName:'X' }` 3. accept correctly with the locked address | 2 → 400 with the email-mismatch message; no tenant for the wrong address 3 → 200 (the invite was not consumed by the failed attempt) | report row |
| R1-08-08 | resend keeps the link working; rotate invalidates it | nightly | api + mail | superAdmin | 1. create an invite for `inv-<runId>-8@example.com`, capture `code1`, `expiresAt1`, `id` 2. `POST /api/admin/tenants/invites/<id>/resend` 3. `GET /api/invites/<code1>` 4. `mail.clear()` then `POST /api/admin/tenants/invites/<id>/resend?rotate=true` → capture `code2` 5. `GET /api/invites/<code1>` and `GET /api/invites/<code2>` 6. `mail.awaitMessage({ to:'inv-<runId>-8@example.com' })` 7. accept with `code2` | 2 → 200, `url` still contains `code1`, `expiresAt > expiresAt1` 3 → 200 (the already-sent link still works) 4 → 200, `code2 !== code1` 5 → `code1` → 404; `code2` → 200 6 → the newest message contains `code2` and not `code1` 7 → 200 | report row + mail evidence |
| R1-08-09 | only a super admin can invite workspaces | PR | api | wsAdmin | 1. as `wsAdmin`: `POST /api/admin/tenants/invites { email:'nope-<runId>@example.com' }` 2. `GET /api/admin/tenants/invites` 3. `DELETE /api/admin/tenants/invites/1` 4. as `superAdmin` create a workspace invite; then as `wsAdmin` `GET /api/admin/invites` | 1–3 → **403** each (class-level `Policies.SuperAdmin`), not 404/400 4 → the returned list contains **no** row whose `url` matches the super admin's workspace invite `code` (null-owner invites are never visible to a tenant admin) | report row |
| R1-08-10 | mail disabled → link-copy fallback | nightly | api + mail | superAdmin | 1. capture settings; `PUT /api/admin/settings` with the full body and `emailEnabled:false` 2. `mail.clear()` 3. create an invite for `inv-<runId>-10@example.com` 4. `mail.assertNoMail({ to:'inv-<runId>-10@example.com', withinMs:3000 })` 5. `GET /api/invites/<code>` 6. restore the captured settings (`finally`) | 3 → 200, `emailSent === false`, `url` non-empty 4 → no message 5 → 200 — the link works regardless of mail | report row + "no mail" evidence |
| R1-08-11 | the direct path still works (seed depends on it) | PR | api | superAdmin | 1. `POST /api/admin/tenants { email:'direct-<runId>@example.com', password:'DirectPass1!', displayName:'Direct Co' }` 2. `POST /api/auth/login { email, password }` 3. `GET /api/admin/tenants` | 1 → 200, response shape unchanged (`id`, `publicId`, `ownerId`, `email`, `displayName`, `approvalStatus:'Approved'`, `isActive:true`) 2 → `status === 'ok'` 3 → the tenant is listed | report row |

## Spec files

- `e2e/api/tenant-invite.spec.mjs` — R1-08-01, 04, 05, 07, 09, 11 (PR tier; `lib/api.mjs`, `lib/report.mjs`).
- `e2e/mail/tenant-invite-mail.spec.mjs` — R1-08-02, 08, 10 (needs `lib/mail.mjs`).
- `e2e/api/tenant-invite-nightly.spec.mjs` — R1-08-03, 06.
- New helpers: `lib/api.mjs` must expose the raw status for the 403/404 assertions (00-HARNESS §4's
  `getRaw`/`postRaw` addition); `lib/settings.mjs` — `captureSettings()` / `applySettings(patch)` /
  `restoreSettings(original)` wrapping the replace-all `PUT`; `lib/tenants.mjs` — `deleteTenantByEmail()`
  used by the suite teardown.
- `e2e/run-e2e.sh` — the invite mail scenarios join the existing `--mail` phase; R1-08-06 needs the
  `db` container, so it stays out of any phase that runs against a remote API.

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

- **`signup` budget**: R1-08-01/04/07 spend 4 of the 5 hourly tokens on `register-invite` plus the
  anonymous `GET /api/invites/{code}` previews, which share the same policy. Run this suite in a phase
  that does **not** also run H-04 (password reset, 2 tokens) or R2-05's redemption scenarios, and never
  twice inside an hour without a `down -v`. If a 429 appears on `register-invite`, that is the budget,
  not a product defect — report it as such.
- R1-08-06 mutates the database directly; it must run after every scenario that lists pending invites,
  and its invite must be created inside the scenario so nothing else observes the backdated row.
- R1-08-10 mutates global settings — it must own the mail phase's tail and restore in `finally`; a
  crashed run leaves `emailEnabled:false` and every later mail scenario silently reports "no mail".
  Start the mail phase by asserting `emailEnabled === true` and fail loudly if not.
- R1-08-08 asserts on "the newest message"; sort Mailpit results by `Created` descending rather than
  assuming inbox order, and `mail.clear()` immediately before the rotating resend.
- Accepted invites leave real tenants behind. Teardown deletes them; if a run is interrupted, the next
  run's addresses differ by `runId`, so a stale tenant never collides — but the Tenants list grows until
  the next `down -v`.
