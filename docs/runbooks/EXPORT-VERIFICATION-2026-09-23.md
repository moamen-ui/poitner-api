# Export/import round-trip verification — 2026-09-23

Live run of the procedure in
[`R5-66-export-and-dsar-runbook.md`](../roadmap/execution/R5-66-export-and-dsar-runbook.md) §3.1,
against a **throwaway copy** of a production dump — never the shared dev database
(`pointer`/`pointer_rehearsal`).

## Environment

- Restored `pointer_export_check` (owner `pointer`) from
  `/Users/momen/.claude/jobs/571920b7/tmp/prod.dump` via
  `pg_restore -U pointer -d pointer_export_check --no-owner --no-privileges` into the same
  Postgres container the dev stack uses (`localhost:5433`). 27 tables restored; baseline counts at
  restore time: 122 comments, 7 users, 27 projects.
- API run from the `feat/r5-66` worktree (`/Users/momen/Desktop/REPOS/pointer-r5-66`), built with
  `dotnet build` (0 errors), started with:
  ```
  ASPNETCORE_URLS=http://localhost:8097 \
  ConnectionStrings__Default="Host=localhost;Port=5433;Database=pointer_export_check;Username=pointer;Password=pointer" \
  DBMigrationEnabled=true DBApplyContractMigrations=true \
  JWT__SigningKey=0123456789abcdef0123456789abcdef0123456789abcdef JWT__Issuer=pointer-api \
  ADMIN__EMAIL=admin@pointer.local ADMIN__PASSWORD='ChangeMe123!' \
  dotnet run --project API --no-build --no-launch-profile
  ```
  Two notes for whoever re-runs this:
  - `dotnet run` ignores `ASPNETCORE_URLS` unless `--no-launch-profile` is passed (the project's
    `launchSettings.json` `applicationUrl` otherwise wins and the API listens on `:5174` instead).
  - `JWT__Issuer` must be set explicitly (`pointer-api`) — `AuthenticationExtensions.cs` reads
    `config["JWT:Issuer"]` directly (not through `IOptions<JwtOptions>`), so the class's C# default
    of `"pointer-api"` does **not** apply when the key is simply absent from configuration; leaving
    it unset produces a token whose issuer/audience don't match `ValidIssuer`/`ValidAudience` and
    every authenticated call 401s with `error_description="The audience 'pointer-api' is invalid"`.
- The seeded super admin (`admin@pointer.local` / `ChangeMe123!`, per `API/Seed/AdminSeeder.cs`) has
  no tenant (`OwnerId` null) and so cannot itself hold exportable comments. Used it only to approve
  a throwaway tenant signup (`scoped_admin_signup_enabled` was already `true` in the restored data,
  confirmed via `app_settings`); registered a fresh disposable Workspace Admin
  (`r566-export-check@pointer.local`) via `POST /api/auth/register-admin`, approved with
  `POST /api/admin/users/{id}/approve {"roleId":6}` (global "Workspace Admin" role id), and used
  that account for the actual export/import calls — never touching any real production tenant's
  data. Two disposable projects were created under it: `r566-export-a` (id 76), `r566-export-b`
  (id 77).

## Procedure and results

1. **Seed** — created 2 comments in `r566-export-a` (one public/Local, one private/Staging), each
   with one reply (via `POST /api/projects/{key}/comments` and `POST /api/comments/{id}/replies`).
2. **Export A** — `GET /api/projects/r566-export-a/export`:
   `schema_version = "1.0"`, `comments` length **2**, both with `replies` length **1**. Field names
   confirmed snake_case (`export_id`, `project_key`, `is_private`, `created_at`, …) exactly as
   `CommentExportDto`/`ExportFileDto` document.
3. **Import into a second disposable project** — `POST /api/projects/r566-export-b/import` with
   export A's file: `{"importedComments":2,"importedReplies":2,"skippedDuplicates":0,"warnings":[]}`
   — **200 OK**, no warnings (no screenshots were present to warn about).
4. **Re-export from B** — `GET /api/projects/r566-export-b/export`: 2 comments, 2 replies.
5. **Diff** (`jq '.comments | map({body, status, environment})'` on both, per the doc's exact
   procedure):
   ```diff
   <     "body": "First round-trip comment",
   ---
   >     "body": "First round-trip comment\n\n*(Imported — originally by: R566 Export Check)*",
   <     "body": "Second round-trip comment, private",
   ---
   >     "body": "Second round-trip comment, private\n\n*(Imported — originally by: R566 Export Check)*",
   ```
   Only difference is the documented attribution footnote (`ExportImportService.AppendAttribution`,
   `ExportImportService.cs:438-441`) appended to `body` on import — expected, since import
   re-attributes authorship to the importer (plan §4.3) and preserves the original author only as
   readable text. `status` and `environment` are byte-identical (`Open`/`Open`, `Local`/`Local`,
   `Staging`/`Staging`). `is_private`, `created_at`, and the full `element` block (selector,
   snapshot, `page_url`, `route`, `screenshot_omitted: false`) were also verified identical
   side-by-side in the full export JSON (not just the diffed subset).
6. **Workspace-level export** — `GET /api/export` returned **4** comments total across
   `["r566-export-a","r566-export-b"]` — confirms `ExportWorkspaceAsync` includes every project the
   tenant owns (no project filter), matching `ExportImportService.cs:63-66`.
7. **Workspace-level bulk import** — `POST /api/import` with export B's file (routes each comment
   by its own `project_key`): `{"importedComments":2,"importedReplies":2,"skippedDuplicates":0,"warnings":[]}`
   — confirmed the `project_key`-routing path also works, not just the single-project import.
8. **Row-count sanity check** (direct SQL against the throwaway DB): 6 comments + 6 replies under
   projects 76/77 (2 seeded + 2 project-level import + 2 workspace-level import = 6, matching
   exactly).
9. **`dotnet test --filter ExportImportServiceTests`**: **Passed! 4/4, 0 failed, 0 skipped.**

## Result: PASS

The round trip is mechanically sound end-to-end — auth, tenant scoping (both disposable projects
lived under the fresh tenant only), re-attribution footnote, `created_at` preservation
(`PreserveCreatedAtOnInsert`), both single-project and workspace-level export/import paths, and the
existing automated test suite. No regressions found.

## Gaps confirmed (static — no live run needed to confirm an absent DTO property; restated from R5-66 §3.1)

1. **Custom fields (R4-01) not exported** — `Comment.CustomFields` has no counterpart on
   `CommentExportDto`.
2. **Page context snapshots not exported** — `Comment.PageContextSnapshotId`/`PageContextSnapshot`
   has no counterpart on the DTO.
3. **Predefined-action associations not exported** — `Comment.PickedActions` has no counterpart on
   the DTO.
4. **Screenshots never transferred** — by design (plan §3.3); confirmed live: `screenshot_omitted`
   was `false` for both test comments (correct — neither had a screenshot) and the field is present
   on every comment.

These are unchanged from the doc's static analysis; this run additionally exercises the mechanical
path (auth, routing, persistence, attribution) the DTO-shape analysis alone can't confirm. See
[`../runbooks/DSAR.md`](DSAR.md) "Follow-up code items" for exact file:line detail and proposed
fixes — none implemented here (out of scope; `Application/`, `Infrastructure/`, `Domain/` are owned
by DB-11a work in progress).

## Cleanup performed

- API process stopped.
- `DROP DATABASE pointer_export_check;` — confirmed gone via `\l`; `pointer`, `pointer_rehearsal`,
  and `pointer_batch` were not touched at any point during this run.
