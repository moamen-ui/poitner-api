# R5-66 — Export verification + DSAR runbook (§66 · Release 5 · 1 d)

## 1. Goal

Verify that the existing `ExportImportService` round-trips correctly on the current schema, document
any gaps, and produce `docs/runbooks/DSAR.md` — a step-by-step Data Subject Access Request
runbook covering request intake, identity verification, export, erasure, and logging.

Effort: 1 d.

## 2. Prerequisites (verified facts)

- **ExportImportService**: `Application/Services/Implementation/ExportImportService.cs:13-608` —
  implements `IExportImportService` at `Application/Services/Interfaces/IExportImportService.cs:6`.
  Methods: `ExportProjectAsync`, `ExportWorkspaceAsync`, `ImportProjectAsync`, `ImportWorkspaceAsync`.
- **Controller**: `API/Controllers/ExportImportController.cs:15` (no class-level `[Tags]` — falls
  back to Swashbuckle's default controller-name tag `"ExportImport"`, already in `orval.config.ts`
  `filters.tags`) — endpoints (route parameter is `{key}`, not `{projectKey}`):
  - `GET /api/projects/{key}/export`
  - `GET /api/export` (workspace-level)
  - `POST /api/projects/{key}/import`
  - `POST /api/import` (workspace-level)
- **Tests**: `Tests/ExportImportServiceTests.cs:22` — existing round-trip tests.
- **Entitlement**: `Domain/ValueObjects/PlanEntitlements.cs:33` `ExportImportEnabled`;
  `Application/Common/EntitlementCatalog.cs:78` declares it `enforced: false, def: false` — it is
  **not currently checked anywhere** (`grep -rn ExportImportEnabled API/ Application/` outside the
  catalog itself returns nothing; `EntitlementCatalog.Enforced` only yields specs with
  `enforced: true`, and this one isn't). The endpoints today are gated **only** by `[Authorize]`
  (export) / `[Authorize(Policy = Policies.Admin)]` (import) — any authenticated tenant user can
  export, any tenant admin can import, regardless of plan. Do not write a "first enable the
  entitlement" step into the verification procedure below; there is nothing to enable.
- **Screenshot storage**: comment screenshots in the compose `uploads` volume
  (`docker-compose.prod.yml:60`), not in Postgres. Export includes comment data from DB; the
  uploads tarball from `backup-db.sh` includes screenshots.
- **Retention (DB-08)**: usage events 180 d, read notifications 90 d, page context snapshots 30 d,
  dead invites 90 d (`docker-compose.prod.yml:40-43`).
- **F5 (screenshots)**: founder decision is "keep with the comment; delete only with the comment or
  workspace" — but **no code currently deletes a screenshot file when its comment is soft-deleted**
  (`CommentService.DeleteAsync` only sets `DeletedAt`; see §3.2.D.1.3). Treat the manual `find …
  -delete` step there as required today, not merely a stopgap.
- **Audit log**: no `audit_events` table exists in the schema **today**. `docs/db/execution/DB-12-audit-log.md`
  is **written 2026-09-22; not implemented** — it will add `audit_events` + `IAuditWriter`, but until
  it ships there is nothing to log DSAR actions into. **Fallback**: a runbook table manually tracking
  DSAR requests (§3.2.F below).
- **Soft delete / DB-11c**: comments are soft-deleted only (`Comment` extends `BaseEntity`, which
  has `DeletedAt`/`DeletedBy`). `docs/db/execution/DB-11c-deletion-semantics-remove-disable-erase.md`
  is **written 2026-09-22; not implemented**, and importantly it does **not** add a comment
  hard-delete — DB-11c's "erase" tombstones the **identity** (`users.email` →
  `erased+<id>@tombstone.invalid`, name → "Deleted user", secrets cleared) while, by founder
  decision **F5**, **comments/replies/screenshots explicitly survive erase**, re-attributed to the
  tombstone. So DB-11c never supersedes §3.2.D.1's manual comment soft-delete for a request that
  wants comment *content* gone — it only ever handles a request to disassociate the person's
  *identity* from their prior activity. Treat these as two different DSAR outcomes (see §3.2.D).
- **PDPL timeline**: 30 days to respond. GDPR: 30 days (extendable to 90).
- **User entity**: `Domain/Entity/User.cs` — `Email`, `DisplayName`, `OwnerId` (tenant, `Guid?`).
  Soft-delete via `DeletedAt`. `SecurityStamp` (Guid) rotated on password change. **`users.id` is an
  `int` PK, but every foreign reference used below (`comments.author_id`, `usage_events.user_id`,
  `notifications.user_id`) is a `Guid` column pointing at `users.public_id`, not at `users.id`**
  (`Comment.AuthorId`/`UsageEvent.UserId`/`Notification.UserId` are all typed `Guid`/`Guid?`). Every
  SQL snippet below uses `<user_public_id>` (quoted, a UUID) accordingly — do not substitute the
  int `id`.

## 3. Design

### 3.1 Export verification

The existing `ExportImportService` round-trip should be verified with the following command
sequence (run in the e2e or local-dev environment):

```bash
# 1. Start the stack (README.md: local API is http://localhost:8090, not 8080)
just up
# 2. Seed data — use dedicated/disposable project keys, NOT e2e-alpha/e2e-beta: those are the
#    shared AI-facing ground truth for the rest of the e2e suite (docs/E2E_TEST_PLAN.md,
#    e2e/widget/lib/ensure-project.ts) and importing into them would corrupt other specs' state.
cd e2e && node scripts/seed.mjs && cd ..
# 3. Export a project (replace <project> with your disposable test project key)
curl -s -H "Authorization: Bearer $TOKEN" "http://localhost:8090/api/projects/<project>/export" > export-a.json
# 4. Inspect the export (should contain comments, replies, project metadata)
jq '.schema_version, .comments | length' export-a.json
# 5. Import into a second, fresh disposable project
curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d @export-a.json "http://localhost:8090/api/projects/<project-2>/import"
# 6. Re-export from the target and compare
curl -s -H "Authorization: Bearer $TOKEN" "http://localhost:8090/api/projects/<project-2>/export" > export-b.json
# 7. Compare (ignoring IDs and timestamps)
jq '.comments | map({body, status, environment})' export-a.json > a.json
jq '.comments | map({body, status, environment})' export-b.json > b.json
diff a.json b.json
```

(Field is `schema_version`, snake_case — `CommentExportDto`/`ExportFileDto` in
`Application/DTOs/Export/` serialize with `[JsonPropertyName]` snake_case throughout, not camelCase.)

**Known gaps — confirmed by reading `Application/DTOs/Export/CommentExportDto.cs` and
`Domain/Entity/Comment.cs` (no round-trip needed to answer these; the DTO simply has no property
for them):**
- **Custom fields (R4-01) — NOT exported.** `Comment.CustomFields` (`Comment.cs:79`, jsonb) has no
  counterpart on `CommentExportDto`. A round-trip silently drops every admin-defined field value.
- **Replies — exported.** `CommentExportDto.Replies` (`CommentExportDto.cs:57-58`) is populated.
- **Page context snapshots — NOT exported.** `Comment.PageContextSnapshotId`/`PageContextSnapshot`
  (`Comment.cs:49-50`) have no counterpart on `CommentExportDto`.
- **Predefined-action associations — NOT exported.** `Comment.PickedActions` (`Comment.cs:56`,
  the `{text, prompt}` snapshot list) has no counterpart on `CommentExportDto`.
- **Workspace-level export includes all projects — confirmed.** `ExportWorkspaceAsync` calls
  `BuildExportFileAsync(options, projectId: null, ...)` (`ExportImportService.cs:63-66`), i.e. no
  project filter, scoped only by the tenant global query filter.
- Screenshots are referenced but not included in the JSON export (expected — file-system artifacts,
  not DB rows; confirmed by `CommentExportDto` carrying no screenshot bytes/URL other than
  `element.screenshot_url`, which is a path, not the file).

These three gaps (custom fields, page-context snapshots, predefined-action associations) are real
and should be filed as a follow-up if the export is ever relied on for full fidelity (e.g. a future
account migration or a DSAR that must include everything). For now, run the bash procedure above
once anyway to confirm the round-trip mechanically works end-to-end (schema version, comment count,
body/status/environment fidelity) — the DTO-shape gaps above don't need a live run to confirm, but
the round-trip itself (auth, tenant scoping, re-attribution, timestamps) does.

### 3.2 DSAR runbook (`docs/runbooks/DSAR.md`)

Content:

#### A. Request intake
- Requests arrive via email to `moamen.ui@gmail.com` (or a dedicated `privacy@` address if set up).
- Record the request in the DSAR log table below (until DB-12 audit log is available).
- Acknowledge receipt within 3 business days.

#### B. Identity verification
- Request the data subject to confirm from the email address on their account.
- If the request comes from a different address, ask for account verification (e.g. screenshot of
  their profile page, or a reply from the registered email).

#### C. Access request (data export)
1. Identify the user by email: `SELECT id, public_id, email, display_name, owner_id FROM users WHERE email = '<email>';`
   — note the public_id (Guid); that is what every query below actually filters on, not `id`.
2. Any authenticated user can self-serve their own workspace's export today: `GET /api/export`
   (the `ExportImportEnabled` entitlement exists but is **not enforced** — see Prerequisites — so
   there is no plan check to satisfy first; the only gate is `[Authorize]`).
3. Otherwise, the super-admin exports on their behalf:
   ```bash
   curl -s -H "Authorization: Bearer $ADMIN_TOKEN" "https://api.pointer.moamen.work/api/export" > dsar-export.json
   ```
   (Workspace-level export includes all projects the user's tenant owns — confirmed, §3.1.)
4. Screenshots: provide the uploads tarball from the latest backup if the data subject requests
   their screenshots. Filter by the comment IDs in the export.
5. Additional data not in the export JSON:
   - User preferences (language, theme, shortcut) — query `users` table.
   - Usage events — query `usage_events WHERE user_id = '<user_public_id>'` (retained 180 d).
   - Notifications — query `notifications WHERE user_id = '<user_public_id>'` (retained 90 d).

#### D. Erasure request

Two different asks — handle whichever the data subject actually made:

- **"Delete my identity / stop attributing things to me"** → once DB-11c ships, its erase endpoints
  (`DELETE /api/me` password-confirmed, or the magic-link flow, or `DELETE /api/admin/identities/{publicId}`
  for a super admin) tombstone the identity (email/name replaced, secrets cleared). **This does
  NOT remove comment content** — by founder decision F5, comments/replies/screenshots explicitly
  survive erase, re-attributed to the tombstone. Until DB-11c ships, there is no automated identity
  erase at all; do the equivalent by hand (rotate `SecurityStamp`, null `PasswordHash`, edit
  `Email`/`DisplayName`) or escalate.
- **"Delete the content I posted"** → always a manual step, DB-11c or not (DB-11c never deletes
  comment content — see above):
  1. **Comments**: soft-delete all comments by the user:
     ```sql
     UPDATE comments SET deleted_at = NOW() WHERE author_id = '<user_public_id>' AND deleted_at IS NULL;
     ```
  2. **Replies**: soft-delete replies by the user (same pattern, `replies.author_id`).
  3. **Screenshots (F5)**: **soft-deleting a comment does NOT delete its screenshot file** —
     `CommentService.DeleteAsync` (`Application/Services/Implementation/CommentService.cs:1040-1057`)
     only sets `DeletedAt`; it never calls `IFileStorage.DeleteAsync`. If the data subject's request
     includes screenshot removal, manually remove the files from the `uploads` volume:
     ```bash
     docker compose exec api find /app/wwwroot/uploads -name '<comment_public_id>*' -delete
     ```
     This manual step is required today regardless of DB-11c/DB-12 status — no code path currently
     deletes screenshot files on comment deletion.
  4. **User account**: soft-delete the user row (`DeletedAt`) and rotate `SecurityStamp` to
     invalidate outstanding tokens (`Auth__ValidateSecurityStamp` is `true` in production, so this
     takes effect on the next request rather than waiting for JWT expiry).
  5. **Invites**: scrub the user's email from `invites` rows (no unique index on `invites.email`,
     so this is safe to run for multiple rows):
     ```sql
     UPDATE invites SET email = 'redacted@deleted' WHERE email = '<email>';
     ```
  6. **What survives**: the tombstoned user row (`id`, `deleted_at`, scrubbed identifiers) for
     referential integrity, and anonymised aggregate usage facts (e.g. `first_comment_at` in
     `usage_events`).

#### E. Timeline
- **PDPL**: respond within 30 days.
- **GDPR**: respond within 30 days (extendable to 90 with notification).

#### F. Logging
`docs/db/execution/DB-12-audit-log.md` is written but not implemented — until its `audit_events`
table and `IAuditWriter` ship, maintain a manual log:

| # | Date | Subject email | Type | Status | Completed | Notes |
|---|---|---|---|---|---|---|
| 1 | | | Access / Erasure | Received / In-progress / Done | | |

Once DB-12 ships, its own action catalog (`docs/db/execution/DB-12-audit-log.md` §3.6) already
reserves `export.downloaded` (both export GETs) and `import.completed` (both import POSTs) — a
DSAR access request that used self-serve export, or the admin-on-behalf-of path, is then already
covered without inventing a new action string. There is no equivalent reserved action for the
manual erasure steps in §3.2.D (they're raw SQL, not one of the audited controller actions) — those
still need the manual log table above even after DB-12 ships, unless a later doc adds an actual
erasure endpoint that DB-12 (or a follow-up) instruments.

## 4. Safety / impact

**No code change.** The verification is read-only (export + import in a test environment). The
runbook is a document. The only change is the new `docs/runbooks/DSAR.md` file.

## 5. File-level tasks

1. **`docs/runbooks/DSAR.md`** (new) — per §3.2.
2. **`docs/roadmap/execution/R5-66-export-and-dsar-runbook.md`** — this document. The static gap
   analysis (custom fields, page-context snapshots, predefined-action associations, replies, all-
   projects) is already recorded in §3.1. Append the *live* round-trip run's result (pass/fail,
   any diff output) as a short "Results" note under §3.1 after running the bash procedure once.

## 6. Tests

Existing `Tests/ExportImportServiceTests.cs` covers the round-trip. No new automated test.
The verification in §3.1 is a manual procedure run once.

## 7. Acceptance criteria

1. `docs/runbooks/DSAR.md` exists.
2. `grep -c 'PDPL' docs/runbooks/DSAR.md` → ≥1.
3. `grep -c 'GDPR' docs/runbooks/DSAR.md` → ≥1.
4. `grep -c '30 days' docs/runbooks/DSAR.md` → ≥1.
5. The runbook covers: intake, identity verification, access export, erasure, invites scrub,
   timeline, logging.
6. The export verification procedure in §3.1 has been run at least once and results documented.
7. `dotnet test --filter ExportImportServiceTests` → green (existing tests still pass).
8. Any gaps found in the export are filed as issues or noted in this doc.

## 8. Rollback

Delete `docs/runbooks/DSAR.md`. No code, no migration.

## 9. Release steps

1. Merge PR.
2. Run the export verification (§3.1) on the local-dev or staging environment.
3. Document results.
4. The runbook is live in the repo for the operator to follow on any DSAR.

## 10. Out of scope

Automated DSAR handling (self-serve deletion portal), implementing DB-11c's identity-erase
endpoints, implementing DB-12's audit log, a comment/screenshot hard-delete endpoint (none exists
today, and DB-11c does not add one — it only tombstones the identity, per F5), DPA generation,
automated PII discovery, data mapping tool, retention policy changes, right-to-portability
machine-readable format (the JSON export is already machine-readable), fixing the three export
gaps found in §3.1 (custom fields, page-context snapshots, predefined-action associations — filed
as a follow-up, not fixed here).

## 11. Dependencies

- **DB-11c** (`docs/db/execution/DB-11c-deletion-semantics-remove-disable-erase.md`, written
  2026-09-22, not implemented): once merged, its erase endpoints handle the "erase my identity"
  DSAR outcome (§3.2.D). It does **not** provide comment/screenshot hard-delete — the "delete my
  content" outcome in §3.2.D remains a manual step independent of DB-11c's status.
- **DB-12** (`docs/db/execution/DB-12-audit-log.md`, written 2026-09-22, not implemented): the
  logging section (§3.2.F) provides a manual fallback table until DB-12 ships; once it does,
  `export.downloaded`/`import.completed` (DB-12 §3.6) already cover the access-request path.
- Neither dependency blocks this doc: the export verification (§3.1) and the runbook (§3.2) are
  both usable today, against the current schema and the current (soft-delete-only) erasure path.
