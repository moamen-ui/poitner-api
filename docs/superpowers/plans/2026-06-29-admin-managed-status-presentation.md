# Admin-Managed Status Presentation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Let admins edit comment-status label/color/order from the dashboard at runtime (DB overrides merged into `GET /api/statuses`), with an admin page in all three dashboards.

**Architecture:** New `status_presentations` override table; `StatusCatalogService` merges code defaults + DB overrides; new admin endpoints (GET/PATCH/DELETE `/api/admin/statuses`); regenerate+republish clients; admin "Statuses" page in angular/react/vue. Then merge + deploy all.

**Tech Stack:** .NET 8 (EF Core/Postgres, auto-migrate on startup), orval `@moamen-ui/pointer-*`, Angular/React/Vue (Tailwind v4) dashboards.

## Global Constraints

- No test framework — verify via `dotnet build` / `npm run build` / `curl` / browser.
- Granular, independently-revertable commits — one focused commit per task.
- Dashboard work built for ALL THREE frameworks (parallel subagents).
- `CommentStatus { Open=1, ReadyToApply=2, Applied=3, Archived=4 }` is unchanged behavior identity. This feature is presentation-only (no add/remove statuses).
- Branch `feat/profile-and-status-catalog` in both repos. API paths: `/Users/momen/Desktop/REPOS/pointer-api`; dashboards `/Users/momen/Desktop/REPOS/pointer-dashboard/{angular,react,vue}`.
- Dashboard build env: `export PATH=/opt/homebrew/opt/node@26/bin:$PATH` and `export NODE_AUTH_TOKEN=$(gh auth token)` for installs.
- Color override format: `^#[0-9a-fA-F]{6}$`. Label ≤ 64 chars, non-empty. Order ≥ 0.
- Deploy ONLY at the very end (user approved deploying all).

---

## Task SP1: StatusPresentation entity + migration + merged GET /api/statuses

**Files:**
- Create: `Domain/Entity/StatusPresentation.cs`
- Create: `Infrastructure/Mappings/StatusPresentationMapping.cs`
- Modify: `Infrastructure/AppDbContext.cs` (add `DbSet<StatusPresentation>`)
- Modify: `Application/Services/Interfaces/IStatusCatalogService.cs` (make async)
- Modify: `Application/Services/Implementation/StatusCatalogService.cs` (inject IUnitOfWork, merge overrides)
- Modify: `API/Controllers/StatusesController.cs` (async)
- Generated: a new migration under `Infrastructure/Migrations`

**Interfaces:**
- Produces: `StatusPresentation { int StatusValue; string? Label; string? Color; int? DisplayOrder }` (BaseEntity); `IStatusCatalogService.GetAllAsync() : Task<Result<List<StatusItem>>>`; the static defaults exposed as `StatusCatalogService.Defaults` (`List<StatusItem>`) for reuse by the admin service (Task SP2).

- [ ] **Step 1: Entity**

```csharp
namespace Pointer.Domain.Entity;

public class StatusPresentation : BaseEntity
{
    public int StatusValue { get; set; }   // CommentStatus int
    public string? Label { get; set; }
    public string? Color { get; set; }
    public int? DisplayOrder { get; set; }
}
```

- [ ] **Step 2: Mapping** (mirror `ProjectMapping`; snake_case BaseEntity cols + unique status_value)

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

public class StatusPresentationMapping : IEntityTypeConfiguration<StatusPresentation>
{
    public void Configure(EntityTypeBuilder<StatusPresentation> b)
    {
        b.ToTable("status_presentations");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        b.Property(x => x.DeletedBy).HasColumnName("deleted_by");
        b.Property(x => x.StatusValue).HasColumnName("status_value").IsRequired();
        b.HasIndex(x => x.StatusValue).IsUnique();
        b.Property(x => x.Label).HasColumnName("label").HasMaxLength(64);
        b.Property(x => x.Color).HasColumnName("color").HasMaxLength(9);
        b.Property(x => x.DisplayOrder).HasColumnName("display_order");
    }
}
```

- [ ] **Step 3: DbSet** — in `AppDbContext` add `public DbSet<StatusPresentation> StatusPresentations => Set<StatusPresentation>();`

- [ ] **Step 4: Refactor `StatusCatalogService`** — keep the four defaults as a public static `Defaults` list (same values as today: 1 Open/Open/#2563eb/1, 2 ReadyToApply/Ready/#d97706/2, 3 Applied/Completed/#16a34a/3, 4 Archived/Archived/#6b7280/4). Inject `IUnitOfWork`. Implement:

```csharp
public async Task<Result<List<StatusItem>>> GetAllAsync()
{
    var overrides = await _unitOfWork.Repository<StatusPresentation>().Query()
        .AsNoTracking().Where(s => s.DeletedAt == null).ToListAsync();
    var merged = Defaults.Select(d =>
    {
        var o = overrides.FirstOrDefault(x => x.StatusValue == d.Value);
        return new StatusItem
        {
            Value = d.Value,
            Name = d.Name,
            Label = o?.Label ?? d.Label,
            Color = o?.Color ?? d.Color,
            Order = o?.DisplayOrder ?? d.Order,
        };
    }).OrderBy(s => s.Order).ToList();
    return Result<List<StatusItem>>.Success(merged);
}
```

Update `IStatusCatalogService` to `Task<Result<List<StatusItem>>> GetAllAsync();` and `StatusesController.Get()` to `public async Task<IActionResult> Get() => Ok(await statusCatalog.GetAllAsync());`.

- [ ] **Step 5: Migration** — run `export PATH=/opt/homebrew/opt/node@26/bin:$PATH` (for node, not needed here) then from repo root: `dotnet ef migrations add AddStatusPresentations -p Infrastructure -s API`. Confirm it creates the `status_presentations` table.

- [ ] **Step 6: Build + verify** — `dotnet build` (0 errors). Rebuild the local container (`docker compose up -d --build api`) and `curl http://localhost:8090/api/statuses` → still returns the four defaults (table empty). Insert is tested in SP2.

- [ ] **Step 7: Commit** — `feat(api): add status_presentations overrides merged into GET /api/statuses`

---

## Task SP2: Admin status endpoints (GET/PATCH/DELETE)

**Files:**
- Create: `Application/DTOs/Status/StatusAdminItem.cs`, `Application/DTOs/Status/UpdateStatusPresentationRequest.cs`
- Create: `Application/Services/Interfaces/IStatusAdminService.cs`, `Application/Services/Implementation/StatusAdminService.cs`
- Create: `API/Controllers/Admin/StatusesController.cs`

**CRITICAL — validation is INLINE, not FluentValidation.** This project registers FluentValidation validators but has NO auto-validation pipeline/filter, so validators are never invoked. Do NOT create a validator class (it would be dead code). Validate inside `StatusAdminService.UpsertAsync` and return `Result<StatusAdminItem>.Failure(msg)` on bad input (mirrors the existing inline-guard pattern used elsewhere).

**Interfaces:**
- Consumes: `StatusCatalogService.Defaults`, `IUnitOfWork.Repository<StatusPresentation>()`, `CommentStatus` enum.
- Produces: `StatusAdminItem { int Value; string Name; string DefaultLabel; string DefaultColor; int DefaultOrder; string Label; string Color; int Order; bool IsOverridden }`; `UpdateStatusPresentationRequest { string? Label; string? Color; int? Order }`; `IStatusAdminService.ListAsync()/UpsertAsync(int value, UpdateStatusPresentationRequest)/ResetAsync(int value)`.

- [ ] **Step 1: DTOs**

```csharp
namespace Pointer.Application.DTOs.Status;

public class StatusAdminItem
{
    public int Value { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DefaultLabel { get; set; } = string.Empty;
    public string DefaultColor { get; set; } = string.Empty;
    public int DefaultOrder { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public int Order { get; set; }
    public bool IsOverridden { get; set; }
}
```

```csharp
namespace Pointer.Application.DTOs.Status;

public class UpdateStatusPresentationRequest
{
    public string? Label { get; set; }
    public string? Color { get; set; }
    public int? Order { get; set; }
}
```

- [ ] **Step 2: Interface**

```csharp
using Pointer.Application.DTOs.Status;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface IStatusAdminService
{
    Task<Result<List<StatusAdminItem>>> ListAsync();
    Task<Result<StatusAdminItem>> UpsertAsync(int value, UpdateStatusPresentationRequest request);
    Task<Result> ResetAsync(int value);
}
```

- [ ] **Step 3: Service** — `StatusAdminService` (injects `IUnitOfWork`). `ListAsync` returns one `StatusAdminItem` per `StatusCatalogService.Defaults`, filling effective values from the override row (if any) and `IsOverridden` = a row exists. `UpsertAsync`:
  1. If `value` is not a defined `CommentStatus` → `Result<StatusAdminItem>.NotFound("Unknown status")`.
  2. INLINE validation (return `Result<StatusAdminItem>.Failure(msg)`): if `request.Label is not null` then it must be non-empty and ≤ 64 chars; if `request.Color is not null` it must match `^#[0-9a-fA-F]{6}$`; if `request.Order is not null` it must be ≥ 0.
  3. Find-or-create the override row for `value`; set only the provided (non-null) fields (null leaves a field unchanged — to revert a single field use Reset); `SaveChangesAsync()` (audit cols auto-stamped); return the recomputed `StatusAdminItem`.
  `ResetAsync(value)`: if `value` not a defined `CommentStatus` → `Result.NotFound`; find the row; if none → `Result.Success` (already default); else remove + save. Use a private merge helper shared with `ListAsync`.

  Use `System.Text.RegularExpressions.Regex.IsMatch(request.Color, "^#[0-9a-fA-F]{6}$")` for the color check.

- [ ] **Step 4: Controller** (mirror `Admin/RolesController` shape; tag `Statuses` so orval includes it under the existing filter)

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.API.Auth;
using Pointer.Application.DTOs.Status;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers.Admin;

[ApiController]
[Route("api/admin/statuses")]
[Tags("Statuses")]
[Authorize(Policy = Policies.Admin)]
public class StatusesController(IStatusAdminService service) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(List<StatusAdminItem>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List()
    {
        var result = await service.ListAsync();
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPatch("{value:int}")]
    [ProducesResponseType(typeof(StatusAdminItem), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(int value, [FromBody] UpdateStatusPresentationRequest request)
    {
        var result = await service.UpsertAsync(value, request);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpDelete("{value:int}")]
    [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
    public async Task<IActionResult> Reset(int value)
    {
        var result = await service.ResetAsync(value);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
```

- [ ] **Step 6: Build + verify** — `dotnet build`; rebuild container; with an admin token: `GET /api/admin/statuses` (4 items, isOverridden=false), `PATCH /api/admin/statuses/3 {"label":"Done","color":"#0ea5e9"}` → 200; then `GET /api/statuses` shows Applied label "Done" color #0ea5e9; `DELETE /api/admin/statuses/3` → 200; `GET /api/statuses` back to "Completed". Validate a bad color (`"red"`) → 400.

- [ ] **Step 7: Commit** — `feat(api): admin endpoints to manage status presentation overrides`

---

## Task SP3: Regenerate + republish clients

- [ ] **Step 1** — API is already running on :8090 with the new endpoints (from SP2). The admin endpoints are tagged `Statuses` (already in `orval.config.ts` filter), so no config change. Run `npm run generate-clients`.
- [ ] **Step 2** — verify all 3 clients emit `getApiAdminStatuses`/`useGetApiAdminStatuses`, `patchApiAdminStatusesValue` (or the generated PATCH name), `deleteApiAdminStatusesValue` (or generated DELETE name), and the `StatusAdminItem`/`UpdateStatusPresentationRequest` models: `grep -rl "AdminStatuses\|StatusAdminItem\|UpdateStatusPresentation" clients/*/src`.
- [ ] **Step 3** — no client commit (clients/ gitignored). Publishing happens after deploy (see Deploy section). Record the generated PATCH/DELETE hook names for the dashboard tasks.

---

## Tasks SP4-NG / SP4-REACT / SP4-VUE: Statuses admin page (×3)

> Built via parallel-but-sequential subagents (shared git repo) AFTER the clients are republished and each dashboard bumped to the new version. Each is its own commit per framework.

**Shared contract:**
- New admin-only route/nav item **"Statuses"** (reuse the admin guard from the prior feature — non-admins must not see or reach it).
- Page lists the 4 statuses from `GET /api/admin/statuses`; each row has: **label** (text input), **color** (`<input type=color>` + hex text), **order** (number input), a **Save** action (`PATCH /api/admin/statuses/{value}` with the row's label/color/order), and a **Reset** action (`DELETE /api/admin/statuses/{value}`, then the row shows its `default*` values).
- After a successful Save/Reset, invalidate/refetch the status catalog query (the same query `useStatusCatalog`/the catalog service uses) so labels/colors update across the dashboard immediately.
- Use each stack's existing form/table/toast/confirm patterns; match the other dashboards' behavior exactly (consistency).

### Task SP4-NG: Angular
**Files:** `angular/package.json` (bump `@moamen-ui/pointer-angular` to the new version + `npm install`); create `angular/src/app/features/statuses/statuses.component.ts`; add a guarded `statuses` child route in `app.routes.ts` (adminGuard); add the nav item in `shell.component.ts` (admin section). Use the generated `getApiAdminStatusesResource` + the PATCH/DELETE service methods. Invalidate the catalog by refreshing the `StatusCatalogService` (add a `reload()` that re-fetches). Verify `npm run build`. Commit `feat(ng): admin statuses presentation page`.

### Task SP4-REACT: React
**Files:** `react/package.json` bump + install; create `react/src/features/statuses/StatusesPage.tsx`; add an admin-guarded route + nav item. Use `useGetApiAdminStatuses` + the generated PATCH/DELETE mutation hooks; on success call `queryClient.invalidateQueries` for the statuses query key (and the admin-statuses key). Verify `npm run build`. Commit `feat(react): admin statuses presentation page`.

### Task SP4-VUE: Vue
**Files:** `vue/package.json` bump + install; create `vue/src/features/statuses/StatusesPage.vue`; add an admin-guarded route (`requiresAdmin`) + nav item. Use `useGetApiAdminStatuses` + generated PATCH/DELETE composables; on success invalidate the statuses query keys via the vue-query client. Verify `npm run build`. Commit `feat(vue): admin statuses presentation page`.

---

## Deploy (final, user-approved)

- [ ] Merge `feat/profile-and-status-catalog` → `main` in BOTH repos (fast-forward if possible; preserves granular commits).
- [ ] Deploy API: VM `git pull` + `docker compose --env-file .env.prod -f docker-compose.prod.yml up -d --build api` (auto-migrate creates `status_presentations`). Verify `GET https://api.pointer.moamen.work/api/statuses` + admin endpoints live.
- [ ] Run `publish-clients.yml` (auto-bump, e.g. 1.0.6); verify versions.
- [ ] Build + deploy all three dashboards to app-{angular,react,vue}.pointer.moamen.work (use the project's existing dashboard-deploy mechanism — discover it at deploy time).
- [ ] Smoke-test live: admin → Statuses page → rename a status → confirm it propagates to a dashboard badge and the widget.

## Self-Review
- Spec coverage: override table (SP1), merged GET (SP1), admin GET/PATCH/DELETE + validation (SP2), clients (SP3), admin page ×3 (SP4), deploy. ✅
- No placeholders: all entity/DTO/service/controller/validator code given; dashboard tasks reference exact generated symbols (names confirmed during SP3) + the shared contract.
- Type consistency: `StatusPresentation`, `StatusAdminItem`, `UpdateStatusPresentationRequest`, `GetAllAsync` names consistent across tasks.
