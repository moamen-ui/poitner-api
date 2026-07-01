# Demo-to-Permanent Upgrade Implementation Plan

> **STATUS: PENDING.** Branch `feat/demo-upgrade` in the API worktree. Dashboards in `pointer-dashboard` (read-only during plan; UI changes tracked here). Do NOT implement feature code until the user approves the plan.

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`). Implementer tags: **[Claude]** = security-critical, implement closely; **[GLM]** = mechanical/low-risk, delegate to opencode + GLM-5.2 in isolated worktree with controller review.

---

## Goal

Allow a user who arrived via the one-click demo ("Try the demo") to convert their ephemeral demo workspace into a permanent registered account — without losing any data. After upgrade they are an ordinary scoped-admin tenant (same `PublicId`, same project, same comments) with a real email, real password, and no expiry.

---

## Key decisions (made here; implementer does not re-decide)

### Decision 1 — Provisioning email persistence

**Problem:** `DemoService.ProvisionAsync` accepts `recipientEmail` (the real human email, used only to send credentials). Today it is NOT stored on the `User` row — `User.Email` is set to the throwaway slug `demo-xxxx@demo.pointer`. The provisioning email exists only in the outgoing Brevo send and the throttle key in `app_settings`.

**Decision: store the provisioning email on `User` at provisioning time** as a new nullable column `User.RecipientEmail`. Rationale: (a) pre-fills the upgrade form so the user does not have to re-type their email after a 24-hour session; (b) lets the upgrade endpoint validate that the chosen email is the same address that requested the demo (optional UX check — see Task U1); (c) zero migration risk (nullable column). The field is read-only after provisioning.

### Decision 2 — Approval policy after upgrade

**Problem:** Normal `RegisterAdminAsync` creates a user with `ApprovalStatus = Pending` and `IsActive = false`, requiring a super-admin to approve. Demo users are already `Approved + IsActive = true` (their workspace is live). Should upgrade require re-approval?

**Decision: auto-approve (keep `ApprovalStatus = Approved`, `IsActive = true`).** Rationale: the user's workspace is already running and data is already there. Requiring approval would lock them out of their own active workspace mid-session, which is bad UX. The only change is swapping the fake demo credentials for real ones. The super-admin retains the ability to disable the account afterwards if needed (existing `UserService` mechanism). The upgrade endpoint does NOT check `ScopedAdminSignupEnabled` — the demo was already approved by provisioning.

### Decision 3 — Endpoint placement

`POST /api/demo/upgrade` on the existing `DemoController`. Authenticated (`[Authorize]`), guarded so only `IsDemo = true` callers may use it. Does NOT need a dedicated `DemoUpgradePolicy` — the guard logic sits in `DemoService.UpgradeAsync`, which validates `IsDemo` on the resolved user.

### Decision 4 — Token reissue

After upgrade the `email` claim in the JWT changes from `demo-xxxx@demo.pointer` to the real email. The endpoint returns a fresh token + a `MeResponse` in the same shape as `LoginResponse` (reuse that DTO). The dashboard replaces `localStorage["pointer_admin_token"]` and `localStorage["pointer_admin_user"]` exactly as `login()` does. The old demo token becomes invalid naturally when the dashboard swaps it; no server-side token revocation is needed (JWT is stateless; the old token's lifetime is at most 12 h and the old email is no longer in the DB).

### Decision 5 — What "upgrade in place" means

Flip on the demo user entity:
- `IsDemo = false`
- `ExpiresAt = null`
- `DemoExtended = false` (reset — no longer relevant)
- `DemoCommentCapOverride = null` (reset — permanent users have no cap)
- `DemoTtlHoursOverride = null` (reset)
- `Email = <chosen real email>` (normalize to lower)
- `PasswordHash = hash(<chosen password>)`
- `DisplayName = <chosen displayName ?? existing "Demo User">`
- `RecipientEmail = null` (clear — no longer needed)

No other rows change. All `Project`, `Comment`, `Reply`, `Role`, `StatusPresentation` rows that are stamped `OwnerId = user.PublicId` carry over for free — the tenant `PublicId` does not change.

### Decision 6 — Comment cap stops applying automatically

Confirmed by reading `CommentService.CreateAsync` (lines 52-72): the cap guard fetches the project owner and checks `demoOwner != null` where `demoOwner` is the user row `WHERE PublicId == owner AND IsDemo = true`. After `IsDemo = false` the query returns null and the cap is skipped. No extra code needed.

### Decision 7 — DemoCleanupService does not delete upgraded users

Confirmed by reading `DemoCleanupService.SweepAsync`: it only hard-deletes users where `IsDemo = true AND ExpiresAt < now`. After upgrade `IsDemo = false`, so the cleanup job ignores the row.

---

## Architecture diagram (state change)

```
Before upgrade:
  User { IsDemo=true, ExpiresAt=T+24h, Email="demo-xxxx@demo.pointer",
          PasswordHash=<random>, DisplayName="Demo User", OwnerId=PublicId,
          ApprovalStatus=Approved, IsActive=true, RecipientEmail="real@user.com" }

After upgrade:
  User { IsDemo=false, ExpiresAt=null, Email="real@user.com",
          PasswordHash=hash("chosen"), DisplayName="Chosen Name", OwnerId=PublicId,
          ApprovalStatus=Approved, IsActive=true, RecipientEmail=null }
  (all Projects/Comments/Replies unchanged — OwnerId still = PublicId)
```

---

## Global constraints

- Branch: `feat/demo-upgrade` (API worktree). Dashboard changes go on the same-named branch in `pointer-dashboard`.
- Every backend task: `dotnet build` + `dotnet test` (existing tests must stay green). No new failures.
- Security invariant: the endpoint MUST reject callers whose `IsDemo = false` (ordinary users must not accidentally call it). Guard in service layer, not just controller.
- Email uniqueness: the chosen real email must not already exist on any other non-demo, non-deleted user (`IgnoreQueryFilters()`, exclude the caller's own row).
- Do NOT deploy until the user approves.

---

## Task U1 [Claude]: Add `RecipientEmail` column to `User` entity + migration

**Files:**
- `Domain/Entity/User.cs` — add `public string? RecipientEmail { get; set; }`
- `Infrastructure/Mappings/UserMapping.cs` — map `recipient_email` (snake_case, nullable text)
- Run: `dotnet ef migrations add AddUserRecipientEmail -p Infrastructure -s API`
- `Infrastructure/Migrations/AddUserRecipientEmail.cs` (generated)

**Steps:**
- [ ] Add `string? RecipientEmail` to `User.cs` with a `/// <summary>` doc comment: "The real human email entered at demo provisioning time. Null for non-demo users. Cleared on upgrade."
- [ ] In `Infrastructure/Mappings/UserMapping.cs`, add `b.Property(u => u.RecipientEmail).HasColumnName("recipient_email");` (nullable text, no index needed).
- [ ] Generate migration; inspect it — confirm it adds a single nullable column with no data loss. `dotnet build`.
- [ ] Commit: `feat(api): add User.RecipientEmail for demo provisioning email pre-fill`.

---

## Task U2 [Claude]: Store provisioning email in `DemoService.ProvisionAsync`

**Files:**
- `Application/Services/Implementation/DemoService.cs`

**Steps:**
- [ ] In the `demoUser` initialiser (around line 83), add `RecipientEmail = recipientEmail,` (the already-validated `recipientEmail` string).
- [ ] `dotnet build`.
- [ ] Manual verification: provision a demo via `POST /api/demo`, then query the DB — `recipient_email` should be populated.
- [ ] Commit: `feat(api): persist provisioning email on demo user for upgrade pre-fill`.

---

## Task U3 [Claude]: `UpgradeAsync` service method + DTOs + validator (TDD)

**Files (new):**
- `Application/DTOs/Demo/UpgradeDemoRequest.cs`
- `Application/DTOs/Demo/UpgradeDemoResponse.cs`
- `Application/Validators/UpgradeDemoValidator.cs`

**Files (modified):**
- `Application/Services/Interfaces/IDemoService.cs` — add `UpgradeAsync`
- `Application/Services/Implementation/DemoService.cs` — implement `UpgradeAsync`
- `Application/Resources/MessageKeys.cs` — add `Demo` static class with upgrade messages

### DTOs

```csharp
// Application/DTOs/Demo/UpgradeDemoRequest.cs
public class UpgradeDemoRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
}

// Application/DTOs/Demo/UpgradeDemoResponse.cs
// Same shape as LoginResponse so the dashboard can use the same token-swap path.
public class UpgradeDemoResponse
{
    public string Token { get; set; } = string.Empty;
    public MeResponse User { get; set; } = null!;
}
```

### Validator

```csharp
// Application/Validators/UpgradeDemoValidator.cs
public class UpgradeDemoValidator : AbstractValidator<UpgradeDemoRequest>
{
    public UpgradeDemoValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty().MinimumLength(8)
            .WithMessage(MessageKeys.User.PasswordWeak);
    }
}
```

### MessageKeys additions

```csharp
// In MessageKeys.cs, add:
public static class Demo
{
    public const string NotDemoUser = "This account is not a demo account.";
    public const string AlreadyUpgraded = "This demo has already been upgraded to a permanent account.";
    public const string DemoExpired = "This demo session has expired. Please start a new demo.";
    public const string EmailTaken = "That email is already registered.";
    public const string UpgradeSuccess = "Your workspace has been upgraded. Welcome to Pointer!";
}
```

### `IDemoService` addition

```csharp
/// <summary>
/// Converts an ephemeral demo user (IsDemo=true) into a permanent registered
/// account in place. All tenant data (projects, comments) is preserved.
/// Returns a fresh JWT + MeResponse on success.
/// </summary>
Task<Result<UpgradeDemoResponse>> UpgradeAsync(Guid callerPublicId, UpgradeDemoRequest request);
```

### `DemoService.UpgradeAsync` implementation logic

1. Validate request via `UpgradeDemoValidator` (inline, not auto-injected).
2. Load caller: `IgnoreQueryFilters()` WHERE `PublicId == callerPublicId AND DeletedAt == null`. If null → `Result.NotFound(MessageKeys.User.NotFound)`.
3. Guard: if `!user.IsDemo` → `Result.Forbidden(MessageKeys.Demo.NotDemoUser)`.
4. Guard: if `user.ExpiresAt != null && user.ExpiresAt < DateTime.UtcNow` → `Result.Failure(MessageKeys.Demo.DemoExpired)`.
5. Email uniqueness: `IgnoreQueryFilters()`, case-insensitive, `Email == emailNormalized AND PublicId != callerPublicId AND DeletedAt == null` → if found → `Result.Conflict(MessageKeys.Demo.EmailTaken)`.
6. Mutate the user entity in place (all fields listed in Decision 5).
7. `_unitOfWork.Repository<User>().Update(user)`.
8. `await _unitOfWork.SaveChangesAsync()`.
9. Re-load the `Role` navigation (needed for JWT claims): `Include(u => u.Role)` OR assign from the already-tracked role entity.
10. Issue fresh token: `_tokenService.Issue(user)`.
11. Return `Result<UpgradeDemoResponse>.Success(new UpgradeDemoResponse { Token = token, User = MapToMeResponse(user) }, MessageKeys.Demo.UpgradeSuccess)`.

`MapToMeResponse` can be reused from `AuthService` — extract it to a shared static helper in `Application/Common/UserMapper.cs`, or inline a copy. Prefer extracting to avoid duplication.

**Steps:**
- [ ] Write `UpgradeDemoRequest.cs`, `UpgradeDemoResponse.cs`, `UpgradeDemoValidator.cs`.
- [ ] Add `Demo` static class to `MessageKeys.cs`.
- [ ] Add `UpgradeAsync` to `IDemoService.cs`.
- [ ] Implement `UpgradeAsync` in `DemoService.cs`.
- [ ] `dotnet build`. No tests yet — tests in U4.
- [ ] Commit: `feat(api): UpgradeAsync service — in-place demo-to-permanent conversion`.

---

## Task U4 [Claude]: Unit tests for `UpgradeAsync` (TDD complement)

**Files (new):**
- `Tests/DemoUpgradeTests.cs`

Write xUnit tests covering:
1. Happy path: demo user + valid request → returns success with `IsDemo=false` on updated entity and fresh token claims with real email.
2. Caller is not a demo user (`IsDemo=false`) → returns `IsForbidden`.
3. Caller's demo is expired (`ExpiresAt < now`) → returns `IsSuccess=false` with `DemoExpired` message.
4. Chosen email already taken by another user → returns `IsConflict`.
5. Password too short (< 8 chars) → returns `IsSuccess=false` with validator message.
6. Empty email → returns `IsSuccess=false` with validator message.

Use the same mocking pattern as existing tests (instantiate real validators; mock `IUnitOfWork` via the `IRepository<T>` abstraction if needed, or use in-memory EF). Keep tests pure unit — no DB.

**Steps:**
- [ ] Write `DemoUpgradeTests.cs`.
- [ ] `dotnet test` — all existing + new tests green.
- [ ] Commit: `test(api): DemoUpgradeTests — 6 cases covering upgrade service logic`.

---

## Task U5 [Claude]: `POST /api/demo/upgrade` controller action

**Files:**
- `API/Controllers/DemoController.cs`

Add:

```csharp
[Authorize]
[HttpPost("upgrade")]
[ProducesResponseType(typeof(Result<UpgradeDemoResponse>), StatusCodes.Status200OK)]
[ProducesResponseType(typeof(Result), StatusCodes.Status400BadRequest)]
[ProducesResponseType(typeof(Result), StatusCodes.Status403Forbidden)]
[ProducesResponseType(typeof(Result), StatusCodes.Status409Conflict)]
public async Task<IActionResult> Upgrade([FromBody] UpgradeDemoRequest request)
{
    var callerId = User.GetId();  // ClaimsPrincipalExtensions.GetId()
    var result = await demoService.UpgradeAsync(callerId, request);
    if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
    if (result.IsConflict) return Conflict(result);
    if (result.IsNotFound) return NotFound(result);
    return result.IsSuccess ? Ok(result) : BadRequest(result);
}
```

No rate limiter needed here (the endpoint is authenticated; each demo user can call it once effectively, since the second call hits the `IsDemo=false` guard and returns 403).

**Steps:**
- [ ] Add the action to `DemoController`.
- [ ] `dotnet build`.
- [ ] Manual curl smoke test: provision a demo → `POST /api/demo/upgrade` with valid body + demo JWT → 200 + fresh token; repeat call → 403. Also test with a regular user token → 403.
- [ ] Commit: `feat(api): POST /api/demo/upgrade endpoint`.

---

## Task U6 [GLM]: Regenerate API clients (add `UpgradeDemoRequest`, `UpgradeDemoResponse`, new action)

**Files:**
- `orval.config.ts` — confirm the `Demo` tag already exists (it does, from Phase 2 Task D5); no new tag needed
- Run `npm run generate-clients` (which runs `scripts/generate-clients.mjs`)
- Verify the generated client packages (`@moamen-ui/pointer-angular`, `@moamen-ui/pointer-react`, `@moamen-ui/pointer-vue`) expose:
  - `postApiDemoUpgrade` / `usePostApiDemoUpgrade` hook (all three frameworks)
  - `UpgradeDemoRequest` model
  - `UpgradeDemoResponse` model
- `npm run build-clients` to build the packages

**Steps:**
- [ ] `npm run generate-clients` from the repo root.
- [ ] Inspect the generated output in `scripts/` or whichever output dir the config specifies; confirm the three symbols above are present in all three packages.
- [ ] `npm run build-clients`.
- [ ] Commit: `chore(clients): regenerate clients — add postApiDemoUpgrade + DTOs`.

---

## Task U7 [Claude]: Angular dashboard — "Keep this workspace" CTA + upgrade form

**Files (new):**
- `angular/src/app/features/shell/upgrade-demo-dialog.component.ts`

**Files (modified):**
- `angular/src/app/features/shell/demo-panel.component.ts` — add "Keep this workspace" button that opens the dialog
- `angular/public/assets/i18n/en.json` — add `upgrade.*` keys
- `angular/public/assets/i18n/ar.json` — add Arabic equivalents

### What to build

A Material Dialog (`MatDialog`) component that contains:
- A heading ("Keep this workspace" / "احتفظ بهذه البيئة")
- Short description ("Upgrade to a permanent account — all your comments and projects carry over.")
- Input: **Email** (pre-filled from `session.recipientEmail` if present in sessionStorage, or blank)
- Input: **Password** (min 8 chars)
- Input: **Display name** (optional, pre-filled from `"Demo User"`)
- Submit button (calls `DemoService.postApiDemoUpgrade` with the demo JWT)
- Error display
- On success: swap token + user via `auth.loginWithToken(response.token)`, clear `sessionStorage["pointer_demo"]`, navigate to `/overview`, show a snack "Welcome to Pointer!"

### `DemoSession` interface extension

The `DemoSession` interface in `demo-panel.component.ts` does not currently include `recipientEmail`. It should be stored from the provisioning response. However, `DemoSessionResponse` does not today contain `recipientEmail` — the API response does not expose it for security (the user sees it in their inbox). The upgrade form therefore collects the email fresh, with the field blank. (Optional UX improvement in a follow-up: the server could echo the masked email like `r***@example.com` in the response, but that is out of scope here.)

### Integration point in `DemoPanelComponent`

Add a "Keep this workspace / Upgrade" button after the setup guide slider, visible only while `session` is active and `!expired`. Clicking it opens `UpgradeDemoDiaogComponent` via `MatDialog.open(...)`.

### i18n keys (en)

```json
"upgrade": {
  "cta": "Keep this workspace",
  "title": "Upgrade to a permanent account",
  "description": "All your comments, projects, and settings carry over — free.",
  "emailLabel": "Email",
  "emailPlaceholder": "your@email.com",
  "passwordLabel": "Password",
  "passwordPlaceholder": "Min. 8 characters",
  "displayNameLabel": "Display name (optional)",
  "submit": "Create permanent account",
  "success": "Welcome to Pointer! Your workspace is now permanent.",
  "emailTaken": "That email is already registered.",
  "notDemo": "This account cannot be upgraded.",
  "expired": "This demo has expired. Please start a new one.",
  "error": "Upgrade failed. Please try again."
}
```

### i18n keys (ar)

```json
"upgrade": {
  "cta": "احتفظ بهذه البيئة",
  "title": "ترقية إلى حساب دائم",
  "description": "تعليقاتك ومشاريعك وإعداداتك كلها ستنتقل معك — مجاناً.",
  "emailLabel": "البريد الإلكتروني",
  "emailPlaceholder": "your@email.com",
  "passwordLabel": "كلمة المرور",
  "passwordPlaceholder": "8 أحرف على الأقل",
  "displayNameLabel": "الاسم المعروض (اختياري)",
  "submit": "إنشاء حساب دائم",
  "success": "مرحباً بك في Pointer! أصبحت بيئتك دائمة الآن.",
  "emailTaken": "هذا البريد الإلكتروني مسجّل مسبقاً.",
  "notDemo": "لا يمكن ترقية هذا الحساب.",
  "expired": "انتهت صلاحية النسخة التجريبية. يرجى بدء نسخة جديدة.",
  "error": "فشل الترقية. يرجى المحاولة مرة أخرى."
}
```

**Steps:**
- [ ] Create `upgrade-demo-dialog.component.ts` with the form, `DemoService` injection, and post-upgrade token-swap logic.
- [ ] Add "Keep this workspace" button to `demo-panel.component.ts` (inject `MatDialog`).
- [ ] Add i18n keys to both `en.json` and `ar.json`.
- [ ] `npm run build` (local-link client if needed; revert before commit).
- [ ] Browser-test: start a demo, open the upgrade dialog, fill valid data, submit, confirm the page reloads as a permanent user (no demo panel, normal session).
- [ ] Commit: `feat(angular): demo upgrade dialog + "Keep this workspace" CTA`.

---

## Task U8 [GLM]: React dashboard — "Keep this workspace" CTA + upgrade form

**Files (new):**
- `react/src/features/demo/UpgradeDemoDialog.tsx`

**Files (modified):**
- `react/src/components/DemoPanel.tsx` — add CTA button
- `react/public/assets/i18n/en.json` — add `upgrade.*` keys (same content as Angular U7)
- `react/public/assets/i18n/ar.json` — add Arabic keys

### Implementation pattern

Use the existing shadcn/ui `Dialog` component (already at `react/src/components/ui/dialog.tsx`). The dialog is opened by a `useState<boolean>` in `DemoPanel`. On success, replicate the post-demo-login token-swap from `LoginPage.tsx` (lines 90-117): `setItem(TOKEN_KEY, token)`, `setAuthHeader(token)`, `setItem(USER_KEY, JSON.stringify(me))`, then `window.location.assign('/overview')`. Also remove `sessionStorage["pointer_demo"]`.

Use `usePostApiDemoUpgrade` hook (from `@moamen-ui/pointer-react`, generated in U6).

**Steps:**
- [ ] Create `UpgradeDemoDialog.tsx`.
- [ ] Add CTA button to `DemoPanel.tsx`.
- [ ] Add i18n keys to both JSON files.
- [ ] `npm run build`.
- [ ] Commit: `feat(react): demo upgrade dialog + "Keep this workspace" CTA`.

---

## Task U9 [GLM]: Vue dashboard — "Keep this workspace" CTA + upgrade form

**Files (new):**
- `vue/src/features/demo/UpgradeDemoDialog.vue`

**Files (modified):**
- `vue/src/features/shell/DemoPanel.vue` — add CTA button
- `vue/public/assets/i18n/en.json` — add `upgrade.*` keys
- `vue/public/assets/i18n/ar.json` — add Arabic keys

### Implementation pattern

Use the existing `Dialog` component family at `vue/src/components/ui/dialog/`. The dialog open/close state is a `ref<boolean>` in `DemoPanel.vue`. On success, use the `loginWithToken` function from `useAuth` (already exists in `vue/src/composables/useAuth.ts` — it persists the token, sets the auth header, and fetches `/api/auth/me`) → then `clearDemoSession()` (already imported in `DemoPanel.vue`) and `router.push('/overview')`.

Use `postApiDemoUpgrade` (plain function from `@moamen-ui/pointer-vue`, generated in U6).

**Steps:**
- [ ] Create `UpgradeDemoDialog.vue`.
- [ ] Add CTA button to `DemoPanel.vue`.
- [ ] Add i18n keys to both JSON files.
- [ ] `npm run build`.
- [ ] Commit: `feat(vue): demo upgrade dialog + "Keep this workspace" CTA`.

---

## Task U10 [Claude]: Edge-case and integration verification

Run through every error path manually (or via curl) to confirm the guard rails work end-to-end.

**Checklist:**

- [ ] **Already-upgraded (double-submit):** call `POST /api/demo/upgrade` twice with the same demo JWT. First call succeeds and returns a new permanent token. Second call: the demo JWT still has `IsDemo=true` in claims, but after the first upgrade the DB row has `IsDemo=false`. The service loads the user from DB by `callerPublicId` and checks the DB value → second call returns `IsForbidden` with `NotDemoUser`. Confirm 403.

- [ ] **Expired demo:** provision a demo, manually set `expires_at` to the past in the DB, call upgrade → 400 with `DemoExpired`. Confirm the cleanup job does not race here (upgrade check happens before cleanup sweep; even if it does run concurrently, after hard-delete the user row is gone and the endpoint returns `NotFound`).

- [ ] **Email already in use:** provision two demos; upgrade the first with `email=shared@test.com`; try to upgrade the second with the same email → 409 with `EmailTaken`. Confirm the uniqueness check uses `IgnoreQueryFilters()` so the cross-tenant check works correctly (the existing `users(email)` partial unique index on `WHERE owner_id IS NULL` is for global users; the upgrade query must check ALL tenants).

- [ ] **Concurrent upgrade calls:** two requests for the same demo user hit the endpoint simultaneously. The `Email` uniqueness check is done before `SaveChangesAsync`. The DB has a partial unique index on `email WHERE owner_id IS NULL` for global users — after upgrade, the row's `owner_id` stays the same (the tenant's own `PublicId`). The composite `(email, owner_id)` unique index (from Phase-1 Task 1) prevents two rows in the same tenant from having the same email, but does NOT protect cross-tenant uniqueness for upgraded scoped admins. The service-layer check is therefore the only cross-tenant uniqueness guard. Under genuine concurrent requests, the second `SaveChangesAsync` will throw a DB exception (the `(email, owner_id)` index fires if the same tenant — unlikely — or the global partial index fires if they are both upgrading to the same email). Wrap `SaveChangesAsync` in a try/catch for `DbUpdateException` and return `Result.Conflict(MessageKeys.Demo.EmailTaken)` in that case.

- [ ] **Demo panel disappears after upgrade:** after a successful upgrade the `sessionStorage["pointer_demo"]` key is cleared by the dashboard → the `DemoPanel` component conditionally renders only when `session != null` → panel disappears on the next render. Verify this in the browser for all three dashboards.

- [ ] **Comment cap no longer applies:** as a newly upgraded user, confirm a comment can be created beyond the previous cap (e.g. if the cap was 10 and they had 10 comments, comment #11 now succeeds).

- [ ] **`dotnet test`** — all existing tests green + the 6 new `DemoUpgradeTests` green.

- [ ] Commit: `test(api): U10 edge-case verification log`.

---

## Task U11 [Claude]: Final review + documentation update

**Files:**
- `docs/TASKS.md` or `docs/DESIGN.md` — add a short note that `User.RecipientEmail` is present and its purpose; note the upgrade endpoint.
- Review `DemoCleanupService`: confirm it still correctly skips upgraded users (should be obvious from Decision 7, but visually verify in the code and add an inline comment).

**Steps:**
- [ ] Add a comment in `DemoCleanupService.SweepAsync` above the `IsDemo &&` predicate: `// Only hard-delete rows that are still demo (IsDemo=true). Upgraded users have IsDemo=false and are intentionally excluded.`
- [ ] Update `docs/DESIGN.md` or a new `docs/DEMO_UPGRADE.md` with the upgrade flow, decision rationale, and data model note.
- [ ] `dotnet build` + `dotnet test` one final time.
- [ ] Commit: `docs: note demo-upgrade flow + DemoCleanupService comment`.

---

## Deploy (user-approved, single deploy)

1. API: merge `feat/demo-upgrade` → `main`; auto-migrate adds `recipient_email` column (nullable, safe).
2. Publish client packages (`npm run build-clients && npm publish`).
3. Dashboard: merge `feat/demo-upgrade` → `main`; build and deploy all three dashboards.
4. Live verify on `api.pointer.moamen.work`:
   - Provision a fresh demo via `demo.pointer.moamen.work` → new UI shows "Keep this workspace" CTA.
   - Fill upgrade form, submit → permanent account, panel gone.
   - Re-login with new credentials on the main dashboard → works.
   - Old demo JWT returns 403 on `/api/demo/upgrade`.

---

## Out of scope

- Email verification step before/after upgrade (could be Phase 4).
- Changing the `OwnerId` or `PublicId` of the user (explicitly out of scope — no data migration needed).
- "Upgrade" from a non-demo Pending user (different flow, handled by the existing approval workflow).
- Billing / plan gating tied to upgrade (Phase 4 / roadmap).
- Masking/echoing the provisioning email in `DemoSessionResponse` to pre-fill the upgrade form (nice-to-have follow-up — the form simply starts blank).
