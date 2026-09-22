# DB-15 — Activation funnel as data: server-emitted steps, `usage_daily` rollup before the sweep, funnel endpoints

Roadmap: §64 (Release 5 row **R5.6** writing pack — this doc is the data half; the "weekly number" definition lives here so the write-up and the code agree),
founder decision **F4** ("Activation funnel = demo → converted → widget installed → first comment → first apply"), foundations report §2 row 11 ("roll up
before the 180 d sweep"). Rules: R1 (one new table, one new partial unique index), R5, R7 (additive → ordinary deploy), R8 (analytics table exemption, as
amended by DB-12), R10 (event `type` strings are frozen identifiers once written — append only), R13.
**Class: Additive.** Ships as an ordinary `bash scripts/deploy-api.sh`. **Status 2026-09-22: written; not implemented. Cross-reviewed 2026-09-22 (GLM, agy —
`docs/db/reviews/`); amendments folded 2026-09-23 (§12).** Owner decisions D15.1–D15.4 have defaults (§3.7); none blocks.

**Dependencies.** None on DB-11/12/13/14 for the schema or the job. The **demo emission sites** (§3.2) are written against the DB-11a version of `DemoService`
(workspace id ≠ admin `public_id`); implement after DB-11a is merged so `OwnerId` is the workspace. The endpoints use `TenantStamp.TryRequireOwner` (DB-11a §3.6).
F4's "convert-to-workspace + 24 h TTL via the retention job" (R5.7) is a later doc — this doc only makes the funnel *measurable*; `workspace_converted` is
emitted by today's `DemoService.UpgradeAsync`, which R5.7 keeps as the convert primitive.

## 1. Goal

The one product question for the next months is "do people who try it get to a first applied comment?". Today: `first_comment`/`first_apply` are emitted
server-side per project (`CommentService.cs:249-262, 678-694`) and kept forever by DB-08; `installed`/`doctor_run`/`apply_started`/`apply_failed` come from the
CLI and `widget_language` from the widget (`RecordEventValidator.cs:10-12`), all swept after 180 days; **nothing** marks a demo start, a demo→workspace conversion,
or "the widget is live on a real site". This doc adds the three missing steps as server-emitted one-shot facts, a tiny daily rollup table so volume events survive
the sweep as counts, and two endpoints — the operator's funnel with a weekly number, and the workspace's own "getting started" checklist. User-visible: the
Overview page shows the funnel and "activated workspaces this week"; a workspace admin sees which step they are on.

## 2. Prerequisites (verified facts, 2026-09-22 @ `1af08ec`)

- `usage_events` (`Domain/Entity/UsageEvent.cs`: `Id int`, `OwnerId Guid?`, `ProjectId int?`, `UserId Guid?`, `Type string(40)`, `Source string(32)`, `Meta jsonb ≤ 2000`, `CreatedAt`; **not** a `BaseEntity`).
  Mapping `Infrastructure/Mappings/UsageEventMapping.cs`: FK `owner_id → workspaces` **SetNull** (`fk_usage_events_workspaces_owner_id`), `project_id → projects` SetNull; index `(owner_id, project_id, type, created_at)`;
  **`ux_usage_events_first_per_project` unique `(project_id, type) WHERE type IN ('first_comment', 'first_apply')`** (`:55-58`). Filter strict-own (`AppDbContext.cs:263-268`).
  Excluded from the R8.5 reflection test (`Tests/WorkspaceTests.cs:312`). `IUnitOfWork.UsageEvents` (`IUnitOfWork.cs:10`).
- Event types in the wild: **server** `first_comment` (`CommentService.CreateAsync :249-262`, `Source = "api"`, race-safe via the unique index),
  `first_apply` (`:678-699`; the `Type = "first_apply"` literal at `:685`, the `catch (DbUpdateException) when …23505… / SqliteErrorCode == 19` at **`:694-698`** — re-verified 2026-09-23, GLM noted the earlier `:686-693` drift); **CLI** `installed` (`cli/src/commands/init.ts:815,1379,1596` — `meta {stack, aiTool, injected, cliVersion, mode}`), `doctor_run` (`doctor.ts`), `apply_started`/`apply_failed` (`apply/run.ts`), `first_apply` is also posted by the CLI (`run.ts:145,395`) — accepted? **No**: `RecordEventValidator.cs:10-12` allows only `installed | doctor_run | apply_started | apply_failed | widget_language`, so the CLI's `first_apply` post is rejected with 400 today and the server emission is the only source (verify with `Tests/RecordEventValidatorTests.cs`); **widget** `widget_language` (`web-component/src/element.ts:448-456`, `Source = "web-component"`, `meta {ui, browser, page}`).
- `POST /api/events` (`API/Controllers/EventsController.cs:15-33`, `[Authorize]`, `[EnableRateLimiting("events")]`) → `UsageEventService.RecordEventAsync(type, source, projectKey, meta)` (`Application/Services/Implementation/UsageEventService.cs:19-59`; owner = project's `OwnerId` when a key is given, else `currentUser.TenantId`).
  `GET /api/admin/events/summary?projectId` (`:35-42`, Admin) → `GetSummaryAsync` (counts + first-at per type).
- Retention (DB-08): `API/Hosted/RetentionService.cs` — `RetentionOptions { Enabled, UsageEventsDays = 180, …, BatchSize = 5000, IntervalHours = 24, InitialDelayMinutes = 5 }` (`:12-22`),
  `SweepOnceAsync(AppDbContext, RetentionOptions, ILogger, ct)` (`:99-118`, internal static, called from `SweepAsync :86`), `SweepUsageEventsAsync` deletes `CreatedAt < cutoff && Type != "first_comment" && Type != "first_apply"` (`:142`) in id-ordered batches.
  `RetentionSweepResult(int UsageEventsDeleted, int NotificationsDeleted, int SnapshotsDeleted, int InvitesDeleted)` (`:25-30`). Config `Retention:*` (`API/appsettings.json:23-29`, `docker-compose.prod.yml:39-43`)
  is bound **field by field** in `RetentionService.BindOptions()` (`:66-77`: `new() { Enabled = config.GetValue("Retention:Enabled", true), UsageEventsDays = config.GetValue("Retention:UsageEventsDays", 180), … }`) — a new option that is not added there keeps its C# default no matter what the environment says (GLM DB-15 #1).
- Demo: `DemoService.ProvisionAsync` (`:45-`; creates workspace + identity + seeded project/comments; token issued at `:208`), `UpgradeAsync` (`:257-`; guard `!user.IsDemo → Forbidden :282-283`, `SaveChangesAsync :330`, `Issue :338`).
  `API/Hosted/DemoCleanupService.cs` hard-deletes expired demo workspaces hourly → `usage_events.owner_id` becomes NULL (FK SetNull) — rows survive.
- Widget-on-a-real-site signal: `GET /api/public/projects/{key}/widget-status` (`API/Controllers/WidgetPublicController.cs:14-23`, **anonymous**, passes `Request.RequestOrigin()`) → `ProjectService.CheckWidgetActiveAsync(key, origin)` (`:1080-1133`): resolves the project by key with `IgnoreQueryFilters`, `Take(2)`, ambiguity → `Active = false`; projection `{ Id, IsActiveLocal, IsActiveStaging, IsActiveProduction }` (`:1094`, no `OwnerId`); `OriginNormalizer.Normalize(origin)` (`:1105-1108`). Localhost detection exists as `ProjectService.IsLocalhostOrigin(string normalisedOrigin)` **private static** (`:71-78`: `localhost`, `127.0.0.1`, `::1`, `*.localhost`).
  The widget calls it on every load; `capture-config` (`CaptureConfigController.cs:21`, `[Authorize]`) only after login — so `widget-status` is the installed signal (D15.2).
- Stats surface: `API/Controllers/Admin/StatsController.cs` (`[Route("api/admin/stats")]`, class `Policies.Admin`, `GetInsights` overrides with `Policies.SuperAdmin` `:24-33`; tag defaults to `Stats`, present in `orval.config.ts:6`).
  DTO folder `Application/DTOs/Stats/` (`PlatformInsightsResponse.cs` defines `CountStat :19`, `FunnelInsights :54`, `WorkspaceFunnelStat :76` — **do not reuse those names**).
  In-memory aggregation precedent: `PlatformInsightsService` (`Application/Services/Implementation/PlatformInsightsService.cs`, header comment cites `AiRuleService.GetInsightsAsync`); tenant-name batch `BuildTenantNameMapAsync :296-310`.
- Filtered-unique-index-on-Sqlite fact: Sqlite honours the `HasFilter` string (snake_case columns in both) — `Tests/UsageEventFirstCommentTests.cs` proves the race path on Sqlite. `AreNullsDistinct(false)` translates only for Npgsql (`WorkspaceSettingMapping.cs:30` precedent; SCHEMA.md "Cross-table facts").
- Test fixtures: Sqlite `TestDb` (`Tests/RetentionServiceTests.cs:36-62`; `SweepOnceAsync` driven directly), `Tests/UsageEventFirstCommentTests.cs`, `Tests/WidgetActivationTests.cs` (drives `CheckWidgetActiveAsync`), `Tests/DemoSessionEmailTests.cs` / `DemoUpgradeTests.cs` (DemoService fixtures), `Tests/RecordEventValidatorTests.cs`, `Tests/TenantQueryFilterTests.cs:20-46`.
- Dashboard: `../pointer-dashboard/react/src/features/overview/{OverviewPage,OverviewCharts,InsightPanels}.tsx` (super-admin insight panels precedent).

## 3. Design

### 3.1 The funnel, as identifiers (D15.1) — `Application/Common/UsageEventTypes.cs`

```csharp
/// <summary>Frozen identifiers (R10): a value written to usage_events.type is never renamed. Append new ones; never reuse.</summary>
public static class UsageEventTypes
{
    // F4 activation steps, in order. One-shot facts: emitted by the SERVER exactly once per workspace/project, never swept (DB-08 exclusion below).
    public const string DemoStarted = "demo_started";               // per demo workspace (DemoService.ProvisionAsync)
    public const string WorkspaceConverted = "workspace_converted"; // per workspace (DemoService.UpgradeAsync)
    public const string WidgetInstalled = "widget_installed";       // per project: first widget-status hit from a NON-localhost origin
    public const string FirstComment = "first_comment";             // per project (exists)
    public const string FirstApply = "first_apply";                 // per project (exists)
    public static readonly string[] OneShotFacts = { DemoStarted, WorkspaceConverted, WidgetInstalled, FirstComment, FirstApply };
    // Volume events (client-posted or server): rolled up daily, then swept after Retention:UsageEventsDays.
    public const string Installed = "installed"; public const string DoctorRun = "doctor_run"; public const string ApplyStarted = "apply_started";
    public const string ApplyFailed = "apply_failed"; public const string WidgetLanguage = "widget_language";
    public static readonly string[] ClientPostable = { Installed, DoctorRun, ApplyStarted, ApplyFailed, WidgetLanguage }; // = RecordEventValidator's list; test asserts equality
}
```
Step semantics per **workspace** (the funnel unit is the workspace, F3/F4): `demo_started` = a `demo_started` row exists (count rows — a hard-deleted demo
keeps its row with `owner_id NULL`); `workspace_converted` = a row exists; `widget_installed` = **any** project of the workspace has one; `first_comment` /
`first_apply` = any project has one. A workspace that never started as a demo (invite / self-serve) enters at step 3 — the funnel reports both "demo path"
and "all workspaces" columns. **NULL-owner rows (hard-deleted workspaces) — one rule for every step and every series (GLM DB-15 #2):** a one-shot row with
`OwnerId = NULL` counts toward its step's total (the workspace existed) and toward its week via the row's **own** `created_at`; it **never merges with another
NULL-owner row** — in the in-memory aggregation, group by `OwnerId` for non-null owners and treat each NULL-owner row as its own pseudo-workspace (key on
`e.Id`), so two hard-deleted demos that both reached `first_apply` are two activated workspaces, not one. The SQL sketch below is the definition, not the
implementation. **Weekly number** (D15.3): *activated workspaces per ISO week* = number of workspaces whose **earliest** `first_apply` row
falls in that week (`date_trunc('week', min(created_at)) GROUP BY owner_id`) — the one line for the weekly note; secondary weekly series: demos started,
converted, widget installed (first per workspace), first comment (first per workspace), plus conversion ratios step N / step N−1 over the selected window.

`RetentionService.SweepUsageEventsAsync :142`: replace `e.Type != "first_comment" && e.Type != "first_apply"` with `!UsageEventTypes.OneShotFacts.Contains(e.Type)`
(translates to `NOT IN`). `RecordEventValidator.cs:10-12`: replace the literal list with `UsageEventTypes.ClientPostable.Contains(t)` — server-only types stay
unpostable by clients (a client cannot forge a funnel step).

### 3.2 Emission sites (server, `Source = "api"`)

| Step | File / method | Exact placement | Row |
|---|---|---|---|
| `demo_started` | `DemoService.ProvisionAsync` | immediately after the seed `SaveChangesAsync` (`:204`, the one before `demoUser.Role = role;` / `Issue :208` today) | `{ Type = DemoStarted, Source = "api", OwnerId = <workspace id>, ProjectId = <seeded demo project id>, UserId = demo identity public_id, CreatedAt = UtcNow }` — one per provision; no uniqueness needed (a fresh workspace each time) |
| `workspace_converted` | `DemoService.UpgradeAsync` | after the successful `SaveChangesAsync` (`:330` today), before `Issue` (`:338`) | `{ Type = WorkspaceConverted, OwnerId = <workspace id>, UserId = caller }`; the `!IsDemo → Forbidden` guard (`:282-283`) makes a second emission impossible |
| `widget_installed` | `ProjectService.CheckWidgetActiveAsync` | after `projectMatches.Count == 1` and the activation check pass (`:1098-1103`), **only when** `origin` is non-blank **and** `!IsLocalhostOrigin(normalized)` (make the method `internal static`; add `p.OwnerId` to the `:1094` projection) | guarded insert: `if (!_cache.TryGetValue($"widget_installed:{project.Id}", out _))` → `if (!await UsageEvents.IgnoreQueryFilters().AnyAsync(e => e.ProjectId == project.Id && e.Type == WidgetInstalled))` → `Add { Type = WidgetInstalled, OwnerId = project.OwnerId, ProjectId = project.Id, Meta = JsonSerializer.Serialize(new { origin = normalized }) }` + `SaveChangesAsync` inside the **same `try/catch (DbUpdateException) when 23505 / Sqlite 19`** as `CommentService.cs:686-693` (copy it verbatim; `ClearChangeTracker()` in the catch) → then `_cache.Set(key, true, TimeSpan.FromHours(24))` in both the success and the caught-duplicate paths. `ProjectService` constructor (`:24`) gains `IMemoryCache` (registered, `Infrastructure/DependencyInjection.cs:33`); `TestProjectServiceDeps` (`Tests/TestProjectServiceDeps.cs`) passes a `MemoryCache`. `Meta.origin` is the customer's site origin — not personal data; ≤ 2000 chars by construction (origins are short) |

New partial unique index (race safety, R1): `UsageEventMapping.cs` — after the existing one:
`builder.HasIndex(e => new { e.ProjectId, e.Type }).IsUnique().HasFilter("type = 'widget_installed'").HasDatabaseName("ux_usage_events_widget_installed_per_project");`
(a **separate** index rather than widening the existing filter — widening would be `DropIndex` + `CreateIndex`, an `index change` marker; this is plain `CreateIndex`.)
`demo_started`/`workspace_converted` need no index: one emission point each, guarded by program flow.

**`widget_installed` is forgeable, by design (GLM DB-15 #4):** `widget-status` is anonymous, so anyone can `curl -H 'Origin: https://x.example' …/widget-status`
for a known project key and mint the one-shot fact. The row carries only an origin string (≤ 2000 chars, R12) — no PII, no privilege — and inflates a
pre-launch metric for a product with one real workspace. Accepted under D15.2; not a bug. If it ever matters, the fix is a second fact keyed on an
authenticated `capture-config` hit, never a change to this one (R10).

### 3.3 Table `usage_daily` — `Domain/Entity/UsageDaily.cs` (**not** a `BaseEntity`)

| property | column | type | null | notes |
|---|---|---|---|---|
| `Id` | `id` | `bigint` identity | no | |
| `Day` | `day` | `date` | no | UTC calendar day (`DateOnly`; Npgsql maps `DateOnly` → `date`) |
| `OwnerId` | `owner_id` | `uuid` FK `workspaces(id)` **ON DELETE SET NULL**, `fk_usage_daily_workspaces_owner_id` | yes | NULL = events with no workspace or a hard-deleted one. Named `OwnerId` for the R8 shape; excluded from `HardDeleteOrder` (analytics record, DB-12/13 precedent) |
| `Type` | `type` | `varchar(40)` | no | `usage_events.type` value |
| `Count` | `count` | `int` | no | rows that day |
| `ComputedAt` | `computed_at` | `timestamptz` | no | last (re)computation |

Unique index `ux_usage_daily_day_owner_type (day, owner_id, type)` `.AreNullsDistinct(false)` (Npgsql; Sqlite tests never write two NULL-owner rows for the same
key because the job upserts in memory — §3.4). Index `ix_usage_daily_owner_day (owner_id, day)`. Filter: strict-own copy of `UsageEvent` (`AppDbContext.cs:263-268`).
`DbSet<UsageDaily> UsageDaily` on `AppDbContext`, `IUnitOfWork`, `UnitOfWork`. `Tests/WorkspaceTests.cs:311-312` → add `&& t != typeof(UsageDaily)` with the comment
"analytics rollup: FK SET NULL, survives the workspace (DB-15)". Retention of `usage_daily`: **forever** (D15.4; ≤ workspaces × 10 types rows per day).

**Migration `AddUsageDailyAndWidgetInstalledIndex`** (scaffolded): `CreateTable("usage_daily", …)` + FK + 2 indexes **and** `CreateIndex("ux_usage_events_widget_installed_per_project", …, filter: "type = 'widget_installed'")`.
Nothing else (an operation on any other table → stop and report). No marker. `Down()` = generated (`DropIndex` + `DropTable`). **Every existing row:** untouched.

### 3.4 The rollup job — `API/Hosted/UsageRollup.cs` (static; run by `RetentionService` **before** the usage sweep)

```csharp
/// <summary>DB-15. Recomputes usage_daily for every UTC day in [today-RollupDays, yesterday] plus every day in the retention window that has no rows yet
/// (first run backfills the whole window). Provider-agnostic upsert in memory (no ON CONFLICT): rows per (day, owner, type) are tiny. Idempotent: a re-run
/// produces identical counts. Runs INSIDE RetentionService.SweepOnceAsync before SweepUsageEventsAsync; if it throws, that pass skips the usage_events
/// delete so no un-rolled row is lost.</summary>
internal static async Task<int> RollupAsync(AppDbContext db, RetentionOptions o, DateTime nowUtc, ILogger log, CancellationToken ct)
```
Algorithm, per day `d` (one `SaveChangesAsync` per day; days processed oldest → newest):
0. **Argument validation (the failure-injection seam §6 test 2 relies on, GLM DB-15 #3):** first statement of `RollupAsync`:
   `if (o.RollupDays < 0 || o.UsageEventsDays < 0 || o.BatchSize <= 0) throw new ArgumentOutOfRangeException(nameof(o), "Retention: RollupDays/UsageEventsDays must be ≥ 0 and BatchSize > 0");`.
   `RollupDays = 0` is legal (only the missing-days backfill runs).
1. `days` = `[today − o.RollupDays, …, yesterday]` ∪ `{ d ∈ [today − o.UsageEventsDays, yesterday] : d ∉ SELECT DISTINCT day FROM usage_daily }`; today is never rolled (incomplete).
2. `groups = db.UsageEvents.IgnoreQueryFilters().Where(e => e.CreatedAt >= d && e.CreatedAt < d + 1 day).GroupBy(e => new { e.OwnerId, e.Type }).Select(g => new { g.Key.OwnerId, g.Key.Type, Count = g.Count() }).ToListAsync(ct)`.
3. `existing = db.UsageDaily.IgnoreQueryFilters().Where(x => x.Day == d).ToListAsync(ct)`; for each group: update `Count`/`ComputedAt` if a row matches `(OwnerId, Type)` (null-safe compare), else `Add`; rows in `existing` with no group → `Count = 0` (events were deleted/moved; keep the row so a re-run is stable).
4. `SaveChangesAsync(ct)`; `ClearChangeTracker`.
`RetentionOptions` gains `public int RollupDays { get; init; } = 3;` — **and `BindOptions()` (`RetentionService.cs:66-77`) gains the line
`RollupDays = config.GetValue("Retention:RollupDays", 3),`** (without it the env override is silently ignored, GLM DB-15 #1); `docker-compose.prod.yml:39-43` gets
`Retention__RollupDays: "${RETENTION_ROLLUP_DAYS:-3}"`, `appsettings.json` `"RollupDays": 3`, `.env.prod.example` a commented `RETENTION_ROLLUP_DAYS=3`.
`SweepOnceAsync` (`:99-118`) becomes: `if (!o.Enabled) …; var rollupOk = true; try { await UsageRollup.RollupAsync(db, o, now, log, ct); } catch (Exception ex) { rollupOk = false; log.LogError(ex, "Retention: usage rollup failed; skipping usage_events sweep this pass"); } var usageEvents = rollupOk ? await SweepUsageEventsAsync(…) : 0; …` — the other three sweeps run regardless. `RetentionSweepResult` is unchanged (rollup outcome is logged, not returned) so `RetentionServiceTests` compile untouched.

### 3.5 Endpoints — `StatsController` + `Application/Services/Implementation/ActivationStatsService.cs : IActivationStatsService`

| Route | Policy | Response |
|---|---|---|
| `GET /api/admin/stats/funnel?weeks=12` (`[Authorize(Policy = Policies.SuperAdmin)]`, `[ProducesResponseType(typeof(ActivationFunnelResponse), 200)]`; DB-12 `[NoAudit("read")]`) | SuperAdmin | `ActivationFunnelResponse { DateTime From, To; List<ActivationStepStat> Steps /* Key, Label, Workspaces (all-time distinct), DemoPathWorkspaces, RateFromPrevious (double?) */; List<ActivationWeekStat> Weeks /* WeekStart (Monday UTC), DemosStarted, Converted, WidgetInstalled, FirstComment, Activated (= first_apply) */; int ActivatedThisWeek; int ActivatedLastWeek; List<ActivationWorkspaceRow> Recent /* WorkspaceId, WorkspaceName, StepReached, ReachedAt — last 50 workspaces by latest step time; names via Workspaces.IgnoreQueryFilters() batch */ }`. Source: `UsageEvents.IgnoreQueryFilters().Where(e => OneShotFacts.Contains(e.Type))` materialised (tiny: ≤ 5 rows per workspace/project), aggregated in memory (`PlatformInsightsService` precedent) with the §3.1 NULL-owner rule: `workspaceKey = e.OwnerId?.ToString() ?? $"deleted:{e.Id}"`; `Recent` rows for NULL owners show `WorkspaceName = "Deleted workspace"`, `WorkspaceId = null`. `weeks` 1–52, default 12 |
| `GET /api/admin/stats/activation` (class-level Admin; `[ProducesResponseType(typeof(WorkspaceActivationResponse), 200)]`; `[NoAudit("read")]`) | Admin (workspace) | `TenantStamp.TryRequireOwner` else `Forbidden` (a super admin uses `/funnel`; under a DB-13 impersonation token `TryRequireOwner` is false → use `_currentUser.TenantId` when `IsImpersonating` — add that branch only if DB-13 is merged); `UsageEvents.Where(e => e.OwnerId == owner && OneShotFacts.Contains(e.Type))` (filter + explicit predicate) → `WorkspaceActivationResponse { List<ActivationStepStatus> Steps /* Key, Label, Done, ReachedAt, ProjectKey? */; string? NextStepKey; bool IsDemo }` — steps `widget_installed`, `first_comment`, `first_apply` (+ `demo_started`/`workspace_converted` only when present) |

DTOs in `Application/DTOs/Stats/ActivationFunnelResponse.cs` and `WorkspaceActivationResponse.cs`. Labels (en) live in the DTO as constants; the dashboard maps
keys to i18n. `GET /api/admin/events/summary` is unchanged.

### 3.6 What is deliberately not changed

`GetSummaryAsync`, the CLI (its `first_apply` post keeps being rejected — the server is the source of truth; note it for the CLI backlog), the widget
(`widget-status` is already called on every load with the browser's `Origin`), `RetentionOptions.UsageEventsDays` (180), `PlatformInsightsService` (comment-status
funnel — a different funnel; keep both, the dashboard labels them "Activation" vs "Comment flow").

### 3.7 Owner decisions encoded here (defaults apply unless the owner says otherwise before §9)

| # | Question | Default |
|---|---|---|
| D15.1 | Step definitions | §3.1 as written; unit = workspace; `widget_installed` per project, counted once per workspace |
| D15.2 | What "widget installed" means | first `GET /api/public/projects/{key}/widget-status` from a **non-localhost** origin (`ProjectService.IsLocalhostOrigin`), unauthenticated — i.e. the widget rendered on a deployed site. Not the CLI `installed` event (a repo install), not `capture-config` (requires login) |
| D15.3 | The weekly number | **Activated workspaces per ISO week** = workspaces whose earliest `first_apply` falls in that week. Reported with demos started, converted, installed, first comment, and step ratios |
| D15.4 | Retention of `usage_daily` and of the five one-shot facts | **Forever** (both tiny). Volume events keep the 180 d sweep |

## 4. Safety classification

**Additive** (R1): one table, one partial unique index, three new `type` values written only by the server, one hosted computation that **inserts/updates `usage_daily`
only** and never deletes from `usage_events` (the deletion is DB-08's existing sweep, now ordered after the rollup and skipped when the rollup fails). Ordinary deploy (R7).
Tenancy: `usage_daily` strict-own + explicit owner predicate on the workspace endpoint; §6 test 6 proves B sees nothing of A. Existing rows untouched.

## 5. File-level tasks

1. `Application/Common/UsageEventTypes.cs` (§3.1 verbatim). `Application/Validators/RecordEventValidator.cs:10-12` → `UsageEventTypes.ClientPostable.Contains(t)`. `API/Hosted/RetentionService.cs:142` → `!UsageEventTypes.OneShotFacts.Contains(e.Type)`; `CommentService.cs:254,685` → the constants.
2. `Domain/Entity/UsageDaily.cs`; `Infrastructure/Mappings/UsageDailyMapping.cs` (copy `UsageEventMapping.cs`; `Day` → `HasColumnName("day").HasColumnType("date")`; unique index with `.AreNullsDistinct(false)`); `UsageEventMapping.cs` → the new partial unique index (§3.2). `AppDbContext.cs` → DbSet + strict-own filter (copy `:263-268`); `IUnitOfWork`/`UnitOfWork` → `DbSet<UsageDaily> UsageDaily`. `Tests/WorkspaceTests.cs:311-312` → exclusion.
3. `just migrate name="AddUsageDailyAndWidgetInstalledIndex"` → read against §3.3 (one table, one FK, two indexes on it, one index on `usage_events`; anything else → stop and report).
4. `DemoService.ProvisionAsync` / `UpgradeAsync` — §3.2 rows 1–2 (`_unitOfWork.UsageEvents.Add(...)`; `SaveChangesAsync`; wrap in `try/catch (Exception ex) { /* analytics must never fail a demo */ }` like the `CommentService` emissions).
5. `ProjectService.CheckWidgetActiveAsync` — §3.2 row 3; `IsLocalhostOrigin` → `internal static`; constructor gains `IMemoryCache cache`; `Tests/TestProjectServiceDeps.cs` passes `new MemoryCache(new MemoryCacheOptions())`.
6. `API/Hosted/UsageRollup.cs` (§3.4, including step 0's `ArgumentOutOfRangeException`); `RetentionService.cs` — `RollupDays` option **+ the `BindOptions()` line `RollupDays = config.GetValue("Retention:RollupDays", 3),` at `:66-77`** + `SweepOnceAsync` ordering (§3.4); `API/appsettings.json:23-29` + `docker-compose.prod.yml:39-43` + `.env.prod.example` → `RollupDays`.
7. `Application/DTOs/Stats/ActivationFunnelResponse.cs`, `WorkspaceActivationResponse.cs`; `Application/Services/Interfaces/IActivationStatsService.cs` + `Implementation/ActivationStatsService.cs` (§3.5). `API/Controllers/Admin/StatsController.cs` — two actions after `GetWorkspaceInsights` (`:35-42`), same result-mapping shape.
8. `Application/Resources/MessageKeys.cs` — `Stats.WeeksOutOfRange = "weeks must be between 1 and 52."`.
9. `DEPLOY.md` § Backups — extend DB-08's retention paragraph with one sentence: "Before each sweep the job rolls `usage_events` up into `usage_daily` (per day/workspace/type, kept forever); the five funnel facts (`demo_started`, `workspace_converted`, `widget_installed`, `first_comment`, `first_apply`) are never deleted."
10. Tests (§6); `just fmt`; `just test`; `docs/db/SCHEMA.md` rows `usage_daily` (planned → present) and `usage_events` (new index, five kept types).

## 6. Tests

Sqlite `TestDb` (`RetentionServiceTests.cs:36-62`) unless noted:
1. `Tests/UsageRollupTests.cs` — `Rollup_GroupsByDayOwnerType` (seed 3 events on day D for (A, installed), 2 for (A, doctor_run), 1 for (null owner, installed) → 3 `usage_daily` rows with counts 3/2/1); `Rollup_IsIdempotent` (run twice → same rows, same counts, `ComputedAt` advanced); `Rollup_RecomputesWindow_UpdatesCount` (add an event on D−1 after the first run → second run updates that row); `Rollup_NeverTouchesToday`; `Rollup_FirstRun_BackfillsWholeWindow` (events 100 days old → their day rolled); `Rollup_NegativeRollupDays_Throws` (`RollupDays = -1` → `ArgumentOutOfRangeException` before any query); `Rollup_ZeroRollupDays_OnlyBackfillsMissingDays`; `BindOptions_ReadsRollupDaysFromConfiguration` (`ConfigurationBuilder` with `Retention:RollupDays = 7` → `RollupDays == 7`; drive `BindOptions()` by making it `internal static RetentionOptions BindOptions(IConfiguration config)` — a one-line refactor, `SweepAsync` calls it with `config`).
2. `Tests/RetentionServiceTests.cs` — extend: `Sweep_RollsUpBeforeDeleting` (old volume events → after `SweepOnceAsync` they are gone **and** `usage_daily` has their counts); `Sweep_KeepsAllFiveOneShotFacts` (replace the existing `first_*` fact with the five types); `Sweep_SkipsUsageDeleteWhenRollupFails` (pass `RollupDays = -1` → `RollupAsync` throws `ArgumentOutOfRangeException` → old usage events still present, notifications still swept).
3. `Tests/DemoSessionEmailTests.cs` / `DemoUpgradeTests.cs` — `Provision_EmitsDemoStarted_WithWorkspaceOwner`; `Upgrade_EmitsWorkspaceConverted_Once` (second upgrade attempt → Forbidden, still one row).
4. `Tests/WidgetActivationTests.cs` — `WidgetStatus_NonLocalhostOrigin_EmitsWidgetInstalledOnce` (two calls from `https://app.example.com` → one row; `Meta` contains the origin); `WidgetStatus_LocalhostOrigin_EmitsNothing` (`http://localhost:5173`, `http://127.0.0.1`, `http://app.localhost`); `WidgetStatus_NoOrigin_EmitsNothing`; `WidgetStatus_AmbiguousKey_EmitsNothing`; `WidgetInstalled_RaceOnUniqueIndex_Swallowed` (insert the row first, clear the cache, call again → no exception).
5. `Tests/RecordEventValidatorTests.cs` — `ServerOnlyTypes_AreRejected` (`[Theory]` over the five one-shot facts → invalid); `ClientPostable_MatchesValidator` (`UsageEventTypes.ClientPostable` set-equals the accepted set).
6. `Tests/ActivationStatsTests.cs` (InMemory `TenantQueryFilterTests` fixture for filters; service over InMemory): `Funnel_CountsWorkspacesPerStep_AndDemoPath`; `Funnel_WeeklyActivated_UsesEarliestFirstApplyPerWorkspace` (two projects in one workspace with `first_apply` in different weeks → counted once, in the earlier week); `Funnel_HardDeletedDemo_StillCountedAsStarted` (row with `OwnerId = null`); `Funnel_TwoHardDeletedDemos_CountedSeparately` (two `first_apply` rows with `OwnerId = null` in different weeks → `Activated` = 1 in each week and `Steps[first_apply].Workspaces` = 2 — the NULL-owner rule of §3.1); `Activation_TenantB_SeesNothingOfA` (R8: B's response has every step `Done = false` although A has all; `usage_daily` under B's context empty); `Activation_SuperAdmin_Forbidden`; `Funnel_WeeksValidated`.
7. Existing data survives: N/A for rows; rehearsal (§7 criterion 6) proves the first rollup pass over the production dump and that the sweep still deletes only old volume rows.

## 7. Acceptance criteria

1. `dotnet ef migrations list -p Infrastructure -s API --no-connect` ends with `_AddUsageDailyAndWidgetInstalledIndex`; `grep -c ContractMigration Infrastructure/Migrations/*_AddUsageDailyAndWidgetInstalledIndex.cs` → 0.
2. `grep -rn '"first_comment"\|"first_apply"\|"demo_started"\|"workspace_converted"\|"widget_installed"' Application API --include='*.cs' | grep -v "UsageEventTypes.cs\|HasFilter" | wc -l` → 0 (constants everywhere; the two `HasFilter` strings are the allowed literals).
3. `grep -c "OneShotFacts" API/Hosted/RetentionService.cs` → 1; `grep -c "UsageRollup.RollupAsync" API/Hosted/RetentionService.cs` → 1 and it precedes `SweepUsageEventsAsync` in `SweepOnceAsync` (reviewer reads).
4. `grep -c "widget_installed" Infrastructure/Mappings/UsageEventMapping.cs` → 1; `grep -c "IMemoryCache" Application/Services/Implementation/ProjectService.cs` → ≥ 1.
4a. `grep -c 'Retention:RollupDays' API/Hosted/RetentionService.cs` → 1 (the `BindOptions` line); `grep -c "ArgumentOutOfRangeException" API/Hosted/UsageRollup.cs` → 1; `grep -c "RETENTION_ROLLUP_DAYS" docker-compose.prod.yml .env.prod.example` → 1 each.
5. `curl -s …/swagger.json | jq '.paths["/api/admin/stats/funnel"].get.tags, .paths["/api/admin/stats/activation"].get.tags'` → `["Stats"]` twice.
6. Rehearsal (R11) on a same-day dump: set `Retention__InitialDelayMinutes=0` on the rehearsal API, wait one pass → `SELECT day, type, sum(count) FROM usage_daily GROUP BY 1,2 ORDER BY 1` non-empty for every day that has events in the last 180 d; `SELECT count(*) FROM usage_events WHERE type IN ('first_comment','first_apply')` unchanged before/after; `GET /api/admin/stats/funnel` as super admin returns `Steps[3].Workspaces` (first_comment) equal to `SELECT count(DISTINCT owner_id) FROM usage_events WHERE type='first_comment'`; open the production project's site (non-localhost) once → one `widget_installed` row for it.
7. `just test` green with the 25+ new facts; DB-10 green.

## 8. Rollback

Migration `Down()` drops `usage_daily` (and its rolled-up counts — recomputable from raw rows still inside the retention window) and the widget-installed index.
Code rollback = revert + ordinary redeploy. The three new `type` values already written stay in `usage_events` (harmless; the old sweep would delete
`demo_started`/`workspace_converted`/`widget_installed` after 180 d — state this in the revert PR). No dump requirement beyond R6's pre-deploy dump.

## 9. Release steps

1. Merge after DB-11a is merged (demo sites). Ordinary R11 rehearsal (§7 criterion 6).
2. `bash scripts/deploy-api.sh`. Expect one `Applying migration` line; 5 min later `Retention: usage rollup wrote N day(s)` in the log, then the usual sweep lines.
3. Verify: `GET /api/admin/stats/funnel` as super admin; `GET /api/admin/stats/activation` as the real workspace admin shows `first_comment`/`first_apply` done; `SELECT count(*) FROM usage_daily`.
4. Watch for `Retention: usage rollup failed; skipping usage_events sweep` (must not appear; if it does, the raw rows are safe — fix and redeploy) and for `widget_installed` 23505 noise (must be absent thanks to the cache + `AnyAsync` pre-check).
5. Dashboard: `dashboard-agent` regenerates the client from production once, then §11. The §64 write-up quotes §3.1's definitions verbatim.

## 10. Out of scope

R5.7 (convert-to-workspace UX, 24 h TTL via the retention job); `GET /api/admin/events/summary` changes; CLI `first_apply` posting (stays rejected; CLI backlog);
per-user funnels; cohort retention curves; exporting the funnel; a DB view (the endpoint is the contract; a view would duplicate the definitions);
`PlatformInsightsService` (comment-status funnel; untouched); `usage_events.meta` schema beyond `{origin}` for `widget_installed`; `clients/`.

## 11. Dashboard / widget / CLI tasks

**Dashboard** (after client regen: `useGetApiAdminStatsFunnel`, `useGetApiAdminStatsActivation`, DTOs):
1. Super admin `OverviewPage.tsx`: an "Activation" panel (in `InsightPanels.tsx`, precedent block) — five-step bar (all workspaces vs demo path, step ratios), a weekly line/bar for `Weeks[]` with `Activated` highlighted, the headline "Activated this week: N (last week: M)", and the `Recent` table (workspace, step reached, when). `weeks` selector 4/12/26.
2. Workspace admin `OverviewPage.tsx`: a "Getting started" checklist card from `/activation` — widget installed, first comment, first applied comment (ticks + dates; the next undone step gets the primary action: "Install the widget" → project page / init skill link; "Post a comment" → open the app URL; "Apply a comment" → docs link). Hide the card once all three are done (collapsible "All set").
3. i18n keys for step labels (en + ar).

**Widget:** none (`widget-status` is already called with the browser `Origin`). **CLI:** none (backlog note: stop posting `first_apply`; it is rejected).

## 12. Cross-review adjudication (2026-09-22 reviews, folded 2026-09-23)

Reports: `docs/db/reviews/REVIEW-GLM-DB12-15-2026-09-22.md`, `docs/db/reviews/REVIEW-AGY-DB12-15-2026-09-22.md`. Every citation re-checked against the tree on 2026-09-23.

| Finding | Claim | Verdict | Where it landed |
|---|---|---|---|
| GLM DB-15 #1 (Minor) | `BindOptions()` not named → env override silently lost | **Accepted** — `RetentionService.cs:66-77` constructs the record field by field | §2 fact, §3.4, §5 task 6, §6 test 1, §7 crit. 4a |
| GLM DB-15 #2 (Minor) | NULL-owner grouping specified only for `demo_started` | **Accepted** — one rule for every step/series; no merging of NULL-owner rows | §3.1, §3.5, §6 test 6 |
| GLM DB-15 #3 (Minor) | Test 2 prescribes an exception the spec never requires | **Accepted** — step 0 argument validation added | §3.4 step 0, §5 task 6, §6 test 1 |
| GLM DB-15 #4 (Minor) | `widget_installed` forgeable by anyone | **Accepted in words** (D15.2 stands) | §3.2 paragraph |
| GLM citation drift | `first_apply` catch at `:694-698`, not `:686-693` | **Accepted** | §2 |
| agy DB-15 #1 (Minor) | Funnel events are not emitted via `RecordEventAsync` | **Confirmation, no change** — the doc already says the server writes them directly and the validator rejects client posts |
| agy DB-15 #2 (Minor) | Rollup idempotent; windows do not overlap | **Confirmation, no change** |
