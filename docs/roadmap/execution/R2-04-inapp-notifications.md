# R2-04 — In-app notifications + author verify loop  (§10 · Release 2 · 2–3 d)

## Goal
When a comment the stakeholder wrote is marked **applied**, they see it — a badge in the widget and a
bell in the dashboard — with the commit link and a one-click "does it look right? 👍 / 👎". 👎 reopens
the comment with their note as a reply so the developer sees it in the queue again. In-app only; no
email (held per `meetings/11-final-decisions.md`).

## Out of scope
- Email/Slack/webhook delivery (§32 later). Notification preferences UI. Real-time push (polling only).
- Deploy awareness (§31 — notifications fire at *applied*, not *deployed*, for now).

## Prerequisites
- None hard. Facts: status change path `CommentService.UpdateStatusAsync` (`CommentService.cs:477-527`)
  sets `AppliedAt/AppliedBy/AppliedByLabel/CommitUrl` and may add a `Reply` (`Domain/Entity/Reply.cs`);
  `Comment.AuthorId`, `Comment.OwnerId` (`Domain/Entity/Comment.cs`); no `Notification` entity exists
  (grep confirmed); widget chrome/badge in `web-component/src/templates.ts:67,135` and
  `element.ts:459-464` (`renderChrome`, `fetchComments`); quick-access users cannot change status
  (`CommentService.cs:482-483`) — verify must therefore **not** go through `UpdateStatusAsync`.

## Design

### Table `notifications` (strict-own)
`Domain/Entity/Notification.cs : BaseEntity`
| column | type | notes |
|---|---|---|
| `OwnerId` | Guid? | tenant; strict-own filter bucket |
| `UserId` | Guid | recipient `User.PublicId` |
| `Type` | `NotificationType` enum | `CommentApplied = 1`, `CommentReopened = 2`, `ReplyAdded = 3` (Decision: ship all three; 2 and 3 are cheap) |
| `CommentId` | int | FK, cascade delete |
| `ProjectId` | int | denormalised for filtering |
| `ActorId` | Guid? | who caused it |
| `Payload` | jsonb (string) | `{ commitUrl?, appliedByLabel?, replyExcerpt? }` |
| `ReadAt` | DateTime? | |
Indexes: `(UserId, ReadAt)`, `(CommentId)`.

### Emission (inside `CommentService`, same transaction)
- In `UpdateStatusAsync` when `request.Status == Applied` and `comment.AuthorId != actorId` →
  `CommentApplied` to `comment.AuthorId` with `{ commitUrl, appliedByLabel }`.
- In the new `VerifyAsync` (below) when `ok == false` → `CommentReopened` to `comment.AppliedBy` (if set)
  with `{ replyExcerpt }`.
- In `RepliesController` path (`ReplyService`/wherever `POST /api/comments/{id}/replies` is handled) →
  `ReplyAdded` to `comment.AuthorId` when the replier is not the author.
Implement as `INotificationService.EnqueueAsync(Notification)` called by the services; no background
worker — rows are written in the same `SaveChangesAsync`.

### Endpoints
| Route | Body / query | Response |
|---|---|---|
| `GET /api/me/notifications?unread=true&page=1&pageSize=20` [Authorize] | | `PagedData<NotificationDto { id, type, commentId, projectKey, projectName, commentBodyExcerpt (≤ 80 chars), payload, createdAt, readAt }>` + header-less `unreadCount` field on the page object (Decision: extend `PagedData` consumers by adding `GET /api/me/notifications/unread-count` → `{ count }` instead of touching `PagedData`) |
| `PATCH /api/me/notifications/{id}/read` | | `NotificationDto` |
| `POST /api/me/notifications/read-all` | | `{ marked: int }` |
| `POST /api/comments/{id}/verify` [Authorize] | `VerifyCommentRequest { ok: bool, note?: string ≤ 500 }` | `CommentResponse` |

`VerifyAsync(id, request, actorId)` rules: actor must be `comment.AuthorId` **or** admin; comment must be
`Applied`; `ok=true` → adds reply `"Verified ✓"` (or the note) and stamps `Comment.VerifiedAt` (new
nullable column, additive) ; `ok=false` → `Status = Open`, `VerifiedAt = null`, adds reply
`"Not fixed: <note>"` (note required when `ok=false`), emits `CommentReopened`. **Quick-access users are
allowed** here (this is the one lifecycle action a Client legitimately owns) — add the explicit bypass
next to the existing guard with a comment.

### Widget
- Poll `GET /api/me/notifications/unread-count` on init and every 60 s while visible (`document.visibilityState`).
- Launcher badge (`templates.ts:135`) shows unread count in a second colour (`--pf-notify`); header
  button "Updates" opens a small list (type icon, excerpt, commit link, time) built from
  `GET /api/me/notifications?unread=true`.
- On an applied comment the author owns (`c._mine && c.status === 'applied' && !c.verifiedAt`), the
  card (`templates.ts` `card()`, near the `✓ completed` pill at `:163`) shows `👍 Looks right` /
  `👎 Not fixed` → `POST /api/comments/{id}/verify`; 👎 opens a one-line note input first.
- Opening the list marks items read (`read-all`).
- Add `verifiedAt?: string | null` to `Comment` in `web-component/src/types.ts`.

### Dashboard
Bell in the top bar with unread count; dropdown list; mark read; link to the comment. Verify buttons on
the comment detail for admins.

## Tasks
1. `Domain/Enums/NotificationType.cs`; `Domain/Entity/Notification.cs`; `Comment.VerifiedAt` (DateTime?).
2. `Infrastructure/Mappings/NotificationMapping.cs`; `AppDbContext` strict-own filter + `DbSet`; `just migrate name="AddNotificationsAndCommentVerifiedAt"`.
3. `Application/DTOs/Notification/NotificationDto.cs`, `UnreadCountResponse.cs`; `Application/DTOs/Comment/VerifyCommentRequest.cs` + validator (`note` required when `ok=false`, ≤ 500).
4. `Application/Services/Interfaces/INotificationService.cs` + `Implementation/NotificationService.cs` (Enqueue, List, UnreadCount, MarkRead, MarkAllRead) — `TenantStamp.OwnerFor`.
5. `CommentService.UpdateStatusAsync` — emit `CommentApplied`; add `VerifyAsync`; `ICommentService` signature.
6. Reply path — emit `ReplyAdded` (locate the service behind `RepliesController.cs:13-20`).
7. `API/Controllers/MeController.cs` — the three notification actions; `API/Controllers/CommentsController.cs` — `verify`; `[ProducesResponseType]` on each.
8. `Comment` DTO mappers (`CommentResponse`, `CommentListItemDto`) — add `VerifiedAt`.
9. Widget: `types.ts`, `templates.ts` (badge, list, verify buttons), `element.ts` (poll, handlers, `apiVerify`, `apiNotifications`), `styles/` (`_notifications.scss`, `--pf-notify` token in `_variables.scss`); `npm run build`; commit artifacts.
10. `MessageKeys` additions: `Comment.VerifyRequiresApplied`, `Comment.VerifyNoteRequired`, `Comment.Verified`, `Comment.Reopened`.

## Dashboard tasks
Regenerate for `NotificationDto`, `UnreadCountResponse`, `VerifyCommentRequest`, `CommentResponse.verifiedAt`. UI: top-bar bell + dropdown; verify buttons on comment detail; `resource.reload()` after mutations.

## Tests
- `Tests/NotificationServiceTests.cs`: applied by another user → one row for the author; applied by the author → none; tenant isolation (user in tenant B sees none); unread count; read-all.
- `Tests/CommentVerifyTests.cs`: author 👍 → `VerifiedAt` set + reply; author 👎 without note → validation error; 👎 with note → `Status=Open`, reply text, `CommentReopened` for `AppliedBy`; non-author non-admin → Forbidden; quick-access author → allowed; verify on non-applied → `VerifyRequiresApplied`.
- E2E (`e2e/widget/notifications.spec.ts`): Client comments → staff marks applied via API → Client reloads widget → badge `1`, list shows commit link → click 👎, note "still red" → comment `status=1` and last reply contains "still red"; staff sees `CommentReopened`. Scenario names: `notify: applied shows badge to author`, `notify: thumbs-down reopens with note`, `notify: read-all clears badge`.

## Acceptance criteria
- [ ] Marking a comment applied (by someone else) creates exactly one `CommentApplied` notification for its author, visible via `GET /api/me/notifications?unread=true` and counted by `unread-count`.
- [ ] Widget launcher shows the unread count within 60 s without reload; opening the list clears it.
- [ ] 👎 with a note sets `Status=Open`, adds the reply, and the developer's `pointer list --status open` shows the comment again.
- [ ] Quick-access (Client) author can verify; cannot change status otherwise (existing guard still holds — `Tests/CommentServiceQuickAccessTests.cs` still green).
- [ ] No notification rows leak across tenants (test).
- [ ] `just test` green; widget `npm run typecheck && npm run build` green; artifacts committed.

## Rollout / compatibility
Additive migration. Existing applied comments have `VerifiedAt = null` → verify buttons appear for their authors (acceptable).

## Report template
Files; migration name; test results; screenshots or DOM dump of the badge/list; the e2e run summary.
