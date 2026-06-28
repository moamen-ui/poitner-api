# User Profile & Status Catalog Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a per-user activity profile (admin-views-anyone + self-service) to all three dashboards, and a single backend-defined status catalog (`GET /api/statuses`) consumed by the widget and all dashboards.

**Architecture:** Two new API surfaces over the existing Clean-Architecture stack (Result<T> envelope, `IUnitOfWork`/`Repository<>`, Scrutor auto-DI for `*Service` classes). The status catalog is a static definition in the API served read-only. The profile is a grouped-count aggregation mirroring `StatsService`, filtered by `Comment.AuthorId == User.PublicId`. Clients are regenerated via orval and republished to GitHub Packages; the widget and dashboards then consume the new endpoints. Dashboard UI is built for angular/react/vue in parallel via subagents.

**Tech Stack:** .NET 8 (EF Core/Postgres), TypeScript web component (esbuild+sass), Angular/React/Vue dashboards (Tailwind v4), orval-generated `@moamen-ui/pointer-{angular,react,vue}` clients.

## Global Constraints

- **No test framework exists** (CLAUDE.md: "No tests or linter"). Each task's verify step is a build/typecheck/`curl`/manual check, NOT a unit test.
- **No DB migration** — every field already exists (`Comment.AuthorId/ProjectId/Environment/Status`, `Reply.AuthorId/CommentId`, `User.PublicId`, `Role.Name/GrantsAdmin`).
- **Granular, independently-revertable commits** — one focused commit per task; never bundle unrelated changes.
- **All dashboard work is built for all three frameworks** (angular, react, vue), dispatched to parallel subagents.
- **Do NOT deploy** anything unless the user explicitly asks.
- Enum facts (verbatim): `CommentStatus { Open=1, ReadyToApply=2, Applied=3, Archived=4 }`; `EnvironmentTag { Local=1, Staging=2, Production=3 }`.
- Canonical join: a user's authored items are `Comment.AuthorId == User.PublicId` (Guid) and `Reply.AuthorId == User.PublicId`.
- Repo paths: API = `/Users/momen/Desktop/REPOS/pointer-api`; dashboards = `/Users/momen/Desktop/REPOS/pointer-dashboard/{angular,react,vue}`; widget = `pointer-api/web-component`.

---

## Part A — API: Status Catalog

### Task A1: Status catalog endpoint (`GET /api/statuses`)

**Files:**
- Create: `Application/DTOs/Status/StatusItem.cs`
- Create: `Application/Services/Interfaces/IStatusCatalogService.cs`
- Create: `Application/Services/Implementation/StatusCatalogService.cs`
- Create: `API/Controllers/StatusesController.cs`

**Interfaces:**
- Produces: `IStatusCatalogService.GetAll() : Result<List<StatusItem>>`; `StatusItem { int Value; string Name; string Label; string Color; int Order }`. Consumed by orval (tag `Statuses`) → widget + dashboards.

- [ ] **Step 1: Create the DTO**

```csharp
namespace Pointer.Application.DTOs.Status;

public class StatusItem
{
    public int Value { get; set; }
    public string Name { get; set; } = string.Empty;   // canonical enum name, e.g. "ReadyToApply"
    public string Label { get; set; } = string.Empty;  // display label, e.g. "Ready"
    public string Color { get; set; } = string.Empty;  // hex
    public int Order { get; set; }
}
```

- [ ] **Step 2: Create the interface**

```csharp
using Pointer.Application.DTOs.Status;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface IStatusCatalogService
{
    Result<List<StatusItem>> GetAll();
}
```

- [ ] **Step 3: Create the service (single source of truth)**

```csharp
using Pointer.Application.DTOs.Status;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Enums;

namespace Pointer.Application.Services.Implementation;

public class StatusCatalogService : IStatusCatalogService
{
    // THE single source of truth for comment-status presentation.
    // Rename / recolor / reorder here → every client reflects it on next load.
    private static readonly List<StatusItem> Catalog = new()
    {
        new() { Value = (int)CommentStatus.Open,         Name = "Open",         Label = "Open",      Color = "#2563eb", Order = 1 },
        new() { Value = (int)CommentStatus.ReadyToApply, Name = "ReadyToApply", Label = "Ready",     Color = "#d97706", Order = 2 },
        new() { Value = (int)CommentStatus.Applied,      Name = "Applied",      Label = "Completed", Color = "#16a34a", Order = 3 },
        new() { Value = (int)CommentStatus.Archived,     Name = "Archived",     Label = "Archived",  Color = "#6b7280", Order = 4 },
    };

    public Result<List<StatusItem>> GetAll() => Result<List<StatusItem>>.Success(Catalog);
}
```

(Auto-registered: class name ends in `Service`, picked up by the Scrutor scan in `Application/DependencyInjection.cs`.)

- [ ] **Step 4: Create the controller**

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.Application.DTOs.Status;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers;

[ApiController]
[Route("api/statuses")]
[Tags("Statuses")]
[AllowAnonymous]
public class StatusesController(IStatusCatalogService statusCatalog) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(List<StatusItem>), StatusCodes.Status200OK)]
    public IActionResult Get() => Ok(statusCatalog.GetAll());
}
```

- [ ] **Step 5: Build**

Run: `cd /Users/momen/Desktop/REPOS/pointer-api && dotnet build`
Expected: Build succeeded, 0 errors.

- [ ] **Step 6: Verify at runtime**

Run (in one shell): `cd /Users/momen/Desktop/REPOS/pointer-api/API && dotnet run &` then `sleep 8 && curl -s http://localhost:5000/api/statuses` (use the port from `Properties/launchSettings.json` if different).
Expected: JSON `{ "data": [ {value:1,name:"Open",label:"Open",color:"#2563eb",order:1}, ... ], "isSuccess": true }`. Stop the server afterwards.

- [ ] **Step 7: Commit**

```bash
cd /Users/momen/Desktop/REPOS/pointer-api
git add Application/DTOs/Status API/Controllers/StatusesController.cs Application/Services/Interfaces/IStatusCatalogService.cs Application/Services/Implementation/StatusCatalogService.cs
git commit -m "feat(api): add GET /api/statuses status catalog"
```

---

## Part B — API: User Profile

### Task B1: Profile DTO + service + endpoints

**Files:**
- Create: `Application/DTOs/Profile/UserProfileResponse.cs`
- Create: `Application/Services/Interfaces/IProfileService.cs`
- Create: `Application/Services/Implementation/ProfileService.cs`
- Modify: `API/Controllers/MeController.cs`
- Modify: `API/Controllers/Admin/UsersController.cs`

**Interfaces:**
- Consumes: `IUnitOfWork.Repository<Comment>()/.Repository<Reply>()/.Repository<User>()/.Repository<Project>()` (`.Query().AsNoTracking()`), `ICurrentUser.Id : Guid?`.
- Produces: `IProfileService.GetByPublicIdAsync(Guid publicId) : Task<Result<UserProfileResponse>>` and `GetByIdAsync(int userId) : Task<Result<UserProfileResponse>>`. Response shape below. orval tags: `Me` (self) and `Users` (admin).

- [ ] **Step 1: Create the DTO**

```csharp
namespace Pointer.Application.DTOs.Profile;

public class UserProfileResponse
{
    public ProfileUser User { get; set; } = new();
    public ProfileTotals Totals { get; set; } = new();
    public List<ProfileProject> Projects { get; set; } = new();
}

public class ProfileUser
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;
}

public class ProfileCounts
{
    public int Comments { get; set; }
    public int Replies { get; set; }
    public int Open { get; set; }
    public int ReadyToApply { get; set; }
    public int Applied { get; set; }
    public int Archived { get; set; }
}

public class ProfileTotals : ProfileCounts
{
    public int ProjectsInvolved { get; set; }
}

public class ProfileEnvironment : ProfileCounts
{
    public int Environment { get; set; } // EnvironmentTag int
}

public class ProfileProject : ProfileCounts
{
    public int ProjectId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public List<ProfileEnvironment> Environments { get; set; } = new();
}
```

- [ ] **Step 2: Create the interface**

```csharp
using Pointer.Application.DTOs.Profile;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface IProfileService
{
    Task<Result<UserProfileResponse>> GetByPublicIdAsync(Guid publicId);
    Task<Result<UserProfileResponse>> GetByIdAsync(int userId);
}
```

- [ ] **Step 3: Create the service**

```csharp
using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Profile;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;

namespace Pointer.Application.Services.Implementation;

public class ProfileService : IProfileService
{
    private readonly IUnitOfWork _unitOfWork;
    public ProfileService(IUnitOfWork unitOfWork) => _unitOfWork = unitOfWork;

    public async Task<Result<UserProfileResponse>> GetByIdAsync(int userId)
    {
        var user = await _unitOfWork.Repository<User>().Query().AsNoTracking()
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == userId && u.DeletedAt == null);
        return user is null
            ? Result<UserProfileResponse>.NotFound("User not found")
            : await BuildAsync(user);
    }

    public async Task<Result<UserProfileResponse>> GetByPublicIdAsync(Guid publicId)
    {
        var user = await _unitOfWork.Repository<User>().Query().AsNoTracking()
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.PublicId == publicId && u.DeletedAt == null);
        return user is null
            ? Result<UserProfileResponse>.NotFound("User not found")
            : await BuildAsync(user);
    }

    private async Task<Result<UserProfileResponse>> BuildAsync(User user)
    {
        var pid = user.PublicId;

        // comments grouped by (project, environment, status)
        var comments = await _unitOfWork.Repository<Comment>().Query().AsNoTracking()
            .Where(c => c.AuthorId == pid && c.DeletedAt == null)
            .GroupBy(c => new { c.ProjectId, c.Environment, c.Status })
            .Select(g => new { g.Key.ProjectId, g.Key.Environment, g.Key.Status, Count = g.Count() })
            .ToListAsync();

        // replies grouped by (project, environment) via the parent comment
        var replies = await _unitOfWork.Repository<Reply>().Query().AsNoTracking()
            .Where(r => r.AuthorId == pid && r.DeletedAt == null && r.Comment.DeletedAt == null)
            .GroupBy(r => new { r.Comment.ProjectId, r.Comment.Environment })
            .Select(g => new { g.Key.ProjectId, g.Key.Environment, Count = g.Count() })
            .ToListAsync();

        var projectIds = comments.Select(c => c.ProjectId)
            .Concat(replies.Select(r => r.ProjectId)).Distinct().ToList();

        var projects = await _unitOfWork.Repository<Project>().Query().AsNoTracking()
            .Where(p => projectIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Key, p.Name, p.IsActive })
            .ToListAsync();

        void Apply(ProfileCounts t, CommentStatus s, int n)
        {
            t.Comments += n;
            switch (s)
            {
                case CommentStatus.Open: t.Open += n; break;
                case CommentStatus.ReadyToApply: t.ReadyToApply += n; break;
                case CommentStatus.Applied: t.Applied += n; break;
                case CommentStatus.Archived: t.Archived += n; break;
            }
        }

        var perProject = new List<ProfileProject>();
        foreach (var p in projects)
        {
            var proj = new ProfileProject { ProjectId = p.Id, Key = p.Key, Name = p.Name, IsActive = p.IsActive };
            var envIds = comments.Where(c => c.ProjectId == p.Id).Select(c => c.Environment)
                .Concat(replies.Where(r => r.ProjectId == p.Id).Select(r => r.Environment))
                .Distinct();
            foreach (var envId in envIds)
            {
                var env = new ProfileEnvironment { Environment = (int)envId };
                foreach (var row in comments.Where(c => c.ProjectId == p.Id && c.Environment == envId))
                    Apply(env, row.Status, row.Count);
                env.Replies = replies.Where(r => r.ProjectId == p.Id && r.Environment == envId).Sum(r => r.Count);
                proj.Environments.Add(env);
            }
            // project-level rollup
            foreach (var e in proj.Environments)
            {
                proj.Comments += e.Comments; proj.Open += e.Open; proj.ReadyToApply += e.ReadyToApply;
                proj.Applied += e.Applied; proj.Archived += e.Archived; proj.Replies += e.Replies;
            }
            proj.Environments = proj.Environments.OrderBy(e => e.Environment).ToList();
            perProject.Add(proj);
        }
        perProject = perProject.OrderByDescending(p => p.Comments + p.Replies).ToList();

        var totals = new ProfileTotals { ProjectsInvolved = perProject.Count };
        foreach (var p in perProject)
        {
            totals.Comments += p.Comments; totals.Open += p.Open; totals.ReadyToApply += p.ReadyToApply;
            totals.Applied += p.Applied; totals.Archived += p.Archived; totals.Replies += p.Replies;
        }

        return Result<UserProfileResponse>.Success(new UserProfileResponse
        {
            User = new ProfileUser { Id = user.Id, DisplayName = user.DisplayName, Email = user.Email, RoleName = user.Role?.Name ?? string.Empty },
            Totals = totals,
            Projects = perProject,
        });
    }
}
```

- [ ] **Step 4: Add the self endpoint to `MeController`**

Modify the class declaration and add the action (inject `IProfileService` + `ICurrentUser`):

```csharp
public class MeController(IPreferencesService preferencesService, IProfileService profileService, Pointer.Application.Abstractions.ICurrentUser currentUser) : ControllerBase
{
    // ...existing UpdatePreferences...

    [HttpGet("profile")]
    [ProducesResponseType(typeof(Pointer.Application.DTOs.Profile.UserProfileResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Profile()
    {
        if (currentUser.Id is null) return Unauthorized();
        var result = await profileService.GetByPublicIdAsync(currentUser.Id.Value);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
```

- [ ] **Step 5: Add the admin endpoint to `Admin/UsersController`**

Add `IProfileService` to the primary constructor and add the action:

```csharp
public class UsersController(IUserService userService, IProfileService profileService) : ControllerBase
{
    // ...existing actions...

    [HttpGet("{id:int}/profile")]
    [ProducesResponseType(typeof(Pointer.Application.DTOs.Profile.UserProfileResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Profile(int id)
    {
        var result = await profileService.GetByIdAsync(id);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
```

- [ ] **Step 6: Build**

Run: `cd /Users/momen/Desktop/REPOS/pointer-api && dotnet build`
Expected: Build succeeded, 0 errors.

- [ ] **Step 7: Verify at runtime**

Start the API, log in (`POST /api/auth/login`) to get a token, then:
- `curl -s -H "Authorization: Bearer <token>" http://localhost:5000/api/me/profile`
- `curl -s -H "Authorization: Bearer <admintoken>" http://localhost:5000/api/admin/users/1/profile`
Expected: `isSuccess:true` with `user`, `totals` (incl. `projectsInvolved`, `replies`), and `projects[]` each carrying `environments[]`. Confirm reply counts appear separately from status counts. Stop the server.

- [ ] **Step 8: Commit**

```bash
cd /Users/momen/Desktop/REPOS/pointer-api
git add Application/DTOs/Profile Application/Services/Interfaces/IProfileService.cs Application/Services/Implementation/ProfileService.cs API/Controllers/MeController.cs API/Controllers/Admin/UsersController.cs
git commit -m "feat(api): add user profile endpoints (me + admin)"
```

---

## Part C — Clients: regenerate + publish

### Task C1: Add `Statuses` tag to orval and regenerate

**Files:**
- Modify: `pointer-api/orval.config.ts:6` (the `filters.tags` array)
- Regenerated (auto): `clients/{angular,react,vue}/src/**`

- [ ] **Step 1: Add the tag**

In `orval.config.ts`, change the filter to include the new tag:

```ts
filters: {
  tags: ['Auth', 'Me', 'Users', 'Stats', 'Projects', 'Roles', 'Statuses'],
},
```

- [ ] **Step 2: Export the OpenAPI doc + regenerate**

Run the project's existing generate flow (the repo's documented way to refresh `openapi.json` then orval). Typically:
`cd /Users/momen/Desktop/REPOS/pointer-api && dotnet run --project API -- --export-openapi || true` then `npx orval` (use whatever script the repo defines — check `package.json`).
Expected: new files for `statuses` + `getApiMeProfile` + `getApiAdminUsersIdProfile` appear under each `clients/<fw>/src`.

- [ ] **Step 3: Verify generated hooks/services exist**

Run: `grep -rl "MeProfile\|Statuses\|UsersIdProfile" clients/*/src`
Expected: matches in all three clients. Confirm GET endpoints generated as queries (angular httpResource/service, react `useGet...`, vue `useGet...`), not mutations.

- [ ] **Step 4: Commit (generated clients)**

```bash
cd /Users/momen/Desktop/REPOS/pointer-api
git add orval.config.ts clients
git commit -m "chore(clients): regenerate clients for statuses + profile endpoints"
```

### Task C2: Publish bumped packages

- [ ] **Step 1: Trigger the auto-publish workflow** the same way as before (push to the branch the publish GitHub Action watches, or run it manually). The action auto-bumps the patch version of each `@moamen-ui/pointer-{angular,react,vue}`.

- [ ] **Step 2: Verify the new versions** are listed on GitHub Packages (note the new version number, e.g. `1.0.5`).

- [ ] **Step 3:** No separate commit (publishing is the workflow). Record the published version in the task notes for Part E.

---

## Part D — Widget: consume the status catalog

### Task D1: Replace hardcoded statuses with the catalog

**Files:**
- Modify: `pointer-api/web-component/src/constants.ts`
- Modify: `pointer-api/web-component/src/element.ts` (fetch catalog on init, before first render of filter chips)
- Read first: `web-component/src/api.ts` (or wherever `fetch(server + ...)` lives) and `element.ts` init path.

**Interfaces:**
- Consumes: `GET {server}/api/statuses` → `{ data: StatusItem[] }`.
- Produces: a runtime status catalog used to build filter chips, `STATUS_STR`, and labels.

- [ ] **Step 1: Read the current shape** of `constants.ts` (STATUS_STR, STATUS_INT, STATUS_LABEL, the filter list) and how `element.ts` builds the filter chips, and how the widget fetches (the `server` attribute base URL).

- [ ] **Step 2: Keep the existing constants as a fallback** and add a catalog loader. In `constants.ts` (or a new `status-catalog.ts`):

```ts
export interface StatusItem { value: number; name: string; label: string; color: string; order: number; }

// Built-in fallback so the toolbar still renders if the fetch fails.
export const STATUS_FALLBACK: StatusItem[] = [
  { value: 1, name: 'Open',         label: 'Open',      color: '#2563eb', order: 1 },
  { value: 2, name: 'ReadyToApply', label: 'Ready',     color: '#d97706', order: 2 },
  { value: 3, name: 'Applied',      label: 'Completed', color: '#16a34a', order: 3 },
  { value: 4, name: 'Archived',     label: 'Archived',  color: '#6b7280', order: 4 },
];

let _catalog: StatusItem[] = STATUS_FALLBACK;
export function getStatusCatalog(): StatusItem[] { return _catalog; }
export async function loadStatusCatalog(server: string): Promise<void> {
  try {
    const res = await fetch(`${server.replace(/\/$/, '')}/api/statuses`);
    if (!res.ok) return;
    const body = await res.json();
    const data: StatusItem[] = body?.data ?? body;
    if (Array.isArray(data) && data.length) _catalog = data.slice().sort((a, b) => a.order - b.order);
  } catch { /* keep fallback */ }
}
export const statusStr = (value: number) =>
  (_catalog.find((s) => s.value === value)?.name ?? '').toLowerCase();
export const statusLabel = (value: number) =>
  _catalog.find((s) => s.value === value)?.label ?? '';
export const statusColor = (value: number) =>
  _catalog.find((s) => s.value === value)?.color ?? '#6b7280';
```

- [ ] **Step 3: Call `loadStatusCatalog(this.server)` during element init**, awaited before the first chrome/sidebar render that draws filter chips (in `element.ts` connect/init path). Build the filter chips from `getStatusCatalog()` (an "All" chip + one per catalog item, using `label`), and replace direct uses of the hardcoded `STATUS_STR`/`STATUS_LABEL` maps with `statusStr`/`statusLabel`/`statusColor`.

- [ ] **Step 4: Typecheck + build**

Run: `cd /Users/momen/Desktop/REPOS/pointer-api/web-component && npm run typecheck && npm run build`
Expected: clean typecheck; `pointer.js` + `pointer.css` regenerated in `API/wwwroot`.

- [ ] **Step 5: Manual smoke test** (reuse the prior local-serve approach): serve `wwwroot`, mount `<pointer-feedback>`, confirm filter chips render from the catalog and comment status labels show the catalog labels. (If the API isn't reachable, confirm the fallback chips still render.)

- [ ] **Step 6: Commit**

```bash
cd /Users/momen/Desktop/REPOS/pointer-api
git add web-component/src API/wwwroot/pointer.js API/wwwroot/pointer.css
git commit -m "feat(widget): render statuses from /api/statuses catalog with fallback"
```

---

## Part E — Dashboards (angular, react, vue)

> Build each framework via a dedicated subagent dispatched in parallel. Each subagent gets the same three deliverables below, follows that framework's existing feature patterns (`features/overview`, `features/users`), uses the newly published `@moamen-ui/pointer-<fw>` generated query hooks/services, and uses Tailwind v4 + each stack's existing shadcn/shadcn-vue components. **Each deliverable is its own commit.**

**Shared contract for all three:**
- Status rendering everywhere (status columns, badges, profile breakdown) reads `label`/`color`/`order` from `GET /api/statuses` via the generated client — no hardcoded status strings. Provide the same `STATUS_FALLBACK` array (from Task D1 Step 2) if the fetch fails.
- Profile data: `GET /api/me/profile` (self) and `GET /api/admin/users/{id}/profile` (admin) via the generated query hooks/services. Response shape per Task B1 Step 1.
- Reply counts are shown as a **separate** number, never inside the status breakdown.

### Task E-NG: Angular dashboard

**Files:**
- Modify: `angular/package.json` (bump `@moamen-ui/pointer-angular` to the version from Task C2), then `npm install`.
- Create: `angular/src/app/core/auth/authenticated.guard.ts`
- Modify: `angular/src/app/app.routes.ts`
- Modify: `angular/src/app/features/login/login.component.ts` (post-login redirect by role)
- Modify: `angular/src/app/features/shell/shell.component.ts` (hide admin nav for non-admins; add a "My profile" link)
- Modify: `angular/src/app/features/users/users.component.ts` (row action → `/users/:id/profile`)
- Create: `angular/src/app/features/profile/profile.component.ts`
- Create: `angular/src/app/core/status/status-catalog.service.ts` (fetch + cache catalog; expose `label(value)`, `color(value)`, `ordered()`)

**Deliverable 1 — access tier (commit `feat(ng): open dashboard to non-admins via authenticated guard`):**
- Add `authenticatedGuard` (authenticated only — mirror `adminGuard` in `core/auth/auth.guard.ts` but drop the `isAdmin()` check).
- Restructure `app.routes.ts`: keep `shell` reachable by any authenticated user (`canActivate: [authenticatedGuard]`); guard the admin children (`overview`, `roles`, `users`, `projects`) with `adminGuard`; add a `profile` child reachable by everyone. Default redirect: admins → `overview`, non-admins → `profile` (do the role-based redirect in `login.component.ts` using `auth.isAdmin()`; and change the `''` redirect to a small resolver/guard that picks the target by role).
- In `shell.component.ts`, hide admin nav items when `!auth.isAdmin()`, always show "My profile".

**Deliverable 2 — status catalog consumption (commit `feat(ng): render statuses from catalog`):**
- Create `status-catalog.service.ts` that loads `GET /api/statuses` (generated `Statuses` service) once, caches, exposes `ordered()/label(v)/color(v)` with the `STATUS_FALLBACK`.
- Replace any hardcoded status labels/colors in `overview` and `users` with the service.

**Deliverable 3 — profile page (commit `feat(ng): user profile page`):**
- `profile.component.ts`: when a `:id` route param is present and `auth.isAdmin()`, call `getApiAdminUsersIdProfile(id)`; otherwise call `getApiMeProfile()`. Render: headline (projects-involved, total comments, total replies, overall status split using catalog colors); a card/row per project with status breakdown + reply count + total, expandable to show the environment split (`environment` int → "Local/Staging/Production"). Use `viewChild()` signals if any template refs are needed (per repo convention). Add the "View profile" row action in `users.component.ts` → `/users/:id/profile`.

**Verify:** `cd angular && npm run build` succeeds; run locally (`npm start` with `NODE_AUTH_TOKEN`), log in as admin → open a user's profile; log in as a non-admin → land on own profile, admin routes blocked.

### Task E-REACT: React dashboard

**Files:** same structure under `react/src` — `react/package.json` bump + install; `react/src/routes/*` (guard + role redirect); `react/src/features/login/*`; `react/src/features/shell/*`; `react/src/features/users/*`; create `react/src/features/profile/*`; create `react/src/lib/status-catalog.ts` (hook around generated `useGet...Statuses`).

Same three deliverables / three commits as Angular, using react-query generated hooks (`useGetApiMeProfile`, `useGetApiAdminUsersIdProfile`, `useGet...Statuses`) and the existing React route-guard pattern. **Verify:** `cd react && npm run build`; runtime login both roles.

### Task E-VUE: Vue dashboard

**Files:** same structure under `vue/src` — `vue/package.json` bump + install; `vue/src/router/*` (guard + role redirect); `vue/src/features/login/*`; `vue/src/features/shell/*`; `vue/src/features/users/*`; create `vue/src/features/profile/*`; create `vue/src/composables/useStatusCatalog.ts` (around generated vue-query `useGet...Statuses`).

Same three deliverables / three commits as Angular, using vue-query generated composables and the existing Vue router-guard pattern. **Verify:** `cd vue && npm run build`; runtime login both roles.

---

## Self-Review

**Spec coverage:**
- Profile: audience "Both" → access tier (E, Deliverable 1) + `/api/me/profile` & `/api/admin/users/{id}/profile` (B1). ✅
- Per-project status breakdown, replies counted separately, environment split, grand-total headline → DTO (B1 Step 1) + UI (E, Deliverable 3). ✅
- Status catalog single source of truth → A1; consumed by widget (D1) and dashboards (E, Deliverable 2). ✅
- No migration; granular commits; all 3 frameworks; no deploy → Global Constraints + per-task commits. ✅

**Placeholder scan:** API and widget tasks carry full code. Dashboard tasks intentionally reference existing per-framework patterns + the exact generated hook names and the shared contract rather than duplicating three full UIs — this matches the project's subagent-built multi-client rule; each has exact files, deliverables, commit messages, and verify steps.

**Type consistency:** `StatusItem{value,name,label,color,order}` identical in API (A1), widget (D1), dashboards (E). `UserProfileResponse` nested types (`ProfileCounts` base → `ProfileTotals`/`ProfileEnvironment`/`ProfileProject`) consistent between B1 and the UI contract. `GetByPublicIdAsync`/`GetByIdAsync` names match between interface, service, and both controllers.

## Out of scope (roadmap)
DB-backed/admin-CRUD statuses; time-series charts; CSV export; a widget "my activity on this project" card.
