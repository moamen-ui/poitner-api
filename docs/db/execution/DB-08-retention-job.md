# DB-08 — Retention job for `usage_events`, read `notifications`, orphaned `page_context_snapshots`

Review finding: S-9. Rules: R3 (batched deletes), R5, R11. **Class: code only, no schema change;
deletes old rows by policy** — ships **disabled** and is switched on by config once the owner has
confirmed the periods (**Q8**). Last of the schema/code docs in the execution order (only DB-01's
off-box half, waiting on Q1, may trail it).

**Owner decision (fill before enabling in prod):** `Retention__UsageEventsDays=____ Retention__NotificationsReadDays=____ Retention__PageContextSnapshotDays=____` (doc defaults: 180 / 90 / 30).

## 1. Goal

Three tables grow without bound and nothing prunes them: `usage_events` (append-only analytics),
`notifications` (kept forever after being read), `page_context_snapshots` (browser console/network
bags, referenced by comments via a nullable FK). A nightly hosted job deletes rows past a
configurable age in small batches. User-visible reason: dashboard notification queries and dumps
stay fast; the retention promise on the planned privacy page (§33, `docs/roadmap/DX-UX-CX-PLAN.md:214`)
becomes true for captured console data.

## 2. Prerequisites (verified facts)

- Hosted-service precedent: `API/Hosted/DemoCleanupService.cs` (`BackgroundService`, initial 30 s delay, `PeriodicTimer(1h)`, one scope per unit of work, all exceptions logged never thrown; registered at `API/Program.cs:80`).
- Relational bulk ops precedent: `UnitOfWork.AtomicClaimInviteSlotAsync` uses `ExecuteUpdateAsync` and branches on `db.Database.ProviderName != "Microsoft.EntityFrameworkCore.InMemory"` (`Infrastructure/Repository/UnitOfWork.cs:26-57`). `ExecuteDeleteAsync` is likewise relational-only (Npgsql and Sqlite fine; InMemory not).
- `usage_events`: not a `BaseEntity`; `created_at` NOT NULL (`UsageEventMapping.cs:38-40`); composite index `(owner_id, project_id, type, created_at)` (`:42`); unique partial index on `first_comment`/`first_apply` (`:44-47`) — **those two `type` values must never be deleted** (they are one-shot facts the app relies on, `Tests/UsageEventFirstCommentTests.cs`).
- `notifications`: `read_at` nullable (`NotificationMapping.cs:30`); index `(user_id, read_at)` (`:37`); FKs cascade from comments/suggestions.
- `page_context_snapshots`: `last_event_at` (`PageContextSnapshotMapping.cs:28`); `comments.page_context_snapshot_id → SetNull` (`CommentMapping.cs:48-49`); a snapshot is shared by every bug-flagged comment on the same page/visit (`PageContextSnapshot.cs:6-11`). A snapshot still referenced by a **live** comment must never be deleted.
- Invites are **not** pruned: they are audit rows and `quick_access_links.invite_id` will reference them with `Restrict` (DB-06).
- Query filters: the job runs with no HTTP context → `ICurrentUser.TenantId` null, `IsSuperAdmin` false; every query must use `IgnoreQueryFilters()` (same reason as `AdminSeeder.cs:39-43` and `DemoCleanupService.cs:44`).
- Config binding style: `builder.Configuration.GetValue<bool>("DBMigrationEnabled")` (`Program.cs:165`); compose env uses `Section__Key` (`docker-compose.prod.yml:26-33`); `API/appsettings.json` holds defaults.
- Per-plan `PlanEntitlements.RetentionDays` (`Domain/ValueObjects/PlanEntitlements.cs:27`) is **not** wired here (out of scope; platform-wide periods only).

## 3. Design

`API/Hosted/RetentionService.cs` — `BackgroundService`, registered after `DemoCleanupService` in `Program.cs`. Config section `Retention`:

| key | default (appsettings.json) | prod (compose) |
|---|---|---|
| `Enabled` | `false` | `Retention__Enabled: "true"` once Q8 is answered |
| `UsageEventsDays` | 180 | owner |
| `NotificationsReadDays` | 90 | owner |
| `PageContextSnapshotDays` | 30 | owner |
| `BatchSize` | 5000 | — |
| `IntervalHours` | 24 | — |

Loop: initial delay 60 s; every `IntervalHours`; when `Enabled` is false log one line and skip. Each pass, per table, repeat until a batch deletes fewer than `BatchSize` rows (R3 batching — each `ExecuteDeleteAsync` is its own implicit transaction):

```csharp
// usage_events: old, and never the one-shot facts
var ids = await db.UsageEvents.IgnoreQueryFilters()
    .Where(e => e.CreatedAt < cutoffUsage && e.Type != "first_comment" && e.Type != "first_apply")
    .OrderBy(e => e.Id).Select(e => e.Id).Take(batch).ToListAsync(ct);
deleted = await db.UsageEvents.IgnoreQueryFilters().Where(e => ids.Contains(e.Id)).ExecuteDeleteAsync(ct);

// notifications: read long ago
… .Where(n => n.ReadAt != null && n.ReadAt < cutoffNotif) …

// page_context_snapshots: stale and not referenced by any live comment
… .Where(s => s.LastEventAt < cutoffSnap
           && !db.Comments.IgnoreQueryFilters().Any(c => c.PageContextSnapshotId == s.Id && c.DeletedAt == null)) …
```
(Select ids first, then delete by id list: keeps each statement short and index-driven; `Take(batch)` on an ordered id scan.) Log per table: `Retention: <table> deleted <n> rows older than <cutoff:u>`.

Existing rows: none touched until `Enabled=true`; then only rows matching the predicates above. Nothing customer-visible references `usage_events`; a deleted notification disappears from the bell's history; a deleted snapshot detaches from **soft-deleted** comments only (their `page_context_snapshot_id` becomes NULL via the FK).

## 4. Safety classification

Code only (no migration). Deletes are policy-driven and irreversible → R5: the nightly dump (03:00 UTC) precedes the job's first run **only if** the job's schedule is placed after it — set the initial delay so the first pass happens after boot, and rely on `IntervalHours=24` thereafter; in prod the daily pass therefore drifts with restarts. Acceptable: every deleted row is ≥ 30 days old and present in ≥ 14 dumps.

## 5. File-level tasks

1. `API/Hosted/RetentionService.cs` (new) — class `RetentionService(IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<RetentionService> logger) : BackgroundService` per §3; read config once per pass; obtain `AppDbContext` from the scope (not `IUnitOfWork` — it has no `Notifications`/`PageContextSnapshots` DbSets; `AppDbContext.cs:56,62` do). Wrap each table in its own try/catch (copy `DemoCleanupService.cs:37-58,72-92` shape).
2. `API/Program.cs` — after line 80 add `builder.Services.AddHostedService<RetentionService>();`.
3. `API/appsettings.json` — add the `Retention` section with the §3 defaults (`Enabled: false`).
4. `docker-compose.prod.yml` — after line 33 add the three `Retention__*Days` lines and `Retention__Enabled: "${RETENTION_ENABLED:-false}"`; `.env.prod.example` — add `RETENTION_ENABLED=false` with a comment pointing here.
5. `DEPLOY.md` § Backups — one sentence: "Row retention (DB-08) deletes only rows older than the configured periods; every such row exists in at least the last 14 nightly dumps."
6. Tests (§6). 7. `just fmt`, `just test`.

## 6. Tests

New `Tests/RetentionServiceTests.cs` with the Sqlite `TestDb` fixture (`Tests/UsageEventFirstCommentTests.cs:43-60`) — Sqlite supports `ExecuteDeleteAsync`. Expose the per-pass method as `internal Task SweepOnceAsync(AppDbContext db, RetentionOptions o, CancellationToken ct)` (static or instance) so tests call it without the timer:

1. `UsageEvents_OlderThanCutoff_AreDeleted_ExceptFirstFacts` — 3 old rows (`type` = `x`, `first_comment`, `first_apply`) + 1 recent; after sweep: the two `first_*` and the recent remain.
2. `Notifications_ReadLongAgo_Deleted_UnreadKept` — old read, old unread, recent read → only old read is deleted.
3. `Snapshots_ReferencedByLiveComment_Kept` — two old snapshots, one referenced by a live comment, one by a soft-deleted comment → the first stays; the second is deleted and the soft-deleted comment's `PageContextSnapshotId` is now null.
4. `Sweep_Disabled_DeletesNothing`.
5. `Sweep_Batches` — `BatchSize=2`, 5 old usage events → all 5 gone after one `SweepOnceAsync` (proves the inner loop).

## 7. Acceptance criteria

1. `grep -n "AddHostedService" API/Program.cs` → two lines (`DemoCleanupService`, `RetentionService`).
2. `appsettings.json` `Retention.Enabled` is `false`; prod compose passes `Retention__Enabled` from `RETENTION_ENABLED` defaulting to `false`.
3. Five new tests pass; `just test` green.
4. Booting locally with `Retention__Enabled=false` logs exactly one `Retention: disabled` line per pass and deletes nothing.
5. On the rehearsal DB with `Enabled=true` and the defaults: `SELECT count(*) FROM usage_events WHERE created_at < now() - interval '180 days' AND type NOT IN ('first_comment','first_apply')` → 0 after one pass; the `first_*` rows are intact.

## 8. Rollback

Set `RETENTION_ENABLED=false` and restart (`up -d api`) — no code rollback needed. **Deleted rows
are not recoverable** except from dumps (`pg_restore -t usage_events …` into a scratch database, then
copy back the wanted rows). No migration to revert.

## 9. Release steps

1. Merge with `Enabled=false`; normal `deploy-api.sh`. Confirm the disabled log line.
2. Owner fills the three periods (Q8); set `RETENTION_ENABLED=true` and the `Retention__*Days` values in `.env.prod`; `up -d api`.
3. After the first enabled pass, check `logs api | grep Retention:` — three lines with counts; `SELECT pg_size_pretty(pg_total_relation_size('usage_events'));` before/after for the record.

## 10. Out of scope

Per-plan `RetentionDays`, screenshot files in the `uploads` volume (§33's blob deletion is a
product feature), invites, `device_logins` (already swept inline), demo tenants
(`DemoCleanupService`), any schema change, `clients/`, dashboard.
