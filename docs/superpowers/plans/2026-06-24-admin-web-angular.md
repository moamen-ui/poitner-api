# Admin Web (Angular 22) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a standalone Angular 22 admin SPA in `pointer-api/admin-web/` that consumes the Pointer APIs (auth, stats, roles, users, projects), replacing the build-free `wwwroot/admin` dashboard (which stays as a fallback).

**Architecture:** Angular 22 standalone app (signals, new control flow `@if`/`@for`, functional guards/interceptors), Angular Material UI, talking to the API at `environment.apiBase` (default `http://localhost:8090`). CORS is already open server-side. State via signals; HTTP envelope `{isSuccess,message,data}` unwrapped in one helper.

**Tech Stack:** Angular 22, Angular Material 22 + CDK, TypeScript, RxJS, Angular CLI, Karma/Jasmine (default test runner).

## Global Constraints

- App root: `pointer-api/admin-web/` (own `package.json`).
- Standalone components only — NO NgModules.
- API base from `src/environments/environment.ts` `apiBase` (default `http://localhost:8090`); never hardcode URLs in components.
- Every API response is an envelope `{ isSuccess: boolean, message: string|null, data: T }` — unwrap via the shared `Api` helper; surface `message` via `MatSnackBar` on failure.
- Auth: JWT in `localStorage['pointer_admin_token']`, user in `localStorage['pointer_admin_user']`. Admin gate uses the user's `isAdmin` flag.
- Role/status enums on the wire are ints: CommentStatus 1=Open,2=ReadyToApply,3=Applied; Environment 1=Local,2=Staging,3=Production. (Admin app mostly shows names/counts from the API, not these directly.)
- Do NOT modify the .NET backend or the static `wwwroot/admin`.
- NO git commits (work stays in the working tree) — matches the repo's standing constraint. Skip every "commit" step.
- Run all `ng`/`npm` commands from `pointer-api/admin-web/`.

---

## File Structure

```
pointer-api/admin-web/
├── package.json, angular.json, tsconfig*.json   (ng new output)
├── src/
│   ├── main.ts, index.html, styles.scss
│   ├── environments/environment.ts              # { apiBase }
│   └── app/
│       ├── app.config.ts                         # providers: router, HttpClient+interceptor, animations
│       ├── app.routes.ts                          # /login + protected shell children
│       ├── app.component.ts                       # <router-outlet>
│       ├── core/
│       │   ├── api/models.ts                      # typed DTOs
│       │   ├── api/api.ts                          # Api service: get/post/patch → unwrapped data
│       │   ├── api/stats.service.ts
│       │   ├── api/roles.service.ts
│       │   ├── api/users.service.ts
│       │   ├── api/projects.service.ts
│       │   └── auth/{auth.service.ts, auth.interceptor.ts, auth.guard.ts}
│       └── features/
│           ├── login/login.component.ts
│           ├── shell/shell.component.ts
│           ├── overview/overview.component.ts
│           ├── roles/roles.component.ts
│           ├── users/users.component.ts
│           └── projects/projects.component.ts
```

---

## Task 1: Scaffold the Angular app + Material + structure

**Files:** Create the `admin-web/` app (CLI), `src/environments/environment.ts`, `src/app/core/api/models.ts`, `src/app/core/api/api.ts`.

**Interfaces — Produces:**
- `environment = { apiBase: string }`
- `models.ts` types (see code).
- `Api` service: `get<T>(path): Observable<T>`, `post<T>(path, body): Observable<T>`, `patch<T>(path, body): Observable<T>` — each unwraps `data` or throws `Error(message)`.

- [ ] **Step 1: Scaffold (run from `pointer-api/`)**

```bash
cd /Users/momen/Desktop/REPOS/pointer-api
npx -y @angular/cli@latest new admin-web --style=scss --routing=false --ssr=false --skip-git --defaults
cd admin-web
npx -y @angular/cli@latest add @angular/material --theme=azure-blue --typography --animations --skip-confirmation
```
(If `ng add` prompts, the flags above answer them; `--skip-confirmation` accepts the package install.)

- [ ] **Step 2: `src/environments/environment.ts`**

```ts
export const environment = {
  production: false,
  apiBase: 'http://localhost:8090',
};
```

- [ ] **Step 3: `src/app/core/api/models.ts`**

```ts
export interface Envelope<T> { isSuccess: boolean; message: string | null; data: T; }

export interface MeResponse { id: string; email: string; displayName: string; roleId: number; roleName: string; isAdmin: boolean; }
export interface LoginResponse { token: string; user: MeResponse; }

export interface RoleResponse { id: number; name: string; grantsAdmin: boolean; isSystem: boolean; isActive: boolean; }
export interface CreateRoleRequest { name: string; grantsAdmin: boolean; }
export interface UpdateRoleRequest { name?: string; grantsAdmin?: boolean; isActive?: boolean; }

export interface UserResponse { id: number; publicId: string; email: string; displayName: string; roleId: number; roleName: string; isAdmin: boolean; isActive: boolean; }
export interface CreateUserRequest { email: string; password: string; displayName: string; roleId: number; }
export interface UpdateUserRequest { roleId?: number; isActive?: boolean; password?: string; }

export interface ProjectResponse { id: number; key: string; name: string; isActive: boolean; }
export interface CreateProjectRequest { key: string; name: string; }
export interface UpdateProjectRequest { name?: string; isActive?: boolean; }

export interface StatsTotals { projects: number; users: number; comments: number; open: number; pending: number; completed: number; }
export interface ProjectStats { projectId: number; key: string; name: string; isActive: boolean; comments: number; open: number; pending: number; completed: number; }
export interface StatsResponse { totals: StatsTotals; projects: ProjectStats[]; }
```

- [ ] **Step 4: `src/app/core/api/api.ts`**

```ts
import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { map, Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { Envelope } from './models';

@Injectable({ providedIn: 'root' })
export class Api {
  private http = inject(HttpClient);
  private base = environment.apiBase;

  private unwrap<T>(o: Observable<Envelope<T>>): Observable<T> {
    return o.pipe(map((e) => {
      if (!e || e.isSuccess === false) throw new Error((e && e.message) || 'Request failed');
      return e.data;
    }));
  }
  get<T>(path: string): Observable<T> { return this.unwrap(this.http.get<Envelope<T>>(this.base + path)); }
  post<T>(path: string, body: unknown): Observable<T> { return this.unwrap(this.http.post<Envelope<T>>(this.base + path, body)); }
  patch<T>(path: string, body: unknown): Observable<T> { return this.unwrap(this.http.patch<Envelope<T>>(this.base + path, body)); }
}
```

- [ ] **Step 5: Build to verify**

Run: `npm run build`
Expected: build succeeds (Angular + Material wired). Fix any TS errors.

---

## Task 2: Auth (service, interceptor, guard) + app config + routes + login

**Files:** Create `core/auth/auth.service.ts`, `core/auth/auth.interceptor.ts`, `core/auth/auth.guard.ts`, `features/login/login.component.ts`; modify `app.config.ts`, `app.routes.ts`, `app.component.ts`; tests `core/auth/auth.interceptor.spec.ts`, `core/api/api.spec.ts`.

**Interfaces — Consumes:** `Api` (Task 1), `LoginResponse`/`MeResponse`. **Produces:**
- `AuthService`: signals `user: Signal<MeResponse|null>`, `isAuthenticated()`, `isAdmin()`; `login(email,password): Observable<MeResponse>`; `logout(): void`; `token(): string|null`.
- `authInterceptor: HttpInterceptorFn`, `adminGuard: CanActivateFn`.

- [ ] **Step 1: `auth.service.ts`**

```ts
import { computed, inject, Injectable, signal } from '@angular/core';
import { Router } from '@angular/router';
import { map, Observable } from 'rxjs';
import { Api } from '../api/api';
import { LoginResponse, MeResponse } from '../api/models';

const TOKEN_KEY = 'pointer_admin_token';
const USER_KEY = 'pointer_admin_user';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private api = inject(Api);
  private router = inject(Router);
  private _user = signal<MeResponse | null>(this.readUser());
  user = this._user.asReadonly();
  isAuthenticated = computed(() => !!this._user() && !!this.token());
  isAdmin = computed(() => !!this._user()?.isAdmin);

  private readUser(): MeResponse | null { try { return JSON.parse(localStorage.getItem(USER_KEY) || 'null'); } catch { return null; } }
  token(): string | null { return localStorage.getItem(TOKEN_KEY); }

  login(email: string, password: string): Observable<MeResponse> {
    return this.api.post<LoginResponse>('/api/auth/login', { email, password }).pipe(map((res) => {
      localStorage.setItem(TOKEN_KEY, res.token);
      localStorage.setItem(USER_KEY, JSON.stringify(res.user));
      this._user.set(res.user);
      return res.user;
    }));
  }
  logout(): void {
    localStorage.removeItem(TOKEN_KEY); localStorage.removeItem(USER_KEY);
    this._user.set(null); this.router.navigateByUrl('/login');
  }
}
```

- [ ] **Step 2: `auth.interceptor.ts`**

```ts
import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const token = localStorage.getItem('pointer_admin_token');
  const authed = token ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req;
  const router = inject(Router);
  return next(authed).pipe(catchError((err) => {
    if (err?.status === 401) {
      localStorage.removeItem('pointer_admin_token');
      localStorage.removeItem('pointer_admin_user');
      router.navigateByUrl('/login');
    }
    return throwError(() => err);
  }));
};
```

- [ ] **Step 3: `auth.guard.ts`**

```ts
import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from './auth.service';

export const adminGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);
  if (auth.isAuthenticated() && auth.isAdmin()) return true;
  return router.parseUrl('/login');
};
```

- [ ] **Step 4: `app.config.ts`** (provide router, HttpClient with interceptor, animations)

```ts
import { ApplicationConfig, provideZoneChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { routes } from './app.routes';
import { authInterceptor } from './core/auth/auth.interceptor';

export const appConfig: ApplicationConfig = {
  providers: [
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideRouter(routes),
    provideHttpClient(withInterceptors([authInterceptor])),
    provideAnimationsAsync(),
  ],
};
```
> Note: keep whatever `app.config.ts` `ng new` produced and merge these providers; if the generated file differs (e.g. `provideBrowserGlobalErrorListeners`), keep those too.

- [ ] **Step 5: `app.routes.ts`** (shell built in Task 3; for now point shell children at a placeholder is unnecessary — define login + a shell route with the overview child added in Task 3)

```ts
import { Routes } from '@angular/router';
import { adminGuard } from './core/auth/auth.guard';

export const routes: Routes = [
  { path: 'login', loadComponent: () => import('./features/login/login.component').then(m => m.LoginComponent) },
  {
    path: '',
    canActivate: [adminGuard],
    loadComponent: () => import('./features/shell/shell.component').then(m => m.ShellComponent),
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'overview' },
      { path: 'overview', loadComponent: () => import('./features/overview/overview.component').then(m => m.OverviewComponent) },
    ],
  },
  { path: '**', redirectTo: '' },
];
```
> Tasks 4–6 append `roles`, `users`, `projects` children here.

- [ ] **Step 6: `app.component.ts`** — ensure it is just `<router-outlet />` (standalone, imports `RouterOutlet`). Replace the CLI starter template.

- [ ] **Step 7: `login.component.ts`** — Material card + reactive form (email, password), calls `auth.login`, on success navigates to `/overview`, rejects non-admin (`if (!user.isAdmin) show error + logout`), shows error via `MatSnackBar`. Use `MatCard`, `MatFormField`, `MatInput`, `MatButton`, `ReactiveFormsModule`.

```ts
import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatSnackBar } from '@angular/material/snack-bar';
import { AuthService } from '../../core/auth/auth.service';

@Component({
  selector: 'app-login',
  standalone: true,
  imports: [ReactiveFormsModule, MatCardModule, MatFormFieldModule, MatInputModule, MatButtonModule],
  template: `
    <div class="login-wrap">
      <mat-card class="login-card">
        <h1>Pointer Admin</h1>
        <form [formGroup]="form" (ngSubmit)="submit()">
          <mat-form-field appearance="outline"><mat-label>Email</mat-label>
            <input matInput type="email" formControlName="email" /></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Password</mat-label>
            <input matInput type="password" formControlName="password" /></mat-form-field>
          <button mat-flat-button color="primary" [disabled]="form.invalid || loading()">Sign in</button>
        </form>
      </mat-card>
    </div>`,
  styles: [`.login-wrap{min-height:100vh;display:flex;align-items:center;justify-content:center;background:#f1f5f9}
    .login-card{width:360px;max-width:92vw;padding:24px;display:flex;flex-direction:column;gap:8px}
    form{display:flex;flex-direction:column;gap:8px} button{margin-top:8px}`],
})
export class LoginComponent {
  private fb = inject(FormBuilder);
  private auth = inject(AuthService);
  private router = inject(Router);
  private snack = inject(MatSnackBar);
  loading = signal(false);
  form = this.fb.nonNullable.group({ email: ['', [Validators.required, Validators.email]], password: ['', Validators.required] });

  submit() {
    if (this.form.invalid) return;
    this.loading.set(true);
    const { email, password } = this.form.getRawValue();
    this.auth.login(email, password).subscribe({
      next: (user) => {
        this.loading.set(false);
        if (!user.isAdmin) { this.snack.open('This account is not an admin.', 'OK', { duration: 4000 }); this.auth.logout(); return; }
        this.router.navigateByUrl('/overview');
      },
      error: (e) => { this.loading.set(false); this.snack.open(e.message || 'Login failed', 'OK', { duration: 4000 }); },
    });
  }
}
```

- [ ] **Step 8: Failing tests** — `api.spec.ts` (envelope unwrap throws on `isSuccess:false`, returns data on success, using `HttpTestingController`) and `auth.interceptor.spec.ts` (adds `Authorization` header when token present). Write them.
- [ ] **Step 9: Run tests** — `npm test -- --watch=false --browsers=ChromeHeadless`. Expected: the two specs pass (and the CLI default `app.component.spec` may need its title assertion relaxed/removed since we replaced the template — fix it).
- [ ] **Step 10: Build** — `npm run build`. Expected: success.

---

## Task 3: Shell + Overview

**Files:** Create `features/shell/shell.component.ts`, `features/overview/overview.component.ts`, `core/api/stats.service.ts`.

**Interfaces — Consumes:** `Api`, `AuthService`, `StatsResponse`. **Produces:** `StatsService.get(): Observable<StatsResponse>`; `ShellComponent` (layout with `<router-outlet>`); `OverviewComponent`.

- [ ] **Step 1: `stats.service.ts`**

```ts
import { inject, Injectable } from '@angular/core';
import { Api } from './api';
import { StatsResponse } from './models';
@Injectable({ providedIn: 'root' })
export class StatsService { private api = inject(Api); get() { return this.api.get<StatsResponse>('/api/admin/stats'); } }
```

- [ ] **Step 2: `shell.component.ts`** — `mat-toolbar` (brand "Pointer Admin", spacer, `auth.user()?.displayName · roleName`, "Sign out" button → `auth.logout()`), `mat-sidenav-container` with a `mat-nav-list` (links: Overview `/overview`, Roles `/roles`, Users `/users`, Projects `/projects` using `routerLink` + `routerLinkActive`), content holds `<router-outlet>`. Imports: `MatToolbarModule, MatSidenavModule, MatListModule, MatIconModule, MatButtonModule, RouterOutlet, RouterLink, RouterLinkActive`.

- [ ] **Step 3: `overview.component.ts`** — on init call `StatsService.get()`, store in a signal; render 6 `mat-card` stat tiles (Projects, Users, Comments, Open, Pending, Completed) using `@if (stats())`; a `mat-table` (or `table mat-table`) for `stats().projects` with columns key, comments, open, pending, completed, status; a Refresh button re-fetches; errors via snackbar. Use `MatCardModule, MatTableModule, MatButtonModule, MatProgressBarModule`.

- [ ] **Step 4: Build + manual check** — `npm run build` (success). (Live verification happens in Task 7 against the running API.)

---

## Task 4: Roles feature

**Files:** Create `features/roles/roles.component.ts`, `core/api/roles.service.ts`; modify `app.routes.ts` (add `roles` child).

**Interfaces — Consumes:** `Api`, `RoleResponse`, `CreateRoleRequest`, `UpdateRoleRequest`. **Produces:** `RolesService { list(): Observable<RoleResponse[]>; create(r): Observable<RoleResponse>; update(id, r): Observable<RoleResponse>; }`.

- [ ] **Step 1: `roles.service.ts`**

```ts
import { inject, Injectable } from '@angular/core';
import { Api } from './api';
import { CreateRoleRequest, RoleResponse, UpdateRoleRequest } from './models';
@Injectable({ providedIn: 'root' })
export class RolesService {
  private api = inject(Api);
  list() { return this.api.get<RoleResponse[]>('/api/admin/roles'); }
  create(r: CreateRoleRequest) { return this.api.post<RoleResponse>('/api/admin/roles', r); }
  update(id: number, r: UpdateRoleRequest) { return this.api.patch<RoleResponse>(`/api/admin/roles/${id}`, r); }
}
```

- [ ] **Step 2: `roles.component.ts`** — `mat-table` of roles: columns Name (+ a `system` chip when `isSystem`), Grants admin (`mat-slide-toggle` bound to `grantsAdmin`, disabled when `isSystem`, change → `update(id,{grantsAdmin})`), Status (Active/Disabled chip), Actions (for non-system: "Rename" → `MatDialog` or `window.prompt` then `update(id,{name})`; Disable/Enable → `update(id,{isActive:!})`). An "Add role" form above the table (name input + grants-admin checkbox + Add button → `create`). Reload list after each mutation. Snackbar on error. Imports: `MatTableModule, MatSlideToggleModule, MatButtonModule, MatInputModule, MatFormFieldModule, MatCheckboxModule, MatChipsModule, FormsModule/ReactiveFormsModule`.
- [ ] **Step 3: Add route** in `app.routes.ts` children: `{ path: 'roles', loadComponent: () => import('./features/roles/roles.component').then(m => m.RolesComponent) }`.
- [ ] **Step 4: Build** — `npm run build`. Expected: success.

---

## Task 5: Users feature

**Files:** Create `features/users/users.component.ts`, `core/api/users.service.ts`; modify `app.routes.ts`.

**Interfaces — Consumes:** `Api`, `RolesService` (for the role dropdown), `UserResponse`, `CreateUserRequest`, `UpdateUserRequest`. **Produces:** `UsersService { list(); create(u); update(id,u); }` (paths `/api/admin/users`).

- [ ] **Step 1: `users.service.ts`** — same shape as roles.service, endpoints `/api/admin/users`, types `UserResponse`/`CreateUserRequest`/`UpdateUserRequest`.
- [ ] **Step 2: `users.component.ts`** — load users (`UsersService.list()`) and roles (`RolesService.list()` for the dropdown). `mat-table`: Email, Name, Role (`mat-select` of active roles bound to `roleId`, change → `update(id,{roleId})`), Status chip, Actions (Disable/Enable → `update(id,{isActive:!})`). "Add user" form: email, display name, password, role `mat-select` → `create`. Reload after mutations; snackbar on error.
- [ ] **Step 3: Add route** `{ path: 'users', loadComponent: () => import('./features/users/users.component').then(m => m.UsersComponent) }`.
- [ ] **Step 4: Build** — `npm run build`. Expected: success.

---

## Task 6: Projects feature

**Files:** Create `features/projects/projects.component.ts`, `core/api/projects.service.ts`; modify `app.routes.ts`.

**Interfaces — Consumes:** `Api`, `ProjectResponse`, `CreateProjectRequest`, `UpdateProjectRequest`. **Produces:** `ProjectsService { list(); create(p); update(id,p); }` (paths `/api/admin/projects`).

- [ ] **Step 1: `projects.service.ts`** — endpoints `/api/admin/projects`.
- [ ] **Step 2: `projects.component.ts`** — `mat-table`: Key (`<code>`), Name, Status chip, Actions (Disable/Enable → `update(id,{isActive:!})`). "Add project" form: key, name → `create`. Reload after mutations; snackbar on error.
- [ ] **Step 3: Add route** `{ path: 'projects', loadComponent: () => import('./features/projects/projects.component').then(m => m.ProjectsComponent) }`.
- [ ] **Step 4: Build** — `npm run build`. Expected: success.

---

## Task 7: Verify (browser e2e) + README

**Files:** Modify `pointer-api/README.md` (admin-web run section); create `pointer-api/admin-web/README.md` (optional, ng default is fine).

- [ ] **Step 1: Ensure backend running** — pointer-api on `:8090` (db on `:5433`). Seeded admin: `admin@pointer.local` / `ChangeMe123!`.
- [ ] **Step 2: Serve the app** — from `admin-web/`: `npm start -- --port 4200` (background).
- [ ] **Step 3: Browser e2e (playwright-cli skill)** — navigate `http://localhost:4200`:
  - Login as the seeded admin → lands on Overview; stat cards + per-project table render.
  - Roles: add a role (e.g. "Designer2"), toggle grants-admin, rename, disable → verify table updates.
  - Users: add a user with a role; change a user's role via the select; disable/enable.
  - Projects: disable then enable a project.
  - Sign out → back to login.
  - Assert **0 console errors** (ignore favicon 404).
- [ ] **Step 4: Build** — `npm run build`. Expected: success (production bundle).
- [ ] **Step 5: README** — add an "Admin Web (Angular)" section to `pointer-api/README.md`: `cd admin-web && npm install && npm start` → http://localhost:4200; note it talks to `apiBase` (`:8090`) and uses the same admin login.

---

## Self-Review notes

- **Spec coverage:** scaffold+Material (T1), auth/interceptor/guard/login (T2), shell+overview/stats (T3), roles (T4), users (T5), projects (T6), verify+README+browser e2e+unit tests (T2 tests, T7 e2e). All design sections mapped.
- **Type consistency:** model names (`RoleResponse`, `UserResponse`, `ProjectResponse`, `StatsResponse`, `MeResponse`, request DTOs) used identically across services and components; service method names `list/create/update` consistent; `Api.get/post/patch` signatures stable.
- **No backend changes** — the API already exposes every endpoint used (`/api/auth/login`, `/api/auth/me`, `/api/admin/{stats,roles,users,projects}`).
- **Constraint:** no git commits — skip the commit steps the sub-skill would otherwise insert.
