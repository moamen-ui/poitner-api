# DB-11d — `POST /api/me/change-email`: change the identity's e-mail with verification

Depends on [DB-11a](DB-11a-identity-and-workspace-memberships.md) **in production** (one identity per
e-mail, `ux_users_email_live` on `lower(email)`, `EmailNormalizer`, `IMembershipService`) and on
[DB-11c](DB-11c-deletion-semantics-remove-disable-erase.md) being **merged** (§3.4a scoped tokens —
`IResetTokenService.CreateScoped/TryValidateScoped`, `TokenPurposes.ChangeEmail`). Independent of
DB-11b. Origin: cross-review 2026-09-22, GLM A4 / chair synthesis §1 row D4 ("no change-e-mail endpoint
exists and DB-11 makes e-mail the identity key"). Rules: R13 (no migration), R14 (one identity per
`lower(email)`; the normaliser), R16 (identity-wide event rotates `users.security_stamp`).
**Class: Code only.** No migration, no schema. Ships as an ordinary `bash scripts/deploy-api.sh`.
**Status: implemented 2026-09-23 (feat/db-11d-change-email); reviewed (Gemini Pro MERGE, Opus MERGE
WITH FIXES → applied).** Owner decision D14 has a default (§3.6). The number
DB-11d was previously pencilled in for the legacy-column contract; that contract is now **DB-11e**
(DB-11a §10, DB-11c §10, DB-REVIEW §7).

## 1. Goal

After DB-11a the e-mail address is the one and only identity key: it decides which `users` row a login,
an invite acceptance or a self-signup lands on. Today nothing lets a person change it
(`API/Controllers/MeController.cs:21-28` offers only `change-password`; `grep -rn ChangeEmail API Application`
→ nothing), so "I changed jobs" becomes an operator hand-edit of `users.email` — the one write path that
can create the duplicate-e-mail state DB-11a's merge machinery was built to remove. This doc adds a
self-service change: the person proves the current password, a verification link goes to the **new**
address, a notice goes to the **old** one, and the change is applied only when the link is redeemed. If
the new address already belongs to another identity the request is refused with **Conflict — never a
merge** (D14). Applying the change rotates the identity stamp (every session ends; the person logs in
again with the new address). Passwordless (magic-link) identities and super admins are excluded (§3.1).

## 2. Prerequisites (verified facts, 2026-09-22 @ `66f9201`, plus DB-11a/DB-11c's additions)

- `MeController` (`API/Controllers/MeController.cs:10-19`): `[Route("api/me")]`, `[Authorize]`, `[Produces]`,
  no `[Tags]` → Swagger tag `Me` (in `orval.config.ts:6`). `ChangePassword` `:21-28` is the shape to copy
  (`authService.ChangePasswordAsync(request)`, `NotFound`/`Ok`/`BadRequest`). No rate limit on any action.
- `AuthController` (`API/Controllers/AuthController.cs:11-17`): `[Route("api/auth")]`, tag `Auth` (in
  `orval.config.ts:6`); `ResetPassword` `:90-99` is the anonymous-token shape (`[AllowAnonymous]`,
  `[EnableRateLimiting("signup")]`, `Result` 200/400). `using Microsoft.AspNetCore.RateLimiting;` at `:3`.
- `AuthService.ChangePasswordAsync` (`Application/Services/Implementation/AuthService.cs:147-197`): own row by
  `_currentUser.Id` (`sub`) `:154-161`, `_passwordHasher.Verify(request.CurrentPassword, user.PasswordHash)`
  `:163-164`, `user.SecurityStamp = Guid.NewGuid()` `:169`, `SaveChangesAsync` `:171`, then a best-effort
  notification e-mail `:173-195` via `_branding.BuildResponseAsync("", new HashSet<string>())`,
  `WorkspaceNameResolver.ResolveForEmailAsync(_unitOfWork, user.OwnerId)` and `_emailService.SendAsync` in
  `try/catch`. `RequestPasswordResetAsync` `:48-105` builds the link as `$"{brand.Urls.App.TrimEnd('/')}/reset?token={Uri.EscapeDataString(token)}"` `:68-70`.
- `ChangePasswordRequest` (`Application/DTOs/Auth/ChangePasswordRequest.cs`: `CurrentPassword`, `NewPassword`),
  `Application/Validators/ChangePasswordRequestValidator.cs` (FluentValidation, auto-validated),
  `Application/Validators/LoginValidator.cs` (`Email` NotEmpty + EmailAddress — the e-mail rule to copy).
- `IResetTokenService` (`Application/Abstractions/IResetTokenService.cs`) after DB-11c §3.4a:
  `CreateScoped(Guid publicId, Guid stamp, string purpose, string? payload)` /
  `TryValidateScoped(string token, string purpose, out Guid publicId, out Guid stamp, out string? payload)`;
  30-min TTL; `Application/Common/TokenPurposes.ChangeEmail = "change-email"`. Registered singleton
  (`Infrastructure/DependencyInjection.cs:45`).
- After DB-11a: `IMembershipService.FindIdentityByEmailAsync(string email)` (live row, normalises
  internally), `FindIdentityByPublicIdAsync(Guid)`, `ListForIdentityAsync(int)`; `EmailNormalizer.NormalizeRequired`
  (`Application/Common/EmailNormalizer.cs`); `ux_users_email_live UNIQUE (lower(email)) WHERE deleted_at IS NULL`
  — the database refuses a duplicate regardless of case; Npgsql surfaces it as `DbUpdateException` wrapping
  `PostgresException` with `SqlState == "23505"` and `ConstraintName == "ux_users_email_live"`.
- `User` (`Domain/Entity/User.cs`): `Email`, `PasswordHash`, `PasswordlessOnly` `:57`, `SecurityStamp` `:31`
  ("bumped on password change … so existing access tokens stop validating"), `Role.IsSuperAdmin`.
  `API/Seed/AdminSeeder.cs:128-139` re-finds the super admin **by e-mail** (`ADMIN__EMAIL`) on every boot — a
  super admin who changed their e-mail in the database would be re-created as a second super admin at the
  next boot. Hence §3.1's super-admin exclusion.
- JWT `email` claim is issued at login (`Infrastructure/Auth/JwtTokenService.cs:14-40`); nothing reads it
  server-side except the token itself, so it refreshes at the next login.
- `Invite.Email` (`Domain/Entity/Invite.cs:37-38`) is an accept lock on the address at invite time; an open
  invite locked to the old address is **not** rewritten (§3.4).
- `IEmailService.SendAsync(to, subject, html)` (`Application/Services/Interfaces/IEmailService.cs:10`), daily
  cap, best-effort. Local mail server for manual tests: `local-mail-server/`, `Email:Provider=smtp`.
- Tests: `Tests/AuthRateLimitingTests.cs:31-44` (`[Theory]` over `AuthController` action names, `PolicyName == "signup"`),
  `Tests/UserGovernanceTests.cs:20-60` (InMemory fixture with `IdentityHasher`, `NoopEmail`, `NoopBrandingService`),
  `Tests/ResetTokenServiceTests.cs` (DB-11c), `Tests/WorkspaceMembershipTests.cs` (DB-11a; `TestSeed.Join`).
- Dashboard: `features/profile/ProfilePage.tsx` shows `ProfileUser.Email`
  (`Application/DTOs/Profile/UserProfileResponse.cs:13`); anonymous token page precedent `App.tsx:45`
  `/reset` → `features/auth/ResetPasswordPage.tsx`.

## 3. Design

### 3.1 Who may change their e-mail

| Identity | Result |
|---|---|
| password account (`!PasswordlessOnly`), not super admin | allowed (§3.2) |
| `PasswordlessOnly` (magic-link Client) | `Failure(MessageKeys.User.ChangeEmailNeedsPassword)` — its address came from the admin's client invite; the admin re-invites the new address and removes the old membership (DB-11c). Owner question D14b (§3.6) if this ever matters |
| super admin (`Role.IsSuperAdmin`) | `Forbidden(MessageKeys.User.ChangeEmailSuperAdmin)` — the seeder would re-create the old address on boot (`AdminSeeder.cs:128-139`); the operator changes `ADMIN__EMAIL` and redeploys |
| erased / soft-deleted row | unreachable (`DeletedAt == null` filter); `NotFound(User.NotFound)` |

### 3.2 Step 1 — `POST /api/me/change-email`

`MeController`, `[Authorize]` (class), `[EnableRateLimiting("signup")]` (it sends two e-mails; same 5/h-per-IP
budget as `forgot-password`), body `ChangeEmailRequest { string CurrentPassword; string NewEmail }`,
`[ProducesResponseType(typeof(Result), 200)]` + 403 + 409.

`IAuthService.RequestEmailChangeAsync(ChangeEmailRequest)` in `AuthService`, after `ChangePasswordAsync`:

1. `_currentUser.Id is not Guid publicId` → `Failure(Auth.InvalidCredentials)` (as `:149-150`).
2. `identity = FindIdentityByPublicIdAsync(publicId)` (live, `Include(Role)`); null → `NotFound(User.NotFound)`.
3. §3.1 exclusions, in that order (super admin first).
4. `if (!_passwordHasher.Verify(request.CurrentPassword, identity.PasswordHash)) return Failure(User.CurrentPasswordIncorrect);`
5. `newEmail = EmailNormalizer.NormalizeRequired(request.NewEmail)`; `newEmail == identity.Email` →
   `Failure(User.EmailUnchanged)`.
6. **D14 — never merge:** `if (await _memberships.FindIdentityByEmailAsync(newEmail) is not null) return Conflict(User.EmailTaken);`
   (`EmailTaken` exists, `MessageKeys.cs:28`). The check is a courtesy; the database re-checks at step 2 (§3.3).
7. `token = _resetTokens.CreateScoped(identity.PublicId, identity.SecurityStamp, TokenPurposes.ChangeEmail, newEmail)`;
   `link = $"{brand.Urls.App.TrimEnd('/')}/confirm-email?token={Uri.EscapeDataString(token)}"`.
8. E-mail **to `newEmail`**: subject `$"Confirm your new {brand.ProductName} e-mail address"`, body (copy the
   reset template `:83-100`): "You asked to use this address for your account. Click the link below to confirm
   — it expires in 30 minutes. After confirming you will be signed out everywhere and sign in again with this
   address. If you did not ask for this, ignore this e-mail; nothing changes." Include the workspace line via
   `WorkspaceNameResolver.ResolveForEmailAsync(_unitOfWork, identity.OwnerId)` exactly as `:75-82`.
9. E-mail **to `identity.Email` (old)**: subject `$"Your {brand.ProductName} e-mail address is being changed"`,
   body: "Someone signed in to your account and asked to change its e-mail address to `{newEmail}`. If that was
   you, confirm it from the e-mail we sent there. If it was not you, change your password now — that cancels
   the request." (Changing the password rotates `SecurityStamp`, which invalidates the pending token — that is
   the whole cancellation mechanism; nothing is stored.) **Review fix:** sent FIRST (before step 8's
   new-address link), and each of the two sends gets its own best-effort `try/catch` — a delivery
   failure on one address must not suppress the other.
10. Nothing is written to the database. Return `Success(User.EmailChangeLinkSent)`.

### 3.3 Step 2 — `POST /api/auth/confirm-email-change`

`AuthController`, `[AllowAnonymous]`, `[EnableRateLimiting("signup")]`, body `ConfirmEmailChangeRequest { string Token }`,
`[ProducesResponseType(typeof(Result), 200)]` + 400 + 409. Anonymous because the person clicks the link in a
mail client with no session (same reasoning as `reset-password`); the token is the credential.

`IAuthService.ConfirmEmailChangeAsync(string token)`:

1. `_resetTokens.TryValidateScoped(token, TokenPurposes.ChangeEmail, out pid, out stamp, out payload)` false, or
   `payload` null/empty → `Failure(User.EmailChangeLinkInvalid)`.
2. `identity` live by `pid` (`IgnoreQueryFilters`, `DeletedAt == null`, `Include(Role)`); null, or
   `identity.SecurityStamp != stamp` (single-use / cancelled), or `identity.PasswordlessOnly`, or
   `identity.Role.IsSuperAdmin` → the same `EmailChangeLinkInvalid` (one message; enumeration resistance).
3. `newEmail = EmailNormalizer.NormalizeRequired(payload)`; `newEmail == identity.Email` → `Success(User.EmailChanged)`
   (idempotent re-click after success is impossible because the stamp rotated, but be explicit).
4. **D14 again:** `FindIdentityByEmailAsync(newEmail) is not null` → `Conflict(User.EmailTaken)` (someone
   registered that address in the 30-minute window).
5. `oldEmail = identity.Email; identity.Email = newEmail; identity.SecurityStamp = Guid.NewGuid();` *(DB-14 forward reference: once `users.email_verified_at` exists, also `identity.EmailVerifiedAt = DateTime.UtcNow;` — the link went to the new address, so it is proven; whichever of DB-11d/DB-14 lands second adds the line.)*
   `Repository<User>().Update(identity); await SaveChangesAsync();` wrapped in
   `try { … } catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: "23505" })
   { return Result.Conflict(MessageKeys.User.EmailTaken); }` — the database (`ux_users_email_live`) is the
   authority for case variants the app-level check might miss. (On the InMemory provider this catch is
   never hit; test 7 covers the app-level check, the rehearsal covers the constraint.)
6. E-mail **to `oldEmail`**: subject `$"Your {brand.ProductName} e-mail address was changed"`, body: "Your
   account's e-mail address is now `{newEmail}`. If you did not do this, contact your workspace admin
   immediately." Best-effort.
7. Return `Success(User.EmailChanged)`.

R16 consequence, spelled out: rotating `users.security_stamp` is an **identity-wide** event — every
workspace's sessions of this person die within the 60 s stamp cache; the JWT `email` claim of any
still-cached token is stale until then and is not read server-side. API keys, memberships, roles,
approval, comments: untouched (the identity's `public_id` and `users.id` do not change).

### 3.4 What is deliberately not touched

- `invites.email` rows locked to the **old** address stay as they are: an open invite addressed to the old
  address was addressed to *that* address; the inviter re-invites the new one. (DB-11c's erase scrub is
  different — there the address must disappear.)
- `users.recipient_email` (demo) — unrelated to the identity key, but **review fix:** the confirm step
  clears it when set, so a demo identity's old human-entered override address stops receiving mail
  once the account's real sign-in address has moved on.
- `users.owner_id`/`role_id` legacy columns — untouched (DB-11e drops them).
- **Review fix:** two DB-12 audit rows are written (hashes only, `email_hash` before/after via
  `PseudonymHasher`, never the raw address) — `auth.email_change_requested` on step 1, `auth.email_changed`
  on step 2 — instead of the "no audit row" this doc originally shipped with.

### 3.5 Message keys (add to `Application/Resources/MessageKeys.cs`, class `User`)

`ChangeEmailNeedsPassword = "Magic-link accounts cannot change their e-mail — ask your workspace admin for a new invite."`,
`ChangeEmailSuperAdmin = "The operator account's e-mail is configured on the server (ADMIN__EMAIL)."`,
`EmailUnchanged = "That is already your e-mail address."`,
`EmailChangeLinkSent = "We've sent a confirmation link to the new address. It expires in 30 minutes; until you confirm, nothing changes."`,
`EmailChangeLinkInvalid = "This confirmation link is invalid or has expired — request the change again."`,
`EmailChanged = "Your e-mail address has been changed. Please sign in again."`.
`EmailTaken` (`:28`) and `CurrentPasswordIncorrect` (`:42`) are reused as-is.

### 3.6 Owner decisions

| # | Question | Default encoded |
|---|---|---|
| D14 | The new address already belongs to another live identity | **Conflict (`EmailTaken`), never merge.** Merging two identities is the one-time, census-guarded operation of DB-11a Migration 2; it is never a runtime feature. The person who owns both addresses erases one account (DB-11c) or asks the admin to remove a membership. |
| D14b | Should magic-link (passwordless) identities be able to change their address by link alone? | **No** for now — the admin re-invites. Revisit with DB-14 (e-mail verification), which gives passwordless accounts a verified-address concept. |

## 4. Safety classification

Code only; no schema, no data migration. The only write is `users.email` + `users.security_stamp` on one
row, on the account holder's password-confirmed request **and** proof of control of the new address.
Tenancy: the identity is resolved by `sub` (step 1) or by a token bound to `pid` + stamp (step 2); no
workspace-scoped data is read or written. Uniqueness is enforced by `ux_users_email_live` (R14); the
app-level checks only improve the message.

## 5. File-level tasks

1. `Application/DTOs/Auth/ChangeEmailRequest.cs` (new: `string CurrentPassword`, `string NewEmail`, doc-comment
   "Body for POST /api/me/change-email. Self-service; password accounts only."), `Application/DTOs/Auth/ConfirmEmailChangeRequest.cs`
   (new: `string Token`).
2. `Application/Validators/ChangeEmailRequestValidator.cs` (new; copy `ChangePasswordRequestValidator.cs`):
   `RuleFor(x => x.CurrentPassword).NotEmpty()`, `RuleFor(x => x.NewEmail).NotEmpty().EmailAddress().MaximumLength(256)`
   (256 = `UserMapping.cs:26 HasMaxLength(256)`). `ConfirmEmailChangeValidator.cs`: `RuleFor(x => x.Token).NotEmpty()`.
3. `Application/Services/Interfaces/IAuthService.cs` — after `ChangePasswordAsync` (`:24`):
   `Task<Result> RequestEmailChangeAsync(ChangeEmailRequest request);` and `Task<Result> ConfirmEmailChangeAsync(string token);`.
4. `Application/Services/Implementation/AuthService.cs` — the two methods of §3.2/§3.3, placed after
   `ChangePasswordAsync`; constructor gains `IMembershipService memberships` if DB-11a did not already add it
   (check `:22-45`); reuse `BuildPasswordChangedEmailHtml`'s layout for the three new bodies (one private
   `BuildEmailChangeHtml(string heading, string paragraph, string? link, string? workspaceLine)` helper).
5. `API/Controllers/MeController.cs` — add after `ChangePassword` (`:28`):
   ```csharp
   /// <summary>Starts changing the caller's e-mail: password check, confirmation link to the new address, notice to the old one. Nothing changes until the link is confirmed.</summary>
   [HttpPost("change-email")]
   [EnableRateLimiting("signup")]
   [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
   [ProducesResponseType(typeof(Result), StatusCodes.Status403Forbidden)]
   [ProducesResponseType(typeof(Result), StatusCodes.Status409Conflict)]
   public async Task<IActionResult> ChangeEmail([FromBody] ChangeEmailRequest request)
   {
       var result = await authService.RequestEmailChangeAsync(request);
       if (result.IsNotFound) return NotFound(result);
       if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
       if (result.IsConflict) return Conflict(result);
       return result.IsSuccess ? Ok(result) : BadRequest(result);
   }
   ```
   (`authService` is already injected at `:17`; add `using Microsoft.AspNetCore.RateLimiting;` and `using Pointer.Application.Response;`.)
6. `API/Controllers/AuthController.cs` — add after `ResetPassword` (`:99`):
   ```csharp
   /// <summary>Applies an e-mail change with the token sent to the new address by POST /api/me/change-email. Anonymous — the token is the credential; signs the person out everywhere.</summary>
   [AllowAnonymous]
   [HttpPost("confirm-email-change")]
   [EnableRateLimiting("signup")]
   [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
   [ProducesResponseType(typeof(Result), StatusCodes.Status400BadRequest)]
   [ProducesResponseType(typeof(Result), StatusCodes.Status409Conflict)]
   public async Task<IActionResult> ConfirmEmailChange([FromBody] ConfirmEmailChangeRequest request)
   {
       var result = await authService.ConfirmEmailChangeAsync(request.Token);
       if (result.IsConflict) return Conflict(result);
       return result.IsSuccess ? Ok(result) : BadRequest(result);
   }
   ```
7. `Application/Resources/MessageKeys.cs` — §3.5.
8. Tests (§6). 9. `just fmt`, `just test`. 10. PR description: **Dashboard tasks** section = §11.

## 6. Tests

`Tests/ChangeEmailTests.cs` (InMemory fixture from `Tests/UserGovernanceTests.cs:20-60`; `CapturingEmail`
double from DB-11c test 14; `ResetTokenService` built as in `Tests/ResetTokenServiceTests.cs`; identities and
memberships via `TestSeed.Join`; `FakeAuditWriter` captures the DB-12 audit rows §3.4 now writes — tests
1 and 5 assert the `email_hash` before/after on each).

1. `RequestChange_HappyPath_SendsTwoEmails_WritesNothing` — one e-mail to the new address containing
   `confirm-email?token=`, one to the old address; `users.Email` unchanged; `SecurityStamp` unchanged;
   `TryValidateScoped(token, "change-email")` true with payload = normalised new address;
   `TryValidateScoped(token, "erase")` false; `TryValidate(token)` false; one
   `AuditActions.AuthEmailChangeRequested` audit row with the before/after `email_hash`.
2. `RequestChange_WrongPassword_Fails_NoEmail`; `RequestChange_SameAddress_DifferentCase_EmailUnchanged`
   (`"USER@x.com"` for an identity `user@x.com` → `EmailUnchanged`, zero e-mails).
3. `RequestChange_AddressOwnedByOtherIdentity_Conflict_NoEmail` (D14) — also with a case variant (`"Other@X.com"`).
4. `RequestChange_Passwordless_Fails`; `RequestChange_SuperAdmin_Forbidden`.
5. `Confirm_ValidToken_ChangesEmail_RotatesStamp_NotifiesOld` — `Email == new` (lower-case), stamp differs,
   one e-mail to the old address; `PublicId`, `users.id`, memberships, api keys unchanged; a comment authored by
   the identity still resolves to its `DisplayName`; one `AuditActions.AuthEmailChanged` audit row with the
   before/after `email_hash`.
6. `Confirm_TokenReuse_Fails` (stamp rotated → `EmailChangeLinkInvalid`); `Confirm_AfterPasswordChange_Fails`
   (request change, then `ChangePasswordAsync`, then confirm → invalid — the cancellation path).
7. `Confirm_AddressTakenMeanwhile_Conflict` — request for `new@x.com`, then create another identity `new@x.com`,
   then confirm → `Conflict(EmailTaken)`, identity unchanged.
8. `Confirm_EraseTokenRejected` (a `CreateScoped(pid, stamp, "erase")` token → invalid); `Confirm_ResetTokenRejected`.
9. `Confirm_ThenLogin_OldEmailFails_NewEmailWorks` (through `AuthService.LoginAsync`).
10. `Tests/AuthRateLimitingTests.cs`: `[InlineData("ConfirmEmailChange")]` on `SignupSurface_KeepsSignupRateLimit`;
    new `ChangeEmail_HasSignupRateLimit` on `typeof(MeController).GetMethod("ChangeEmail")`.
11. `ChangeEmailRequestValidator_RejectsBadEmail_AndOver256` (copy `Tests/LoginValidatorTests.cs`).
12. **Review finding #10:** `Confirm_TamperedPayloadSwappedToAnotherAddress_Fails` — a token minted for
    address A has its payload segment swapped for a well-formed encoding of address B (not garbage); the
    HMAC covers `id|stamp|exp|purpose|payload`, so signature validation fails just as it does for garbage,
    and the identity's e-mail is unchanged.
13. **Review finding #2:** `Confirm_DuplicateKeyOnSave_RevertsInMemoryAndDetaches_Conflict` — a `IUnitOfWork`
    decorator makes `SaveChangesAsync` throw a `DbUpdateException` wrapping a `PostgresException` with
    `SqlState "23505"` / `ConstraintName "ux_users_email_live"` (InMemory never produces this itself); asserts
    `Conflict(EmailTaken)` and that the `User` entity is no longer tracked (`ChangeTracker.Entries<User>()`
    empty) instead of left `Modified` with the reverted change.

Tenancy invariant: this doc reads no workspace data, so the R8 test is the existing DB-11a
`InWorkspace_TenantB_SeesNothingOfTenantA`; test 5's "memberships unchanged" is the relevant assertion.
Existing data survives: no migration; test 5 asserts every other column and child row is unchanged.

## 7. Acceptance criteria

1. `curl -s http://localhost:8090/swagger/v1/swagger.json | jq '.paths["/api/me/change-email"].post.tags, .paths["/api/auth/confirm-email-change"].post.tags'` → `["Me"]`, `["Auth"]`.
2. `grep -c 'EnableRateLimiting("signup")' API/Controllers/MeController.cs` → 2 (`request-erase` from DB-11c + `change-email`); `grep -c "ConfirmEmailChange" API/Controllers/AuthController.cs` → ≥ 2.
3. `grep -c "TokenPurposes.ChangeEmail" Application/Services/Implementation/AuthService.cs` → 2; `grep -c "EmailNormalizer" Application/Services/Implementation/AuthService.cs` → ≥ 2 more than before this doc.
4. `grep -rn "Trim().ToLower" Application API --include='*.cs' | grep -i email | wc -l` → 0 (DB-11a criterion still holds).
5. `dotnet ef migrations has-pending-model-changes -p Infrastructure -s API` → "No changes have been made to the model since the last migration." (no schema).
6. `just test` green with the 11+ new facts.
7. Manual on the rehearsal API with the local mail server: change a test identity's e-mail to a **case variant of another identity's address** → 409; to a fresh address → two e-mails; confirm → the old token is 401 within 60 s, login with the new address works, `SELECT email FROM users WHERE public_id = '<pid>'` shows the lower-cased new address; re-click → 400.

## 8. Rollback

Revert the commit; ordinary redeploy. No schema. E-mail changes already confirmed stay (they are the
feature); a person who wants the old address back runs the flow again. Pending links die with the
deploy only if the signing key changes (it does not).

## 9. Release steps

1. Merge after DB-11a is verified in production and DB-11c is merged. 2. `bash scripts/deploy-api.sh`
(ordinary). 3. Verify criterion 7 against production with a disposable identity (invite → accept →
change-email → confirm → erase it via DB-11c). 4. Watch `docker compose logs api` for `23505` /
`ux_users_email_live` lines (would mean the app-level D14 check is being bypassed — expected only under a
genuine race). 5. Dashboard: client regen once (adds `postApiMeChangeEmail`, `postApiAuthConfirmEmailChange`,
`ChangeEmailRequest`, `ConfirmEmailChangeRequest`), then §11.

## 10. Out of scope

E-mail **verification at signup** and the `users.email_verified_at` column (DB-14 — same token rail, its
own doc); password policy (DB-14); change-e-mail for passwordless identities (D14b); an audit row
(DB-12); rewriting `invites.email` for open invites addressed to the old address (§3.4); the super
admin's address (`ADMIN__EMAIL`); DB-11e (legacy column drop); the widget (stakeholders change their
e-mail from the dashboard profile page — the widget shows no e-mail-editing UI; a password stakeholder
who cannot reach the dashboard asks their admin); the CLI (nothing changes for keys).

## 11. Dashboard / widget / CLI tasks

**Dashboard** (after client regen):
1. `features/profile/ProfilePage.tsx`: next to the displayed e-mail, a "Change e-mail…" action → dialog with
   current password + new e-mail → `POST /api/me/change-email`; on 200 show `envelope.message`; on 409/403/400
   show `envelope.message`. Hide the action when `me.isSuperAdmin` (the server refuses anyway).
2. New anonymous route `/confirm-email` (`App.tsx`, next to `/reset` at `:45`) → `features/auth/ConfirmEmailPage.tsx`
   copied from `ResetPasswordPage.tsx`: reads `?token=`, calls `POST /api/auth/confirm-email-change { token }`
   on mount (or on one button click — pick the button; a mail scanner pre-fetching the URL must not consume the
   token: **use the button**), shows `envelope.message`, then a link to `/login`.
3. `Shell`/auth store: on a 401 after a successful confirm (stamp rotated), the existing "session expired →
   login" handling applies; nothing new.

**Widget**: none. **CLI**: none.
