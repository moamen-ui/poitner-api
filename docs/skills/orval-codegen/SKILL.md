# Orval Code Generation Skill

> **For AI agents:** This document explains how the admin-web Angular app generates
> type-safe services and models from the .NET API's OpenAPI/Swagger spec using Orval,
> how to regenerate them after backend changes, and how to consume them correctly.

## Architecture Overview

```
.NET API (Swashbuckle)
    ↓ serves dynamic spec at /swagger/v1/swagger.json
generate-services script (scripts/generate-services.mjs)
    ↓ downloads spec → openapi.json
    ↓ runs: npx orval --config orval.config.ts
Orval (orval.config.ts)
    ↓ reads openapi.json + generates TypeScript
admin-web/src/app/core/api/generated/
    ├── auth/auth.service.ts       ← GET = httpResource fn, POST = HttpClient service
    ├── me/me.service.ts
    ├── users/users.service.ts
    ├── roles/roles.service.ts
    ├── projects/projects.service.ts
    ├── stats/stats.service.ts
    └── model/                     ← TypeScript interfaces for all DTOs
        ├── index.ts               ← barrel re-export
        ├── userResponse.ts
        ├── roleResponse.ts
        └── ...
```

## Key Design Decisions

| Aspect | Choice | Why |
|---|---|---|
| Orval client | `angular` | Native Angular DI + HttpClient |
| Retrieval mode | `httpResource` | Signal-first GETs (Angular 19.2+), auto-refetch on param change |
| Mutations (POST/PATCH/DELETE) | `HttpClient` service methods | Imperative, subscribe-based |
| Envelope unwrapping | HTTP interceptor (`apiInterceptor`) | No custom mutator needed; works for both httpResource and HttpClient |
| Output mode | `tags-split` | One file per Swagger tag; tree-shakeable |
| Path alias | `@api/*` | Short imports: `@api/users/users.service` |

## The Envelope Pattern

The .NET API wraps ALL responses in `Result<T>`:

```json
{ "isSuccess": true, "isNotFound": false, "isConflict": false, "message": null, "data": { ... } }
```

The `apiInterceptor` (in `src/app/core/auth/auth.interceptor.ts`) automatically:
1. Prepends `environment.apiBase` to URLs starting with `/api/`
2. Adds `Authorization: Bearer <token>` header
3. Unwraps the envelope: extracts `.data`, throws `Error(message)` if `isSuccess === false`
4. Redirects to `/login` on HTTP 401

**Generated types use the INNER type** (e.g., `UserResponse`, not `Result<UserResponse>`)
because backend controllers annotate with `[ProducesResponseType(typeof(UserResponse), 200)]`.

## Generated Code Structure

### httpResource functions (GETs)

Each GET operation generates a standalone exported function. These are **not** injectable services —
they are plain functions that must be called within an injection context (field initializers in a component).

```ts
// Generated: src/app/core/api/generated/stats/stats.service.ts
import { getApiAdminStatsResource } from '@api/stats/stats.service';

// In a component:
statsResource = getApiAdminStatsResource();
// statsResource.value()        → StatsResponse | undefined
// statsResource.isLoading()    → boolean
// statsResource.error()        → Error | undefined
// statsResource.hasValue()     → boolean
// statsResource.reload()       → void (re-fetch)
```

**With parameters** (auto-refetch when signal changes):

```ts
import { getApiAdminUsersResource } from '@api/users/users.service';
import { computed, signal } from '@angular/core';

filter = signal('approved');
usersResource = getApiAdminUsersResource(
  computed(() => ({ status: this.filter() }))
);
// Changing filter auto-refetches:
this.filter.set('pending'); // ← triggers new HTTP request automatically
```

### HttpClient service methods (POSTs/PATCHs/DELETEs)

Mutations generate `@Injectable({ providedIn: 'root' })` service classes:

```ts
import { UsersService } from '@api/users/users.service';

// In a component:
private usersService = inject(UsersService);

// Returns Observable<UserResponse> (envelope already unwrapped by interceptor)
this.usersService.postApiAdminUsers({ email, password, displayName, roleId }).subscribe({
  next: (createdUser) => { ... },
  error: (e) => console.log(extractMessage(e)),
});

// After mutation, reload resources:
this.usersResource.reload();
```

### Models

All DTOs are generated as TypeScript interfaces in `src/app/core/api/generated/model/`:

```ts
import type { UserResponse, RoleResponse, CreateUserRequest } from '@api/model';
```

**All properties are optional** (`id?: number`, `email?: string | null`, etc.) because Swashbuckle
doesn't mark them as required. Use `!` non-null assertion for known-present fields:
`user.id!`, `project.isActive!`.

## Import Conventions

```ts
// ✅ CORRECT — use @api alias
import { UsersService, getApiAdminUsersResource } from '@api/users/users.service';
import type { UserResponse, RoleResponse } from '@api/model';

// ❌ WRONG — never use relative paths to generated code
import { UsersService } from '../../core/api/generated/users/users.service';

// ❌ WRONG — never edit generated files
// All files under src/app/core/api/generated/ are auto-generated.
// Changes will be overwritten on regeneration.
```

## How to Regenerate After API Changes

### Prerequisites

The .NET API must be running on `http://localhost:8090`:

```bash
# Option A: Docker (recommended)
just up
# or: docker compose up -d

# Option B: Local dotnet run
cd /Users/momen/Desktop/REPOS/pointer-api
ASPNETCORE_URLS="http://localhost:8090" \
ConnectionStrings__Default="Host=localhost;Port=5433;Database=pointer;Username=pointer;Password=pointer" \
dotnet run --project API --no-launch-profile
```

### Regenerate

```bash
cd admin-web
npm run generate-services
```

This script:
1. Downloads `swagger.json` from `http://localhost:8090/swagger/v1/swagger.json`
2. Saves it as `admin-web/openapi.json`
3. Runs `npx orval` to generate TypeScript code
4. Output goes to `src/app/core/api/generated/` (old files are cleaned first)

### When to regenerate

Regenerate when ANY of these change on the backend:
- New endpoint added
- Existing endpoint's parameters, request body, or response type changed
- DTO properties added/removed/renamed
- New query parameters

You do NOT need to regenerate for:
- Internal logic changes in the API (if DTOs are unchanged)
- Frontend-only changes

## Backend Requirements for Clean Generation

For Orval to generate properly typed code, backend controllers need two things:

### 1. `[ProducesResponseType]` with inner type

Every controller action that admin-web uses must declare its response type:

```csharp
[HttpGet]
[ProducesResponseType(typeof(List<UserResponse>), StatusCodes.Status200OK)]
public async Task<IActionResult> List() { ... }
```

**Use the inner data type** (e.g., `List<UserResponse>`), NOT the wrapper (`Result<List<UserResponse>>`).
The interceptor handles the `Result<T>` unwrapping at runtime.

### 2. `[Produces("application/json")]` (global filter)

Already configured globally in `Program.cs`:

```csharp
builder.Services.AddControllers(options =>
{
    options.Filters.Add(new ProducesAttribute("application/json"));
});
```

This prevents Swashbuckle from generating `text/plain` and `text/json` content types,
which would cause Orval to generate verbose `accept` parameter overloads.

## Adding a New Endpoint: Full Workflow

### Step 1: Backend — Add controller action with annotations

```csharp
// API/Controllers/Admin/UsersController.cs
[HttpDelete("{id:int}")]
[ProducesResponseType(StatusCodes.Status204NoContent)]
public async Task<IActionResult> Delete(int id)
{
    var result = await userService.DeleteAsync(id);
    return result.IsSuccess ? NoContent() : BadRequest(result);
}
```

### Step 2: Regenerate

```bash
# Ensure API is running
cd admin-web && npm run generate-services
```

### Step 3: Use the new generated method

```ts
// The new DELETE endpoint generates a service method:
import { UsersService } from '@api/users/users.service';

// In component:
this.usersService.deleteApiAdminUsersId(user.id!).subscribe({
  next: () => this.usersResource.reload(),
  error: (e) => this.snack.open(extractMessage(e), 'OK'),
});
```

## Tree-Shaking Guarantees

The generated code is fully tree-shakeable:

- **httpResource functions** are standalone exports — if a component imports only `listUsersResource`,
  the `UsersService` class (with mutation methods) is NOT bundled.
- **Service classes** use `providedIn: 'root'` — unused services are tree-shaken entirely.
- **Model interfaces** are TypeScript types — erased at compile time, zero bundle cost.
- **Tag filtering** in `orval.config.ts` excludes unused endpoints (Comments, Replies, Uploads).

## Method Naming Convention

Orval generates method names from the HTTP verb + URL path:

| HTTP | Path | Generated method name |
|---|---|---|
| GET | /api/admin/users | `getApiAdminUsersResource()` (httpResource function) |
| POST | /api/admin/users | `postApiAdminUsers()` (service method) |
| PATCH | /api/admin/users/{id} | `patchApiAdminUsersId(id, body)` |
| POST | /api/admin/users/{id}/approve | `postApiAdminUsersIdApprove(id, body)` |
| DELETE | /api/admin/users/{id} | `deleteApiAdminUsersId(id)` |

Pattern: `{httpVerb}{PathSegmentsInCamelCase}({pathParams}, {body}, {options})`.

## Common Patterns

### Read data (GET)

```ts
// Define resource — auto-fetches on creation
usersResource = getApiAdminUsersResource(
  computed(() => ({ status: this.filter().toLowerCase() || undefined }))
);

// Derive signals
users = computed(() => this.usersResource.value() ?? []);
loading = computed(() => this.usersResource.isLoading());

// Template uses signals
// @if (loading()) { <progress-bar /> }
// @for (u of users(); track u.id) { ... }
```

### Mutate data (POST/PATCH)

```ts
private usersService = inject(UsersService);
busy = signal(false);

createUser(body: CreateUserRequest) {
  this.busy.set(true);
  this.usersService.postApiAdminUsers(body).subscribe({
    next: () => {
      this.busy.set(false);
      this.usersResource.reload(); // refresh data
    },
    error: (e) => {
      this.busy.set(false);
      this.snack.open(extractMessage(e), 'OK', { duration: 4000 });
    },
  });
}
```

### Error handling

```ts
import { extractMessage } from '../../core/api/extract-message';

// extractMessage extracts the server's error message from Result body in 400 responses
error: (e) => this.snack.open(extractMessage(e), 'OK', { duration: 4000 })
```

## Files to NEVER Edit

- `src/app/core/api/generated/**` — all auto-generated, will be overwritten
- `admin-web/openapi.json` — downloaded spec, not hand-written

## Config Files Reference

| File | Purpose |
|---|---|
| `admin-web/orval.config.ts` | Orval generation config (client mode, tags, output paths) |
| `admin-web/scripts/generate-services.mjs` | Download + generate script |
| `admin-web/package.json` | `generate-services` npm script |
| `admin-web/tsconfig.json` | `@api/*` path alias |
| `admin-web/src/app/core/auth/auth.interceptor.ts` | Unified API interceptor (envelope unwrap) |
| `admin-web/src/app/core/api/extract-message.ts` | Error message extraction utility |
| `admin-web/src/environments/environment.ts` | `apiBase` URL |
