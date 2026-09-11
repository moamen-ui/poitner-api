# Opus review — R1-08 (tenant invitation): execution spec + test scenarios

Reviewer: Opus, read-only pass over `docs/roadmap/execution/R1-08-tenant-invitation.md` and
`docs/roadmap/testing/R1-08-tests.md` against the real code. Verbatim.

## 1. The "ALREADY EXISTS" claim

| Capability | Verdict | Evidence |
|---|---|---|
| Super-admin-only workspace invite creation | **SHIPS** | `InviteService.cs:66-76` (super-admin branch), `:107-108` (`Result.Forbidden` for anyone else); reachable today via `POST /api/admin/invites` (`API/Controllers/Admin/InvitesController.cs:29-37`) |
| Null-owner invite | **SHIPS** | `InviteService.cs:71` `owner = null`; `Domain/Entity/Invite.cs:18-22`; filter bucket `Infrastructure/AppDbContext.cs:81` |
| Accept with the invitee's own password | **SHIPS** | `AcceptAsync:316-343` → `AcceptCreateNewWorkspaceAsync:445-501`, hash at `:474` |
| Tenant + owner user + role created | **SHIPS** | `:457-464` (global `Workspace Admin`), `:470-481` (`OwnerId == PublicId`, `Approved`, `IsActive`) |
| Atomic single-use slot claim | **PARTIAL** | The *claim* ships (`:466`; `Infrastructure/Repository/UnitOfWork.cs:24-39`), but **single-use does not**: `CreateAsync:143` leaves `MaxUses = null` and `ResolveValidInviteAsync:626-627` treats null as unlimited-within-TTL. The Goal paragraph ("supports … an atomic single-use claim") contradicts Decision 4 in the same document. Re-word to "atomic claim ships; single-use is new." |
| E-mail with no plaintext credential | **PARTIAL** | `BuildInviteEmailHtml:688-699` contains no password ✔. But the promised copy "You've been invited to create a workspace" does **not** exist — `roleLine` is empty when `roleName == null` (`:690-692`), so a workspace invite mail is heading + button + expiry only. The builder edit is described in §Email but is **absent from Tasks 1-9**. |
| Expiry / uses / revoke | **SHIPS** | `:151`, `RevokeAsync:222-233`, `ResolveValidInviteAsync:608-630` |
| Anonymous preview | **PARTIAL** | `GetPreviewAsync:269-277` returns `IsNewWorkspace`/`EmailLocked` ✔, but `WorkspaceName` is left `""` (`InvitePreviewResponse.cs:17`) and there is no `DisplayName`/`PlanName`. The Data table promises "the accept form shows it [DisplayName]" — **no endpoint can deliver that**; `InvitePreviewResponse` needs the field and it is in neither Endpoints nor Tasks. |

Assumed-present but **absent**: resend (nothing); plan-on-invite (nothing); a writable `app_base_url` (see §4.1).

## 2. Factual errors

1. Task 6 — "`Policies.SuperAdmin` is already class-level" ✔ (`TenantsController.cs:12`), but "**the existing `[Produces("application/json")]`**" is **false**: `TenantsController` has no `[Produces]` attribute at all (contrast `Admin/InvitesController.cs:16`). Task must say *add* it.
2. Decision 1 cites "`CreateAsync:145-155`" for the self-owned-email uniqueness slot; that range in `InviteService` is the `new Invite { … }` literal. The real check is `InviteService.cs:448-455` / `TenantService.cs:144-153`.
3. Prerequisites: "plus `email_from_email`" — the from-address is **not** required; `EmailService.cs:35-41` passes null and `SmtpEmailSender` falls back (00-HARNESS §3).
4. `SettingsController.cs:31-55` → the `PUT` is `:32-55` (both documents).
5. Signup limit is shared by **six** endpoints, not five — `GET /api/invites/{code}` also carries `[EnableRateLimiting("signup")]` (`API/Controllers/InvitesController.cs:23`). 00-HARNESS §9 lists five; the tests doc is right, the harness needs the amendment.
6. Cosmetic drift: `BuildJoinUrl:684-686`→`:685-686`; `GenerateCode:670-675`→`:670-674`; `AcceptCreateNewWorkspaceAsync:445-500`→`:445-501`; `TenantService.CreateAsync:169-183`→`:169-182`; tests doc `AcceptCreateNewWorkspaceAsync:449-456`→`:448-455`.
7. Tests doc: "a partial body silently clears the **brand**/demo/extension settings" — branding is not written here (`SettingsController.cs:36-52` writes signup/email/demo/extension only; branding is `BrandingService.cs:46` via `PUT /api/admin/branding`). Drop "brand".
8. Tests doc R1-08-06: `psql -U postgres -d pointer` is wrong — compose seeds `POSTGRES_USER/DB = pointer` (`docker-compose.yaml:5-7`). Must be `-U pointer -d pointer`.
9. Tests doc: "join the **existing** `--mail` phase" — no `--mail` phase exists in `e2e/run-e2e.sh` today; R1-07 adds it.
10. Execution doc "Prerequisites — **None inside R1**" contradicts the tests doc: AC-2 and AC-8 are E2E-provable only after R1-07 (mailpit + `lib/mail.mjs`).

## 3. Design holes (the decisions — note: the doc has **nine**, not ten)

1. **D8(a) — the plan→Subscription mechanism contradicts an existing invariant.** `AuthService.RegisterAdminAsync:395-417` creates a paid-plan subscription as `Status = PendingActivation`, skipping `slug == "free"` and `DisplayState.Hidden`; `TenantService.ChangePlanAsync:300-354` creates `Status = Active` **and** calls `IBillingProvider`. The doc picks `ChangePlanAsync`, so an invited paid plan goes Active with no payment and no super-admin activation — a monetization bypass relative to self-serve signup. Adopt the `RegisterAdminAsync` shape, or state explicitly that the super-admin invite *is* the activation.
2. **D8(b) — the specified extraction is impossible.** "extract to a private `AssignPlanAsync(Guid ownerId,int planId)` used by both": the two callers are in different classes, and §Service already makes `TenantService` compose `IInviteService` — making `InviteService` depend on `ITenantService` is a **DI cycle**. `InviteService`'s ctor (`:39-57`) has no `IBillingProvider`. Either inline the plan write in `InviteService` (it needs only `Repository<Plan>`/`Repository<Subscription>`), or add a third `ISubscriptionProvisioner`.
3. **D8(c) — subscription write on an anonymous path.** `Subscription.OwnerId` is non-null strict-own (`Subscription.cs:13-14`, `AppDbContext.cs:122`) and accept runs with `TenantId == null`, where `TenantStamp.OwnerFor` returns **null** (`TenantStamp.cs:11`). The doc must say "set `OwnerId = publicId` explicitly; read with `IgnoreQueryFilters()`", otherwise 01-OVERVIEW:57's default rule misleads the implementer.
4. **D8(d) — column naming.** There is no global snake_case convention; every column is named explicitly (`InviteMapping.cs:14-33`). Task 1 must specify `HasColumnName("plan_id")` and `HasColumnName("display_name")` or the migration adds PascalCase columns.
5. **D4 — the new list filter breaks the Rollout promise.** `Uses < MaxUses` is false in SQL when `MaxUses IS NULL`, so every pre-change null-owner invite (which Rollout promises "keeps working") becomes invisible in the pending list while still acceptable. Use `ListAsync`'s form (`InviteService.cs:196`): `(i.MaxUses == null || i.Uses < i.MaxUses)`, and add `i.DeletedAt == null`.
6. **D4 — silent ignore vs 400.** `POST /api/admin/invites {createNewWorkspace:true, maxUses:5}` stays reachable and would now be silently rewritten to 1. Decide (400 or ignore) and write it down.
7. **D6 — `ttl` is undefined.** The resend route takes no TTL; specify "the invite's original TTL, else `DefaultTtlDays`".
8. **Missing null-owner guard on the new routes.** `IInviteService.RevokeAsync` → `LoadOwnAsync:239-249` gives a super admin **every** invite. As specified, `DELETE /api/admin/tenants/invites/{id}` would revoke another tenant's staff or quick-access invite through the Tenants surface. List/resend/revoke must all assert `OwnerId == null` (404 otherwise).
9. **D9 — `EmailSent` on list rows.** `InviteResponse.EmailSent` is documented as "always false on list rows" and `ListAsync` never sets it (`InviteResponse.cs:28-34`, `:212-217`). `TenantInviteResponse` reuses the field in the **list** response, so the dashboard rule "when `emailSent === false` show the warning" fires on every pending row. Drop it from the list projection (create/resend only) or make it nullable.
10. **D5 — TTL bound diverges.** The new validator bounds `ExpiresInDays` to 1-30, but the shared `CreateInviteRequestValidator.cs:20-22` allows 1-365 on the old route into the same service branch. Put the bound in the service branch or note the divergence.
11. **Route/policy split is sound but has one caveat.** The strict-own filter excludes null-owner rows from any caller with a tenant claim ✔ — but `AppDbContext.cs:81` also has the branch `(currentUser.TenantId == null && !strict && e.OwnerId == null)`, and `Tenancy:StrictNullTenantIsolation` defaults **false** (`:19-20`). An admin-tier principal with no `tenant` claim would see every workspace invite (code included) via `GET /api/admin/invites`. Pin this assumption in AC-7 in one line.
12. **D7 is fine** (byte-compatible, no guard) — but "guarded so it compiles before R1-02 lands, **or** skipped with a TODO" is two options; pick one.

## 4. Missing work

1. **`app_base_url` has no writer anywhere.** It is read only at `InviteService.cs:678`; it is not in `UpdateSettingsRequest`/`SettingsController`, not in `AdminSeeder`. Every join link is therefore hard-coded to `https://app.pointer.moamen.work` unless a row is INSERTed by hand — which breaks the primary flow for exactly the self-hosted/air-gapped installs the doc protects. Add it to the settings writer, or fall back to the writable `brand_url_app` (`BrandingService.cs:46`). Neither document mentions this; the tests doc even calls the default "fine".
2. `InvitePreviewResponse.DisplayName` (and `PlanName`) — required by the Data table's own promise, missing from Endpoints and Tasks.
3. The e-mail body change (§Email) is missing from Tasks.
4. **An existing test breaks.** `Tests/InviteServiceTests.cs:374-391` (`SuperAdmin_Create_NewWorkspaceInvite_IgnoresTargetOwnerIdAndRole`) creates a `CreateNewWorkspace` invite with **no email** and asserts success — Task 3 makes it fail. Name it in §Tests instead of "extend only where behaviour changes".
5. MessageKeys for the new not-found/resend paths (`MessageKeys.cs:135-146`); `User.EmailRequired:22` is reusable ✔.
6. Swagger **contract guard** (00-HARNESS §10 layer 1) for the four operations + two DTOs — the doc has Dashboard tasks but no guard.
7. Orval tag: the new routes inherit `[Tags("Tenants")]` (`TenantsController.cs:13`) — state that the dashboard tag filter must include `Tenants` (cf. commit `992b63f`).
8. Usage/audit events for `tenant_invited` / `tenant_invite_accepted` (only `tenant_created_direct` is mentioned).
9. **Invitee's address already owns a workspace:** `AcceptCreateNewWorkspaceAsync:448-455` returns 409 *after* the invite was created and mailed and *before* the claim, so the invite is never consumed and the pending row never clears. Decide: pre-check at `POST /api/admin/tenants/invites` (400/409), and/or add the accept-time 409 to the AC list.
10. No mention that `POST /api/admin/invites {createNewWorkspace:true}` remains a second, unlabelled entry point to the same flow under `Policies.Admin`.

## 5. Test document, per scenario

**Blocking, suite-wide — the `signup` budget is exceeded 2-3×.** Every `register-invite` *and* every anonymous `GET /api/invites/{code}` spends one token (`AuthController.cs:113`, `InvitesController.cs:23`) against 5/hour/IP (`RateLimitingExtensions.cs:30-38`). Actual spend: 01=2, 03=1, 04=2, 05=2, 06=2, 07=2, 08=4, 10=1 → **PR tier 8, full nightly 16**. The doc's "at most 4" is wrong and the suite **cannot pass as written**. Fix by dropping the redundant previews (05/06/08/10 can assert via the accept 404), and/or running the suite in the isolated `docker compose -p e2e-429` project (00-HARNESS §2, line 37), then declare the real spend in 00-HARNESS §9 as that section requires.

- **01** — proves AC-1/3/6. Executable. Missing: never asserts `emailSent`, never asserts `maxUses === 1`.
- **02** — proves AC-2 within the current builder. "`html` does not contain the string `password`" is stronger than the criterion and will break the moment the body says "set your password"; "contains no 8+-char credential-looking token other than the code" has **no implementable oracle**. Reduce to: contains the exact `url`, and does not match `/password\s*:/i`.
- **03 — does not prove AC-10; guaranteed false green.** Only `free` (Visible) and `legacy` (Hidden) are seeded (`API/Seed/AdminSeeder.cs:179-231`), and a tenant with **no** subscription already reports `planName: "Free"` (`TenantService.cs:96-102`). "pick a visible plan" therefore selects Free and the final assertion passes even if `PlanId` is ignored entirely. Fix: `POST /api/admin/plans` a distinct plan inside the scenario, and also assert `subscriptionStatus` (which distinguishes a real row from the missing-row default). Step 1's `GET /api/plans` is useless — `PlanPublicResponse` has no `id` (`PlanPublicResponse.cs:11-18`).
- **04 — does not prove single use.** Every workspace invite is email-locked (D5), so the second accept "with a different email" is rejected at `InviteService.cs:337-338` → **400 EmailMismatch**, never reaching the usage check; it merely duplicates 07, and the expected "404 or 409" is wrong. Re-accept with the **same** address: `ResolveValidInviteAsync:626-627` then returns null and `AcceptAsync:333-334` gives **404**.
- **05** — sound.
- **06** — sound in shape; psql credentials wrong (§2.8). Nightly + `db` container ✔.
- **07** — sound; the lock check precedes the claim (`:337` before `:466`), so "not consumed" holds.
- **08** — steps fine, but it is the most expensive scenario (4 tokens); step 2's `expiresAt > expiresAt1` is unverifiable until D6 names the TTL; `mail.clear()` before step 4 leaves step 2's message in the box (the ordering note covers it).
- **09** — step 3 needs a **`delRaw`** helper; 00-HARNESS §4 adds only `getRaw/postRaw/patchRaw` and `lib/api.mjs:43` throws on non-2xx. Step 4 proves invisibility only for an admin *with* a tenant claim (§3.11). Add `DELETE /api/admin/invites/<workspaceInviteId>` as wsAdmin → 404 (`LoadOwnAsync:251-258`).
- **10** — sound; needs a `put` primitive that `lib/api.mjs:40-43` does not have (the doc names `lib/settings.mjs` but not `put`).
- **11** — proves AC-9 ✔; assert the listed fields as a **superset** — the real `TenantResponse` also carries `projects`, `comments`, plan and demo fields (`TenantResponse.cs:5-31`).

**Scenarios that should exist and do not:** invitee address already owns a workspace (create OK → accept 409 → pending row stranded, §4.9); `POST …/invites` without `email` → 400; the old `POST /api/admin/invites {createNewWorkspace:true}` is also forced to `MaxUses:1` + email (the shared-column change has zero E2E); the swagger contract guard (00-HARNESS §10 layer 1); resend while `email_enabled:false`.

## 6. Verdicts

**`R1-08-tenant-invitation.md` — NOT-READY.** Two blocking items: the plan→`Subscription` semantics contradict the signup invariant and the prescribed `AssignPlanAsync` extraction is a DI cycle (§3.1-3.2); and the primary flow's deliverable (the join link) is unconfigurable because `app_base_url` has no writer (§4.1). Required edits: (a) rewrite Task 4 to the `RegisterAdminAsync:395-417` shape with the plan write inline in `InviteService`; (b) add a task for the `app_base_url` writer (or `brand_url_app` fallback); (c) fix the list filter to `(MaxUses == null || Uses < MaxUses) && DeletedAt == null`; (d) add the `OwnerId == null` guard to list/resend/revoke; (e) Task 6: *add* `[Produces("application/json")]` (it does not exist); (f) add `DisplayName` to `InvitePreviewResponse`; (g) add the e-mail-builder task; (h) name `InviteServiceTests.cs:374-391` as the test to update; (i) specify `HasColumnName("plan_id"/"display_name")` and the resend TTL; (j) soften the Goal's "atomic single-use claim" and fix the citations in §2.

**`R1-08-tests.md` — NOT-READY.** The `signup` budget is exceeded 2-3× so the suite cannot run green; R1-08-03 is a guaranteed false green; R1-08-04 tests the e-mail lock, not single use; R1-08-06 is not executable (wrong psql credentials). Fix those four, add `delRaw`/`put` to the helper list, add the five missing scenarios, and correct §2.7/§2.9.
