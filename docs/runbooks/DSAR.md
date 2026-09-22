# DSAR runbook — Data Subject Access / Erasure Requests

Operator runbook for handling a Data Subject Access Request (DSAR) — an access request or an
erasure request — under the **PDPL** (Saudi Personal Data Protection Law) and, where applicable,
the **GDPR**. Written for [R5-66](../roadmap/execution/R5-66-export-and-dsar-runbook.md); see that
doc for the underlying research and the live round-trip verification results
([`EXPORT-VERIFICATION-2026-09-23.md`](EXPORT-VERIFICATION-2026-09-23.md)).

**Ground truth used throughout this runbook** (verified 2026-09-23, current schema/code):
- Local API is `http://localhost:8090` (not 8080); production is `https://api.pointer.moamen.work`.
- The `ExportImportEnabled` plan entitlement exists (`Domain/ValueObjects/PlanEntitlements.cs:33`)
  but is **not enforced** anywhere (`Application/Common/EntitlementCatalog.cs:78`,
  `enforced: false`) — export/import today are gated only by `[Authorize]` (export) /
  `[Authorize(Policy = Policies.Admin)]` (import). Do not tell a data subject or an admin they need
  to "enable exports first" — there is nothing to enable.
- `users.id` is an `int` PK; every foreign column referenced below (`comments.author_id`,
  `replies.author_id`, `usage_events.user_id`, `notifications.user_id`) is a **Guid** pointing at
  `users.public_id`, never at the int `id`. Every SQL snippet below filters on
  `<user_public_id>` (a UUID), not the int id.
- `docs/db/execution/DB-11c-deletion-semantics-remove-disable-erase.md` (identity erase/tombstone)
  and `docs/db/execution/DB-12-audit-log.md` (`audit_events` + `IAuditWriter`) are both **written,
  not implemented**, as of 2026-09-22/23. Nothing below depends on either shipping — this runbook
  works against the codebase exactly as it exists today (soft-delete only; no audit table).
- Screenshots live in the `uploads` volume (filesystem), not in Postgres
  (`docker-compose.prod.yml:60`). The export JSON never carries screenshot bytes — it carries
  `element.screenshot_url = null` and `element.screenshot_omitted = true|false`
  (`Application/DTOs/Export/CommentExportDto.cs`, `ExportImportService.MapElementForExport`).

---

## A. Request intake

1. Requests arrive by email to `moamen.ui@gmail.com` (or a dedicated `privacy@` address once one
   exists).
2. Log the request immediately in the table in §F, even before identity is verified — the clock
   for the response timeline (§E) starts on receipt.
3. Acknowledge receipt to the data subject within **3 business days**, stating the expected
   completion date (see §E).
4. Classify the request as one or both of:
   - **Access** — "send me my data" (§C).
   - **Erasure** — "delete my data", split further into the two outcomes in §D (identity vs.
     content) — ask the requester which they mean if it is not clear from their message, since the
     two outcomes differ substantially (§D).

## B. Identity verification

1. Preferred: the request comes from the email address on the account (`users.email`). This is
   sufficient identity verification for a self-serve request.
2. If the request comes from a different address (e.g. a personal email for a work account, or the
   account's registered address is unreachable):
   - Ask for a reply from the registered email, **or**
   - Ask for a screenshot of the requester's own profile/account page showing their display name
     and workspace, **or**
   - For a request routed through a workspace admin acting on a member's behalf, verify the
     requester is that admin (dashboard login) before granting access to another member's data.
3. Never action an erasure or export request from an unverified identity. When in doubt, escalate
   rather than guess.

## C. Access request (data export)

1. Identify the account:
   ```sql
   SELECT id, public_id, email, display_name, owner_id
   FROM users
   WHERE lower(email) = lower('<email>');
   ```
   Note the `public_id` (a UUID) — every query below filters on it, never on the int `id`.

2. **Self-serve is already live and requires no special grant.** Any authenticated user can export
   their own workspace today:
   ```
   GET /api/export
   ```
   (`API/Controllers/ExportImportController.cs:53-68`, `[Authorize]` only — no entitlement check,
   see the ground-truth note above). Point the data subject at this endpoint (or the dashboard
   export button, if the React app wires one) as the first option — it is faster than an
   admin-on-behalf-of export and needs no additional identity verification beyond their own login.

3. If the data subject cannot self-serve (account disabled, they no longer have access, or they
   asked the operator directly), an operator with the super-admin token exports on their behalf:
   ```bash
   curl -s -H "Authorization: Bearer $ADMIN_TOKEN" \
     "https://api.pointer.moamen.work/api/export" > dsar-export-<public_id>.json
   ```
   This is the **workspace-level** export — it includes every project the person's tenant owns,
   confirmed by reading `ExportImportService.ExportWorkspaceAsync` (`ExportImportService.cs:63-66`,
   `BuildExportFileAsync(options, projectId: null, ...)` — no project filter, only the tenant's own
   `OwnerId`). If only one project is relevant, use
   `GET /api/projects/{key}/export` instead to scope the download.

4. **What's in the export JSON** (verified by reading `Application/DTOs/Export/CommentExportDto.cs`
   and confirmed live in [`EXPORT-VERIFICATION-2026-09-23.md`](EXPORT-VERIFICATION-2026-09-23.md)):
   comment body, environment, status, privacy flag, timestamps (created/applied/edited), author
   display name, the captured element (selector, snapshot, computed styles, page URL/route,
   viewport, device), and replies (body, author display name, timestamp).

   **What's NOT in the export JSON** (confirmed by reading the DTO — no round-trip needed to
   confirm an absent property):
   - **Custom fields** (`comments.custom_fields`, R4-01) — dropped silently.
   - **Page context snapshots** (`comments.page_context_snapshot_id`) — dropped silently.
   - **Predefined-action associations** (`comments.picked_actions`) — dropped silently.
   - **Screenshot bytes** — by design (`element.screenshot_url` is always `null`;
     `element.screenshot_omitted` flags that one existed). See point 5.

   If a DSAR access request must be complete (not just "the export file"), pull these separately:
   ```sql
   SELECT custom_fields, page_context_snapshot_id, picked_actions
   FROM comments
   WHERE author_id = '<user_public_id>' AND deleted_at IS NULL;
   ```

5. **Screenshots**: not included in the JSON (they're files, not DB rows). If the data subject asks
   for their screenshots specifically, pull them from the latest `uploads` backup tarball, filtered
   by the comment IDs in their export (screenshot paths are
   `wwwroot/uploads/<ownerSeg>/<project>/<file>`, referenced from each comment's
   `element.screenshot_url` before it was nulled for export — resolve the *live* URL from the DB,
   not the export file, since the export always nulls it).

6. **Additional data not in the export JSON at all** — query directly:
   ```sql
   -- User profile / preferences (language, theme, shortcut)
   SELECT email, display_name, language, theme, add_comment_shortcut FROM users WHERE public_id = '<user_public_id>';

   -- Usage events (retained 180 days — DB-08)
   SELECT * FROM usage_events WHERE user_id = '<user_public_id>' ORDER BY created_at DESC;

   -- Notifications (retained 90 days for read ones — DB-08)
   SELECT * FROM notifications WHERE user_id = '<user_public_id>' ORDER BY created_at DESC;
   ```

## D. Erasure request

There are **two different asks**. Ask the data subject which one they mean (or infer from context)
before acting — they are not interchangeable, and neither one is fully automated today.

### D.1 "Delete my identity / stop attributing things to me"

`docs/db/execution/DB-11c-deletion-semantics-remove-disable-erase.md` designs an automated erase
endpoint (`DELETE /api/me`, `DELETE /api/admin/identities/{publicId}`, or the magic-link
confirmation flow) that tombstones the identity — but **it is written, not implemented**, as of
2026-09-23. Two important facts about it once it does ship, both worth knowing now because they
shape what "erase" will and will not mean for this product:

- It tombstones the **identity only**: `email` → `erased+<id>@tombstone.invalid`, `display_name` →
  `"Deleted user"`, secrets cleared. It does **not** delete comment/reply content.
- By founder decision **F5**, comments, replies, **and their screenshots explicitly survive
  erase**, re-attributed to the tombstone — the screenshot depicts the *customer's application*,
  not the person, so it is treated as the workspace's asset, like the comment text.

Until DB-11c ships, do the equivalent by hand, inside a single transaction, for the identity's
`public_id`:
```sql
UPDATE users
SET email = 'erased+' || public_id || '@tombstone.invalid',
    display_name = 'Deleted user',
    password_hash = NULL,
    security_stamp = gen_random_uuid(),
    is_active = false,
    deleted_at = NOW()
WHERE public_id = '<user_public_id>';
```
Also revoke that person's credentials — API keys, quick-access links, and any pending device logins
— and scrub their address out of `invites.email` (§D.3). Do **not** null `invites.email` — a null
email on an invite means "anyone with the link may accept" (`Invite.cs:37`), so nulling would
*unlock* a still-open invite instead of locking the person out of it; overwrite with a tombstone
value instead, exactly as the automated DB-11c design does.

### D.2 "Delete the content I posted"

Always a manual step today, and will remain manual even after DB-11c ships (DB-11c never deletes
comment content — see D.1):

1. **Comments** — soft-delete every comment by the person:
   ```sql
   UPDATE comments SET deleted_at = NOW() WHERE author_id = '<user_public_id>' AND deleted_at IS NULL;
   ```
2. **Replies** — same pattern on `replies.author_id`.
3. **Screenshots — read this carefully, it does not happen automatically.** Soft-deleting a comment
   does **not** delete its screenshot file. Confirmed by reading
   `Application/Services/Implementation/CommentService.cs:1040-1057` (`DeleteAsync`): it sets
   `comment.DeletedAt` and saves — it never calls `IFileStorage.DeleteAsync`. (The only place this
   codebase calls `_fileStorage.DeleteAsync` on a comment's screenshot is `EditAsync`,
   `CommentService.cs:816-824`, when the author explicitly ticks "remove screenshot" on an edit —
   an unrelated code path.) If the request includes screenshot removal, delete the files by hand:
   ```bash
   docker compose exec api find /app/wwwroot/uploads -name '<comment_public_id>*' -delete
   ```
   This manual step is required **today, regardless of DB-11c/DB-12 status** — see the **Follow-up
   code items** section below; the public privacy policy's wording assumes this already happens
   automatically, which it does not for a soft-delete.
4. **User account**: soft-delete the row (`DeletedAt`) and rotate `security_stamp` so outstanding
   JWTs stop working. Production runs with `Auth:ValidateSecurityStamp = true`, so this takes
   effect on the next request rather than waiting out the token's 12-hour lifetime.
5. **Invites (D.3)**: scrub the person's email out of every invite row, whether accepted, open,
   expired, or revoked (there is no unique index on `invites.email`, so this is safe to run
   repeatedly / for multiple rows):
   ```sql
   UPDATE invites SET email = 'redacted@deleted' WHERE lower(email) = lower('<email>');
   ```
   Do this even for an "identity only" erasure (D.1) — an accepted invite's email is never swept by
   retention (DB-08 only sweeps *unused* expired/revoked invites), so it would otherwise survive
   indefinitely.
6. **What survives, on purpose**: the tombstoned/soft-deleted user row (kept for referential
   integrity — `comments.author_id` etc. must keep resolving to *something*), and anonymized
   aggregate usage facts (e.g. `usage_events.user_id` pointing at the now-anonymous id).
7. A **full, unrecoverable hard-delete** of a workspace's comment rows (and its screenshots) only
   happens when the entire workspace is deleted — that is a different, existing operation
   (`TenantService` hard-delete path), not part of a per-person DSAR.

## E. Timeline

- **PDPL**: respond within **30 days** of a verified request.
- **GDPR**: respond within **30 days**, extendable to 90 days with notice to the data subject, for
  requests from EEA-based data subjects (Pointer does not claim EU establishment — see
  `landing/privacy.html` §13).

Track the acknowledgement date and the due date in the log below the moment a request is
classified (§A.2) — don't wait for identity verification to finish before starting the clock.

## F. Logging

`docs/db/execution/DB-12-audit-log.md` designs an `audit_events` table and `IAuditWriter` for
exactly this kind of accountability — but **it is written, not implemented**, as of 2026-09-23.
Until it ships, log every DSAR manually in this table (copy this file's row format into an actual
tracking doc/spreadsheet — do not rely on memory or email search):

| # | Date received | Subject email | Type (Access / Erase-identity / Erase-content) | Status | Completed | Notes |
|---|---|---|---|---|---|---|
| 1 | | | | Received / In-progress / Done | | |

Once DB-12 ships, its action catalogue (`docs/db/execution/DB-12-audit-log.md` §3.6) already
reserves `export.downloaded` (covers both export GETs — self-serve and admin-on-behalf-of) and
`import.completed` — an access request that used either export path is then already covered by the
automated audit log, with no new action string needed. There is **no** equivalent reserved action
for the manual erasure steps in §D above (they're raw SQL against the database, not a controller
action DB-12 instruments) — those keep using the manual table above even after DB-12 ships, unless
a future doc adds a real erasure *endpoint* that DB-12 (or a follow-up) instruments.

## G. What the public privacy policy promises vs. what the code does today

Cross-checked against `landing/privacy.html` on 2026-09-23:

- **§6 (Screenshots)** states a screenshot "is deleted when the comment or the workspace it belongs
  to is deleted." This is **only true for a workspace hard-delete**, not for an individual
  comment's soft-delete — see the Follow-up item below. Handle any request specifically about
  screenshot deletion with the manual `find … -delete` step in §D.2.3 until the code matches the
  promise (or the promise is corrected).
- **§7 (Data retention)**: "a full account deletion tombstones your account: your comments and
  replies are kept but re-attributed to the tombstone" — matches D.1 exactly (once DB-11c ships;
  today, do the manual equivalent).
- **§11 (Internal access)**: operator database access can, today, read any workspace's comment
  content, and that access is not yet logged to a customer-visible security log (DB-12 pending).
  Nothing in this runbook changes that; it is disclosed as current status in the policy itself.
- **§12/§13 (Rights, PDPL/GDPR)**: access, correction, deletion, portability, objection — this
  runbook is the operational implementation of those rights.

---

## Follow-up code items found while writing this runbook (NOT implemented here)

Per this task's scope, no C# was changed (`Application/`, `Infrastructure/`, `Domain/` are owned by
DB-11a work in progress). These are handed off for a future PR:

1. **Screenshot files are never deleted when a comment is soft-deleted**, contradicting
   `landing/privacy.html` §6's public promise ("deleted when the comment … is deleted").
   - File: `Application/Services/Implementation/CommentService.cs:1040-1057` (`DeleteAsync`).
   - Current behavior: sets `comment.DeletedAt = DateTime.UtcNow;` and saves — never touches
     `comment.Element.ScreenshotUrl` or calls `IFileStorage.DeleteAsync`.
   - Precedent for the fix already exists in the same file: `EditAsync`,
     `CommentService.cs:816-824`, calls `await _fileStorage.DeleteAsync(comment.Element.ScreenshotUrl!);`
     then nulls the field, when `request.RemoveScreenshot` is set.
   - Intended change: in `DeleteAsync`, before/after setting `DeletedAt`, if
     `!string.IsNullOrEmpty(comment.Element.ScreenshotUrl)`, call
     `await _fileStorage.DeleteAsync(comment.Element.ScreenshotUrl!);` (mirroring `EditAsync`).
     Needs a product decision first: F5 (DB-11c) says screenshots survive *identity erase*
     specifically because they're the workspace's asset — deleting the screenshot on ordinary
     comment soft-delete is a separate question the founder should confirm before implementing,
     since it's irreversible (soft-delete is recoverable; deleting the file is not). Alternative:
     correct `landing/privacy.html` §6 instead, to describe today's actual behavior (screenshot
     survives a soft-deleted comment; only a hard-delete — i.e. workspace deletion — removes it).
   - Owner: whoever picks up DB-11c/DB-11 follow-on work, or a dedicated privacy-copy fix; flag to
     the founder for the policy-vs-code decision either way.

2. **Export/import fidelity gaps** (already documented in
   `docs/roadmap/execution/R5-66-export-and-dsar-runbook.md` §3.1, restated here since a DSAR access
   request depends on knowing what's missing from a "just give me the export file" answer):
   - Custom field values (`comments.custom_fields`, R4-01) — no property on `CommentExportDto`
     (`Application/DTOs/Export/CommentExportDto.cs`).
   - Page context snapshots (`comments.page_context_snapshot_id` /
     `Comment.PageContextSnapshot`, `Domain/Entity/Comment.cs:49-50`) — no property on the DTO.
   - Predefined-action associations (`comments.picked_actions`, `Comment.PickedActions`,
     `Domain/Entity/Comment.cs:56`) — no property on the DTO.
   - Intended change (if ever relied on for full-fidelity migration or a DSAR that must include
     everything): add three properties to `CommentExportDto` and populate them in
     `ExportImportService.BuildExportFileAsync` (`Application/Services/Implementation/ExportImportService.cs:153-188`),
     plus round-trip them in `InsertCommentsAsync` (`ExportImportService.cs:346-436`) on import.
     Out of scope for this task (§10 of R5-66 explicitly defers this).

3. **Reused primary keys against restored/rehearsal databases can make the `first_comment` usage
   event's unique-constraint fallback fire on the very first comment of a brand-new project** — an
   observed-but-benign artifact during this task's verification (a project created against a
   restored production dump copy got an `id` that a previously-existing, unrelated `usage_events`
   row already referenced), not a real bug in the live production sequence (which never reuses
   ids). No code change proposed; noted only so a future throwaway-DB verification run isn't
   surprised by the same harmless `23505` log line
   (`Application/Services/Implementation/CommentService.cs:246-266`, the existing
   `catch (DbUpdateException ex) when (... SqlState: "23505" ...)` already swallows it correctly).
