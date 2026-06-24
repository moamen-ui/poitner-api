# Deferred login, self-signup, and admin approval — Design

**Date:** 2026-06-24
**Status:** Approved (design); pending spec review
**Repo:** `pointer-api` (backend `.NET`, frontend web component `API/wwwroot/pointer.js`, admin dashboard `admin-web` Angular)

## Goal

Let stakeholders start using the Pointer feedback tool without being forced to log in
up front, and let them **self-register** for access. Registrations land in a **pending**
queue that an admin approves or rejects from the dashboard.

Three outcomes:
1. The tool's login popup no longer appears on page load — only when the user acts.
2. Users can sign up (name, email, password, non-admin role) from the popup; rejected
   users can re-apply with a different role.
3. Admins see pending signups on the dashboard landing page and the users page, and
   approve (confirming/changing the role) or reject them.

## Non-goals (YAGNI)

- Email verification / email notifications.
- Password reset / "forgot password".
- OAuth / SSO.
- Self-service profile editing.
- Migrating or changing the old vanilla `wwwroot/admin/` dashboard (superseded by `admin-web`).

## Approach decision

A single **`ApprovalStatus` enum on the existing `User`** (chosen over extra booleans or a
separate `SignupRequest` table). Rejected/pending users must authenticate (password check)
to drive the re-apply flow, so they must be real `User` rows — a separate table would
duplicate the user + password model.

---

## 1. Data model

Add to `Domain/Entity/User.cs`:

```csharp
public ApprovalStatus ApprovalStatus { get; set; } = ApprovalStatus.Approved;
```

New `Domain/Enums/ApprovalStatus.cs`:

```csharp
public enum ApprovalStatus { Approved = 1, Pending = 2, Rejected = 3 }
```

Rules:
- **Admin-created** users (`POST /api/admin/users`) → `Approved` (default preserves current behavior).
- **Self-signup** → `Pending`, `IsActive = false`.
- **Approve** → `Approved`, `IsActive = true`, role = admin's chosen role.
- **Reject** → `Rejected`, `IsActive = false` (record kept).

EF migration: add the `approval_status` column (int, default `1` = Approved so existing rows
stay valid). Generated via `dotnet ef migrations add AddUserApprovalStatus -p Infrastructure -s API`.

---

## 2. Backend API

### Public endpoints (`[AllowAnonymous]`)

**`GET /api/roles`** — roles for the signup / re-apply / rejected-login dropdowns.
Returns active, **non-admin** roles only (`GrantsAdmin == false && IsActive`), shape
`Result<List<{ id, name }>>`. Never exposes admin-granting roles.

**`POST /api/auth/register`** — self-signup AND re-apply (one endpoint).
Request: `{ email, password, displayName, roleId }`.
Logic:
1. Validate `roleId` exists, is active, and is **non-admin** (`GrantsAdmin == false`) — else `BadRequest`.
2. Look up user by normalized email (`DeletedAt == null`).
   - **None** → create `User { Pending, IsActive=false, hashed password, displayName, roleId }`.
   - **Exists & `Rejected` & password matches** → reset to `Pending`, update `RoleId` to the new role (this powers "Request again").
   - **Exists & `Rejected` & password does NOT match** → `BadRequest` (generic invalid — don't leak).
   - **Exists & `Pending` or `Approved`** → `Conflict` ("An account with this email already exists.").
3. Response: `Result` success with message "Request submitted for approval." (no token).

### Auth login changes

**`POST /api/auth/login`** (`AuthService.LoginAsync`) — replace the current
`WHERE IsActive && DeletedAt == null` lookup with: find by email (`DeletedAt == null`),
**verify password first**, then branch (so status is only revealed to correct credentials):

| Condition | Result |
|---|---|
| user not found OR wrong password | `BadRequest` — "Invalid email or password." |
| `ApprovalStatus == Pending` | success=false, `status: "pending"`, msg "Your request is awaiting admin approval." |
| `ApprovalStatus == Rejected` | success=false, `status: "rejected"`, msg "Your request was rejected." |
| `Approved && !IsActive` | success=false, `status: "disabled"`, msg "Your account is disabled." |
| `Approved && IsActive` | success=true, token + `MeResponse`, `status: "ok"` |

The login response envelope gains a `status` string so the web component can branch.
(Implementation: extend `LoginResponse` / the result payload with `Status`; token/user null
unless `ok`.)

### Admin endpoints (`[Authorize(Policy = Policies.Admin)]`)

- **`POST /api/admin/users/{id}/approve`** body `{ roleId }` → validate role exists & active
  (admin MAY pick an admin-granting role here); set `ApprovalStatus=Approved`, `IsActive=true`,
  `RoleId=roleId`. Returns updated `UserResponse`.
- **`POST /api/admin/users/{id}/reject`** → `ApprovalStatus=Rejected`, `IsActive=false`. Returns `UserResponse`.
- **`GET /api/admin/users`** gains optional `?status=pending|approved|rejected` filter.
- **`UserResponse`** gains `approvalStatus` serialized as a **string** (`"Approved"|"Pending"|"Rejected"`) for frontend readability.
- **`StatsResponse.Totals`** gains `pendingUsers` (count of `ApprovalStatus==Pending`, `DeletedAt==null`).

---

## 3. Web component (`API/wwwroot/pointer.js`)

### Deferred login
- `_boot()`: if no token → `renderChrome()` (show toolbar only). **Do not** call `showLoginModal()` on load.
- Login modal is shown only on user action: **+ Comment** (already gated) and opening **Comments**
  (sidebar) when there is no token.
- Login modal gets a **Skip / ✕** control that dismisses it (toolbar remains; no token stored).

### Signup / re-apply in the popup
- Login modal gains a **"Create account"** toggle → signup form: `displayName`, `email`,
  `password`, **role** `<select>` populated from `GET /api/roles`.
- Submit → `POST /api/auth/register` → on success show "Request submitted — an admin will review it."
- **Login response branching** (uses `status`):
  - `pending` / `disabled` → show the message, stay on login.
  - `rejected` → show message + a **role `<select>`** (from `GET /api/roles`) + **"Request again"**
    button → calls `POST /api/auth/register` with the typed email/password + chosen role.
  - `ok` → store token + user, proceed as today.

### Notes
- `GET /api/roles`, `POST /api/auth/register`, and login are all anonymous — no `Authorization`
  header needed for those (the existing `api()` helper adds Bearer; use plain fetch or allow it
  to send no token).

---

## 4. Admin dashboard (`admin-web`, Angular)

### Models / services
- `models.ts`: `UserResponse.approvalStatus`; `StatsTotals.pendingUsers`.
- `users.service.ts`: `list(status?)`, `approve(id, roleId)`, `reject(id)`.

### Overview (`/overview`) — landing page
- A **"Pending approvals"** card: lists pending signups (name, email, requested role, date).
  Each row: **Approve** (opens a small role-confirm `<select>` defaulting to the requested role
  → confirm) and **Reject**. Card header shows the count badge (`totals.pendingUsers`).
- Quick action: approving/rejecting updates the list and the badge without leaving the page.

### Users (`/users`)
- A **Pending** section (or status filter) above/within the table with the same
  Approve (role-confirm) / Reject actions.
- Main table continues to list approved users; rejected users visible via the filter.

---

## 5. Security considerations

- **Self-signup can never request an admin role.** `GET /api/roles` and `POST /api/auth/register`
  both reject/omit `GrantsAdmin` roles. Admin elevation is possible **only** at the admin-driven
  approve step.
- **Status non-disclosure:** login/register reveal pending/rejected status only after a correct
  password, so the endpoints don't leak which emails exist to anonymous guessers.
- Approve/reject endpoints are admin-policy protected.
- Password hashing reuses the existing hasher; pending/rejected users store a real hash.

---

## 6. Acceptance criteria

1. Loading a host page with the tool shows the toolbar but **no** login popup.
2. Clicking **+ Comment** or **Comments** with no token shows the popup; **Skip** dismisses it.
3. Signing up creates a `Pending` user; the user cannot log in and sees the pending message.
4. The pending user appears on `/overview` (count + card) and `/users` (pending filter).
5. Admin **Approve** with a chosen role activates the user (can now log in with that role);
   admin can change the requested role at this step, including granting admin.
6. Admin **Reject** marks the user rejected; on next login they see the rejection message,
   a role dropdown, and a "Request again" button.
7. "Request again" with a new role moves the user back to `Pending` and re-queues them.
8. Existing admin-created users and existing login continue to work unchanged.
9. No anonymous path can obtain an admin-granting role.

## 7. Affected files (orientation, not exhaustive)

- **Backend:** `Domain/Entity/User.cs`, `Domain/Enums/ApprovalStatus.cs` (new),
  `Application/DTOs/Auth/*` (register request, login response `status`),
  `Application/DTOs/User/UserResponse.cs`, `Application/DTOs/Stats/StatsResponse.cs`,
  `Application/Services/Implementation/AuthService.cs`, `UserService.cs`, `StatsService.cs`,
  `API/Controllers/AuthController.cs`, `API/Controllers/RolesController.cs` (new public),
  `API/Controllers/Admin/UsersController.cs`, EF migration.
- **Web component:** `API/wwwroot/pointer.js` (+ `pointer.css` for any new styles).
- **Admin-web:** `core/api/models.ts`, `core/api/users.service.ts`,
  `features/overview/*`, `features/users/*`.
