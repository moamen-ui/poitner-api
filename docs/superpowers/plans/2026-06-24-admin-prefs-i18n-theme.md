# Admin Preferences (i18n + Dark Theme + Persistence) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Per-user language (ar/en) and theme (light/dark) for the admin SPA, persisted in the DB via new endpoints, with runtime AR/EN switching (RTL) and a dark-theme toggle.

**Architecture:** Backend adds nullable `Language`/`Theme` on `User`, surfaces them in `MeResponse`, and a `PATCH /api/me/preferences`. Frontend uses **Transloco** for runtime i18n (+ `dir` flip for RTL), a CSS-variable dark theme toggled by a `html.dark` class, and a `PreferencesService` that resolves saved→browser/system→`en`/`dark` and persists changes.

**Tech Stack:** .NET 8 / EF Core (backend), Angular 22 + Angular Material + **@jsverse/transloco** (frontend).

## Global Constraints

- Backend root `/Users/momen/Desktop/REPOS/pointer-api/`; SPA root `/Users/momen/Desktop/REPOS/pointer-api/admin-web/`.
- **Angular/npm commands need Node 26:** prefix with `PATH="/opt/homebrew/opt/node@26/bin:$PATH"`. SPA build `npm run build`; SPA tests `npm test` (vitest via ng — NOT bare vitest, no `--browsers`).
- Backend: build via `dotnet build` (run from repo root; stop any running API first to free DLLs — a local API may be running on :8090 against Postgres on :5433). Migrations via `dotnet ef ... -p Infrastructure -s API`.
- Allowed values: `Language` ∈ {`ar`,`en`}, `Theme` ∈ {`light`,`dark`}. Columns nullable (null = unset).
- Resolution order (frontend): saved pref → browser lang (`navigator.language` starts with `ar`→`ar` else `en`) + system theme (`prefers-color-scheme: dark`→`dark` else `light`) → fallback `en`/`dark`.
- Standalone Angular components + signals only. NO git commits (skip commit steps). Don't break existing tests.
- Don't change unrelated backend behavior; follow existing patterns (Result<T>, Scrutor `*Service`, FluentValidation, snake_case, audit via SaveChangesAsync).

---

## Task 1: Backend — preferences columns, MeResponse fields, endpoint, migration

**Files:**
- Modify: `Domain/Entity/User.cs` (add `Language`, `Theme`).
- Modify: `Infrastructure/Mappings/UserMapping.cs` (map the two columns).
- Modify: `Application/DTOs/Auth/MeResponse.cs` (add `Language`, `Theme`).
- Modify: `Application/Services/Implementation/AuthService.cs` (map them in `MapToMeResponse`).
- Create: `Application/DTOs/Preferences/UpdatePreferencesRequest.cs`.
- Create: `Application/Services/Interfaces/IPreferencesService.cs`, `Application/Services/Implementation/PreferencesService.cs`.
- Create: `Application/Validators/UpdatePreferencesValidator.cs`.
- Create: `API/Controllers/MeController.cs`.
- Create migration `Infrastructure/Migrations/*_AddUserPreferences.cs` (generated).
- Test: `Tests/PreferencesValidatorTests.cs`.

**Interfaces — Produces:**
- `User.Language: string?`, `User.Theme: string?`.
- `MeResponse.Language: string?`, `MeResponse.Theme: string?`.
- `UpdatePreferencesRequest { string? Language; string? Theme; }`.
- `IPreferencesService { Task<Result<MeResponse>> UpdateAsync(UpdatePreferencesRequest request); }` (reads current user from `ICurrentUser`).

- [ ] **Step 1: Entity** — add to `User`:
```csharp
public string? Language { get; set; }
public string? Theme { get; set; }
```

- [ ] **Step 2: Mapping** — in `UserMapping.Configure`, after `IsActive`:
```csharp
b.Property(x => x.Language).HasColumnName("language").HasMaxLength(8);
b.Property(x => x.Theme).HasColumnName("theme").HasMaxLength(8);
```

- [ ] **Step 3: MeResponse** — add `public string? Language { get; set; }` and `public string? Theme { get; set; }`.

- [ ] **Step 4: AuthService.MapToMeResponse** — add `Language = user.Language, Theme = user.Theme,` to the object initializer. (The login + Me queries already load the `User`; no query change needed.)

- [ ] **Step 5: `UpdatePreferencesRequest.cs`**
```csharp
namespace Pointer.Application.DTOs.Preferences;
public class UpdatePreferencesRequest { public string? Language { get; set; } public string? Theme { get; set; } }
```

- [ ] **Step 6: MessageKeys** — add to `Application/Resources/MessageKeys.cs` a nested class:
```csharp
public static class Preferences { public const string Invalid = "Invalid preference value."; public const string NotFound = "User not found."; }
```

- [ ] **Step 7: `IPreferencesService.cs` + `PreferencesService.cs`**
```csharp
// Interfaces/IPreferencesService.cs
using Pointer.Application.DTOs.Auth;
using Pointer.Application.DTOs.Preferences;
using Pointer.Application.Response;
namespace Pointer.Application.Services.Interfaces;
public interface IPreferencesService { Task<Result<MeResponse>> UpdateAsync(UpdatePreferencesRequest request); }
```
```csharp
// Implementation/PreferencesService.cs
using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.DTOs.Preferences;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
namespace Pointer.Application.Services.Implementation;
public class PreferencesService : IPreferencesService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;
    public PreferencesService(IUnitOfWork unitOfWork, ICurrentUser currentUser)
    { _unitOfWork = unitOfWork; _currentUser = currentUser; }

    public async Task<Result<MeResponse>> UpdateAsync(UpdatePreferencesRequest request)
    {
        var publicId = _currentUser.Id;
        if (publicId == null) return Result<MeResponse>.Failure(MessageKeys.Auth.InvalidCredentials);

        var user = await _unitOfWork.Repository<User>().Query()
            .Where(u => u.DeletedAt == null && u.PublicId == publicId.Value)
            .FirstOrDefaultAsync();
        if (user == null) return Result<MeResponse>.NotFound(MessageKeys.Preferences.NotFound);

        if (request.Language != null) user.Language = request.Language;
        if (request.Theme != null) user.Theme = request.Theme;
        _unitOfWork.Repository<User>().Update(user);
        await _unitOfWork.SaveChangesAsync();

        return Result<MeResponse>.Success(new MeResponse
        {
            Id = user.PublicId, Email = user.Email, DisplayName = user.DisplayName,
            RoleId = user.RoleId, RoleName = string.Empty, IsAdmin = false,
            Language = user.Language, Theme = user.Theme,
        });
    }
}
```
> Note: RoleName/IsAdmin aren't needed by the preferences response consumer (the SPA only reads language/theme back); leaving them minimal is fine. If you prefer, `Include(u => u.Role)` and populate them — optional.

- [ ] **Step 8: `UpdatePreferencesValidator.cs`**
```csharp
using FluentValidation;
using Pointer.Application.DTOs.Preferences;
using Pointer.Application.Resources;
namespace Pointer.Application.Validators;
public class UpdatePreferencesValidator : AbstractValidator<UpdatePreferencesRequest>
{
    public UpdatePreferencesValidator()
    {
        RuleFor(x => x.Language).Must(v => v is "ar" or "en").When(x => x.Language != null)
            .WithMessage(MessageKeys.Preferences.Invalid);
        RuleFor(x => x.Theme).Must(v => v is "light" or "dark").When(x => x.Theme != null)
            .WithMessage(MessageKeys.Preferences.Invalid);
    }
}
```

- [ ] **Step 9: `MeController.cs`**
```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.Application.DTOs.Preferences;
using Pointer.Application.Services.Interfaces;
namespace Pointer.API.Controllers;
[ApiController]
[Route("api/me")]
[Authorize]
public class MeController(IPreferencesService preferencesService) : ControllerBase
{
    [HttpPatch("preferences")]
    public async Task<IActionResult> UpdatePreferences([FromBody] UpdatePreferencesRequest request)
    {
        var result = await preferencesService.UpdateAsync(request);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
```

- [ ] **Step 10: Failing test `Tests/PreferencesValidatorTests.cs`** (vitest? NO — backend xUnit). Use the existing xUnit + FluentValidation.TestHelper pattern:
```csharp
using FluentValidation.TestHelper;
using Pointer.Application.DTOs.Preferences;
using Pointer.Application.Validators;
using Xunit;
public class PreferencesValidatorTests
{
    [Fact] public void Rejects_bad_language()
    { new UpdatePreferencesValidator().TestValidate(new UpdatePreferencesRequest { Language = "fr" }).ShouldHaveValidationErrorFor(x => x.Language); }
    [Fact] public void Accepts_valid_and_null()
    { new UpdatePreferencesValidator().TestValidate(new UpdatePreferencesRequest { Language = "ar", Theme = null }).ShouldNotHaveAnyValidationErrors(); }
}
```

- [ ] **Step 11: Migration + build + test**
```bash
cd /Users/momen/Desktop/REPOS/pointer-api
pkill -f "Pointer.API" 2>/dev/null; sleep 2          # free DLLs if the API is running
dotnet build
dotnet ef migrations add AddUserPreferences -p Infrastructure -s API
dotnet test
```
Expected: build succeeds; migration created (adds `language`,`theme` to `users`); tests pass (incl. the 2 new). The dev DB picks up the migration on next API start (auto-migrate).

---

## Task 2: Frontend foundation — Transloco, models, dark CSS, PreferencesService

**Files:**
- Add Transloco (CLI), `src/app/transloco-loader.ts` (or use the generated one), update `app.config.ts` (provideTransloco), create `src/assets/i18n/en.json` + `ar.json` (full key set).
- Modify `src/styles.scss` (add `html.dark { … }` variable overrides + `color-scheme`).
- Modify `src/app/core/api/models.ts` (`MeResponse.language?`, `MeResponse.theme?`; add `UpdatePreferencesRequest`).
- Create `src/app/core/prefs/preferences.service.ts`.
- Modify `src/app/core/auth/auth.service.ts` (call prefs init on login + at construction from stored user).

**Interfaces — Produces:**
- `MeResponse.language?: 'ar'|'en'|null; theme?: 'light'|'dark'|null;`
- `PreferencesService`: signals `language()`, `theme()`; `init(saved?: {language?: string|null; theme?: string|null}): void`; `setLanguage('ar'|'en')`; `setTheme('light'|'dark')`; `apply()`.

- [ ] **Step 1: Install Transloco**
```bash
cd /Users/momen/Desktop/REPOS/pointer-api/admin-web
PATH="/opt/homebrew/opt/node@26/bin:$PATH" npx -y @jsverse/transloco@latest ng-add
```
Answer prompts: languages `en,ar`, no SSR, prefer the runtime HTTP loader writing files under `src/assets/i18n/`. If the schematic differs in Angular 22, configure manually: install `@jsverse/transloco`, add `provideTransloco({ config: { availableLangs: ['en','ar'], defaultLang: 'en', reRenderOnLangChange: true, prodMode: false }, loader: TranslocoHttpLoader })` to `app.config.ts`, and create a `TranslocoHttpLoader` that GETs `/assets/i18n/${lang}.json`.

- [ ] **Step 2: Dictionaries** — create `src/assets/i18n/en.json` and `ar.json` with keys for EVERY UI string. Derive the key list by reading the current templates in `src/app/features/*` and `shell`. Namespaces: `common` (save/cancel/active/disabled/refresh/add/rename/disable/enable/yes/no), `nav` (overview/roles/users/projects), `header` (brand/signOut), `login` (title/email/password/signIn/notAdmin/failed), `overview` (projects/users/comments/open/pending/completed/breakdown/key/name/status), `roles` (title/addRole/name/grantsAdmin/system/status/actions), `users` (title/addUser/email/displayName/password/role/status), `projects` (title/addProject/key/name/status). English values = the current literal strings; Arabic values = correct AR translations (e.g. nav.overview "نظرة عامة", roles "الأدوار", users "المستخدمون", projects "المشاريع", header.signOut "تسجيل الخروج", common.active "نشط", common.disabled "معطّل", etc.).

- [ ] **Step 3: Dark theme CSS** — in `styles.scss`, after the light `:root` palette add:
```scss
html.dark {
  color-scheme: dark;
  --app-bg: #0f141b;
  --header-bg: #131a23;
  --sidebar-bg: #0c1116;
  --panel-bg: #161d27;
  --border: #243042;
  --brand: #4f8cff;
  --brand-tint: #1c2struct; /* replace with #1c2a3f */
  --ink: #e6edf5;
  --muted: #94a3b8;
}
```
(Use `--brand-tint: #1c2a3f;`.) Ensure `body { background: var(--app-bg); color: var(--ink); }` already applies. Add `html.dark` does not need Material re-theming for v1; the CSS variables drive the custom chrome and `color-scheme: dark` shifts Material surfaces acceptably.

- [ ] **Step 4: models.ts** — add to `MeResponse`: `language?: 'ar' | 'en' | null; theme?: 'light' | 'dark' | null;`. Add:
```ts
export interface UpdatePreferencesRequest { language?: 'ar' | 'en'; theme?: 'light' | 'dark'; }
```

- [ ] **Step 5: `preferences.service.ts`**
```ts
import { inject, Injectable, signal } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';
import { Api } from '../api/api';
import { MeResponse, UpdatePreferencesRequest } from '../api/models';

type Lang = 'ar' | 'en';
type Theme = 'light' | 'dark';
const LANG_KEY = 'pointer_admin_lang';
const THEME_KEY = 'pointer_admin_theme';

@Injectable({ providedIn: 'root' })
export class PreferencesService {
  private transloco = inject(TranslocoService);
  private api = inject(Api);
  language = signal<Lang>('en');
  theme = signal<Theme>('dark');

  init(saved?: { language?: string | null; theme?: string | null }): void {
    const lang = (saved?.language as Lang) || (localStorage.getItem(LANG_KEY) as Lang) || this.browserLang();
    const theme = (saved?.theme as Theme) || (localStorage.getItem(THEME_KEY) as Theme) || this.systemTheme();
    this.language.set(lang === 'ar' ? 'ar' : 'en');
    this.theme.set(theme === 'light' ? 'light' : 'dark');
    this.apply();
  }
  private browserLang(): Lang { return (navigator.language || '').toLowerCase().startsWith('ar') ? 'ar' : 'en'; }
  private systemTheme(): Theme { return window.matchMedia?.('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'; }

  apply(): void {
    const l = this.language(); const t = this.theme();
    this.transloco.setActiveLang(l);
    const html = document.documentElement;
    html.setAttribute('lang', l);
    html.setAttribute('dir', l === 'ar' ? 'rtl' : 'ltr');
    html.classList.toggle('dark', t === 'dark');
    localStorage.setItem(LANG_KEY, l); localStorage.setItem(THEME_KEY, t);
  }
  setLanguage(l: Lang) { this.language.set(l); this.apply(); this.persist({ language: l }); }
  setTheme(t: Theme) { this.theme.set(t); this.apply(); this.persist({ theme: t }); }
  private persist(p: UpdatePreferencesRequest) {
    if (!localStorage.getItem('pointer_admin_token')) return; // guest: local only
    this.api.patch<MeResponse>('/api/me/preferences', p).subscribe({ next: () => {}, error: () => {} });
  }
}
```

- [ ] **Step 6: Wire init** — in `AuthService`: after a successful `login()` (in the `map`/`tap`), call `inject(PreferencesService).init(res.user)`. Also at app startup, initialise from the stored user: in `app.ts` (root `App`) constructor, `inject(PreferencesService).init(inject(AuthService).user() ?? undefined)`. (Avoid a circular DI: PreferencesService injects Api + Transloco only, not AuthService.)

- [ ] **Step 7: Build** — `npm run build` succeeds; `npm test` passes.

---

## Task 3: Header controls + translate shell, login, overview

**Files:** Modify `features/shell/shell.component.ts`, `features/login/login.component.ts`, `features/overview/overview.component.ts`.

- [ ] **Step 1: Shell header controls** — add two buttons to the toolbar (before/after Sign out):
  - Language: `<button mat-button (click)="togglePrefsLang()">{{ prefs.language() === 'ar' ? 'EN' : 'ع' }}</button>` (show the target language).
  - Theme: `<button mat-icon-button (click)="toggleTheme()"><mat-icon>{{ prefs.theme() === 'dark' ? 'light_mode' : 'dark_mode' }}</mat-icon></button>`.
  - Inject `prefs = inject(PreferencesService)`; `togglePrefsLang()` sets the opposite lang; `toggleTheme()` sets the opposite theme.
- [ ] **Step 2: Translate shell + login + overview templates** — replace literal strings with Transloco keys (`{{ 'nav.overview' | transloco }}`, etc.), importing `TranslocoModule` (or `TranslocoPipe`) into each standalone component's `imports`. Cover nav labels, brand/sign-out, login form, overview card labels + table headers + "Projects Breakdown"/"Refresh".
- [ ] **Step 3: Build + test** — `npm run build` + `npm test` green.

---

## Task 4: Translate roles, users, projects

**Files:** Modify `features/roles/roles.component.ts`, `features/users/users.component.ts`, `features/projects/projects.component.ts`.

- [ ] **Step 1:** Import `TranslocoModule`/`TranslocoPipe` into each; replace literal strings (titles, add-form labels + buttons, table column headers, status Active/Disabled, "system" chip, Rename/Disable/Enable, confirm() messages) with the `*.json` keys defined in Task 2 (add any missing keys to BOTH `en.json` and `ar.json`).
- [ ] **Step 2: Build + test** — `npm run build` + `npm test` green.

---

## Task 5: Verify (browser e2e) + docs

**Files:** Modify `pointer-api/README.md` (note language/theme prefs) — optional.

- [ ] **Step 1: Backend running** on :8090 (it auto-applies the new migration on start; db on :5433). Seeded admin `admin@pointer.local` / `ChangeMe123!`.
- [ ] **Step 2: Serve SPA** — `PATH="/opt/homebrew/opt/node@26/bin:$PATH" npm start -- --port 4200` (background).
- [ ] **Step 3: Browser e2e (playwright-cli skill):**
  - Login → header shows language + theme toggles.
  - Toggle **dark/light** → chrome (header/sidebar/content/cards) switches; `html` gets/loses `.dark`.
  - Toggle **Arabic** → nav/labels translate; `html[dir=rtl]`; layout mirrors. Toggle back to EN.
  - Confirm `PATCH /api/me/preferences` fired (network) and DB persisted: re-login (fresh page) → the chosen language+theme are restored from the server (clear localStorage first to prove it's the DB, not local).
  - 0 console errors.
- [ ] **Step 4: Build** — `npm run build` (prod) succeeds.

---

## Self-Review notes

- **Spec coverage:** backend columns+MeResponse+endpoint+validator+migration (T1); Transloco+dicts+models+dark CSS+PreferencesService+wiring (T2); header toggles + translate shell/login/overview (T3); translate roles/users/projects (T4); e2e+persistence verification (T5). All design sections mapped.
- **Type consistency:** `MeResponse.language/theme` (`'ar'|'en'|null` / `'light'|'dark'|null`) consistent across models, PreferencesService, and the backend DTO (`string?`). `UpdatePreferencesRequest` shapes match (FE `{language?,theme?}` ↔ BE `string? Language/Theme`). Endpoint path `/api/me/preferences` consistent (MeController + PreferencesService.persist).
- **DI:** PreferencesService depends only on Api + TranslocoService (no AuthService) to avoid a cycle; AuthService/app.ts call `prefs.init(...)`.
- **Constraint:** no git commits — skip commit steps. Fix the `--brand-tint` value to `#1c2a3f` (typo guard in Task 2 Step 3).
