# DB-12 — Append-only audit log (`audit_events`), `IAuditWriter`, request id, audit read API

Roadmap: §16 (un-held 2026-09-22), Release 5 row **R5.2**; foundations report §2 row 2 ("precondition for
F2"). Rules: R1, R5, R7 (one raw-SQL migration → explicit deploy path or an R7.1 batch), R8 (amended by this
doc: operator/analytics tables), R10, R11, R12, R13, R14 (audit rows carry uuids and hashes, never an
address), **R17 (new — append-only tables and the audit obligation; written by this doc into DB-RULES.md)**.
**Class: Additive** (one new table, three indexes, one FK) **+ one raw-SQL migration** (the append-only
trigger; `.Sql(` ⇒ DB-02 marker + `[ContractMigration("DB-12")]` ⇒ ships through `POINTER_APPLY_CONTRACT=1`,
alone as `pre-db12` or batched with DB-14 as `pre-db12-14` under R7.1).
**Status 2026-09-23: Complete.** Part 1 deployed to production 2026-09-23 (contract label `pre-db12`, commit `30d1b46`); **part 2 deployed to production 2026-09-23 03:42 UTC** (merged `fda2717`; attributes on 103 actions — 71 `[Audited]`, 32 `[NoAudit]` — and `IAuditWriter` calls in 20 services; `Audit:StrictCoverage=true` in Development). First production row verified (`auth.login.failed`, hashed e-mail, request id, ip hash). Reviews: Opus 11 findings + Gemini Pro 4, all applied. Dashboard **Security log page** (`/security-log`, workspace + super-admin /all views) shipped in pointer-dashboard `60bbd0d` and deployed; client `@moamen-ui/pointer-react` 1.0.42.
Owner decisions D12.1–D12.5 have defaults (§3.9); none blocks.

**Dependencies.** Independent of DB-11a for the *schema*. The *writer call sites* (§3.6) are written against
the **DB-11a versions** of `AuthService`, `UserService`, `InviteService`, `TenantService`, `DemoService`
(membership vocabulary: `actor_membership_id`, `member.*` actions). Implement DB-12 **after DB-11a is merged**
(it is being implemented now) so the implementer edits each service once. DB-11b (`auth.workspace_switched`),
DB-11c (`member.left`, `identity.erased`, `identity.erase_requested`) and DB-11d (`auth.email.change_requested`,
`auth.email.changed`) add their rows when they land — their action strings are reserved in §3.6 so no later
doc invents a second spelling. DB-13 depends on this doc (impersonation rows, `impersonation_session_id`).

## 1. Goal

Today nothing records who did what: an admin can disable a member, rotate a key, rename the workspace or
delete a tenant and the only trace is `updated_by` on the row (and nothing at all for deletes, logins, or
key use). Before real users arrive the product needs one append-only table that every security-relevant
mutation writes to, a request id to correlate a row with a log line, and a read API so a workspace admin can
see what happened in **their** workspace (and the operator across all of them). User-visible result: a
"Security log" page in the dashboard; the workspace admin can answer "who removed Sara on Tuesday?" and, after
DB-13, "did the operator look at our comments, when and why?".

## 2. Prerequisites (verified facts, 2026-09-22 @ `1af08ec`)

- 65 migrations; newest `20260922115038_DropUsersLegacyApiKey` (DB-11a adds three more; scaffold DB-12 **after**
  them). `just migrate name="…"` (`justfile:6`) = `dotnet ef migrations add … -p Infrastructure -s API`.
- DB-02 guard (`Tests/MigrationSafetyTests.cs:75-82`): risky-operation regex includes `\.Sql\(`; marker regex
  accepts exactly `R2 contract | R3 backfill | index change | R4 constraint` followed by `approved yyyy-mm-dd by <token>`;
  marker ⇔ `[ContractMigration("…")]` (`Infrastructure/Migrations/ContractMigrationAttribute.cs`). A trigger is
  none of the four; **use `R4 constraint`** (a trigger that enforces immutability is a constraint on the table) —
  R17 records this convention. DB-09 gate: `API/Startup/MigrationGate.cs` refuses a pending `[ContractMigration]`
  unless `DBApplyContractMigrations=true` (`Program.cs:166-177`).
- DB-10 CI (`.github/workflows/db-migrations.yml`): applies every migration to `postgres:15`, checks
  `has-pending-model-changes`, round-trips the newest `Down()`/`Up()`, has `psql` on the runner; DB-11a adds a
  "mixed-case duplicate e-mail" psql step after the round-trip step — DB-12 adds its trigger step right after it.
- Non-`BaseEntity` table precedent: `UsageEvent` (`Domain/Entity/UsageEvent.cs`, `Infrastructure/Mappings/UsageEventMapping.cs`:
  `HasKey`, snake_case `HasColumnName`, `HasOne<Workspace>().WithMany().HasForeignKey(e => e.OwnerId).OnDelete(SetNull).HasConstraintName("fk_usage_events_workspaces_owner_id")`,
  `HasIndex` composite). `UsageEvent` is excluded by name from the R8.5 reflection test
  (`Tests/WorkspaceTests.cs:305-313`: `t.GetProperty("OwnerId") != null && t != typeof(Workspace) && t != typeof(UsageEvent)`;
  `:323` asserts `HardDeleteOrder.Length == 22`, DB-11a makes it 23).
- jsonb converter: `Infrastructure/Mappings/JsonColumn.cs` — `ConfigureJsonColumn<T>(column, defaultSql)` for
  `Dictionary<string,string>` (sorted keys, tolerant parse, canonical comparer); precedent `comments.custom_fields` (R4-01).
- Query-filter shapes: `Infrastructure/AppDbContext.cs` — strict-own for `UsageEvent` `:263-268`; DbSets `:45-71`
  (last: `Workspaces` `:71`); audit-stamping `SaveChangesAsync` `:304-334` (loops `ChangeTracker.Entries<BaseEntity>()` only).
- `IUnitOfWork` exposes `DbSet<UsageEvent> UsageEvents`, `DbSet<Workspace> Workspaces` (`Application/Abstractions/IUnitOfWork.cs:10-11`,
  `Infrastructure/Repository/UnitOfWork.cs:21-22`); `ExecuteInTransactionAsync` (`IUnitOfWork.cs:18`).
- DI: Scrutor registers Application classes whose name ends in `Service` (`Application/DependencyInjection.cs:11-13`);
  Infrastructure registers by hand (`Infrastructure/DependencyInjection.cs:35 AddHttpContextAccessor`, `:45 ResetTokenService` singleton,
  `:54 NoopBillingProvider` scoped — the manual-DI precedent).
- `ICurrentUser` (`Application/Abstractions/ICurrentUser.cs`: `Id`, `IsAdmin`, `IsSuperAdmin`, `IsQuickAccess`, `TenantId`, `RoleId`)
  read from claims by `Infrastructure/CurrentUser/HttpCurrentUser.cs`. After DB-11a the JWT also carries `mstamp`; there is
  **no membership id claim** — the writer resolves `actor_membership_id` from `(sub, tenant)` (§3.4).
- `Program.cs`: `AddControllers(options => options.Filters.Add(new ProducesAttribute("application/json")))` `:49-52`;
  `UseForwardedHeaders` `:183-189`; the global exception handler `app.Use(...)` `:196-216`; `UseAuthentication/UseAuthorization`
  `:343-344`; `MapControllers` `:418`. **No request-id middleware exists** (`grep -rn "TraceIdentifier\|X-Request-Id" API Infrastructure Application` → nothing).
- Admin surface = every controller in `API/Controllers/Admin/` (15 files; class-level `[Authorize(Policy = Policies.Admin)]`
  or `Policies.SuperAdmin` — `TenantsController`, `SettingsController`, `PlansController`, `BrandingController`, `StatsController.GetInsights`).
  Their mutating actions (verified list, 53 actions) are enumerated in §3.6 with the service method each calls.
- Auth surface: `API/Controllers/AuthController.cs` (`Login :20`, `LoginWithInvite :38`, `LoginWithKey :48`, `Register :58`, `ForgotPassword :80`,
  `ResetPassword :91`, `RegisterAdmin :111`, `RegisterInvite :130`, `DeviceStart :151`, `DevicePoll :163`, `DeviceApprove :188`, `DeviceDeny :203`),
  `API/Controllers/MeController.cs` (`ChangePassword :21`, `UpdatePreferences :30`, `GetApiKey :49`, `RegenerateApiKey :59`, notification reads/marks),
  `API/Controllers/DemoController.cs` (`Create :17`, `Upgrade :37`), `API/Controllers/ExportImportController.cs` (exports `:38,:53`, imports `:78,:99`).
- Services (DB-11a versions; method names are stable, line numbers will move): `AuthService.LoginAsync/LoginWithApiKeyAsync/LoginWithInviteAsync/RegisterAsync/RegisterAdminAsync/RequestPasswordResetAsync/ResetPasswordAsync/ChangePasswordAsync`;
  `UserService.CreateAsync/ApproveAsync/RejectAsync/UpdateAsync/DeleteAsync/TransferOwnershipAsync`; `InviteService.CreateAsync/RevokeAsync/RotateQuickLinkAsync/AcceptAsync/ResendAsync`;
  `TenantInviteService.CreateAsync/ResendAsync/RevokeAsync`; `TenantService.CreateAsync/SetStatusAsync/ExtendDemoAsync/SetDemoConfigAsync/ChangePlanAsync/HardDeleteAsync`;
  `DemoService.ProvisionAsync/UpgradeAsync`; `ProjectService.CreateAsync/UpdateAsync/DeleteAsync/SetAppUrlAsync/DeleteAppUrlAsync`; `WorkspaceService.RenameAsync`;
  `CommentFieldService.UpdateDefinitionsAsync`; `RoleService`, `AppEnvironmentService`, `StatusAdminService`, `PredefinedActionService`, `SuggestionService`,
  `AiRuleService`, `PlanService`, `SettingsService`, `BrandingService`, `ApiKeyService.GetOrCreateAsync/RegenerateAsync`, `DeviceLoginService.ApproveAsync/DenyAsync`,
  `ExportImportService.Export*/Import*`. `API/Hosted/DemoCleanupService.cs:78` calls `HardDeleteAsync` with no HTTP context.
- Name resolution from `public_id`: `UserNameResolver.ResolveAsync(IUnitOfWork, IEnumerable<Guid>)` (DB-11a §3.5; falls back to `user_aliases`).
- Paging shape: `Application/Response/PagedData.cs:5-11` (`PagedData<T>(items, Pagination, …)`), `Pagination { PageNumber, PageSize, TotalItems, TotalPages }`
  (precedent `NotificationService.ListAsync`).
- `orval.config.ts:6` `filters.tags` — a controller tag missing there generates no client (CLAUDE.md rule 1b). Add `'Audit'`.
- Test fixtures: InMemory `AppDbContext` + `FakeCurrentUser` (`Tests/TenantQueryFilterTests.cs:20-46`); Sqlite `TestDb` (`Tests/RetentionServiceTests.cs:36-62`,
  `Tests/UsageEventFirstCommentTests.cs:43-62`); reflection-over-controllers precedent `Tests/AuthRateLimitingTests.cs:21-45`.
- Retention (DB-08): `API/Hosted/RetentionService.cs` sweeps exactly four tables; `audit_events` is **not** among them and must never be.
- Dashboard: `../pointer-dashboard/react/src/features/shell/Shell.tsx` (nav `:261-335`, `isSuperAdmin` `:300`), settings feature folder
  `features/settings/` (`SettingsPage.tsx`, `WorkspaceNameCard.tsx` — card precedent), `features/tenants/TenantsPage.tsx` (DataTable + row actions precedent).

## 3. Design

### 3.1 Table `audit_events` — `Domain/Entity/AuditEvent.cs` (**not** a `BaseEntity`; `long` PK)

| property | column | type | null | notes |
|---|---|---|---|---|
| `Id` | `id` | `bigint` identity | no | `long`; `UseIdentityByDefaultColumn()` |
| `OccurredAt` | `occurred_at` | `timestamptz` | no | `DateTime.UtcNow` set by the writer |
| `OwnerId` | `owner_id` | `uuid` FK `workspaces(id)` **ON DELETE SET NULL**, `fk_audit_events_workspaces_owner_id` | **yes** | the workspace the action happened in; **NULL** for operator-level actions (tenant create/hard-delete, plan/settings/branding edits, failed logins with no identity) and after the workspace is hard-deleted (the FK detaches the row; the trigger permits exactly that change, §3.2). Named `OwnerId`/`owner_id` so the R8 filter shape applies unchanged |
| `ActorUserId` | `actor_user_id` | `uuid` | yes | identity `public_id` (R14 content reference: no FK, never rewritten; tombstone resolves to "Deleted user"). NULL for `System` and for anonymous failures |
| `ActorMembershipId` | `actor_membership_id` | `int` | yes | `workspace_memberships.id` of the acting membership when the actor acted inside a workspace. **No FK** (memberships are hard-deleted with the workspace; the audit row survives). Resolved by the writer from `(sub, tenant)` |
| `ActorKind` | `actor_kind` | `int` | no | enum `AuditActorKind { User = 1, SuperAdmin = 2, System = 3, Impersonation = 4 }` (`Domain/Enums/AuditActorKind.cs`; append-only, R10). `Impersonation` = a super admin acting under a DB-13 session |
| `Action` | `action` | `varchar(64)` | no | dotted, lower-case, `^[a-z_]+(\.[a-z_]+){1,3}$`, from the catalogue §3.6 (`Application/Common/AuditActions.cs` constants — never a literal at a call site) |
| `TargetType` | `target_type` | `varchar(64)` | no | `workspace`, `user`, `membership`, `invite`, `tenant_invite`, `api_key`, `device_login`, `project`, `project_app_url`, `role`, `environment`, `status`, `predefined_action`, `suggestion`, `ai_rule`, `plan`, `settings`, `branding`, `export`, `import`, `impersonation_session`, `email_hash` (constants in `AuditTargets`) |
| `TargetId` | `target_id` | `varchar(128)` | yes | the target's id as text: uuid for identities/workspaces, int for rows, `sha256-16` for `email_hash` (§3.4). Never a raw e-mail |
| `Before` | `before` | `jsonb` default `'{}'` | no | `Dictionary<string,string>` via `JsonColumn`; **whitelisted keys only** (§3.5) |
| `After` | `after` | `jsonb` default `'{}'` | no | same |
| `RequestId` | `request_id` | `varchar(64)` | yes | from the middleware (§3.7); NULL for hosted jobs |
| `IpHash` | `ip_hash` | `varchar(64)` | yes | HMAC-SHA256(hex) of `RemoteIpAddress` keyed with `Audit:HashKey` (fallback `JWT:SigningKey`) — pseudonymous, not reversible without the key |
| `UserAgent` | `user_agent` | `varchar(256)` | yes | truncated to 256 |
| `ImpersonationSessionId` | `impersonation_session_id` | `bigint` | yes | DB-13's `impersonation_sessions.id`; **no FK** (logical, so DB-13 can ship after this doc); set by the writer from `ICurrentUser.ImpersonationSessionId` (DB-13) — until DB-13 exists the writer always writes NULL |

All properties `{ get; init; }` (no setter ⇒ no accidental EF update path from application code). Indexes:
`ix_audit_events_owner_occurred` `(owner_id, occurred_at DESC)`; `ix_audit_events_actor_occurred` `(actor_user_id, occurred_at DESC)`
(`HasIndex(...).IsDescending(false, true)`); `ix_audit_events_target` `(target_type, target_id)`. No unique index (rows are events).
Mapping `Infrastructure/Mappings/AuditEventMapping.cs` — copy `UsageEventMapping.cs` shape; `Before`/`After` via
`b.Property(x => x.Before).ConfigureJsonColumn("before", "'{}'")`. `DbSet<AuditEvent> AuditEvents` on `AppDbContext` (after `:71`),
`IUnitOfWork`, `UnitOfWork` (same shape as `UsageEvents`).

**Query filter** — strict-own, copy of `UsageEvent` (`AppDbContext.cs:263-268`): a workspace admin sees rows with `owner_id == tenant`;
a super admin sees all (audit rows are metadata — DB-13 does not change this filter). Rows with `owner_id IS NULL` are visible only to
super admins in production (`strict=true`).

**Code-level append-only guard** (provider-agnostic, testable on InMemory): in `AppDbContext.SaveChangesAsync` (`:304`), before the
stamping loop, add
```csharp
// DB-12: audit_events is append-only. The Postgres trigger is the authority; this is the early, provider-agnostic error.
if (ChangeTracker.Entries<AuditEvent>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
    throw new InvalidOperationException("audit_events is append-only (DB-12): update/delete is not allowed.");
```

### 3.2 Migrations (two files, this order)

**Migration 1 — `AddAuditEvents`** (scaffolded). Expected `Up()`: `CreateTable("audit_events", …)` with the columns above (`before`/`after`
`jsonb` with `defaultValueSql: "'{}'"`), `AddForeignKey`/table-builder FK `fk_audit_events_workspaces_owner_id … onDelete: ReferentialAction.SetNull`,
`CreateIndex` × 3 (`descending: new[] { false, true }` on the two time indexes). Nothing else — an operation on any other table means a mapping
edit leaked: stop and report. No marker, no attribute (R1). `Down()` = generated `DropTable`.

**Migration 2 — `AddAuditEventsAppendOnlyTrigger`** (scaffolded with **no** model change → empty `Up()`/`Down()`; fill by hand).
`[ContractMigration("DB-12")]` on the class and, on the line above `Up(`, verbatim:
`// DB-RULES: R4 constraint approved 2026-09-22 by Moamen (owner; foundations report §2 row 2 "append-only audit log", relayed by the orchestrator; docs/db/execution/DB-12-audit-log.md)`
`Up()` is exactly one `migrationBuilder.Sql(@"…")` containing:
```sql
CREATE OR REPLACE FUNCTION audit_events_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'UPDATE' THEN
    -- The ONLY permitted change: fk_audit_events_workspaces_owner_id ON DELETE SET NULL detaching the row
    -- from a hard-deleted workspace. Every other column must be byte-identical.
    IF OLD.owner_id IS NOT NULL AND NEW.owner_id IS NULL
       AND (to_jsonb(NEW) - 'owner_id') = (to_jsonb(OLD) - 'owner_id') THEN
      RETURN NEW;
    END IF;
  END IF;
  RAISE EXCEPTION 'audit_events is append-only (DB-12): % is not allowed', TG_OP USING ERRCODE = 'restrict_violation';
END $$;
CREATE TRIGGER trg_audit_events_append_only BEFORE UPDATE OR DELETE ON audit_events FOR EACH ROW EXECUTE FUNCTION audit_events_append_only();
CREATE TRIGGER trg_audit_events_no_truncate BEFORE TRUNCATE ON audit_events FOR EACH STATEMENT EXECUTE FUNCTION audit_events_append_only();
```
`Down()` is exactly one call: `migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_audit_events_no_truncate ON audit_events; DROP TRIGGER IF EXISTS trg_audit_events_append_only ON audit_events; DROP FUNCTION IF EXISTS audit_events_append_only();");`
**What the exemption is, honestly (GLM DB-12 #4):** the trigger admits the FK action's *shape* — `owner_id` non-null → NULL, every other column
byte-identical — it cannot attribute the UPDATE to the FK (Postgres gives a trigger no reliable "I am a referential action" signal; `pg_trigger_depth()`
does not distinguish it). A manual `UPDATE audit_events SET owner_id = NULL WHERE …` of exactly that shape is equally permitted and only the release log /
psql history would show it. Acceptable: detaching a row from a workspace hides nothing (the operator `/all` view still lists it) and changes no fact.
The trigger is **not** modelled (EF has no trigger API); `AuditEventMapping.cs` carries a two-line comment naming the migration (as DB-11a does for
its expression index, R9). Sqlite (`EnsureCreated`) has no trigger — the §3.1 code guard covers tests; Postgres behaviour is proven by the DB-10 CI
step (§6 test 3) and the R11 rehearsal. The ERRCODE `restrict_violation` (`23001`) is what a future caller sees as `PostgresException.SqlState`.

**Every existing row:** no table touched except the new one. `workspaces` gains one referencing FK.

Rehearsal verification (R11 step 4):
```sql
\d audit_events                                            -- 3 indexes, FK … ON DELETE SET NULL, Triggers: trg_audit_events_append_only, trg_audit_events_no_truncate
INSERT INTO audit_events (occurred_at, actor_kind, action, target_type, before, after) VALUES (now(), 3, 'ci.probe', 'settings', '{}', '{}');
UPDATE audit_events SET action = 'x' WHERE action = 'ci.probe';   -- ERROR: audit_events is append-only (DB-12): UPDATE is not allowed
DELETE FROM audit_events WHERE action = 'ci.probe';               -- ERROR: … DELETE is not allowed
-- the probe row stays forever on the REHEARSAL copy only; never run these two INSERT/UPDATE lines on production.
```

### 3.3 `IAuditWriter` — one implementation, called from every mutation

`Application/Abstractions/IAuditWriter.cs`:
```csharp
/// <summary>DB-12. Writes one append-only audit row for a security-relevant mutation. Call it AFTER the mutation's
/// SaveChangesAsync succeeded (or inside the same ExecuteInTransactionAsync block, after the mutation's save). The writer
/// fills occurred_at, actor (from ICurrentUser), request id, ip hash, user agent and impersonation session itself; the
/// caller supplies action, target and the whitelisted before/after fields. Best-effort by default (§3.9 D12.1): a write
/// failure is logged at Error and swallowed unless Audit:FailClosed=true.</summary>
public interface IAuditWriter
{
    Task WriteAsync(AuditEntry entry, CancellationToken ct = default);
}

/// <summary>What a call site knows. Owner = the workspace the action happened in (null = operator-level).</summary>
public sealed record AuditEntry(
    string Action,                       // AuditActions.* constant
    string TargetType,                   // AuditTargets.* constant
    string? TargetId,
    Guid? OwnerId,
    IReadOnlyDictionary<string, string>? Before = null,
    IReadOnlyDictionary<string, string>? After = null,
    Guid? ActorUserIdOverride = null,    // anonymous paths that resolved an identity (login failure with a known e-mail) pass it here
    AuditActorKind? ActorKindOverride = null); // System for hosted jobs / migrations-free seeding
```
`Infrastructure/Audit/AuditWriter.cs : IAuditWriter` (scoped; registered by hand in `Infrastructure/DependencyInjection.cs` next to `:45`:
`s.AddScoped<IAuditWriter, AuditWriter>();`). Constructor: `AppDbContext db, ICurrentUser currentUser, IHttpContextAccessor http, IConfiguration config, ILogger<AuditWriter> log`.
`WriteAsync`:
1. `actorKind = entry.ActorKindOverride ?? (currentUser.Id is null ? System : currentUser.IsImpersonating ? Impersonation : currentUser.IsSuperAdmin ? SuperAdmin : User)`
   (`IsImpersonating`/`ImpersonationSessionId` exist only after DB-13 — until then use `currentUser.IsSuperAdmin ? SuperAdmin : User` and leave a `// DB-13:` comment).
2. `actorUserId = entry.ActorUserIdOverride ?? currentUser.Id`.
3. `actorMembershipId`: when `actorUserId != null && entry.OwnerId is Guid w && !currentUser.IsSuperAdmin` →
   `db.WorkspaceMemberships.IgnoreQueryFilters().Where(m => m.User.PublicId == actorUserId && m.OwnerId == w && m.DeletedAt == null).OrderByDescending(m => m.LeftAt == null).ThenByDescending(m => m.JoinedAt).Select(m => (int?)m.Id).FirstOrDefaultAsync(ct)`; else null.
4. `requestId = http.HttpContext?.Items[RequestIdMiddleware.ItemKey] as string` (§3.7); `ipHash = Hash(http.HttpContext?.Connection.RemoteIpAddress?.ToString())`;
   `userAgent = Truncate(http.HttpContext?.Request.Headers.UserAgent.ToString(), 256)`.
5. `before/after = AuditFields.Sanitize(entry.Before)` (§3.5). `action` must match `AuditActions.All` (a `HashSet<string>` built by reflection over the constants) else `throw new ArgumentException` — a typo is a bug, not an audit row.
6. `db.AuditEvents.Add(new AuditEvent { … }); await db.SaveChangesAsync(ct);` inside `try`. On exception: `log.LogError(ex, "AUDIT WRITE FAILED {Action} {TargetType}/{TargetId}", …)`; if `config.GetValue("Audit:FailClosed", false)` rethrow, else return.
7. On success set `http.HttpContext?.Items[AuditWriter.WrittenItemKey] = true` (read by the coverage filter, §3.8).

Why `SaveChangesAsync` inside the writer: the row must exist even when the caller saved earlier; when the caller is inside
`ExecuteInTransactionAsync` the save joins that transaction. Consequence the implementer must respect: **call the writer only after the
mutation's own `SaveChangesAsync`** (never before — the writer's save would flush the caller's half-built changes).

Hosted jobs (`DemoCleanupService`, `RetentionService`, DB-13's sweep) have no HTTP context: they resolve `IAuditWriter` from their scope and
pass `ActorKindOverride: AuditActorKind.System`; request id/ip/ua stay NULL.

`Application/Common/PseudonymHasher.cs` (static): `EmailHash(string normalisedEmail)` = lower-case hex of the first 8 bytes of
`SHA256(normalisedEmail)` (16 hex chars — enough to correlate repeated failures against one address, not enough to enumerate). The IP HMAC lives
in the writer (Infrastructure has the key). Hash inputs are always the **normalised** e-mail (`EmailNormalizer.NormalizeRequired`, DB-11a §3.3a).

### 3.4 Actor resolution rules (what `actor_*` means per path)

| Path | `actor_kind` | `actor_user_id` | `actor_membership_id` | `owner_id` |
|---|---|---|---|---|
| Workspace admin / member acting in their workspace | `User` | JWT `sub` | membership of `(sub, tenant)` | JWT `tenant` |
| Super admin acting on operator surfaces (tenants, plans, settings, branding) | `SuperAdmin` | `sub` | NULL | NULL, or the affected workspace when there is one (`tenant.status_changed` → that workspace) |
| Super admin under a DB-13 session | `Impersonation` | `sub` | NULL | the impersonated workspace; `impersonation_session_id` set |
| Anonymous auth path that resolved an identity (login failure with a known address, reset requested) | `User` | the identity's `public_id` (override) | NULL | NULL |
| Anonymous path with no identity (login failure, unknown address) | `System` | NULL | NULL | NULL; `target_type = email_hash`, `target_id = PseudonymHasher.EmailHash(email)` |
| Hosted job | `System` | NULL | NULL | the affected workspace (or NULL) |

### 3.5 `before` / `after` — whitelisted keys only (R12 shape note)

`Application/Common/AuditFields.cs`: `public static readonly HashSet<string> Allowed = new(StringComparer.Ordinal) { "role_id", "role_name", "is_active",
"approval_status", "plan_id", "plan_name", "status", "action", "name", "key", "url", "environment_id", "reason", "minutes", "count", "keys", "scopes",
"label", "kind", "max_uses", "expires_at", "email_hash", "session_id", "request_count", "duration_seconds", "source", "project_id", "with_password" }`;
`Sanitize(dict)` drops unknown keys, truncates values to 200 chars, sorts (JsonColumn sorts anyway). **Never in a whitelist:** `email`, `display_name`,
`password`, `hash`, `token`, `code`, `body`, `prompt`, `text` — names and addresses of people, secrets, content. Shape versioning: keys are added,
never repurposed (R12). Size: ≤ 30 keys × 200 chars ≈ 6 KB worst case; typical rows are < 300 bytes.

### 3.6 The catalogue — every mutation, its action string and its call site

`Application/Common/AuditActions.cs` holds one `public const string` per row below (name = PascalCase of the string). The implementer adds the
`IAuditWriter` constructor dependency to each service listed and inserts `await _audit.WriteAsync(new AuditEntry(...))` **after the method's
successful `SaveChangesAsync`** (or after the transaction body's save). Attribute `[Audited(AuditActions.X)]` goes on the controller action
(§3.8). `owner` below = the workspace the action happened in (`TenantStamp.TryRequireOwner` result for tenant users; the target workspace id for
super-admin actions; `project.OwnerId` for project-scoped ones).

**Auth (`AuthService`, `AuthController`)**

| action | where | target | before → after |
|---|---|---|---|
| `auth.login.succeeded` | `LoginAsync` after `Issue(...)`; `LoginWithApiKeyAsync` (`after: {source: "api_key"}`); `LoginWithInviteAsync` (`source: "magic_link"`) | `user` / `public_id`; `owner` = the membership's workspace (super admin: NULL) | `after: {source}` |
| `auth.login.failed` | every non-success return of `LoginAsync` **and** `LoginWithApiKeyAsync`, `LoginWithInviteAsync` | identity found → `user`/`public_id` (override); else `email_hash`/hash (key login: `api_key`/id if resolvable, else `email_hash` = hash of the literal `"api_key"`) | `after: {reason: invalid_credentials \| passwordless \| pending \| rejected \| disabled \| locked \| no_workspace \| revoked_key}` — a correct password landing on the workspace picker is NOT a failure and writes no row (the response is a 400 "choose one") |
| `auth.workspace_switched` | DB-11b `SwitchWorkspaceAsync` (reserved; DB-11b's implementer adds it if DB-12 is merged first, else DB-12 adds it) | `workspace`/id | — |
| `auth.password.reset_requested` | `RequestPasswordResetAsync` — always, identity found or not | `user`/`public_id` or `email_hash` | — |
| `auth.password.reset` | `ResetPasswordAsync` success | `user`/`public_id` | — |
| `auth.password.changed` | `ChangePasswordAsync` success | `user`/`public_id` | — |
| `auth.email.change_requested`, `auth.email.changed` | DB-11d (reserved) | `user`/`public_id` | `before: {email_hash: old}`, `after: {email_hash: new}` — hashes only (D12.3) |
| `auth.email.verified` | DB-14 (reserved) | `user`/`public_id` | — |
| `auth.register.stakeholder` | `RegisterAsync` success (new identity or re-apply) | `user`/`public_id`; `owner` = project's workspace | `after: {role_id, status: pending}` |
| `auth.register.admin` | `RegisterAdminAsync` success | `workspace`/new workspace id; `owner` = it | `after: {plan_id?}` |
| `auth.demo.provisioned` | `DemoService.ProvisionAsync` after the seed save | `workspace`/demo workspace id | `after: {source: "demo"}` (recipient e-mail: **never**) |
| `auth.demo.upgraded` | `DemoService.UpgradeAsync` success | `workspace`/id | — |
| `device.approved`, `device.denied` | `DeviceLoginService.ApproveAsync/DenyAsync` | `device_login`/row id; `owner` = tenant | — |
| `apikey.created`, `apikey.regenerated` | `ApiKeyService.GetOrCreateAsync` (only when it inserts), `RegenerateAsync` | `api_key`/new row id; `owner` = key's workspace | `after: {scopes}` |

**Members and invites (`UsersController`, `InvitesController`, `MeController`, DB-11c endpoints)**

| action | controller action → service | target | before → after |
|---|---|---|---|
| `member.created` | `UsersController.Create` → `UserService.CreateAsync` | `membership`/id | `after: {role_id, is_active, approval_status}` |
| `member.approved` / `member.rejected` | `Approve`/`Reject` → `ApproveAsync`/`RejectAsync` | `membership`/id | `before/after: {approval_status, role_id}` |
| `member.updated` | `Update` → `UpdateAsync` | `membership`/id | `before/after: {role_id, is_active}` + `after.with_password = "true"` when a password was set (never the password) |
| `member.removed` | `Delete` → `DeleteAsync` (DB-11a: ends the membership) | `membership`/id | `before: {role_id, is_active}` |
| `ownership.transferred` | `Promote` → `TransferOwnershipAsync` | `membership`/deputy membership id | `before/after: {role_id}` on both memberships → two rows, one per membership |
| `member.left`, `identity.erase_requested`, `identity.erased` | DB-11c (reserved: `POST /api/me/leave-workspace`, `POST /api/me/request-erase`, `DELETE /api/me`, `POST /api/auth/confirm-erase`, `DELETE /api/admin/identities/{publicId}`) | `membership`/id; `user`/`public_id` | `after: {count}` = memberships ended |
| `invite.created` | `InvitesController.Create` → `InviteService.CreateAsync` | `invite`/id | `after: {role_id, max_uses, expires_at, kind: admin \| quick_access, email_hash?}` |
| `invite.revoked` | `Revoke` → `RevokeAsync` | `invite`/id | `after: {count}` = invitee memberships returned (DB-11c) |
| `invite.quick_link_rotated` | `RotateQuickLink` → `RotateQuickLinkAsync` | `invite`/id | — |
| `invite.resent` | `InviteService.ResendAsync` (if exposed) | `invite`/id | — |
| `invite.accepted` | `AuthController.RegisterInvite` → `InviteService.AcceptAsync` (both branches) | `invite`/id; `owner` = joined/new workspace | `after: {role_id, kind: join \| new_workspace}` |
| `tenant_invite.created` / `.resent` / `.revoked` | `TenantsController.CreateInvite/ResendInvite/RevokeInvite` → `TenantInviteService` | `tenant_invite`/id; `owner` NULL | `after: {plan_id, expires_at}` |

**Workspace and tenants**

| action | controller action → service | target | before → after |
|---|---|---|---|
| `workspace.renamed` | `WorkspaceController.Rename` → `WorkspaceService.RenameAsync` | `workspace`/id | `before/after: {name}` |
| `workspace.comment_fields_updated` | `UpdateCommentFields` → `CommentFieldService.UpdateDefinitionsAsync` | `workspace`/id | `before/after: {count, keys}` (`keys` = comma-joined field keys, ≤ 200 chars) |
| `workspace.created` | `TenantService.CreateAsync`, `InviteService.AcceptCreateNewWorkspaceAsync`, `RegisterAdminAsync`, `DemoService.ProvisionAsync` (write **this** in addition to the auth.* row) | `workspace`/id | `after: {source: super_admin \| invite \| self_serve \| demo}` |
| `tenant.created` | `TenantsController.Create` → `TenantService.CreateAsync` | `workspace`/id; `owner` = it | `after: {plan_id}` |
| `tenant.status_changed` | `SetStatus` → `SetStatusAsync` | `membership`/admin membership id; `owner` = workspace | `after: {action}` (approve/enable/disable) |
| `tenant.demo_extended`, `tenant.demo_config_changed` | `ExtendDemo`, `SetDemoConfig` | `workspace`/id | `after: {expires_at}` / `before/after: {count (cap), minutes (ttl h)}` |
| `tenant.plan_changed` | `ChangePlan` → `ChangePlanAsync` | `workspace`/id | `before/after: {plan_id}` |
| `tenant.hard_deleted` | `TenantsController.Delete` → `HardDeleteAsync` **and** `DemoCleanupService` (System, `reason: demo_expired`) — written **before** the delete, with `OwnerId = null` deliberately (the row must not be detached mid-transaction) and `target_id` = workspace id | `workspace`/id | `after: {reason, count}` = comments deleted |

**Projects, roles, environments, statuses, actions, suggestions, AI rules** (`owner` = caller's workspace)

| action | controller action → service | target | after |
|---|---|---|---|
| `project.created` / `.updated` / `.deleted` | `ProjectsController.Create/Update/Delete` → `ProjectService.CreateAsync/UpdateAsync/DeleteAsync` | `project`/id | `{key, name}`; update: changed keys among `name, key, is_active_*, enforce_allowed_origins, commit_style` as `keys` |
| `project.app_url_set` / `.app_url_deleted` | `SetAppUrl/DeleteAppUrl` → `SetAppUrlAsync/DeleteAppUrlAsync` | `project_app_url`/`{projectId}:{environmentId}` | `{url, environment_id}` |
| `role.created` / `.updated` / `.deleted` | `RolesController.Create/Update/Delete` → `RoleService` | `role`/id | `{name}`; delete: `{count}` reassigned |
| `environment.created` / `.updated` / `.deleted` | `AppEnvironmentsController.*` → `AppEnvironmentService` | `environment`/id | `{name}` |
| `status.updated` / `status.reset` | `StatusesController.Update/Reset` → `StatusAdminService` | `status`/value | `{label}` |
| `predefined_action.created` / `.updated` / `.deleted` | `PredefinedActionsController.*` → `PredefinedActionService` | `predefined_action`/id | `{project_id?}` (never `text`/`prompt`) |
| `suggestion.approved` / `.rejected` / `.changes_requested` | `SuggestionsController.*` → `SuggestionService` | `suggestion`/id | `{status}` |
| `ai_rule.created` / `.updated` / `.deleted` | `Admin/AiRulesController.*` → `AiRuleService` | `ai_rule`/id | `{project_id?}` (never the prompt) |
| `import.completed` | `ExportImportController.ImportProject/ImportWorkspace` → `ExportImportService.Import*` | `import`/project id or `workspace` | `{count}` |
| `export.downloaded` | `ExportProject/ExportWorkspace` (GET — audited because it is a bulk content read) | `export`/project id or `workspace` | `{count}` |

**Operator surfaces (`owner` NULL, actor `SuperAdmin`)**

| action | controller action → service | target | after |
|---|---|---|---|
| `plan.created` / `.updated` / `.deleted` | `PlansController.*` → `PlanService` | `plan`/id | `{name}` |
| `settings.updated` | `SettingsController.Update` → `SettingsService` | `settings`/`"global"` | `{keys}` = the setting keys that changed (never values) |
| `branding.updated` / `.asset_uploaded` / `.asset_deleted` | `BrandingController.Update/UploadAsset/DeleteAsset` → `BrandingService` | `branding`/kind or `"global"` | `{keys}` / `{kind}` |
| `impersonation.started` / `.ended` | DB-13 (reserved) | `impersonation_session`/id; `owner` = target workspace | `{reason, minutes, session_id}` / `{request_count, duration_seconds, reason: manual \| expired}` |

**Explicitly not audited (`[NoAudit("…")]` with these reasons):** `MeController.UpdatePreferences` ("personal UI preference"),
`MarkRead/MarkAllRead` ("inbox state"), `EventsController.RecordEvent` ("analytics beacon"), comment/reply/upload/verify endpoints
("content, not security"), `DeviceStart/DevicePoll` ("anonymous polling; the decision is audited by device.approved/denied"),
`ProjectStackController`/`ProjectBuildsController` writes ("CLI telemetry"), `SuggestionsController` **public** `Suggest`/`Update`
("stakeholder content").

### 3.7 Request id — `API/Middleware/RequestIdMiddleware.cs`

Static `Resolve(string? headerValue, string traceIdentifier) → string`: if `headerValue` is non-empty, ≤ 64 chars and matches
`^[A-Za-z0-9._-]+$` return it, else return `traceIdentifier`. Middleware: `ctx.Items[ItemKey] = Resolve(ctx.Request.Headers["X-Request-Id"], ctx.TraceIdentifier)`;
`ctx.Response.OnStarting(() => { ctx.Response.Headers["X-Request-Id"] = id; })`. Registered in `Program.cs` **immediately after**
`app.UseForwardedHeaders(fwd);` (`:189`) and **before** the exception handler (`:196`) so the handler can include it:
change `LogError(ex, "Unhandled exception on {Method} {Path}", …)` (`:210`) to add `{RequestId}` from `ctx.Items`. §58 (observability) reuses
this middleware; do not write a second one.

### 3.8 Coverage enforcement — attribute + reflection test + runtime filter

- `API/Auth/AuditedAttribute.cs`: `[AttributeUsage(Method)] AuditedAttribute(string action)`; `NoAuditAttribute(string reason)` (reason required, non-empty).
- Every mutating action (`[HttpPost|HttpPut|HttpPatch|HttpDelete]`) on a controller whose namespace starts with `Pointer.API.Controllers.Admin`,
  plus every action of `AuthController`, `MeController`, `DemoController`, `ExportImportController` (including the two GET exports), carries exactly one
  of the two attributes. §6 test 1 enforces it by reflection.
- `API/Auth/AuditCoverageFilter.cs : IAsyncActionFilter` (added in `Program.cs:49-52` as `options.Filters.Add<AuditCoverageFilter>()`). Exact mechanics
  (GLM DB-12 #2 — the original wording "response status" / "rewrite the response" was ambiguous; this is the contract):
  ```csharp
  public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
  {
      var executed = await next();                       // runs the ACTION only; the IActionResult has NOT been executed yet (MVC runs result filters +
                                                         // result execution after every action filter returns) — Response.HasStarted is false here.
      var audited = (ctx.ActionDescriptor as ControllerActionDescriptor)?.MethodInfo.GetCustomAttribute<AuditedAttribute>();
      if (audited is null || executed.Exception is not null || executed.Canceled) return;
      var status = (executed.Result as IStatusCodeActionResult)?.StatusCode ?? StatusCodes.Status200OK;   // NEVER HttpContext.Response.StatusCode — it is still the default 200 at this point
      if (status >= 400) return;
      if (ctx.HttpContext.Items.ContainsKey(AuditWriter.WrittenItemKey)) return;
      ctx.HttpContext.Items["audit.gap"] = audited.Action;
      _logger.LogError("AUDIT GAP: {Action} completed without an audit row ({Method} {Path})", audited.Action, ctx.HttpContext.Request.Method, ctx.HttpContext.Request.Path);
      if (_config.GetValue("Audit:StrictCoverage", false))
          executed.Result = new ObjectResult(Result.Failure("Audit gap")) { StatusCode = StatusCodes.Status500InternalServerError };   // legal: the result is replaced before it executes
  }
  ```
  Constructor: `ILogger<AuditCoverageFilter> logger, IConfiguration config` (type-registered filters are DI-activated). `Audit:StrictCoverage=true` is set in
  `appsettings.Development.json` and the test host — so a forgotten writer call fails locally and in e2e, never silently in production. `Ok(result)` /
  `BadRequest(result)` in this codebase are `ObjectResult`s whose `StatusCode` is set (`IStatusCodeActionResult`) — that is why the status must be read from
  `executed.Result`, not from the response.

### 3.8a Read API — scope and operator-identity redaction (D12.5; GLM DB-12 #3 / DB-13 #1, GLM DB-12 #5)

`AuditQueryService` (`Application/Services/Implementation/AuditQueryService.cs : IAuditQueryService`; ctor `IUnitOfWork, ICurrentUser`):

- **`ListForWorkspaceAsync(q)` — scope.** `owner` is resolved as: `if (!TenantStamp.TryRequireOwner(_currentUser, out var owner)) return Forbidden(MessageKeys.Common.Forbidden);`
  — a super admin is Forbidden here and uses `/all`. **DB-13 amends this one line** to
  `if (_currentUser.IsImpersonating && _currentUser.TenantId is Guid t) owner = t; else if (!TryRequireOwner…) return Forbidden;` so an impersonating operator
  can read the target's own Security log mid-session (GLM DB-12 #5; DB-15 §3.5 uses the same branch). Until DB-13 lands leave a `// DB-13: impersonating
  operator → owner = TenantId` comment on the line. Then `.Where(e => e.OwnerId == owner)` **explicitly** on top of the filter (R8 belt-and-braces, as
  `WorkspaceSetting` reads do); order `OccurredAt DESC, Id DESC`.
- **`ListAllAsync(q)`** — `IgnoreQueryFilters()` is *not* needed (the super-admin branch of the filter admits everything); optional `q.WorkspaceId` predicate.
- **Redaction — the operator is never named to a workspace (D12.5).** When the **caller is not a super admin** (`!_currentUser.IsSuperAdmin`) and the row's
  `ActorKind` is `SuperAdmin` **or** `Impersonation`, the DTO is emitted with `ActorUserId = null` and `ActorName = "Operator"` (constant
  `AuditEventDto.OperatorLabel`). Applied in one place: `AuditEventDto Map(AuditEvent e, IReadOnlyDictionary<Guid,string> names, bool callerIsSuperAdmin)`.
  `/all` (super admins only) keeps both. This is what makes DB-13's D13.6 ("the operator's identity is shown to super admins only") true — without it the
  workspace admin's Security log would render the operator's display name on `impersonation.started` and on every `tenant.*` row. The `q.Actor` filter is
  still honoured for workspace callers (a uuid they do not know finds nothing; a uuid they do know — a member — is fine).
- **Column visibility per view:** workspace view: `Id, OccurredAt, WorkspaceId, ActorUserId*, ActorName*, ActorKind, Action, TargetType, TargetId, Before,
  After, RequestId, ImpersonationSessionId` (`*` redacted per above); `/all` adds `UserAgent, IpHash`. Same DTO type, nulls for the hidden ones.

### 3.9 Owner decisions encoded here (defaults apply unless the owner says otherwise before §9)

| # | Question | Default |
|---|---|---|
| D12.1 | If the audit row cannot be written, does the mutation fail? | **Best-effort** (log Error, mutation stands); `Audit:FailClosed=true` flips it. Revisit at first paying customer |
| D12.2 | Retention of `audit_events` | **Forever**; never swept by DB-08 (R17). Size: ~300 B/row; 1 000 admin mutations/day ≈ 110 MB/year |
| D12.3 | E-mail addresses in audit rows (login failures, e-mail changes) | **Never raw** — `sha256` 16-hex pseudonym; the identity row and the DB-11d notification e-mails are the readable trail. Append-only cannot be erased (R14) |
| D12.4 | `user_agent` stored | **Yes**, 256 chars (device forensics); `ip_hash` keyed HMAC, never the address |
| D12.5 *(added 2026-09-23, GLM DB-12 #3)* | Does a workspace admin see **which** operator acted (`tenant.*`, `impersonation.*` rows)? | **No** — `actor_user_id` null, `actor_name = "Operator"` for `SuperAdmin`/`Impersonation` actor kinds in the workspace view; full identity in `/all`. Consistent with DB-13 D13.6 |

## 4. Safety classification

**Additive** (Migration 1: R1) **+ one raw-SQL constraint migration** (Migration 2: marker `R4 constraint` + `[ContractMigration("DB-12")]`, R7 explicit
path). No existing row changes. No data destroyed — the table cannot be updated or deleted from by design. Tenancy: strict-own filter (R8.1–4);
the R8.5 hard-delete rule is **exempted** by name for this table (FK `SET NULL` instead, trigger permits only that; R8 amended). Erase (DB-11c):
rows keep `actor_user_id`/`target_id` uuids (resolve to "Deleted user"), `ip_hash`, `user_agent`; nothing else about a person — added to DB-11c's
erase inventory as "kept by design".

## 5. File-level tasks

Order: 1–5 compile without behaviour change; 6–7 migrations; 8–13 wiring; 14 tests.

1. `Domain/Enums/AuditActorKind.cs` — `public enum AuditActorKind { User = 1, SuperAdmin = 2, System = 3, Impersonation = 4 }` + R10 append-only comment.
   `Domain/Entity/AuditEvent.cs` — §3.1 properties, all `{ get; init; }`, class doc-comment "Append-only (DB-12). Never updated or deleted; Postgres trigger `trg_audit_events_append_only` + `AppDbContext.SaveChangesAsync` guard."
2. `Infrastructure/Mappings/AuditEventMapping.cs` — copy `UsageEventMapping.cs`: `ToTable("audit_events")`, `HasKey(x => x.Id)`, `Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn()`, every column per §3.1 (`HasMaxLength(64)` action/target_type/request_id/ip_hash, `128` target_id, `256` user_agent), FK `HasOne<Workspace>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_audit_events_workspaces_owner_id")`, `ConfigureJsonColumn` for `Before`/`After`, the three indexes with `HasDatabaseName`, and the two-line trigger comment.
3. `Infrastructure/AppDbContext.cs` — `public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();` after `:71`; strict-own filter for `AuditEvent` copied from `:263-268` with the comment "DB-12: audit rows are metadata; super admin sees all (DB-13 does not narrow this)"; the §3.1 guard at the top of `SaveChangesAsync`. `Application/Abstractions/IUnitOfWork.cs` + `Infrastructure/Repository/UnitOfWork.cs` — `DbSet<AuditEvent> AuditEvents`.
   **`Tests/WorkspaceTests.cs:311-312`** (R8.5 reflection test `HardDeleteOrder_CoversEveryOwnerCarryingEntity`) — after the line `&& t != typeof(UsageEvent)` add
   `&& t != typeof(AuditEvent) // operator record: FK SET NULL, survives the workspace (DB-12, R8.8)`. Without this line `just test` fails after task 1 with
   "Type(s) not covered by TenantService.HardDeleteOrder: AuditEvent" (GLM DB-12 #1). Do **not** touch `:323` (`HardDeleteOrder.Length`) — `AuditEvent` is not in the array.
4. `Application/Common/AuditActions.cs`, `AuditTargets.cs`, `AuditFields.cs`, `PseudonymHasher.cs` — §3.5/§3.6 constants (`AuditActions.All` = reflection over the public const strings).
5. `Application/Abstractions/IAuditWriter.cs` (+ `AuditEntry` record) — §3.3 verbatim. `Infrastructure/Audit/AuditWriter.cs` — §3.3 steps 1–7; `public const string WrittenItemKey = "audit.written";`. `Infrastructure/DependencyInjection.cs` — `s.AddScoped<IAuditWriter, AuditWriter>();` after `:45`.
6. `just migrate name="AddAuditEvents"` → read against §3.2 Migration 1 (only `audit_events` + its FK/indexes; anything else → stop and report).
7. `just migrate name="AddAuditEventsAppendOnlyTrigger"` → must be **empty**; paste the §3.2 SQL into `Up()`/`Down()`; add `[ContractMigration("DB-12")]` and the marker verbatim. `dotnet ef migrations has-pending-model-changes -p Infrastructure -s API` → "No changes".
8. `API/Middleware/RequestIdMiddleware.cs` + `Program.cs` registration after `:189`; extend the `LogError` at `:210` with `{RequestId}`.
9. `API/Auth/AuditedAttribute.cs`, `NoAuditAttribute.cs`, `AuditCoverageFilter.cs` (§3.8 code verbatim — status from `executed.Result`, never from `HttpContext.Response`; strict mode replaces `executed.Result`); `Program.cs:49-52` → `options.Filters.Add<AuditCoverageFilter>();`. `API/appsettings.Development.json` → `"Audit": { "StrictCoverage": true }`.
10. Attributes on every action listed in §3.6/§3.8 (53 admin mutations + auth/me/demo/export). Reason strings for `[NoAudit]` verbatim from §3.6.
11. Writer calls in every service listed in §3.6 (constructor gains `IAuditWriter audit`; tests that construct services by hand — `WorkspaceAdminOwnershipTests`, `UserGovernanceTests`, `InviteServiceTests.BuildService`, `TenantInviteServiceTests`, `DemoSessionEmailTests`, `DemoUpgradeTests`, `ChangePasswordTests`, `ApiKeyServiceTests`, `DeviceLoginServiceTests`, `RoleService*Tests`, `AppEnvironmentServiceTests`, `PlanServiceTests`, `ExportImportServiceTests`, `SuggestionChangesRequestedTests`, `AiRuleServiceTests`, `ProjectsControllerAuthzTests` — pass `new FakeAuditWriter()` from `Tests/TestDoubles.cs`).
    `DemoCleanupService.cs` and `TenantsController.Delete` write `tenant.hard_deleted` per §3.6 (System / SuperAdmin).
12. `API/Controllers/Admin/AuditController.cs` — `[Route("api/admin/audit")]`, `[Tags("Audit")]`, `[Produces("application/json")]`, `[Authorize(Policy = Policies.Admin)]`:
    `GET` (`List([FromQuery] AuditQuery q)`) → `IAuditQueryService.ListForWorkspaceAsync(q)`; `GET all` (`[Authorize(Policy = Policies.SuperAdmin)]`) → `ListAllAsync(q)`.
    `Application/DTOs/Audit/AuditQuery.cs` (`DateTime? Since, Until; string? Action (prefix match, e.g. "member."), Guid? Actor, string? TargetType, string? TargetId, Guid? WorkspaceId (all only), int Page = 1, int PageSize = 50 (≤ 200)`),
    `AuditEventDto` (`Id, OccurredAt, WorkspaceId, ActorUserId, ActorName (UserNameResolver; null → "System"), ActorKind (string), Action, TargetType, TargetId, Before, After, RequestId, ImpersonationSessionId; UserAgent and IpHash only in the all view`),
    `[ProducesResponseType(typeof(PagedData<AuditEventDto>), 200)]`. `Application/Services/Implementation/AuditQueryService.cs` per **§3.8a**: workspace view scope via `TenantStamp.TryRequireOwner` (super admin → Forbidden, use `/all`; `// DB-13:` comment for the impersonation branch), explicit `.Where(e => e.OwnerId == owner)`, order `OccurredAt DESC, Id DESC`; **one `Map(...)` with the D12.5 redaction** (`ActorKind ∈ {SuperAdmin, Impersonation}` and caller not super admin → `ActorUserId = null`, `ActorName = AuditEventDto.OperatorLabel` = `"Operator"`). Both actions `[NoAudit("read of the audit log itself")]`. `orval.config.ts:6` → add `'Audit'`.
13. `Application/Resources/MessageKeys.cs` — `Audit.PageSizeTooLarge = "Page size must be 200 or fewer."`.
14. Tests (§6); `just fmt`; `just test`; rehearsal (§3.2); `docs/db/SCHEMA.md` row `audit_events` from *(planned)* to present (only doc edit allowed here).

## 6. Tests

1. `Tests/AuditCoverageTests.cs` — reflection: for every controller type in namespace `Pointer.API.Controllers.Admin` and for `AuthController`, `MeController`, `DemoController`, `ExportImportController`: every method with an `HttpPost/Put/Patch/Delete` attribute (and the two export GETs) has exactly one of `[Audited]`/`[NoAudit]`; every `[Audited]` action string is in `AuditActions.All`; every `[NoAudit]` reason is non-empty. Precedent shape: `AuthRateLimitingTests.cs:21-45`.
2. `Tests/AuditWriterTests.cs` (Sqlite `TestDb` from `RetentionServiceTests.cs:36-62`, with a fake `IHttpContextAccessor` carrying `Items[RequestId]`, a `RemoteIpAddress`, a 400-char UA): row written with `request_id`, 64-hex `ip_hash`, UA truncated to 256, `Before/After` sanitised (unknown key dropped, value truncated to 200), `Items["audit.written"] == true`; unknown action → `ArgumentException`; failing `SaveChanges` (dispose the context first) → no throw with default config, throws with `Audit:FailClosed=true`; `ActorKindOverride: System` with no HttpContext → `actor_user_id` NULL.
3. **Database-level append-only** (CI step in `.github/workflows/db-migrations.yml`, after DB-11a's step): insert a probe row with `psql -v ON_ERROR_STOP=1`, then `if psql -c "UPDATE audit_events SET action='x'" 2>/tmp/upd.err; then echo "UPDATE succeeded"; exit 1; fi; grep -q "append-only" /tmp/upd.err`; same for `DELETE` and `TRUNCATE audit_events`. Then `DELETE FROM workspaces …` is **not** probed (needs a seeded workspace) — the SET NULL path is covered by test 4.
4. `Tests/AuditAppendOnlyGuardTests.cs` (InMemory): `db.Entry(ev).State = Modified` → `SaveChangesAsync` throws `InvalidOperationException`; `Remove(ev)` → throws; adding is fine. (Sqlite `TestDb`) `HardDelete_Workspace_DetachesAuditRows_KeepsThem` — seed workspace + one audit row with `OwnerId` = it; `TenantService.HardDeleteAsync(workspaceId)` → the audit row still exists with `OwnerId == null` (Sqlite honours `ON DELETE SET NULL`; the guard in `SaveChangesAsync` is not hit because the FK action runs in the database).
5. `Tests/AuditQueryFilterTests.cs` (copy `TenantQueryFilterTests`): tenant B's context sees **no** audit row of A; `AuditQueryService.ListForWorkspaceAsync` under tenant B with `WorkspaceId = A` in the query still returns only B's rows; super admin `ListAllAsync` returns both plus `owner_id IS NULL` rows; super admin calling the workspace view → Forbidden.
   **Redaction (D12.5):** seed in A one row `ActorKind = Impersonation, ActorUserId = <operator uuid>` and one `ActorKind = SuperAdmin` (`tenant.status_changed`) and one `ActorKind = User`; as A's admin `ListForWorkspaceAsync` → the two operator rows have `ActorUserId == null && ActorName == "Operator"` and the serialised DTO contains neither the operator uuid nor the operator's display name; the `User` row keeps its uuid/name; as super admin `ListAllAsync` → all three carry `ActorUserId`.
   **R8.5 exclusion list:** in `Tests/WorkspaceTests.cs` add `Assert.Equal(new[] { typeof(Workspace), typeof(UsageEvent), typeof(AuditEvent) }.Length, 3)`-style guard — concretely a `[Fact] HardDeleteOrder_Exclusions_AreExactlyTheNamedOperatorTables` that lists the excluded types as a static array `Exclusions` used by the reflection test and asserts it equals `{ Workspace, UsageEvent, AuditEvent }` (DB-13 appends `ImpersonationSession`, DB-15 `UsageDaily`).
6. `Tests/AuditWrittenByServicesTests.cs` (InMemory fixture `UserGovernanceTests.cs:20-60` + `TestSeed.Join` from DB-11a; `FakeAuditWriter` records entries): one fact per row — `LoginAsync` success → `auth.login.succeeded` with `OwnerId` = membership workspace; wrong password → `auth.login.failed` with `target_type == email_hash` and `TargetId == PseudonymHasher.EmailHash(email)` and **no raw e-mail anywhere in the entry**; `ChangePasswordAsync` → `auth.password.changed`; `UserService.UpdateAsync` role change → `member.updated` with `before.role_id != after.role_id`; `DeleteAsync` → `member.removed`; `InviteService.CreateAsync` → `invite.created`; `RevokeAsync` → `invite.revoked`; `WorkspaceService.RenameAsync` → `workspace.renamed` before/after name; `ProjectService.CreateAsync` → `project.created`; `TenantService.SetStatusAsync` → `tenant.status_changed`; `TransferOwnershipAsync` → two `ownership.transferred` rows.
7. `Tests/AuditCoverageFilterTests.cs` — build an `ActionExecutingContext` for a `ControllerActionDescriptor` whose `MethodInfo` carries `[Audited("x.y")]`, with a `next` delegate returning an `ActionExecutedContext` whose `Result = new OkObjectResult(Result.Success())` and no `Items` key → `Items["audit.gap"] == "x.y"`; with `Result = new BadRequestObjectResult(Result.Failure("x"))` (status 400 **on the result**, `HttpContext.Response.StatusCode` untouched) → no gap; with the key → no gap; with `[NoAudit]` → no gap; with `executed.Exception` set → no gap; `StrictCoverage=true` → `executed.Result is ObjectResult { StatusCode: 500 }` whose `Value` is a failed `Result` with message `"Audit gap"`.
8. `Tests/RequestIdMiddlewareTests.cs` — `Resolve("abc-123", "t")` → `"abc-123"`; 65 chars → `"t"`; `"a b"` → `"t"`; null → `"t"`.
9. Existing-data test: N/A (new table). Rehearsal (§3.2) is the Postgres proof: `\d audit_events` shows both triggers; the UPDATE/DELETE probes fail.

## 7. Acceptance criteria

1. `dotnet ef migrations list -p Infrastructure -s API --no-connect` ends with `_AddAuditEvents`, `_AddAuditEventsAppendOnlyTrigger` (after DB-11a's three).
2. `grep -c "ContractMigration(\"DB-12\")" Infrastructure/Migrations/*_AddAuditEventsAppendOnlyTrigger.cs` → 1; `grep -c "ContractMigration" Infrastructure/Migrations/*_AddAuditEvents.cs` → 0; `grep -c "trg_audit_events_append_only\|trg_audit_events_no_truncate" Infrastructure/Migrations/*_AddAuditEventsAppendOnlyTrigger.cs` → ≥ 4 (Up + Down).
3. `grep -rn "\[Audited(\|\[NoAudit(" API/Controllers --include='*.cs' | wc -l` → ≥ 70; `just test` green including `AuditCoverageTests`.
3a. `grep -c "typeof(AuditEvent)" Tests/WorkspaceTests.cs` → ≥ 1 (the R8.5 exclusion); `grep -c "HttpContext.Response.StatusCode" API/Auth/AuditCoverageFilter.cs` → 0; `grep -c "IStatusCodeActionResult" API/Auth/AuditCoverageFilter.cs` → 1.
3b. `grep -c '"Operator"' Application/DTOs/Audit/AuditEventDto.cs` → 1; `grep -c "OperatorLabel" Application/Services/Implementation/AuditQueryService.cs` → ≥ 1.
4. `grep -rn "AuditActions\.\|new AuditEntry(" Application API --include='*.cs' | grep -v "Common/AuditActions.cs" | wc -l` → ≥ 60 (one per catalogue row that exists at implementation time).
5. `grep -rn '"auth\.\|"member\.\|"invite\.\|"tenant\.\|"project\.' Application/Services --include='*.cs' | grep -v AuditActions | wc -l` → 0 (no literal action strings at call sites).
6. `curl -s http://localhost:8090/swagger/v1/swagger.json | jq '.paths["/api/admin/audit"].get.tags, .paths["/api/admin/audit/all"].get.tags'` → `["Audit"]` twice; `grep -c "'Audit'" orval.config.ts` → 1.
7. `curl -si http://localhost:8090/api/meta | grep -i x-request-id` → one header; sending `X-Request-Id: test-1` echoes `test-1`.
8. Rehearsal: `\d audit_events` shows the FK `ON DELETE SET NULL`, three indexes, two triggers; the UPDATE and DELETE probes fail with `append-only`; `SELECT count(*) FROM audit_events` on the rehearsal API after one login + one rename → 2.
9. DB-10 workflow green including the new append-only step.
10. On the rehearsal API: `GET /api/admin/audit` as the workspace admin lists the rename with `actorName`; as super admin → 403; `GET /api/admin/audit/all` as super admin lists it with `ipHash`; the dashboard-less check `GET /api/admin/audit?action=workspace.` filters by prefix; after a super-admin `PUT /api/admin/tenants/{id}/status`, the workspace admin's `GET /api/admin/audit?action=tenant.` shows `actorName: "Operator"` and `actorUserId: null`.

## 8. Rollback

Migration 2 `Down()` drops both triggers and the function; Migration 1 `Down()` drops the table (**and every audit row written so far** — say so in
the release notes; rows are not customer data but they are the security trail). Code rollback = revert the commits; the table can stay (additive)
while the code is reverted — no reader depends on it. Dump label `pre-db12` (or the R7.1 batch label) is mandatory because Migration 2 is marked.

## 9. Release steps

1. Merge after DB-11a is verified in production (§ Dependencies). R11 rehearsal on a same-day dump; paste `\d audit_events` and the probe errors into the PR.
2. On the VM: `POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-db12 bash scripts/deploy-api.sh` (or `pre-db12-14` when batched with DB-14 under R7.1; R7.1 point 7 already holds — the DB-09 script is on the VM).
   Expect two `Applying migration` lines and `DB-09: applying 1 contract migration(s)`.
3. Verify: log in as the real admin → `GET /api/admin/audit` shows `auth.login.succeeded`; rename the workspace and rename it back → two `workspace.renamed` rows with before/after; `docker compose … exec -T db psql -U pointer -d pointer -c "\d audit_events"` shows the triggers; `docker compose logs --since 10m api | grep -c "AUDIT GAP"` → 0 after clicking through the dashboard's admin pages.
4. Watch for `AUDIT WRITE FAILED` (writer error — D12.1 means the mutation still happened) and `AUDIT GAP` (a missing call site — file a fix, add the row).
5. Dashboard: `dashboard-agent` regenerates the client from production once, then §11.

## 10. Out of scope

DB-13 (impersonation rows are reserved here, written there); the DB-11b/c/d rows (reserved, written by those docs or by DB-12 if they land later);
observability's JSON logs/Sentry (§58 — reuses `RequestIdMiddleware`, adds nothing here); alerting on audit events; an audit export endpoint;
signing/hash-chaining rows (tamper evidence beyond the trigger — trigger + DB role separation is enough at this stage; revisit at first paying
customer); auditing comment/reply content changes; changing `RetentionService`; `ON-DISK-CONTRACT.md` (no identifier in it changes); `clients/`
(regenerated by the dashboard-agent, never hand-edited).

## 11. Dashboard / widget / CLI tasks

**Dashboard** (after client regen: `useGetApiAdminAudit`, `useGetApiAdminAuditAll`, `AuditEventDto`, `PagedData<AuditEventDto>`):
1. New route `/security-log` → `features/security-log/SecurityLogPage.tsx` (DataTable precedent `TenantsPage.tsx`): columns time, actor (`actorName` — for `SuperAdmin`/`Impersonation` rows the server already sends `"Operator"` and no uuid; render it with the operator badge, never try to resolve it), badge for `actorKind`, action (rendered from a label map `member.updated → "Member updated"`, fallback the raw string), target, details (compact `before → after` diff of the whitelisted keys), request id (copyable). Filters: since/until, action prefix select (`auth.`, `member.`, `invite.`, `workspace.`, `project.`, `impersonation.`), actor. Pagination 50.
2. Nav: `Shell.tsx` — add "Security log" under the admin group (`:261-295`); for super admins the page calls `/all` and adds a workspace column + filter.
3. Empty state text: "Nothing yet — actions taken in this workspace will appear here."
4. i18n keys for the action label map (en + ar; RTL audit §65 covers layout).

**Widget:** none (the widget never calls admin endpoints). **CLI:** none (`login-with-key` is audited server-side).

## 12. Cross-review adjudication (2026-09-22 reviews, folded 2026-09-23)

Reports: `docs/db/reviews/REVIEW-GLM-DB12-15-2026-09-22.md`, `docs/db/reviews/REVIEW-AGY-DB12-15-2026-09-22.md`. Every citation was re-checked against the tree.

| Finding | Claim | Verdict | Where it landed |
|---|---|---|---|
| GLM DB-12 #1 (Major) | R8.5 reflection test not amended → build break | **Accepted** — `Tests/WorkspaceTests.cs:305-313` excludes only `Workspace`/`UsageEvent`; `AuditEvent` has `OwnerId` | §5 task 3 (explicit edit), §6 test 5 (exclusion-list fact), §7 crit. 3a |
| GLM DB-12 #2 (Major) | `StrictCoverage` 500 rewrite impossible from an action filter after `await next()` | **Partially accepted.** The mechanism claim is wrong: `IAsyncActionFilter.OnActionExecutionAsync`'s `await next()` returns after the *action*, before result filters and result execution — `ActionExecutedContext.Result` may be replaced and `Response.HasStarted` is false. The real trap is the doc's "response status" wording: `HttpContext.Response.StatusCode` is still the default 200 there; the status must be read from `executed.Result` (`IStatusCodeActionResult`) | §3.8 now contains the filter verbatim; §5 task 9; §6 test 7 (400-on-result case); §7 crit. 3a greps |
| GLM DB-12 #3 / DB-13 #1 (Major) | Workspace audit view leaks the operator's identity (breaks D13.6) | **Accepted, widened** to every `SuperAdmin` row too (`tenant.*` rows would name the operator as well) | §3.8a redaction, D12.5, §5 task 12, §6 test 5, §7 crit. 3b/10, §11.1 |
| GLM DB-12 #4 (Minor) | Trigger exemption is shape-based, not FK-attributed; wording overclaims | **Accepted** | §3.2 paragraph; DB-RULES R17 sentence |
| GLM DB-12 #5 (Minor) | Workspace audit view 403s an impersonating operator | **Accepted** — `TryRequireOwner` is false for super admins (DB-11a §3.6) | §3.8a (one-line branch owned by DB-13; comment placeholder here); DB-13 §3.4 row |
| agy DB-12 #1 (Minor, "safe as-is") | Trigger "reliably permits only the workspace hard-delete cascade" | **Overclaim, no action** — see GLM #4: the trigger admits the *shape*, it cannot attribute the UPDATE to the FK. The conclusion (safe) stands | §3.2 wording covers it |
