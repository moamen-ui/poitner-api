# Pointer API — Fix Plan (from `BACKEND_REVIEW.md` + `fabel-comments.md`)

**Owner/orchestrator:** Fable 5 · **Started:** 2026-07-05 · **Base:** `main` @ `cca6f0b`
**Status legend:** ☐ todo · ◐ in-progress · ☑ done · ⏸ deferred (needs prod data / product decision)

> Work is partitioned by **file** so three workers run without collision. **All schema/migration
> changes funnel through Fable** (one migration, no snapshot conflicts). GLM and the Opus subagent
> edit disjoint files only. **Nothing is committed or pushed** — the result is one uncommitted diff
> for the user to review. Independent review (Opus) runs over the integrated diff at the end.

## Workstreams & ownership

| WS | Worker | Isolation | Files (exclusive) |
|----|--------|-----------|-------------------|
| **A** | **Fable (me)** — critical + schema | main working tree | `AppDbContext.cs`, `CommentService.cs`, `AuthService.cs`, `RepliesController.cs`, `SuggestionService.cs`, `Domain/Entity/User.cs`, `Infrastructure/Mappings/*`, `JwtTokenService.cs`, `AuthenticationExtensions.cs`, `ResetTokenService.cs`, `Tests/*`, the EF migration |
| **B** | **GLM-5.2 (opencode)** — mechanical | git worktree `../pointer-api-glm` | `PreferencesService.cs`, `LocalFileStorage.cs`, `UploadsController.cs`, new `API/Extensions/ResultExtensions.cs`, `Admin/InvitesController.cs`, `Admin/PredefinedActionsController.cs`, `Program.cs`, `BrandingController.cs`, `Infrastructure/DependencyInjection.cs` |
| **C** | **Opus 4.8 subagent** — perf | edits `ExportImportService.cs` only | `ExportImportService.cs` |

---

## Phase 1 — Critical (WS-A, Fable)

- ◐ **T1 · C1a — Stop cross-project by-id reads in the null bucket.** Scope `CommentService.GetByIdAsync`
  and `AddReplyAsync` by the comment's `ProjectId`/owner, not the tenant filter alone, so an id enumeration
  can never cross a project boundary even when the filter collapses. *(code — doable now)*
- ◐ **T2 · C1b — Deterministic tenant resolution for anonymous register (M15).** `AuthService.RegisterAsync`
  and `RoleService` public-roles resolve `FirstOrDefault(Key==key)` with no owner scope. Make project keys
  effectively unambiguous: reject registration when the key resolves to >1 project, or require an explicit
  owner/tenant hint. *(code — doable now)*
- ◐ **T3 · C1c — Regression test for the null-tenant collapse.** Add a `TenantQueryFilterTests` case:
  a principal with `TenantId == null, IsSuperAdmin == false` must NOT see another owner's `Comment`/
  `Project`/`User` rows. Documents + guards the boundary. *(code — doable now)*
- ⏸ **T4 · C1d — Back-fill `owner_id` + own the global projects.** The durable fix is a data migration
  assigning every legacy null-owner row to its true owner and giving global projects a real owner. This
  **requires operator input on live data** (I cannot read prod). Deliverable: a migration scaffold +
  runbook + the guard SQL, NOT a blind back-fill. *(needs prod data)*
- ☐ **T5 · C1e — Latent same-trap assertion (`PredefinedActionSuggestion`).** Add an explicit invariant
  check / comment so a future null-owner suggestion write can't silently re-open the collapse.

## Phase 2 — High (WS-A Fable unless noted)

- ☐ **T6 · H1 — Token/session revocation.** Add `User.SecurityStamp` (guid); embed it as a claim; bump on
  password change / disable / reject; validate in `OnTokenValidated`.
- ☐ **T7 · H2 — Single-use reset tokens.** Include the security stamp (or a reset nonce) in the signed
  reset payload; invalidate on first use and on password change.
- ☐ **T8 · H4 — Sargable email lookups + index.** Drop `Email.ToLower()` in favor of the already-normalized
  column comparison (also `RoleService`/`UserService`); rely on `(email, owner_id)`.
- ☐ **T9 · H5 — Stream/paginate export (WS-C Opus).** Keyset-paginate `ExportImportService`, project to the
  export DTO, cap export size.
- ☐ **T10 · H6 — Slim list payloads.** List/apply-queue DTOs omit the heavy `element` blob; load it only in
  `GetByIdAsync`. *(WS-A — same file as C1a)*
- ⏸ **T11 · H3 — Gate demo provisioning.** Verify-link/CAPTCHA is a product decision; interim: flag the
  inline-password fallback off by default. *(product decision; interim flag doable)*

## Phase 3 — Medium

- ☐ **T12 · M6/M7 — Indexes** `(project_id, created_at DESC)` partial + composite owner indexes. *(WS-A migration)*
- ☐ **T13 · M9 — `AsSplitQuery()`** on comment+replies includes. *(WS-A — CommentService/ExportImport)*
- ☐ **T14 · M10 — Batch import `SaveChanges`** every ~200 rows. *(WS-C Opus)*
- ☐ **T15 · M12 — Async `Me()`.** *(WS-A — AuthService)*
- ☐ **T16 · M13 — `ToActionResult` extension** + fix `Forbid()` body-discard divergence. *(WS-B GLM)*
- ☐ **T17 · M3 — Host reflection → `Pointer:PublicUrl`** at all 5 sites + pin `AllowedHosts`. *(WS-B GLM)*
- ☐ **T18 · M5 — Upload magic-byte sniff + `X-Content-Type-Options: nosniff`.** *(WS-B GLM)*
- ☐ **T19 · M8 — `MaxPoolSize`** in the Npgsql connection string. *(WS-B GLM)*
- ☐ **T20 · M4 — Demo throttle** → atomic upsert + TTL cleanup (move off `app_settings`). *(WS-A/later)*
- ☐ **T21 · M11 — `IMemoryCache`** for branding/settings/project-key/plan catalog. *(WS-A/later — larger)*
- ☐ **T22 · M14 — `ExecuteDeleteAsync`** in `TenantService.HardDeleteAsync`. *(later)*

## Phase 4 — Low

- ☐ **T23 · L5 — `PreferencesService` → `UserMapper.ToMeResponse`.** *(WS-B GLM)*
- ☐ **T24 · L1 — `LocalFileStorage` separator guard.** *(WS-B GLM)*
- ☐ **T25 · L2 — Global exception handler / ProblemDetails.** *(WS-B GLM)*
- ☐ **T26 · L3/L6/L7/L8/L9/L10** — hygiene (weak example creds guard, logging, migrate-on-boot doc,
  `AllowedHosts`, stats rollup, catalog reflection). *(batch later)*

## Integration & verification

- ☐ **T27** — Integrate WS-B/WS-C diffs into main tree (file-disjoint → clean apply).
- ☐ **T28** — Fable generates the single EF migration (indexes + `SecurityStamp`); hand-add the C1 back-fill
  SQL template (guarded, not auto-run).
- ☐ **T29** — `dotnet build` + `dotnet test` (must stay green, 122+).
- ☐ **T30** — Opus review over the full integrated diff; reconcile.
- ☐ **T31** — Update this plan's statuses; leave everything uncommitted for the user.

### This session executes
Phase 1 (T1–T3, T5; T4 scaffold), the GLM batch (T16–T19, T23–T25), the Opus batch (T9, T14),
plus T8/T15 and the migration (T12/T28). Remaining items are tracked for follow-up.

---

## Session 1 — results (2026-07-05)

**Build:** green · **Tests:** 124 passing (122 original + 2 new C1 regression tests) · **Committed:** no
(one uncommitted working-tree diff + branch `fix/glm-mechanical` was integrated then removed).

**☑ Done & verified**
- **C1 (Fable)** — config-flagged strict-null-tenant filter in `AppDbContext` (default OFF = identical to
  prior behavior; ON closes the collapse) + `IModelCacheKeyFactory` so the flag varies the model cache.
  Regression tests pin both modes. Back-fill **runbook** delivered: `docs/reviews/fable-c1-backfill-runbook.md`.
- **M15 (Fable)** — anonymous register + public-roles refuse/ignore an ambiguous (colliding) project key.
- **C1e (Fable)** — `SuggestionService` refuses to write a null-owner suggestion (strict-own invariant).
- **H4 + M12 (Fable)** — sargable email lookups (8 sites) + async `Me()`.
- **H5 + M10 (Opus)** — export keyset-paginated + capped (5000); import batched `SaveChanges` +
  `ChangeTracker.Clear()` (new `IUnitOfWork.ClearChangeTracker`). Exercised by the SQLite-backed test.
- **M13/M3/M5/M8/L1/L5/L2 (GLM)** — `ToActionResult` + `Forbid()` body fix; host-reflection→`Pointer:PublicUrl`;
  upload magic-byte sniff + `nosniff`; `MaxPoolSize=40`; storage separator guard; `PreferencesService`
  uses `UserMapper`; global exception handler.
- **M6/M7 (Fable)** — migration `20260705041708_AddCommentPerfIndexes`: partial `(project_id, created_at)`
  and `(owner_id, created_at)` indexes `WHERE deleted_at IS NULL`.

**⏸ Deferred (tracked, follow-up)**
- **C1 data step (T4)** — the actual `owner_id` back-fill + flag flip is operator-run per the runbook
  (needs live data; cannot be blind-migrated).
- **H1/H2 (T6)** — ✅ IMPLEMENTED (session 2), flag-gated `Auth:ValidateSecurityStamp` (default OFF),
  Opus-reviewed (no lockout; TenantService-disable gap + OnTokenValidated fail-open fixed). `User.SecurityStamp`
  rotates on password-reset/disable/reject/demo-upgrade; embedded in the JWT `stamp` claim + reset-token
  payload (single-use). Migration `20260705173225_AddUserSecurityStamp`. **Deployed flag-OFF** (commit
  `93809c6`): migration applied, all 6 users back-filled with distinct stamps, `Auth:ValidateSecurityStamp`
  unset (dormant, zero behavior change), API healthy. **To finish:** set `Auth__ValidateSecurityStamp=true`
  in prod compose + redeploy — this forces a one-time re-login of everyone and needs a login smoke test.
- **H6 (T10)** — slim list DTOs: breaking change for the Orval-generated dashboard client; coordinate a regen.
- **M9** — `AsSplitQuery()` isn't available in the Application layer (relational-only extension); keyset
  batching already bounds export memory. Would need a package reference.
- **M4, M11, M14, and the L-tier hygiene batch** — not started; tracked in Phases 3–4 above.

**Opus review reconciliation** — an adversarial Opus pass over the integrated diff confirmed C1/M15/H4,
the export rework, and the GLM batch behavior-preserving, and found two issues, both fixed:
- **Import atomicity (medium):** the M10 batched `SaveChanges` had dropped the original all-or-nothing
  guarantee. Fixed — both import methods now run inserts inside `ExecuteInTransactionAsync` (validation
  is a read-only pre-pass; inserts commit atomically). Verified against the SQLite-backed test.
- **Npgsql pool guard (low):** the "already set?" check missed the `Maximum Pool Size` spelling. Fixed
  with a whitespace-stripped check covering all three spellings.
