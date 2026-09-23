# DB-13 — Metadata-only operator by default; audited, time-boxed impersonation to read content (F2)

Roadmap: §56, Release 5 row **R5.3**; founder decision **F2** (`00-founder-decisions.md`: "Operator access to customer comment content:
metadata only by default … requires an audited, time-boxed impersonation session visible to that workspace's admin"). Rules: R1, R5, R7
(additive → ordinary deploy), R8 (operator table exemption, as amended by DB-12), R10, R13, R16 (a new token scope with an exact fence —
`SelectionScopeFence` precedent), R17 (every start/end is an audit row).
**Class: Additive** (one new table) **+ a permission narrowing in code** (six query filters lose their unconditional super-admin branch).
Ships as an ordinary `bash scripts/deploy-api.sh`.
**Status: deployed 2026-09-23 (~06:15 UTC, merged `441a462` + `5f1fbc8` + fixes `790b9b8`; 72 migrations); reviewed (Gemini Pro MERGE, Opus MERGE WITH FIXES → applied).**
Owner decisions D13.1–D13.7 have defaults (§3.8); none blocks.

**Dependencies.** Requires **DB-12 merged** (`IAuditWriter`, `AuditActions.ImpersonationStarted/Ended`, `audit_events.impersonation_session_id`)
and **DB-11a in production** (`ITokenService.Issue(User, WorkspaceMembership?, …)` signature; the validator's membership branch this doc adds an
`else if` around; `IMembershipService.InWorkspace` for the admin e-mail list). Requires **DB-11b merged** (the `OnTokenValidated` handler is
hoisted out of `if (validateStamp)` there and `SelectionScopeFence` is the fence precedent). Independent of DB-11c/d and DB-14.

## 1. Goal

Today the super admin's token passes every query filter (`AppDbContext.cs` — `currentUser.IsSuperAdmin ||` on 20 entities), so the operator can
read any workspace's comment bodies, replies, screenshots, console snapshots, custom fields and AI prompts silently, from any admin page. F2
reverses that: the operator keeps **metadata** (workspaces, members, counts, statuses, plans, health, audit) and gets **content** only inside an
**impersonation session** — started with a reason and a time box (≤ 60 min), audited at start and end, e-mailed to the workspace's admins, visible
in that workspace's security log, and **read-only**. User-visible: the workspace admin receives "An operator is viewing your workspace (reason:
…, until …)" and sees `impersonation.started/ended` rows; the operator sees a "View as…" action on the Tenants page and a banner while viewing.

## 2. Prerequisites (verified facts, 2026-09-22 @ `1af08ec`, plus DB-11a/b and DB-12 additions)

- Query filters with an unconditional `currentUser.IsSuperAdmin ||` branch (`Infrastructure/AppDbContext.cs`): `Project :85-90`, `User :91-96`,
  `ApiKey :99-104`, **`Comment :105-110`**, `ProjectBuild :113-117`, **`Reply :118-123`**, **`PageContextSnapshot :126-131`**, `Invite :136-141`,
  `QuickAccessLink :144-148`, **`PredefinedAction :158-166`** (own-plus-global), **`PredefinedActionSuggestion :170-175`**, `Role :180-188`,
  `StatusPresentation :189-197`, `RoleTenantOverride :202-206`, `AppEnvironment :211-219`, `ProjectAppUrl :222-227`, `Subscription :235-240`,
  `ExtensionSite :241-246`, **`AiRule :247-252`**, `WorkspaceSetting :257-262`, `UsageEvent :263-268`, `Notification :269-274`, `Workspace :288-292`
  (+ DB-11a `WorkspaceMembership`, DB-12 `AuditEvent`). Bold = **content** entities (§3.1). `DeviceLogin` has no filter (`:276-284`).
- `ICurrentUser` (`Application/Abstractions/ICurrentUser.cs`) / `HttpCurrentUser` (`Infrastructure/CurrentUser/HttpCurrentUser.cs`): `TenantId` = claim
  `tenant`; `IsSuperAdmin` = claim `is_super_admin`. `TenantStamp.OwnerFor(u) => u.IsSuperAdmin ? null : u.TenantId` (`Application/Common/TenantStamp.cs:11`);
  DB-11a adds `TryRequireOwner` (false for super admins → writes Forbidden).
- JWT: `Infrastructure/Auth/JwtTokenService.cs` (`JwtOptions { SigningKey, Issuer, LifetimeHours = 12 }`; DB-11b adds `SelectionLifetimeMinutes`,
  `IssueSelection`); `ITokenService` (`Application/Abstractions/ITokenService.cs`). Validator `API/Extensions/AuthenticationExtensions.cs:55-105`
  (after DB-11b: unconditional `OnTokenValidated`, first statement = `SelectionScopeFence`; after DB-11a: `tenant` claim ⇒ live membership + `mstamp` required).
  `API/Extensions/SelectionScopeFence.cs` (DB-11b) — exact `PathString.Equals`, never `StartsWithSegments` (GLM A6).
- Super-admin-only surfaces: `TenantsController` (`[Route("api/admin/tenants")]`, `[Tags("Tenants")]`, `Policies.SuperAdmin`; routes use `{id:int}` = admin
  user id, `TenantResponse.OwnerId` = the workspace uuid), `SettingsController`, `PlansController`, `BrandingController`, `StatsController.GetInsights :24-33`.
- Code that widens reads for super admins outside the filters (each line verified today): `AiRuleService.GetInsightsAsync :308-318` (`effectiveTenantId`),
  `:320-325` (`IgnoreQueryFilters`), `:476` (`detailed = (includeDetails || _currentUser.IsSuperAdmin) ? allRules…` — rule **prompts**), `recent = allRules.Take(10)` (prompts);
  `AiRuleService.ListAllRulesAsync :507-530` (`IgnoreQueryFilters`, prompts); `PlatformInsightsService.GetPlatformInsightsAsync :34-66`
  (`LoadCommentsAsync(ignoreFilters: false)` + `LoadWidgetLanguageEventsAsync(ignoreFilters: false)` — projects to `CommentRow` **without** body: metadata);
  `StatsService.GetAsync` (comment counts through the filter; constructor takes only `IUnitOfWork`); `ExportImportService.FilteredCommentQuery :76-77`
  (`includePrivate = options.IncludePrivate && _currentUser.IsAdmin`); `ProjectService.EnsureAsync :881-888` (`if (_currentUser.IsSuperAdmin) return NotFound`;
  `ownerId = TenantStamp.OwnerFor(_currentUser)`); `WorkspaceService.GetAsync :33` / `RenameAsync :56` (super admin → Forbidden); `CommentFieldService :250,268`;
  `CommentService.CreateAsync :63` (super admin → Forbidden, write); `PredefinedActionService.CreateTenantAsync :46` (write).
- **Reads that skip the owner predicate for super admins outside the filters (agy DB-13 #1, verified 2026-09-23):** `SuggestionService.LoadOwnAsync :320-334`
  (`IgnoreQueryFilters()`, then `if (!_currentUser.IsSuperAdmin) q = q.Where(s => s.OwnerId == owner)` — a plain operator loads **any** workspace's
  `PredefinedActionSuggestion`, whose `Text`/`Prompt`/`AdminFeedback` are content; callers `ApproveAsync :121`, `RejectAsync :171`, `RequestChangesAsync :193`
  return `MapToResponse(suggestion, …)` and **mutate** the row). `PredefinedActionService.LoadOwnTenantWideAsync :91-101` is safe (owner = `OwnerFor ?? Id`,
  becomes `TryRequireOwner` in DB-11a → null for super admins → NotFound). `ProfileService.BuildAsync :74-89` (agy #2) reads `Comment`/`Reply` **through the
  filter** with `AuthorId == pid` — counts only, no body — and is reached by `GET /api/admin/users/{id}/profile` (`UsersController.cs:74-78`, `Policies.Admin`)
  and `GET /api/me/profile`; its constructor is `(IUnitOfWork, IApiKeyService)` (`:17`). `ExportImportService.ExportWorkspaceAsync :63-66` resolves no project
  (no `EnsureAsync`), unlike `ExportProjectAsync :53-61` (GLM DB-13 #3).
- E-mail precedent: `AuthService.ChangePasswordAsync :173-195` (`_branding.BuildResponseAsync("", new HashSet<string>())`, `brand.ProductName`,
  `WorkspaceNameResolver.ResolveForEmailAsync`, `_emailService.SendAsync` in best-effort `try/catch`). `IEmailService` is capped per day.
- Notification rows require `Notification.ProjectId int` **NOT NULL** with FK `projects` Restrict (`Domain/Entity/Notification.cs:12-13`) — a workspace-level
  notification has no project (drives D13.4).
- Hosted-job precedent for a short-period sweep: `API/Hosted/DemoCleanupService.cs` (30 s initial delay, `PeriodicTimer(1h)`, one scope per unit, never throws).
- R8.5 reflection test exclusions: `Tests/WorkspaceTests.cs:311-312` (`Workspace`, `UsageEvent`; DB-12 adds `AuditEvent`).
- Test doubles implementing `ICurrentUser` by hand (each gains the new members): `grep -rln "class FakeCurrentUser : ICurrentUser" Tests` → `TenantQueryFilterTests`,
  `RetentionServiceTests`, `UserGovernanceTests`, `WorkspaceAdminOwnershipTests`, `WorkspaceTests`, `UsageEventFirstCommentTests` and others — the grep is the list.
  NSubstitute mocks (`UsageEventServiceTests`) need nothing.
- Dashboard: `../pointer-dashboard/react/src/features/tenants/TenantsPage.tsx` (row actions via `RowActionItem`, `Dialog`, `ConfirmDialog`, mutations `:121-340`),
  `features/shell/Shell.tsx` (`useAuth` `:82`, `isSuperAdmin` `:300`), auth store holds the token used by the axios mutator.

## 3. Design

### 3.1 What is content, what is metadata (D13.1, D13.2)

**Content** (invisible to the operator unless impersonating; read-only then): `comments` (body, `element` — selector/snapshot/page/screenshot URL —,
`custom_fields`, `picked_actions`, `payload_flags`, replies), `replies`, `page_context_snapshots`, screenshot files (`/api/uploads/file` — only reachable
through a comment), `predefined_action_suggestions` (stakeholder text, admin feedback), `ai_rules` (title, prompt), `predefined_actions` **tenant rows**
(text, prompt — D13.2; global null-owner rows stay operator-managed), export files, **private comments — never, not even impersonating (D13.7)**.

**Review fix #2 (2026-09-23, screenshot-URL clamp):** a signed screenshot URL (`UploadSigner`, 3600s TTL) handed to an impersonating operator must
not keep working after their session ends. Decision taken: **Option 1 as written** (`IUploadSigner.SignedUrl` gains an optional `notAfter` clamp,
`ICurrentUser.ImpersonationExpiresAt` sources it from the token's own `exp` claim), made non-invasive via C# default interface members — both new
members have a default body (`SignedUrl(relPath, notAfter) => SignedUrl(relPath)`; `ImpersonationExpiresAt => null`), so none of the ~80 hand-written
`ICurrentUser`/`IUploadSigner` test doubles across `Tests/` needed touching; only `HttpCurrentUser` and the concrete `UploadSigner` implement the real
logic. `CommentService`'s two screenshot mappers pass `_currentUser.ImpersonationExpiresAt` unconditionally (null outside impersonation, so the URL's
TTL is unaffected for every ordinary caller). The `IImpersonationClock`-style fallback in the review finding was not needed — the plumbing turned out
cheap once expressed as a default interface member instead of a breaking signature change.

**Metadata** (operator sees across all workspaces, unchanged): `workspaces`, `workspace_memberships`, `users` (names/e-mails of members — support needs
them), `projects` (keys, names, URLs, tech stack, activation), `project_app_urls`, `app_environments`, `roles`, `role_tenant_overrides`,
`status_presentations`, `invites`, `quick_access_links` (never the raw token — it is not stored), `api_keys` (never the secret), `device_logins`,
`subscriptions`/`plans`, `extension_sites`, `workspace_settings` (field *definitions*), `usage_events` (+ DB-15 `usage_daily`), `notifications`
(scoped to the caller's own inbox by `MyNotifications()` anyway), `audit_events`, `impersonation_sessions`, `project_builds`, counts and statuses
of comments (`StatsService`, `PlatformInsightsService`, `TenantService.ListAsync`).

### 3.2 Table `impersonation_sessions` — `Domain/Entity/ImpersonationSession.cs` (**not** a `BaseEntity`; `long` PK)

| property | column | type | null | notes |
|---|---|---|---|---|
| `Id` | `id` | `bigint` identity | no | the JWT `imp` claim |
| `OwnerId` | `owner_id` | `uuid` FK `workspaces(id)` **ON DELETE SET NULL**, `fk_impersonation_sessions_workspaces_owner_id` | yes | the impersonated workspace (NULL after it is hard-deleted). Named `OwnerId` for the R8 filter shape; excluded from `HardDeleteOrder` like `audit_events` (operator record) |
| `OperatorUserId` | `operator_user_id` | `uuid` | no | super admin's `public_id` (R14 content reference, no FK) |
| `Reason` | `reason` | `varchar(500)` | no | free text, 10–500 chars (validator) |
| `StartedAt` | `started_at` | `timestamptz` | no | |
| `ExpiresAt` | `expires_at` | `timestamptz` | no | `StartedAt + minutes` |
| `EndedAt` | `ended_at` | `timestamptz` | yes | set by `end` or by the sweep |
| `EndReason` | `end_reason` | `int` | yes | enum `ImpersonationEndReason { Manual = 1, Expired = 2 }` (`Domain/Enums/…`; append-only) |
| `RequestCount` | `request_count` | `int` | no | default 0; incremented per authenticated request carrying `imp` |
| `LastRequestAt` | `last_request_at` | `timestamptz` | yes | |

Indexes: `ix_impersonation_sessions_owner_started (owner_id, started_at DESC)`, **`ux_impersonation_sessions_operator_live UNIQUE (operator_user_id) WHERE ended_at IS NULL`**
(`.IsUnique().HasFilter("ended_at IS NULL")` — one live session per operator is enforced by the **database**, GLM DB-13 #4; the `AlreadyActive` pre-check in
`StartAsync` is the friendly message, the unique violation is the race guard — see §3.6 for the catch). Sqlite honours the `HasFilter` string (DB-15 §2 fact). Filter: strict-own copy of `UsageEvent` (`:263-268`) —
a workspace admin may list the sessions that targeted **their** workspace (`GET /api/admin/impersonation` for admins returns own-workspace rows;
super admin sees all). `DbSet<ImpersonationSession> ImpersonationSessions` on `AppDbContext`, `IUnitOfWork`, `UnitOfWork`. `Tests/WorkspaceTests.cs:311-312`
→ add `&& t != typeof(ImpersonationSession)` with the comment "operator record: FK SET NULL, survives the workspace (DB-13)".

**Migration `AddImpersonationSessions`** (scaffolded): `CreateTable` + FK + 2 indexes (`CreateIndex(... unique: true, filter: "ended_at IS NULL")` for the second), nothing else (R1; no marker). `Down()` = `DropTable`.
**Every existing row:** untouched.

### 3.3 The impersonation token and `ICurrentUser`

`ITokenService.IssueImpersonation(User operator, Guid workspaceId, long sessionId, DateTime expiresAt)` (new). Claims: `sub`, `email`, `name`, `role_id`,
`role`, `is_admin = "true"` (so `Policies.Admin` GETs work), `is_super_admin = "true"`, `stamp` (identity stamp — revocable like any token),
**`tenant` = workspaceId**, **`scope = "impersonate"`**, **`imp` = sessionId**; `expires: expiresAt` (hard; ≤ 60 min); **no** `mstamp`, no `is_quick_access`.
Because `tenant` is set, `HttpCurrentUser.TenantId` = the target and every strict-own filter's second branch
(`currentUser.TenantId != null && e.OwnerId == currentUser.TenantId`) admits exactly that workspace's rows — **no new filter branch is needed for
impersonation**; the change is to *remove* the unconditional super-admin branch from the content entities (§3.4).

`ICurrentUser` gains:
```csharp
/// <summary>DB-13: the impersonation_sessions.id from the JWT "imp" claim; null for every ordinary token.</summary>
long? ImpersonationSessionId { get; }
/// <summary>True only for a super admin acting under a live impersonation session (scope=impersonate).</summary>
bool IsImpersonating { get; }   // => ImpersonationSessionId != null
```
`HttpCurrentUser`: `ImpersonationSessionId` = `long.TryParse(FindFirst("imp"))`; `IsImpersonating = ImpersonationSessionId != null`. Every hand-written
`ICurrentUser` test double (§2 grep) gains `public long? ImpersonationSessionId { get; set; } public bool IsImpersonating => ImpersonationSessionId != null;`.

**Invariant the validator guarantees:** a super-admin token carries a `tenant` claim **only** when it is an impersonation token with a live session.
`JwtTokenService.Issue(User, WorkspaceMembership?, …)` (DB-11a) emits `tenant` only from a membership, and super admins have none.

### 3.4 Filter and service changes — enumerated

**`AppDbContext.cs` — delete the `currentUser.IsSuperAdmin ||` line (first branch) in exactly these six filters**, leaving the tenant and legacy-null
branches as they are, and add above each the comment `// DB-13 (F2): content — no unconditional super-admin branch; an operator reads this only under an impersonation token, whose tenant claim is the target workspace.`:
`Comment :105-110`, `Reply :118-123`, `PageContextSnapshot :126-131`, `PredefinedActionSuggestion :170-175`, `AiRule :247-252`.
`PredefinedAction :158-166` (own-plus-global): replace the first branch with `(currentUser.IsSuperAdmin && currentUser.TenantId == null && e.OwnerId == null)`
— a plain operator still manages the **global** actions; tenant rows are content. Every other filter keeps its super-admin branch (metadata).

**Services (each line verified in §2):**

| File:line | Today | Change |
|---|---|---|
| `AiRuleService.GetInsightsAsync :318` | `effectiveTenantId = IsSuperAdmin ? tenantId : TenantId` | `IsSuperAdmin && !IsImpersonating ? tenantId : TenantId` (an impersonating operator is pinned to the session's workspace) |
| `AiRuleService.GetInsightsAsync :476` and `recent` | prompts returned to any super admin | `var canReadRules = !_currentUser.IsSuperAdmin || _currentUser.IsImpersonating;` → `recent = canReadRules ? … : new()`, `detailed = canReadRules && (includeDetails || IsSuperAdmin) ? … : null`. Counts/tool usage/tenant summaries unchanged (metadata) |
| `AiRuleService.ListAllRulesAsync :507-517` | `IgnoreQueryFilters`, any tenant | first line: `if (_currentUser.IsSuperAdmin && !_currentUser.IsImpersonating) return Forbidden(MessageKeys.Impersonation.Required);` and `effectiveTenantId = _currentUser.TenantId` when impersonating |
| `PlatformInsightsService.GetPlatformInsightsAsync :41,:49` | `LoadCommentsAsync(ignoreFilters: false)`, `LoadWidgetLanguageEventsAsync(ignoreFilters: false)` | both `ignoreFilters: true` (the method is already super-admin-only at `:36`; the projection `CommentRow` carries no body — metadata). Without this the platform insights page goes empty |
| `StatsService.GetAsync` (comment grouped query) | through the filter | inject `ICurrentUser`; `var comments = Repository<Comment>().Query(); if (_currentUser.IsSuperAdmin && !_currentUser.IsImpersonating) comments = comments.IgnoreQueryFilters();` — counts only. Same for `usersCount`/`pendingUsersCount` after DB-11a (membership counts) |
| `TenantService.ListAsync` comment/project counts | already `IgnoreQueryFilters` | unchanged |
| `ExportImportService.FilteredCommentQuery :76-77` | `includePrivate = IncludePrivate && IsAdmin` | `&& !_currentUser.IsSuperAdmin` (D13.7 — the operator never exports private notes, impersonating or not) |
| `ProjectService.EnsureAsync :881` | `if (IsSuperAdmin) return NotFound` | `if (_currentUser.IsSuperAdmin && !_currentUser.IsImpersonating) return NotFound` |
| `ProjectService.EnsureAsync :888` | `var ownerId = TenantStamp.OwnerFor(_currentUser);` | `var ownerId = _currentUser.TenantId;` — identical for every non-super caller (`OwnerFor` returns `TenantId` for them) and correct under impersonation. Comment: "DB-13: read scope, not a write stamp" |
| `WorkspaceService.GetAsync :33` | super admin → Forbidden | `if (IsSuperAdmin && !IsImpersonating) return Forbidden` (the impersonating operator sees the target's workspace card). `RenameAsync :56` unchanged (write) |
| `CommentFieldService :250` (read) / `:268` (write) | super admin branches | read: same pattern as `WorkspaceService.GetAsync`; write: unchanged |
| `CommentService.CreateAsync :63`, `PredefinedActionService.CreateTenantAsync :46`, DB-11a `TryRequireOwner` | writes refuse super admins | unchanged — and the fence (§3.5) blocks every non-GET anyway |
| **`SuggestionService.LoadOwnAsync :320-334`** (agy DB-13 #1, **Blocker**) | `IgnoreQueryFilters`; owner predicate skipped when `IsSuperAdmin` → a plain operator reads **and approves/rejects** any workspace's suggestion text | replace the `if (!_currentUser.IsSuperAdmin) { … }` block with: `if (_currentUser.IsSuperAdmin) return null; // DB-13 (F2): suggestion review is a workspace write on content — the operator never performs it (the fence blocks it under impersonation too)` followed by an **unconditional** `q = q.Where(s => s.OwnerId == _currentUser.TenantId);`. Callers already map `null` → `NotFound(Suggestion.NotFound)`. Delete the "Super-admin sees all" comment at `:319` |
| **`ProfileService.BuildAsync :74-89`** (agy DB-13 #2) | `Comment`/`Reply`/`Project` reads through the filter → after the six-filter change a plain operator's `GET /api/admin/users/{id}/profile` shows **zero** counts | inject `ICurrentUser` (ctor `:17` gains it; tests constructing `ProfileService` by hand pass a fake); `bool wide = _currentUser.IsSuperAdmin && !_currentUser.IsImpersonating;` and append `.IgnoreQueryFilters()` to the three queries when `wide` (the `AuthorId == pid` predicate stays — counts and project key/name only, no body: metadata). Under impersonation the filter pins to the target |
| **`ExportImportService.ExportWorkspaceAsync :63-66`** (GLM DB-13 #3) | no `EnsureAsync`; a plain operator gets a 200 export with every workspace's project metadata and zero comments | first line: `if (_currentUser.IsSuperAdmin && !_currentUser.IsImpersonating) return Result<ExportFileDto>.Forbidden(MessageKeys.Impersonation.Required);` (mirrors `ListAllRulesAsync`). Impersonating: the export is a GET (fence allows), audited `export.downloaded`, private notes clamped by the row above |
| **`AuditQueryService.ListForWorkspaceAsync`** (DB-12 §3.8a; GLM DB-12 #5) | `TryRequireOwner` false for super admins → an impersonating operator gets 403 on the target's Security log | replace the `// DB-13:` placeholder with `if (_currentUser.IsImpersonating && _currentUser.TenantId is Guid t) owner = t; else if (!TenantStamp.TryRequireOwner(_currentUser, out owner)) return Forbidden(...)`. The D12.5 redaction is keyed on `IsSuperAdmin`, so the operator still sees full actor identity in that view |
| `ActivationStatsService.GetWorkspaceActivationAsync` (DB-15 §3.5) | `TryRequireOwner` | same two-line branch — **only if DB-15 is already merged**; otherwise DB-15 adds it (its §3.5 says so) |
| `UserNameResolver`, `CommentService.GetByIdAsync :547`, `ListAsync :317`, `SuggestionService.ListPendingAsync :83`, `PredefinedActionService.ListTenantAsync :29` | filter-scoped | unchanged — they now return nothing for a plain operator and the target's rows under impersonation |

Rule for anything not listed — three shapes (the third was missed by the first draft, agy DB-13 #1):
1. **a read that derives its scope from `_currentUser.TenantId`** (filter or explicit predicate) works unchanged — empty/404 for a plain operator, the target's rows under impersonation;
2. **a read that widens on `IsSuperAdmin`** (`IgnoreQueryFilters`, `effectiveTenantId`, `ignoreFilters: true`) must add `&& !IsImpersonating` (pinned to the session) — and if it exposes content it must instead refuse a non-impersonating operator (`Forbidden(Impersonation.Required)` or `NotFound`);
3. **a loader that `IgnoreQueryFilters()` and then *skips its owner predicate* when `IsSuperAdmin`** (`LoadOwnAsync` shape) is a content read in disguise — the owner predicate becomes unconditional and the super admin gets `null`.
Checklists the reviewer reads line by line: `grep -rn "IsSuperAdmin" Application/Services/Implementation --include='*.cs'` (60 lines today) **and**
`grep -rn "IgnoreQueryFilters" Application/Services/Implementation --include='*.cs'` (≈130 lines today) — for the second, every hit on `Comment`, `Reply`,
`PageContextSnapshot`, `PredefinedActionSuggestion`, `AiRule`, `PredefinedAction` is either a count/projection without body (metadata — say so in a comment) or
has an explicit owner predicate that a super admin cannot skip. Swept 2026-09-23: the only shape-3 hit was `SuggestionService.LoadOwnAsync`; `PredefinedActionService.LoadOwnTenantWideAsync`
is safe after DB-11a; `CommentService :129-158`, `ExportImportService :529-544`, `TenantService :83`, `PlatformInsightsService :234` are counts/`CommentRow`;
`ProjectService :650-688` is the delete cascade (write, unreachable for super admins via `EnsureAsync`).

### 3.5 Fence, liveness, request counting — `AuthenticationExtensions.OnTokenValidated`

Insert **after** the DB-11b `SelectionScopeFence` statement and **before** the DB-11a stamp/membership block:
```csharp
var scope = principal?.FindFirst("scope")?.Value;
if (scope == "impersonate")
{
    if (!ImpersonationScopeFence.Allows(ctx.HttpContext.Request.Method, ctx.HttpContext.Request.Path))
    { ctx.Fail("Impersonation tokens are read-only."); return; }
    if (!long.TryParse(principal?.FindFirst("imp")?.Value, out var imp)
        || !Guid.TryParse(principal?.FindFirst("tenant")?.Value, out var target)
        || principal?.FindFirst("is_super_admin")?.Value != "true")
    { ctx.Fail("Invalid impersonation token."); return; }
    var live = await db.ImpersonationSessions.IgnoreQueryFilters().AsNoTracking()
        .AnyAsync(s => s.Id == imp && s.OwnerId == target && s.OperatorUserId == publicId && s.EndedAt == null && s.ExpiresAt > DateTime.UtcNow);
    if (!live) { ctx.Fail("Impersonation session has ended."); return; }
    // fall through to the identity-stamp check below; SKIP the membership/mstamp check (operators have no membership)
}
```
and make the DB-11a membership branch `else if (tenantClaim is present && scope != "impersonate")`. No cache for the liveness lookup (one PK
read per request from a single operator); `end` therefore takes effect on the next request. Fail-open on exception stays as today for the stamp
lookup only — the liveness lookup **fails closed** (`ctx.Fail`) because the whole point is that the session is provably live.

`API/Extensions/ImpersonationScopeFence.cs` (static, testable):
```csharp
public static class ImpersonationScopeFence
{
    public static readonly PathString EndPath = new("/api/admin/impersonation/end");
    /// <summary>DB-13: an impersonation token may READ anything its tenant claim admits, and may do exactly one write — end itself.
    /// Exact path comparison (GLM A6 precedent), never StartsWithSegments.</summary>
    public static bool Allows(string method, PathString path) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)
        || (HttpMethods.IsPost(method) && path.HasValue && path.Equals(EndPath, StringComparison.OrdinalIgnoreCase));
}
```
`API/Auth/ImpersonationRequestCounter.cs : IAsyncActionFilter` (global, `Program.cs:49-52`): if `currentUser.IsImpersonating`, after `next()` run
`db.ImpersonationSessions.IgnoreQueryFilters().Where(s => s.Id == imp).ExecuteUpdateAsync(s => s.SetProperty(x => x.RequestCount, x => x.RequestCount + 1).SetProperty(x => x.LastRequestAt, DateTime.UtcNow))`
in a best-effort `try/catch` (relational-only; InMemory tests skip it via `db.Database.IsRelational()`).

### 3.6 Endpoints and service — `Application/Services/Implementation/ImpersonationService.cs : IImpersonationService`

| Route | Policy | Behaviour |
|---|---|---|
| `POST /api/admin/tenants/{workspaceId:guid}/impersonate` (`TenantsController`; `[Audited(AuditActions.ImpersonationStarted)]`) | SuperAdmin | body `StartImpersonationRequest { string Reason; int Minutes = 30 }` (validator: `Reason` 10–500 chars, `Minutes` 1–60). `StartAsync(workspaceId, req)`: caller `IsSuperAdmin && !IsImpersonating` else `Forbidden`; workspace live (`Workspaces.IgnoreQueryFilters()`, `DeletedAt == null`) else `NotFound(Workspace.NotFound)`; a live session for this operator exists → `Conflict(Impersonation.AlreadyActive)`; insert the row; `SaveChangesAsync` inside `try/catch (DbUpdateException) when 23505 / Sqlite 19` (copy the predicate from `CommentService.cs:694-695` verbatim) → `_unitOfWork.ClearChangeTracker(); return Conflict(Impersonation.AlreadyActive)` (the unique partial index `ux_impersonation_sessions_operator_live` is the race guard, GLM DB-13 #4); `token = IssueImpersonation(operator, workspaceId, session.Id, expiresAt)`; audit `impersonation.started` (`OwnerId` = workspace, `after: {reason, minutes, session_id}`); e-mail (§3.7); return `ImpersonationStartResponse { Token, SessionId, ExpiresAt, WorkspaceId, WorkspaceName }` |
| `POST /api/admin/impersonation/end` (new `ImpersonationController`, `[Route("api/admin/impersonation")]`, `[Tags("Impersonation")]`, `[Produces]`, `Policies.SuperAdmin`; `[Audited(AuditActions.ImpersonationEnded)]`) | SuperAdmin (accepts the **impersonation** token — the only write the fence allows — or a plain super-admin token with body `{ long? SessionId }`) | `EndAsync(long? sessionId)`: session = `currentUser.ImpersonationSessionId ?? sessionId` else `Failure(Impersonation.NoSession)`; row live and `OperatorUserId == caller` else `NotFound`; set `EndedAt = now`, `EndReason = Manual`; save; audit `impersonation.ended` (`after: {request_count, duration_seconds, reason: "manual"}`); return `Result.Success(Impersonation.Ended)` |
| `GET /api/admin/impersonation?workspaceId&page&pageSize` (`ImpersonationController`; `Policies.Admin`; `[NoAudit("read")]`) | Admin | `ListAsync`: super admin → all (optional `workspaceId`); workspace admin → `TryRequireOwner` + explicit `OwnerId == owner` (their own history). `PagedData<ImpersonationSessionDto>` (`Id, WorkspaceId, WorkspaceName, StartedAt, ExpiresAt, EndedAt, EndReason, RequestCount, Reason`; **operator identity omitted for workspace admins — D13.6**) |

`orval.config.ts:6` → add `'Impersonation'`.

**Sweep** — `API/Hosted/ImpersonationSweepService.cs` (copy `DemoCleanupService.cs` shape; `PeriodicTimer(TimeSpan.FromMinutes(5))`, 60 s initial delay;
registered in `Program.cs` after `:81`): every pass, `sessions = ImpersonationSessions.IgnoreQueryFilters().Where(s => s.EndedAt == null && s.ExpiresAt <= now)`;
for each: `EndedAt = ExpiresAt`, `EndReason = Expired`, save, audit `impersonation.ended` (`ActorKindOverride: System`, `after: {…, reason: "expired"}`).
So every session has exactly one `started` and one `ended` row within 5 minutes of its end.

### 3.7 Telling the workspace (D13.4, D13.6)

At **start**, one best-effort e-mail to every live admin of the workspace — after DB-11a:
`_memberships.InWorkspace(workspaceId).Where(m => m.LeftAt == null && m.IsActive && m.ApprovalStatus == ApprovalStatus.Approved && m.Role.GrantsAdmin && !m.Role.IsSuperAdmin).Select(m => new { m.User.Email, m.User.DisplayName })`.
Subject `$"An operator is viewing your {brand.ProductName} workspace"`; body (copy the `ChangePasswordAsync` template shape; HTML-encode reason and names):
"Hi {DisplayName}, the {ProductName} operator opened a read-only view of the **{WorkspaceName}** workspace at {StartedAt:u} for up to {Minutes} minutes.
Reason given: *{Reason}*. This is logged in your Security log (Settings → Security log), where you will also see when it ended. If you did not
expect this, reply to this e-mail." No operator name/e-mail (D13.6). **A workspace with zero live admins** (DB-11a §3.5 allows it — `TenantService.ListAsync` shows it with `Id = 0`) gets
**no e-mail at all**; the start is still recorded as an `impersonation.started` row that the next admin (or the operator via `/all`) can read — F2's "visible
to that workspace's admin" degrades to the audit row (GLM DB-13 #2). `StartAsync` logs `Information` "impersonation of {WorkspaceId}: no admin to notify"
in that case and still succeeds. At **end**: no e-mail (the audit row is the record; the sweep writes it for expiries).
**D13.6 depends on DB-12 §3.8a (D12.5):** the workspace audit view redacts `actor_user_id`/`actor_name` to `"Operator"` for `SuperAdmin`/`Impersonation`
rows — without that redaction the promise below is false. It is DB-12's task 12; this doc's §6 test 13 re-asserts it end to end. **No `notifications` row** (D13.4): `notifications.project_id` is NOT NULL with a Restrict FK to `projects` — a workspace-level bell
entry would need a marked `AlterColumn`; the e-mail + Security log cover the requirement; revisit if the owner wants the bell.

### 3.8 Owner decisions encoded here (defaults apply unless the owner says otherwise before §9)

| # | Question | Default |
|---|---|---|
| D13.1 | Content vs metadata boundary | §3.1 as written. Member names/e-mails are metadata (support needs them) |
| D13.2 | Are AI rules and tenant predefined actions (instruction text) content? | **Yes** — workspace-authored text about their product. Global (null-owner) predefined actions stay operator-managed |
| D13.3 | Is an impersonation session read-only? | **Yes** — GET/HEAD/OPTIONS + the `end` call only (`ImpersonationScopeFence`). An operator who must change a workspace's data does it through the existing super-admin surfaces (tenants/plans) or asks the admin |
| D13.4 | How is the workspace told? | E-mail to every live admin at start + `impersonation.started/ended` rows in their Security log. No `notifications` bell row (schema reason in §3.7) |
| D13.5 | Time box | `Minutes` 1–60, default 30; token `exp` = hard expiry; the sweep closes expired sessions within 5 min |
| D13.6 | Is the operator's identity shown to the workspace? | **No** — "the operator"; `operator_user_id` is visible to super admins only (`/all` audit view, `impersonation` list for super admins). Enforced by DB-12 D12.5 (audit DTO redaction) + this doc's `ImpersonationSessionDto` (no operator field for workspace callers) |
| D13.7 | Private comments (`is_private`) under impersonation | **Never visible** (list/get already hide them from non-authors; export gets the explicit `!IsSuperAdmin` clamp) |

## 4. Safety classification

**Additive** migration (R1) — ordinary deploy (R7). Code **narrows** what the super-admin token can read (six filters, §3.4) and adds a new
token scope fenced to reads (R16). No existing row changes; nothing is deleted. Tenancy: the impersonation token's `tenant` claim is the *only*
way an operator reaches content, the validator proves the session is live on every request, and §6 tests 2–4 prove: plain operator sees no
content; impersonating operator sees only the target's content; workspace B sees no session of A.

## 5. File-level tasks

1. `Domain/Enums/ImpersonationEndReason.cs`; `Domain/Entity/ImpersonationSession.cs` (§3.2, doc-comment "Operator record (DB-13): FK to workspaces SET NULL; survives the workspace; excluded from HardDeleteOrder").
2. `Infrastructure/Mappings/ImpersonationSessionMapping.cs` (copy `UsageEventMapping.cs`; FK `SetNull` named `fk_impersonation_sessions_workspaces_owner_id`; two indexes with `HasDatabaseName`, the partial one `.IsUnique().HasFilter("ended_at IS NULL").HasDatabaseName("ux_impersonation_sessions_operator_live")`). `AppDbContext.cs` — DbSet after `Workspaces`; strict-own filter copied from `:263-268`. `IUnitOfWork`/`UnitOfWork` — `DbSet<ImpersonationSession> ImpersonationSessions`. `Tests/WorkspaceTests.cs:311-312` — add the exclusion.
3. `just migrate name="AddImpersonationSessions"` → read against §3.2 (one table, one FK, two indexes; anything else → stop and report). No marker.
4. `Application/Abstractions/ICurrentUser.cs` + `Infrastructure/CurrentUser/HttpCurrentUser.cs` — §3.3 members; every `FakeCurrentUser` in `Tests` (§2 grep) gains them.
5. `Application/Abstractions/ITokenService.cs` + `Infrastructure/Auth/JwtTokenService.cs` — `IssueImpersonation` per §3.3 (claims list verbatim; `expires: expiresAt`).
6. `Infrastructure/AppDbContext.cs` — the six filter edits of §3.4, comments verbatim.
7. Service edits of §3.4 (`AiRuleService`, `PlatformInsightsService`, `StatsService` (+ `ICurrentUser` ctor param; tests constructing `StatsService` by hand pass a fake), `ExportImportService` (`FilteredCommentQuery` clamp **and** `ExportWorkspaceAsync` refusal), `ProjectService`, `WorkspaceService`, `CommentFieldService`, **`SuggestionService.LoadOwnAsync`** (unconditional owner predicate; super admin → `null`), **`ProfileService`** (+ `ICurrentUser` ctor param; `IgnoreQueryFilters` when `IsSuperAdmin && !IsImpersonating`; tests constructing `ProfileService` by hand pass a fake), **`AuditQueryService.ListForWorkspaceAsync`** (impersonation branch replacing DB-12's `// DB-13:` placeholder), `ActivationStatsService` if DB-15 is merged).
8. `API/Extensions/ImpersonationScopeFence.cs` (§3.5 verbatim); `API/Extensions/AuthenticationExtensions.cs` — the §3.5 block after the selection fence, membership branch becomes `else if`; `API/Auth/ImpersonationRequestCounter.cs` + `Program.cs:49-52` `options.Filters.Add<ImpersonationRequestCounter>()`.
9. `Application/DTOs/Impersonation/StartImpersonationRequest.cs`, `ImpersonationStartResponse.cs`, `ImpersonationSessionDto.cs`, `EndImpersonationRequest.cs { long? SessionId }`; `Application/Validators/StartImpersonationValidator.cs` (`Reason` `NotEmpty().Length(10, 500)`, `Minutes` `InclusiveBetween(1, 60)`).
10. `Application/Services/Interfaces/IImpersonationService.cs` + `Implementation/ImpersonationService.cs` (§3.6; ctor: `IUnitOfWork, ICurrentUser, ITokenService, IAuditWriter, IMembershipService, IEmailService, IBrandingService, ILogger`; `StartAsync` wraps its `SaveChangesAsync` in the 23505/Sqlite-19 catch → `Conflict(AlreadyActive)`; zero admins → log + succeed).
11. `API/Controllers/Admin/TenantsController.cs` — add `Impersonate(Guid workspaceId, [FromBody] StartImpersonationRequest request)` with `[HttpPost("{workspaceId:guid}/impersonate")]`, `[Audited(AuditActions.ImpersonationStarted)]`, `[ProducesResponseType(typeof(ImpersonationStartResponse), 200)]` + 403/404/409 `Result`. New `API/Controllers/Admin/ImpersonationController.cs` per §3.6. `orval.config.ts:6` → `'Impersonation'`.
12. `API/Hosted/ImpersonationSweepService.cs` + `Program.cs` registration after `:81`.
13. `Application/Resources/MessageKeys.cs` — class `Impersonation`: `Required = "This content is only available inside an impersonation session (View as…)."`, `AlreadyActive = "You already have a live impersonation session — end it first."`, `NoSession = "No impersonation session to end."`, `Ended = "Impersonation session ended."`, `ReadOnly = "Impersonation sessions are read-only."`.
14. Tests (§6); `just fmt`; `just test`; `docs/db/SCHEMA.md` row `impersonation_sessions` from *(planned)* to present.

## 6. Tests

`Tests/ImpersonationTests.cs` (InMemory fixture `TenantQueryFilterTests.cs:20-46` for filters; `UserGovernanceTests.cs:20-60` + `TestSeed.Join` for the service; Sqlite `TestDb` for the counter):

1. `SuperAdmin_WithoutTenantClaim_SeesNoContent` — seed a comment, reply, snapshot, suggestion, AI rule, tenant predefined action for workspace A; `FakeCurrentUser { IsSuperAdmin = true }` → each of the six sets is empty; `Projects`, `Users`, `Workspaces`, `Invites`, `UsageEvents`, `AuditEvents` still list A's rows (metadata). Global predefined action (null owner) still visible.
2. `SuperAdmin_Impersonating_SeesOnlyTargetContent` — `FakeCurrentUser { IsSuperAdmin = true, TenantId = A, ImpersonationSessionId = 1 }` → sees A's six content sets, nothing of B.
3. `TenantB_SeesNoSessionOfA` (R8) — `ImpersonationSessions` under B's context empty; `ListAsync` under B with `workspaceId = A` returns only B's.
4. `Start_InsertsSession_IssuesToken_Audits_Emails` — token decoded with `JwtSecurityTokenHandler` (`TokenServiceTests.cs`): `scope == "impersonate"`, `tenant == A`, `imp == session.Id`, `is_super_admin == "true"`, no `mstamp`, `exp` within `Minutes`; `FakeAuditWriter` got `impersonation.started` with `OwnerId == A`; `CapturingEmail` got one mail per live admin membership (two admins seeded, one disabled → 2 mails), body contains the reason and **not** the operator's e-mail.
5. `Start_SecondLiveSession_Conflict`; `Start_NonSuperAdmin_Forbidden`; `Start_UnknownWorkspace_NotFound`; `Start_Validator_RejectsShortReason_And61Minutes`.
6. `End_SetsEndedAt_Audits_WithRequestCount`; `End_ByOtherOperator_NotFound`; `Sweep_ClosesExpired_WritesEndedRow_ReasonExpired` (drive the sweep's static `SweepOnceAsync(AppDbContext, IAuditWriter, …)` directly, as `RetentionServiceTests` does).
7. `Fence_AllowsReadsAndEndOnly` — `Allows("GET", "/api/comments/1")` true; `("POST", "/api/admin/impersonation/end")` true; `("POST", "/api/admin/impersonation/end/")` false (trailing slash); `("POST", "/api/admin/impersonation/endx")` false; `("PUT", "/api/admin/workspace/name")` false; `("DELETE", "/api/admin/users/1")` false; `("POST", "/api/events")` false.
8. `Validator_RejectsEndedOrExpiredSession` — extract the liveness check into `static ImpersonationLiveness.IsLiveAsync(AppDbContext, long imp, Guid target, Guid operatorId)` and test: live → true; ended → false; expired → false; wrong workspace → false; wrong operator → false.
9. `AiRules_ListAll_PlainOperator_Forbidden_Impersonating_PinnedToTenant`; `AiRules_Insights_PlainOperator_NoPrompts_CountsIntact`; `PlatformInsights_PlainOperator_StillCountsAllComments` (regression for the `ignoreFilters: true` switch); `Stats_PlainOperator_CountsAllComments`; `Export_Impersonating_NeverIncludesPrivate`.
10. `ProjectEnsure_Impersonating_ResolvesTargetProject_PlainOperator_NotFound`; `WorkspaceGet_Impersonating_ReturnsTarget`.
11. `RequestCounter_IncrementsOnSqlite` (Sqlite `TestDb`; InMemory skipped via `IsRelational()`).
12. Existing data survives: N/A (new table); the migration is exercised by DB-10 from empty and by the R11 rehearsal.
13. **Cross-review additions (2026-09-23):** `Suggestions_PlainOperator_ApproveRejectRequestChanges_NotFound` (seed a pending suggestion in A; `FakeCurrentUser { IsSuperAdmin = true }` → all three return `IsNotFound`, the row is still `Pending`, and the returned `Result` carries no `Text`/`Prompt`); `Suggestions_WorkspaceAdmin_StillApproves` (regression: A's admin approves A's suggestion; B's admin → NotFound);
    `Profile_PlainOperator_CountsAcrossWorkspaces` (author with comments in A and B; plain operator → both counted; A's admin → only A's; impersonating A → only A's);
    `ExportWorkspace_PlainOperator_Forbidden` (`IsForbidden`, message `Impersonation.Required`) and `ExportWorkspace_Impersonating_ReturnsTargetComments_NoPrivate`;
    `Start_ConcurrentSecondSession_Conflict_ViaUniqueIndex` (Sqlite `TestDb`: insert a live session row for the operator directly, then call `StartAsync` → `IsConflict`, exactly one live row; proves the catch, not just the pre-check);
    `AuditWorkspaceView_Impersonating_ReturnsTargetRows` (`FakeCurrentUser { IsSuperAdmin = true, TenantId = A, ImpersonationSessionId = 1 }` → `ListForWorkspaceAsync` returns A's rows, not Forbidden, with actor identity **not** redacted because the caller is a super admin);
    `AuditWorkspaceView_WorkspaceAdmin_SeesNoOperatorIdentity` (end to end: `StartAsync` as the operator, then as A's admin `ListForWorkspaceAsync(action: "impersonation.")` → `ActorName == "Operator"`, `ActorUserId == null`, and the serialised page contains neither the operator's uuid nor display name);
    `Start_WorkspaceWithoutAdmins_SucceedsWithoutMail` (zero live admin memberships → `IsSuccess`, zero mails, one `impersonation.started` row).

## 7. Acceptance criteria

1. `dotnet ef migrations list -p Infrastructure -s API --no-connect` ends with `_AddImpersonationSessions`; `grep -c ContractMigration Infrastructure/Migrations/*_AddImpersonationSessions.cs` → 0.
2. `grep -n "currentUser.IsSuperAdmin$\|currentUser.IsSuperAdmin *$" Infrastructure/AppDbContext.cs` — reviewer confirms the line is **absent** from the `Comment`, `Reply`, `PageContextSnapshot`, `PredefinedActionSuggestion`, `AiRule` filters and rewritten in `PredefinedAction`; present everywhere else (count of `IsSuperAdmin` occurrences in the file drops by exactly 5).
3. `grep -c "StartsWithSegments" API/Extensions/ImpersonationScopeFence.cs` → 0; `grep -c '"impersonate"' Infrastructure/Auth/JwtTokenService.cs API/Extensions/AuthenticationExtensions.cs` → ≥ 1 each.
4. `grep -rn "IsSuperAdmin" Application/Services/Implementation --include='*.cs' | grep -v "IsImpersonating\|Forbidden\|//\|GrantsAdmin\|IsSuperAdmin = \|r.IsSuperAdmin\|Role.IsSuperAdmin\|u.Role" ` — reviewer reads every remaining line against the §3.4 rule (three shapes).
4a. `grep -n "IsSuperAdmin" Application/Services/Implementation/SuggestionService.cs` → exactly one hit, `if (_currentUser.IsSuperAdmin) return null;` (plus the `!u.Role.IsSuperAdmin` admin-list predicate); `grep -c "IgnoreQueryFilters" Application/Services/Implementation/ProfileService.cs` → 3; `grep -c "Impersonation.Required" Application/Services/Implementation/ExportImportService.cs` → 1; `grep -c "IsImpersonating" Application/Services/Implementation/AuditQueryService.cs` → 1.
4b. `grep -c "ux_impersonation_sessions_operator_live" Infrastructure/Migrations/*_AddImpersonationSessions.cs` → ≥ 1 and the `CreateIndex` carries `unique: true` and `filter: "ended_at IS NULL"`.
5. `curl -s …/swagger.json | jq '.paths["/api/admin/tenants/{workspaceId}/impersonate"].post.tags, .paths["/api/admin/impersonation/end"].post.tags'` → `["Tenants"]`, `["Impersonation"]`; `grep -c "'Impersonation'" orval.config.ts` → 1.
6. `just test` green with the 20+ new facts; DB-10 green.
7. Manual on the rehearsal API: as super admin `GET /api/projects/{key}/comments` → 404, `GET /api/admin/ai-rules/all` → 403, `POST /api/admin/suggestions/{id}/approve` → 404, `GET /api/admin/export/workspace` → 403; `POST …/impersonate {reason, 15}` → token; with it `GET /api/projects/{key}/comments` → the target's comments, `PUT /api/admin/workspace/name` → 401, a private comment is absent; `POST /api/admin/impersonation/end` → 200 and the next GET with that token → 401; `GET /api/admin/audit?action=impersonation.` as the workspace admin shows start and end; the local mail server received the start e-mail without the operator's address; `/api/admin/stats/insights` and `/api/admin/stats` still show non-zero comment counts for the plain operator.
8. Leave a session to expire on the rehearsal API (Minutes = 1): within 6 minutes `impersonation_sessions.ended_at` is set with `end_reason = 2` and an `impersonation.ended` audit row with `reason: expired` exists.

## 8. Rollback

Revert the commit; ordinary redeploy. The table may stay (additive; `Down()` = `DropTable` if wanted). Reverting **re-opens** silent operator
access to content — say so in the release notes; the audit rows already written stay (append-only).

## 9. Release steps

1. Merge after DB-12 is in production and DB-11b is merged. R11 rehearsal (§7 criterion 7–8) on a same-day dump.
2. `bash scripts/deploy-api.sh` (ordinary). Expect one `Applying migration` line.
3. Verify criterion 7 against production with the owner's super-admin account and the real workspace — **one that has a live Workspace Admin membership** (`TenantService.ListAsync` row with `id != 0`), otherwise no e-mail is expected (GLM DB-13 #2) — (reason "post-deploy verification, DB-13"; end it immediately; the workspace's admin — the owner — receives the e-mail; `GET /api/admin/audit?action=impersonation.` as that admin shows `actorName: "Operator"`).
4. Watch `docker compose logs api` for `Impersonation session has ended` (expected after `end`), `Impersonation tokens are read-only` (the dashboard tried a write under the banner — UI bug, not a server bug) and for `ImpersonationSweepService` errors.
5. Dashboard: `dashboard-agent` regenerates the client from production once, then §11.

## 10. Out of scope

Operator MFA (§61); a bell notification row (D13.4); making the super admin's *metadata* view narrower; auditing every GET under impersonation
individually (the counter + start/end rows are the record; per-request rows would be noise); per-session scoping to one project; the
`legal hold` flag (DB-11c forward reference); the widget and CLI (neither carries an operator token); the tenant-isolation CI probe (§67 —
add "plain super-admin token gets 404/403 on content" to its inventory when it is written); `clients/`.

## 11. Dashboard / widget / CLI tasks

**Dashboard** (after client regen: `usePostApiAdminTenantsWorkspaceIdImpersonate`, `usePostApiAdminImpersonationEnd`, `useGetApiAdminImpersonation`, DTOs):
1. `TenantsPage.tsx`: row action "View as…" (icon `Eye`) → dialog with a required reason (10–500 chars, helper text "The workspace's admins are e-mailed this reason") and a duration select (15 / 30 / 60 min, default 30) → on success: store the **operator** token under `pointer.operatorToken`, replace the active token with the impersonation token, `queryClient.clear()`, navigate to `/`.
2. `Shell.tsx`: when the decoded active token has `scope === "impersonate"`, render a persistent top banner: "Viewing **{workspaceName}** as operator (read-only) · ends {expiresAt, relative} · [End session]". End → `POST /api/admin/impersonation/end`, restore the operator token, `queryClient.clear()`, navigate to `/tenants`. On any 401 while the banner is shown (session ended/expired) → same restore path with a toast "Impersonation session ended".
3. Under the banner hide/disable write affordances the shell controls (create/edit/delete buttons on projects, users, invites, settings, roles); server-side 401s from the fence are shown as the toast `envelope.message` — the banner is the primary guard, the server the real one.
4. Security log page (DB-12): label map entries `impersonation.started → "Operator viewed this workspace"`, `impersonation.ended → "Operator view ended"`; render `after.reason`, `after.minutes`, `after.request_count`.
5. Settings (workspace admin): a small "Operator access" card listing `GET /api/admin/impersonation` (own workspace) — date, duration, reason, requests. No operator identity (D13.6).

**Widget:** none. **CLI:** none.

## 12. Cross-review adjudication (2026-09-22 reviews, folded 2026-09-23)

Reports: `docs/db/reviews/REVIEW-GLM-DB12-15-2026-09-22.md`, `docs/db/reviews/REVIEW-AGY-DB12-15-2026-09-22.md`. Every citation re-checked against the tree on 2026-09-23.

| Finding | Claim | Verdict | Where it landed |
|---|---|---|---|
| agy DB-13 #1 (**Blocker**) | `SuggestionService.LoadOwnAsync` (`:320-334`) skips the owner predicate for super admins → a plain operator reads and approves/rejects any workspace's suggestion content | **Accepted** — verified; GLM's sweep missed it because it only walked `IsSuperAdmin` reads that *widen*, not loaders that *skip* the owner predicate | §2 fact, §3.4 new row + rule shape 3, §5 task 7, §6 test 13, §7 crit. 4a/7 |
| agy DB-13 #2 (Major) | `ProfileService.BuildAsync` counts go to zero for operators | **Accepted as Minor** — counts/project names only (metadata), no leak, no data loss; reachable via `GET /api/admin/users/{id}/profile` | §3.4 row, §5 task 7, §6 test 13 |
| GLM DB-13 #1 (Major, = GLM DB-12 #3) | D13.6 broken by DB-12's audit DTO | **Accepted** — fixed in DB-12 §3.8a (D12.5); this doc re-asserts it end to end | §3.7 paragraph, D13.6 row, §6 test 13 |
| GLM DB-13 #2 (Minor) | Admin-less workspaces get no notification | **Accepted** (words + log + release-step workspace choice) | §3.7, §5 task 10, §6 test 13, §9 step 3 |
| GLM DB-13 #3 (Minor) | `ExportWorkspaceAsync` returns a misleading 200 for a plain operator | **Accepted** — `ExportImportService.cs:63-66` has no `EnsureAsync` | §2, §3.4 row, §5 task 7, §6 test 13, §7 crit. 4a/7 |
| GLM DB-13 #4 (Minor) | One-live-session-per-operator is code-only | **Accepted** — the partial index becomes unique (same DDL cost) + 23505 catch | §3.2, §3.6, §5 tasks 2/10, §6 test 13, §7 crit. 4b |
| GLM DB-13 "verified correct" list | Six-filter removal breaks no operator screen; token design sound | **Confirmed**, with the one omission above (`LoadOwnAsync`); the §3.4 checklist now includes the `IgnoreQueryFilters` grep so the class of miss cannot recur | §3.4 rule |
