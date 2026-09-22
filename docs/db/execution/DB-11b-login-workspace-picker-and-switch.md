# DB-11b — Login workspace picker, `switch-workspace`, `/me` memberships

Depends on [DB-11a](DB-11a-identity-and-workspace-memberships.md) being **in production** (the
membership table, the `mstamp` claim and `IMembershipService`). Owner acceptance 2026-09-22: "login
with an email that exists in several workspaces must offer a workspace choice instead of an
arbitrary row". Rules: R8 (point 5 test), R13 (no migration — nothing to read), R16.
**Class: Code only.** No migration. Ships as an ordinary `bash scripts/deploy-api.sh`.
**Status 2026-09-22: written; not implemented.** Owner decision D11 has a default (§3.6).
**Amended 2026-09-22 (evening)** after the cross-review (`docs/roadmap/meetings/2026-09-22-foundations/04-chair-synthesis.md`
§1 rows D5/D6 = GLM A5/A6; cited below by finding id): **GLM A5** the new endpoint's rate limit is named
explicitly — `[EnableRateLimiting("login")]` — because `Login` carries no such attribute to copy; **GLM A6**
the selection-token fence compares the exact path, not a prefix.

## 1. Goal

After DB-11a a person with memberships in several workspaces logs in and lands in their *home*
workspace silently. This doc makes the choice explicit: the login response can say
`"choose-workspace"` and list the workspaces; the client picks one with
`POST /api/auth/switch-workspace`; a signed-in user can switch at any time from the dashboard header;
`GET /api/auth/me` lists every membership so the header can render the switcher. The widget, which
is embedded in a known project, sends the project key and is auto-routed to the workspace that owns
that project (D11), so a stakeholder never sees a picker inside someone's app. The CLI is unaffected
(its key is per membership). A person whose every membership has ended gets a clear
`"no-workspace"` instead of `"disabled"`.

## 2. Prerequisites (verified facts, 2026-09-22 @ `2756bd6`, plus DB-11a's additions)

- `AuthController` (`API/Controllers/AuthController.cs`): `[Route("api/auth")]` `:12`; no class-level
  `[Tags]` — the Swagger tag defaults to the controller name `Auth`, which **is** in
  `orval.config.ts:6` `filters.tags`. `Login` `:20-23`, `Me` `:68-71` (`[Authorize]`, `MeResponse`).
- `LoginRequest` (`Application/DTOs/Auth/LoginRequest.cs`: `Email`, `Password`), `LoginValidator`
  (`Application/Validators/LoginValidator.cs`: `Email` NotEmpty+EmailAddress, `Password` NotEmpty;
  tests `Tests/LoginValidatorTests.cs`). `LoginResponse` (`Status`, `Token`, `User`) — doc-comment
  lists `"ok" | "pending" | "rejected" | "disabled"`. `MeResponse` fields incl. `TenantName`.
- After DB-11a: `IMembershipService.ListForIdentityAsync(int userId)`, `GetMembershipAsync(int, Guid)`,
  `FindIdentityByPublicIdAsync(Guid)`; `ITokenService.Issue(User, WorkspaceMembership?, int?)`;
  `AuthenticationExtensions.OnTokenValidated` runs only when `Auth:ValidateSecurityStamp` is true
  (prod `docker-compose.prod.yml:35`; dev default false, `AuthenticationExtensions.cs:34`).
- `Workspace.Name` / `PlaceholderName` (`Domain/Entity/Workspace.cs`); `WorkspaceNameResolver`
  treats the placeholder as "unnamed".
- Widget: `element.ts:921-927 apiLogin(email, password)`; `auth-ui.ts:79-108` status branches;
  `this.project` holds the project key (`element.ts:930` uses it for `/api/roles?project=`).
  Dashboard: `features/login/LoginPage.tsx` (status handling around `:105-111`), `features/shell/Shell.tsx:82-89`
  (`useAuth`, `me?.tenantName`), `features/cli-login/` (device approval page).
  CLI: `cli/src/commands/login.ts:45-50`, `whoami.ts:35-37` use `login-with-key` + `/api/auth/me`.
- Rate limiting (**GLM A5**, verified today): `AuthController.Login` (`API/Controllers/AuthController.cs:19-23`)
  has **no** `[EnableRateLimiting]` — password login is deliberately unlimited (`API/Program.cs:90-92`) and
  `Tests/AuthRateLimitingTests.cs:21-29 Login_IsNotRateLimited` pins that. The `"login"` policy exists
  (`RateLimitingExtensions.cs:124-132`: per IP, 60/min, `QueueLimit = 0`) and is applied today only to
  `login-with-invite` (`AuthController.cs:38-39`). The new endpoint uses **that** policy by name; this doc
  does not touch `Login` (the foundations "login limiter" item is a separate ops doc).

## 3. Design

### 3.1 Login statuses

`LoginResponse.Status` gains two values; the doc-comment becomes
`"ok" | "choose-workspace" | "pending" | "rejected" | "disabled" | "no-workspace"`. New fields:

```csharp
/// <summary>Present only when Status == "choose-workspace": the caller's approved, active memberships.</summary>
public List<WorkspaceChoice>? Workspaces { get; set; }
```
`Application/DTOs/Auth/WorkspaceChoice.cs`: `Guid WorkspaceId`, `string Name` (the workspace's
`Name`; `Workspace.PlaceholderName` is sent as-is — the client labels it "Unnamed workspace"),
`string RoleName`, `bool IsAdmin`, `bool IsHome` (`OwnerId == identity.OwnerId`).

Decision table in `AuthService.LoginAsync`, after password/`PasswordlessOnly`/super-admin handling
(replacing DB-11a's "home wins" pick):

| live memberships (`LeftAt == null`) | result |
|---|---|
| none at all | `Failure(MessageKeys.Auth.NoWorkspace, { Status = "no-workspace" })` |
| ≥1 with `IsActive && Approved` — exactly one | `"ok"`, token for it |
| ≥1 with `IsActive && Approved` — several, and `request.ProjectKey` names a project in one of them | `"ok"`, token for that one (D11) |
| ≥1 with `IsActive && Approved` — several, otherwise | `"choose-workspace"`, `Workspaces` = those, `Token` = **selection token** (§3.2), `User = null` |
| none active/approved, some `Pending` | `"pending"` (as today) |
| none active/approved, some `Rejected`, no Pending | `"rejected"` |
| otherwise (all inactive) | `"disabled"` |

`LoginRequest` gains `public string? ProjectKey { get; set; }` (validator: `MaximumLength(64)` when
not null; nothing else — an unknown key is simply ignored). Resolution:
`Projects.IgnoreQueryFilters().Where(p => p.DeletedAt == null && p.Key == key.Trim().ToLower()).Select(p => p.OwnerId)`,
then intersect with the candidates' `OwnerId`s; if exactly one → pick it; if zero or several → fall
through to the picker.

### 3.2 Selection token and `switch-workspace`

A **selection token** is a JWT issued by `ITokenService.IssueSelection(User identity)` (new method):
claims `sub`, `email`, `name`, `stamp`, and `scope = "select_workspace"`; **no** `tenant`, `role_id`,
`role`, `is_admin`, `is_super_admin`, `is_quick_access`, `mstamp`; lifetime **5 minutes**
(`JwtOptions.SelectionLifetimeMinutes = 5`). It cannot pass `Policies.Admin`/`SuperAdmin`, but a bare
`[Authorize]` endpoint would accept it — so `AuthenticationExtensions` registers `OnTokenValidated`
**unconditionally** (move the `new JwtBearerEvents { OnTokenValidated = … }` outside the
`if (validateStamp)` block; the stamp lookup inside stays gated by `validateStamp`) and adds, first:
```csharp
if (principal?.FindFirst("scope")?.Value == "select_workspace"
    && !SelectionScopeFence.Allows(ctx.HttpContext.Request.Path))
{ ctx.Fail("Selection token."); return; }
```
with, in `API/Extensions/SelectionScopeFence.cs` (new, static, so §6 test 5 can call it directly):
```csharp
public static class SelectionScopeFence
{
    public static readonly PathString SwitchWorkspacePath = new("/api/auth/switch-workspace");
    /// <summary>GLM A6 (DB-11b): EXACT path match, case-insensitive, no trailing slash, no sub-routes —
    /// a future /api/auth/switch-workspace/anything must NOT accept a selection token.</summary>
    public static bool Allows(PathString path) =>
        path.HasValue && path.Equals(SwitchWorkspacePath, StringComparison.OrdinalIgnoreCase);
}
```
(`StartsWithSegments` is **not** used — GLM A6.)

`POST /api/auth/switch-workspace` (`AuthController`, `[Authorize]`, `[EnableRateLimiting("login")]` (GLM A5),
`[ProducesResponseType(typeof(LoginResponse), 200)]`, 403 `Result`):
body `SwitchWorkspaceRequest { Guid WorkspaceId }`. `AuthService.SwitchWorkspaceAsync(Guid workspaceId)`:
identity = `FindIdentityByPublicIdAsync(_currentUser.Id)` (live, `IsActive`); if `identity.Role.IsSuperAdmin`
→ `Forbidden(MessageKeys.Common.Forbidden)` (super admins have no workspaces); membership =
`GetMembershipAsync(identity.Id, workspaceId)` live + `IsActive` + `Approved`, else
`Forbidden(MessageKeys.Auth.NotAMember)`; return `"ok"` with `Issue(identity, membership)` and
`UserMapper.ToMeResponse(identity, membership.Role, workspaceName)`. Works with a full token **or** a
selection token (the same identity check applies). No state is written — switching is stateless;
the old token keeps working until it expires (both are valid sessions of the same person).

### 3.3 `/api/auth/me`

`MeResponse` gains:
```csharp
/// <summary>Current workspace id (JWT tenant). Null for super admins.</summary>
public Guid? WorkspaceId { get; set; }
/// <summary>Every live, approved, active membership of this identity (DB-11b). Empty for super admins.</summary>
public List<WorkspaceChoice> Workspaces { get; set; } = new();
```
`MeAsync` fills both (`ListForIdentityAsync`, `IsHome`, names via one `Workspaces.IgnoreQueryFilters()`
batch — same shape as `TenantService.ListAsync`'s `wsNames`). `LoginAsync`/`SwitchWorkspaceAsync`
responses fill them too (one helper `BuildMeAsync(identity, membership)` in `AuthService`).
`TenantName` stays (it is the current workspace's name).

### 3.4 Message keys

`Auth.NoWorkspace` (exists since DB-11a), `Auth.ChooseWorkspace = "Choose which workspace to open."`,
`Auth.NotAMember = "You are not an active member of that workspace."`.

### 3.5 Widget, dashboard, CLI (what each must do — see §11 for the task list)

- **Widget**: sends `projectKey: this.project` with the login body. Because of D11 it normally never
  sees `choose-workspace`; if it does (the person is not a member of the embedding project's
  workspace but is a member elsewhere), render the list from `data.workspaces` in the auth panel
  and call `POST /api/auth/switch-workspace` with the selection token; `no-workspace` shows
  `envelope.message`.
- **Dashboard**: `LoginPage` handles `choose-workspace` (radio list → switch), `no-workspace`
  (message). `Shell` renders a workspace switcher when `me.workspaces.length > 1` (calls
  `switch-workspace` with the current token, stores the new token, invalidates every query).
  `cli-login` page shows "Approving for workspace **{tenantName}**" so the developer knows which
  workspace's key the CLI will receive.
- **CLI**: nothing; optional `pointer whoami` prints `me.tenantName` (already does) — no change.

### 3.6 Owner decision

| # | Question | Default encoded |
|---|---|---|
| D11 | Should the widget auto-select the workspace that owns the embedding project instead of showing a picker? | **Yes** — the project key is the widget's context; a stakeholder inside a customer's app should never choose between that customer and another. The dashboard always shows the picker when there are several. |

## 4. Safety classification

Code only; no schema, no data. Tenancy: the switch endpoint reads memberships by (identity,
workspace) with `IgnoreQueryFilters` and an explicit predicate; §6 test 3 proves a non-member gets 403
and no token. The selection token is fenced by the path check in §3.2 (test 5).

## 5. File-level tasks

1. `Application/DTOs/Auth/WorkspaceChoice.cs` (new), `LoginResponse.cs` (+`Workspaces`, doc-comment),
   `MeResponse.cs` (+`WorkspaceId`, `Workspaces`), `LoginRequest.cs` (+`ProjectKey`),
   `Application/DTOs/Auth/SwitchWorkspaceRequest.cs` (new: `Guid WorkspaceId`).
2. `Application/Validators/LoginValidator.cs` — `RuleFor(x => x.ProjectKey).MaximumLength(64).When(x => x.ProjectKey != null);`.
   New `Application/Validators/SwitchWorkspaceValidator.cs` — `RuleFor(x => x.WorkspaceId).NotEmpty()`.
3. `Application/Abstractions/ITokenService.cs` + `Infrastructure/Auth/JwtTokenService.cs` — `IssueSelection(User)` per §3.2; `JwtOptions.SelectionLifetimeMinutes` (default 5).
4. `API/Extensions/AuthenticationExtensions.cs` — hoist `OnTokenValidated` out of `if (validateStamp)`; add the `scope` fence as the first statement, calling `SelectionScopeFence.Allows` (new file `API/Extensions/SelectionScopeFence.cs`, §3.2 verbatim — exact `PathString.Equals`, never `StartsWithSegments`); keep the stamp lookup under `if (validateStamp)`.
5. `Application/Services/Interfaces/IAuthService.cs` — `Task<Result<LoginResponse>> SwitchWorkspaceAsync(Guid workspaceId);`. `AuthService.cs` — `LoginAsync` decision table (§3.1), `SwitchWorkspaceAsync` (§3.2), `MeAsync` (§3.3), private `BuildMeAsync`.
6. `API/Controllers/AuthController.cs` — add after `Login`:
   ```csharp
   /// <summary>Opens a session in one of the caller's workspaces. Accepts a full token or the 5-minute selection token returned with status "choose-workspace".</summary>
   [Authorize]
   [HttpPost("switch-workspace")]
   [EnableRateLimiting("login")]
   [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
   [ProducesResponseType(typeof(Result), StatusCodes.Status403Forbidden)]
   public async Task<IActionResult> SwitchWorkspace([FromBody] SwitchWorkspaceRequest request)
   {
       var result = await authService.SwitchWorkspaceAsync(request.WorkspaceId);
       if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
       return result.IsSuccess ? Ok(result) : BadRequest(result);
   }
   ```
   (the attribute is written out above — there is nothing on `Login` to copy (GLM A5); `using Microsoft.AspNetCore.RateLimiting;` is already imported at `:3`; the field name `authService` must match the primary constructor at `:13`).
7. `Application/Resources/MessageKeys.cs` — §3.4.
8. Widget (`web-component/src/element.ts:921-927`, `auth-ui.ts:79-108`, `templates.ts`, i18n strings) — §3.5; `npm run build`; commit `API/wwwroot/pointer.*`; check the 64 KB budget noted in memory (`MetaWidgetVersionTests`/size test if present).
9. Tests (§6). 10. `just fmt`, `just test`. 11. PR description: **Dashboard tasks** section = §11.

## 6. Tests

`Tests/WorkspaceSwitchTests.cs` (fixture from `Tests/WorkspaceMembershipTests.cs` created by DB-11a):

1. `Login_TwoActiveMemberships_ReturnsChooseWorkspace_WithSelectionToken` — `Status == "choose-workspace"`, `Workspaces.Count == 2`, `IsHome` true for exactly one, token has `scope=select_workspace` and **no** `tenant`/`is_admin` claim, `User == null`.
2. `Login_TwoMemberships_ProjectKeyPicksTheOwner` — `ProjectKey` of a project in B → `"ok"`, `tenant == B`. Unknown key → `"choose-workspace"`.
3. `Switch_NonMember_Forbidden_NoToken`; `Switch_InactiveMembership_Forbidden`; `Switch_Member_Ok_TenantAndMstampMatch`.
4. `Login_NoLiveMemberships_ReturnsNoWorkspace`.
5. `SelectionToken_RejectedOutsideSwitchEndpoint` — `SelectionScopeFence.Allows(PathString)` (GLM A6): `/api/auth/switch-workspace` → true, `/API/Auth/Switch-Workspace` → true (case), `/api/auth/switch-workspace/` → false (trailing slash), `/api/auth/switch-workspace/anything` → false (sub-route — the prefix bug), `/api/auth/switch-workspacex` → false, `/api/comments` → false, empty path → false.
6. `Me_ListsMemberships_AndCurrentWorkspaceId`.
7. `SwitchWorkspaceValidator_RejectsEmptyGuid`; `LoginValidator_ProjectKey_MaxLength` (extend `LoginValidatorTests`).
8. **GLM A5** in `Tests/AuthRateLimitingTests.cs`: add `SwitchWorkspace_HasLoginRateLimit` (reflection on `AuthController.SwitchWorkspace`, `PolicyName == "login"`, same shape as `SignupSurface_KeepsSignupRateLimit`). `Login_IsNotRateLimited` stays untouched and must still pass.

## 7. Acceptance criteria

1. `curl -s http://localhost:8090/swagger/v1/swagger.json | jq '.paths["/api/auth/switch-workspace"].post.tags'` → `["Auth"]`; `jq '.components.schemas.LoginResponse.properties.workspaces, .components.schemas.MeResponse.properties.workspaces, .components.schemas.LoginRequest.properties.projectKey'` → all non-null.
2. `grep -c "select_workspace" Infrastructure/Auth/JwtTokenService.cs API/Extensions/AuthenticationExtensions.cs` → ≥1 each.
3. `grep -n "if (validateStamp)" API/Extensions/AuthenticationExtensions.cs` → the block no longer wraps the `new JwtBearerEvents` construction (reviewer reads it). `grep -c "StartsWithSegments" API/Extensions/SelectionScopeFence.cs API/Extensions/AuthenticationExtensions.cs` → 0 each (GLM A6). `grep -c 'EnableRateLimiting("login")' API/Controllers/AuthController.cs` → 2 (`login-with-invite` + `switch-workspace`; GLM A5), and `Login` still has none.
4. `just test` green with the 7+ new facts.
5. Manual on the rehearsal API: seed a second membership for the production admin's identity (via an invite accept) → login returns `choose-workspace`; `switch-workspace` with the selection token → full token; the selection token on `GET /api/comments/...` → 401; widget login with `projectKey` → `ok` without a picker.
6. Widget bundle within budget; `web-component` typecheck passes.

## 8. Rollback

Revert the commit; ordinary redeploy. No data written. Clients that already stored a selection
token simply get 401 and log in again.

## 9. Release steps

1. Merge after DB-11a is verified in production. 2. `bash scripts/deploy-api.sh` (ordinary).
3. Verify criterion 5 against production (the owner's own account if it has two memberships; else
   create a test membership and remove it with DB-11c later — or with `DELETE /api/admin/users/{id}`,
   which after DB-11a ends the membership).
4. Dashboard: `dashboard-agent` regenerates the client from production once, then §11.

## 10. Out of scope

Remembering the last chosen workspace server-side (client-side `localStorage` is enough);
per-workspace refresh tokens; removing `TenantName` from `MeResponse`; the CLI choosing a workspace
(its key already is one); any migration; DB-11c's endpoints.

## 11. Dashboard / widget / CLI tasks

**Dashboard** (after client regen; `@moamen-ui/pointer-react` exposes `postApiAuthSwitchWorkspace`, `LoginResponse.workspaces`, `MeResponse.workspaces/workspaceId`):
1. `LoginPage`: on `status === "choose-workspace"` render the `workspaces` list (name, role, "home" badge; placeholder name → "Unnamed workspace"), call `switch-workspace` with `Authorization: Bearer <selection token>`, then proceed exactly as on `ok`. On `no-workspace` show the message.
2. `Shell`: when `me.workspaces.length > 1`, a switcher next to `tenantName`; on select → `switch-workspace` with the current token → replace the stored token/header, `queryClient.invalidateQueries()`, navigate to `/`.
3. `cli-login` page: show `tenantName` in the approval card ("This will sign the CLI into **{tenantName}**").
4. `UsersPage`: no change (DB-11a kept `UserResponse`).

**Widget**: §5 task 8. **CLI**: none.
