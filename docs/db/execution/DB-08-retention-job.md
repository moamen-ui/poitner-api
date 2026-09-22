# DB-08 — Retention job for `usage_events`, read `notifications`, orphaned `page_context_snapshots`, dead `invites`

Review finding: S-9. Rules: R3 (batched deletes), R5, R11. **Class: code only, no schema change;
deletes old rows by policy.** **Status 2026-09-22 (evening): Q8 answered — the doc defaults stand
(180 / 90 / 30 / 90 days) and the job ships ENABLED in production, overridable by config.** Last of
the schema/code docs in the execution order; may ride in the DB-03..08 batch deploy (DB-RULES R7.1)
because it has no migration.

**Owner decision (Q8, answered 2026-09-22 by Moamen — "your defaults stand", relayed by the
orchestrator):** `Retention__UsageEventsDays=180 Retention__NotificationsReadDays=90 Retention__PageContextSnapshotDays=30 Retention__InvitesDays=90`, `Retention__Enabled=true`.

**Deployed 2026-09-22 12:05 UTC, enabled**, batched with DB-03/DB-06/DB-07/DB-03b(API) in one
contract deploy per R7.1 (dump `pre-db03-08`). Implementation note: `API/AssemblyInfo.cs` gained
an `InternalsVisibleTo` entry for the retention tests.

## 1. Goal

Four tables grow without bound and nothing prunes them: `usage_events` (append-only analytics),
`notifications` (kept forever after being read), `page_context_snapshots` (browser console/network
bags, referenced by comments via a nullable FK), and `invites` that expired or were revoked without
ever being used. A daily hosted job deletes rows past a configurable age in small batches.
User-visible reason: dashboard notification queries and dumps stay fast; the retention promise on
the planned privacy page (§33, `docs/roadmap/DX-UX-CX-PLAN.md:214`) becomes true for captured
console data.

## 2. Prerequisites (verified facts, 2026-09-22 @ `ff25a8d`)

- Hosted-service precedent: `API/Hosted/DemoCleanupService.cs` (`BackgroundService`, initial 30 s delay, `PeriodicTimer(1h)`, one scope per unit of work, all exceptions logged never thrown; registered at `API/Program.cs:80`).
- Relational bulk ops precedent: `UnitOfWork.AtomicClaimInviteSlotAsync` uses `ExecuteUpdateAsync` and branches on `db.Database.ProviderName != "Microsoft.EntityFrameworkCore.InMemory"` (`Infrastructure/Repository/UnitOfWork.cs:26-57`). `ExecuteDeleteAsync` is likewise relational-only (Npgsql and Sqlite fine; InMemory not).
- `usage_events`: not a `BaseEntity`; `created_at` NOT NULL (`UsageEventMapping.cs:38-40`); composite index `(owner_id, project_id, type, created_at)` (`:42`); unique partial index on `first_comment`/`first_apply` (`:44-47`) — **those two `type` values must never be deleted** (they are one-shot facts the app relies on, `Tests/UsageEventFirstCommentTests.cs`).
- `notifications`: `read_at` nullable (`NotificationMapping.cs:30`); index `(user_id, read_at)` (`:37`); FKs cascade from comments/suggestions.
- `page_context_snapshots`: `last_event_at` (`PageContextSnapshotMapping.cs:28`); `comments.page_context_snapshot_id → SetNull` (`CommentMapping.cs:48-49`); a snapshot is shared by every bug-flagged comment on the same page/visit (`PageContextSnapshot.cs:6-11`). A snapshot still referenced by a **live** comment must never be deleted.
- `invites` (`Domain/Entity/Invite.cs`): `ExpiresAt` NOT NULL (`:41`), `RevokedAt` nullable (`:50`), `Uses` int (`:47`, incremented per accept); `quick_access_links.invite_id` (`QuickAccessLink.cs:26-27`, "the audit invite this link was issued for") gets a `Restrict` FK in DB-06. An invite with `Uses > 0` is an audit row (who joined through it) and is **kept forever**; a quick-access link cannot be minted from an expired invite, so an invite that is both dead and unused is only referenced if a link already existed — the predicate excludes those.
- Query filters: the job runs with no HTTP context → `ICurrentUser.TenantId` null, `IsSuperAdmin` false; every query must use `IgnoreQueryFilters()` (same reason as `AdminSeeder.cs:39-43` and `DemoCleanupService.cs:44`).
- Config binding style: `builder.Configuration.GetValue<bool>("DBMigrationEnabled")` (`Program.cs:165`); compose env uses `Section__Key` (`docker-compose.prod.yml:26-33`); `API/appsettings.json` holds defaults.
- Nightly dump: cron 03:00 UTC (`DEPLOY.md:104`); `KEEP_DAYS=14` (`scripts/backup-db.sh:17`).
- Per-plan `PlanEntitlements.RetentionDays` (`Domain/ValueObjects/PlanEntitlements.cs:27`) is **not** wired here (out of scope; platform-wide periods only).

## 3. Design

`API/Hosted/RetentionService.cs` — `BackgroundService`, registered after `DemoCleanupService` in `Program.cs`. Config section `Retention`:

| key | default (`appsettings.json`) | prod (`docker-compose.prod.yml`) |
|---|---|---|
| `Enabled` | **`true`** | `Retention__Enabled: "${RETENTION_ENABLED:-true}"` — set `RETENTION_ENABLED=false` in `.env.prod` to pause |
| `UsageEventsDays` | 180 | `Retention__UsageEventsDays: "${RETENTION_USAGE_EVENTS_DAYS:-180}"` |
| `NotificationsReadDays` | 90 | `Retention__NotificationsReadDays: "${RETENTION_NOTIFICATIONS_READ_DAYS:-90}"` |
| `PageContextSnapshotDays` | 30 | `Retention__PageContextSnapshotDays: "${RETENTION_SNAPSHOT_DAYS:-30}"` |
| `InvitesDays` | 90 | `Retention__InvitesDays: "${RETENTION_INVITES_DAYS:-90}"` |
| `BatchSize` | 5000 | — |
| `IntervalHours` | 24 | — |
| `InitialDelayMinutes` | 5 | — |

Loop: initial delay `InitialDelayMinutes` (5 min — long enough for `deploy-api.sh`'s smoke check
and a human look at the logs to finish **before** the first deletion); then every `IntervalHours`;
when `Enabled` is false log one `Retention: disabled` line per pass and skip. A period set to `0`
or a negative number disables that table only (log `Retention: <table> skipped (period 0)`). Each
pass, per table, repeat until a batch deletes fewer than `BatchSize` rows (R3 batching — each
`ExecuteDeleteAsync` is its own implicit transaction):

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

// invites: dead (expired or revoked) long ago, never used, and not the audit row of a quick-access link
… .Where(i => i.Uses == 0
           && (i.ExpiresAt < cutoffInv || (i.RevokedAt != null && i.RevokedAt < cutoffInv))
           && !db.Set<QuickAccessLink>().IgnoreQueryFilters().Any(q => q.InviteId == i.Id)) …
```
(Select ids first, then delete by id list: keeps each statement short and index-driven; `Take(batch)`
on an ordered id scan.) Log per table: `Retention: <table> deleted <n> rows older than <cutoff:u>`.

Existing rows: from the first pass (5 min after the deploy) only rows matching the predicates above
are deleted. Nothing customer-visible references `usage_events`; a deleted notification disappears
from the bell's history (it was read ≥ 90 days ago); a deleted snapshot detaches from
**soft-deleted** comments only (their `page_context_snapshot_id` becomes NULL via the FK); a
deleted invite was unusable for ≥ 90 days and nobody ever joined through it.

## 4. Safety classification

Code only (no migration). Deletes are policy-driven and irreversible → R5: every row the job can
delete is ≥ 30 days old and therefore present in ≥ 14 nightly dumps (`KEEP_DAYS=14`) plus the
labelled deploy dump taken minutes before the first pass (`pre-db03-08` in the batch, or
`pre-deploy`). The daily pass drifts with restarts (it is boot + 5 min, then every 24 h); acceptable
for the same reason.

## 5. File-level tasks

1. `API/Hosted/RetentionService.cs` (new) — class `RetentionService(IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<RetentionService> logger) : BackgroundService` per §3; bind the section into a `RetentionOptions` record (`Enabled`, the four `*Days`, `BatchSize`, `IntervalHours`, `InitialDelayMinutes`) once per pass; obtain `AppDbContext` from the scope (not `IUnitOfWork` — it has no `Notifications`/`PageContextSnapshots` DbSets; `AppDbContext.cs:56,62` do). Wrap each table in its own try/catch (copy `DemoCleanupService.cs:37-58,72-92` shape). Expose `internal static Task<RetentionSweepResult> SweepOnceAsync(AppDbContext db, RetentionOptions o, ILogger log, CancellationToken ct)` for the tests (`RetentionSweepResult` = four ints).
2. `API/Program.cs` — after line 80 add `builder.Services.AddHostedService<RetentionService>();`.
3. `API/appsettings.json` — add the `Retention` section with the §3 defaults (`Enabled: true`).
4. `docker-compose.prod.yml` — after line 33 add the five `Retention__*` lines from the §3 table (enabled and the four periods, each `${VAR:-default}`); `.env.prod.example` — add
   ```
   # DB-08 row retention (days). Job is ON by default; RETENTION_ENABLED=false pauses it. 0 disables one table.
   RETENTION_ENABLED=true
   RETENTION_USAGE_EVENTS_DAYS=180
   RETENTION_NOTIFICATIONS_READ_DAYS=90
   RETENTION_SNAPSHOT_DAYS=30
   RETENTION_INVITES_DAYS=90
   ```
5. `DEPLOY.md` § Backups — one paragraph: "Row retention (DB-08, on by default) deletes `usage_events` > 180 d (never the `first_*` facts), read `notifications` > 90 d, `page_context_snapshots` > 30 d with no live comment, and never-used expired/revoked `invites` > 90 d, in batches of 5000, daily from 5 min after boot. Every such row exists in at least the last 14 nightly dumps. Pause with `RETENTION_ENABLED=false` + `up -d api`."
6. Tests (§6). 7. `just fmt`, `just test`.

## 6. Tests

New `Tests/RetentionServiceTests.cs` with the Sqlite `TestDb` fixture (`Tests/UsageEventFirstCommentTests.cs:43-60`) — Sqlite supports `ExecuteDeleteAsync`. Call `SweepOnceAsync` directly:

1. `UsageEvents_OlderThanCutoff_AreDeleted_ExceptFirstFacts` — 3 old rows (`type` = `x`, `first_comment`, `first_apply`) + 1 recent; after sweep: the two `first_*` and the recent remain.
2. `Notifications_ReadLongAgo_Deleted_UnreadKept` — old read, old unread, recent read → only old read is deleted.
3. `Snapshots_ReferencedByLiveComment_Kept` — two old snapshots, one referenced by a live comment, one by a soft-deleted comment → the first stays; the second is deleted and the soft-deleted comment's `PageContextSnapshotId` is now null.
4. `Invites_DeadAndUnused_Deleted_UsedOrLinkedOrFreshKept` — four invites older than the cutoff: expired+`Uses=0` (deleted), revoked+`Uses=0` (deleted), expired+`Uses=1` (kept), expired+`Uses=0` with a `QuickAccessLink` pointing at it (kept); plus one expired yesterday (kept).
5. `Sweep_Disabled_DeletesNothing` — `Enabled=false` → all four counts 0, rows intact.
6. `Sweep_PeriodZero_SkipsThatTableOnly` — `NotificationsReadDays=0` → notifications intact, old usage events gone.
7. `Sweep_Batches` — `BatchSize=2`, 5 old usage events → all 5 gone after one `SweepOnceAsync` (proves the inner loop).

## 7. Acceptance criteria

1. `grep -n "AddHostedService" API/Program.cs` → two lines (`DemoCleanupService`, `RetentionService`).
2. `appsettings.json` `Retention.Enabled` is `true`; `docker-compose.prod.yml` has the five `Retention__*` lines with the §3 defaults; `.env.prod.example` has the five `RETENTION_*` lines.
3. Seven new tests pass; `just test` green.
4. Booting locally with `Retention__Enabled=false` logs exactly one `Retention: disabled` line per pass and deletes nothing; with the defaults, the first `Retention:` lines appear ≈ 5 min after `Now listening`, not before.
5. On the rehearsal DB with the defaults: `SELECT count(*) FROM usage_events WHERE created_at < now() - interval '180 days' AND type NOT IN ('first_comment','first_apply')` → 0 after one pass; the `first_*` rows are intact; `SELECT count(*) FROM invites WHERE uses = 0 AND expires_at < now() - interval '90 days' AND NOT EXISTS (SELECT 1 FROM quick_access_links q WHERE q.invite_id = invites.id)` → 0.

## 8. Rollback

Set `RETENTION_ENABLED=false` in `.env.prod` and `docker compose --env-file .env.prod -f docker-compose.prod.yml up -d api` — no code rollback needed. **Deleted rows
are not recoverable** except from dumps (`pg_restore -t usage_events …` into a scratch database, then
copy back the wanted rows). No migration to revert.

## 9. Release steps

1. Merge; ships in the DB-03..08 batch (`pre-db03-08` dump) or an ordinary `bash scripts/deploy-api.sh` (the `pre-deploy` dump precedes the first pass by ≥ 5 min either way).
2. After `deploy OK`, wait for the first pass: `docker compose --env-file .env.prod -f docker-compose.prod.yml logs --since 10m api | grep "Retention:"` → four lines with counts (expected small or zero on today's single workspace).
3. Record `SELECT pg_size_pretty(pg_total_relation_size('usage_events'));` before/after for the record.
4. Nothing to fill in: Q8 is answered; the defaults are live. To change a period later, set the `RETENTION_*` variable in `.env.prod` and `up -d api`.

## 10. Out of scope

Per-plan `RetentionDays`, screenshot files in the `uploads` volume (§33's blob deletion is a
product feature), used invites (`Uses > 0`, audit), `device_logins` (already swept inline), demo
tenants (`DemoCleanupService`), any schema change, `clients/`, dashboard.
