# DB-14 — E-mail verification (`users.email_verified_at`) and password policy

Roadmap: §57, Release 5 row **R5.4**; foundations report §2 row 4 ("verification after launch splits users into cohorts"). Rules: R1 (one nullable
column), R3 (one guarded backfill — grandfathering), R5, R7 (the backfill is raw SQL → marker + `[ContractMigration]` → explicit deploy path, alone as
`pre-db14` or batched with DB-12 as `pre-db12-14` under R7.1), R8 (no new tenant data), R10, R11, R13, R14 (the normaliser; verification is bound to the
**normalised** address), R16 (e-mailed one-time tokens are `IResetTokenService` scoped tokens with a purpose — **`TokenPurposes.VerifyEmail`**, never a table).
**Class: Expand** (additive column + a one-time, idempotent backfill; no drop, no rename). **Status 2026-09-22: written; not implemented. Cross-reviewed
2026-09-22 (GLM, agy — `docs/db/reviews/`); amendments folded 2026-09-23 (§12).** Owner decisions D14.1–D14.7 have defaults (§3.8); none blocks.

**Dependencies.** Requires **DB-11a in production** (`IMembershipService.FindIdentityByPublicIdAsync/FindIdentityByEmailAsync`, `EmailNormalizer`,
`ITokenService.Issue(User, WorkspaceMembership?, …)`, the join-or-create rule — every "identity is created here" site below is the DB-11a version) and
**DB-11c merged** (`IResetTokenService.CreateScoped/TryValidateScoped`, `TokenPurposes` — DB-11c §3.4a; this doc adds one constant). Independent of
DB-11b, DB-11d (one forward reference: DB-11d's confirm step sets `EmailVerifiedAt`, §3.3), DB-12 (writes `auth.email.verified` when `IAuditWriter` exists)
and DB-13.

## 1. Goal

Nobody proves they own the address they sign up with: `RegisterAdminAsync` creates a workspace admin for any string that parses as an e-mail, invite links
without an address lock can be accepted by anyone, and a demo upgrade takes whatever address is typed. After DB-11a the address *is* the identity key, so
an unverified address is an unverified identity. This doc adds one nullable column, one e-mailed link on the DB-11c token rail, a **gate** (an unverified
identity can read and comment but cannot act as an admin — create projects, invite, change members, rename, change settings) and a **password policy**
(10+ characters, not in the top-1000 list, not the address). Existing identities are grandfathered as verified (D14.1) and existing passwords are never
re-checked (D14.5). User-visible: a "Verify your e-mail" banner with a resend button; admin buttons refuse with one clear message until verified; a
weak password is refused at every place a password is set.

## 2. Prerequisites (verified facts, 2026-09-22 @ `1af08ec`, plus DB-11a/c additions)

- `Domain/Entity/User.cs` (`Email :8`, `PasswordHash :9`, `IsDemo :35`, `PasswordlessOnly :57`, `SecurityStamp :31`; DB-11c adds `ErasedAt`).
  `Infrastructure/Mappings/UserMapping.cs` (`Email` `HasMaxLength(256)` `:26`; `RecipientEmail` property line = insertion point per DB-11c task 1).
- Identity-creation sites (DB-11a `MembershipService.NewIdentity` is the one constructor; the **callers** decide verified-at-creation, §3.2):
  `AuthService.RegisterAsync` (stakeholder, pending), `AuthService.RegisterAdminAsync` (self-serve workspace), `InviteService.AcceptJoinExistingWorkspaceAsync` /
  `AcceptCreateNewWorkspaceAsync` (`invite.Email` = optional address lock, `Domain/Entity/Invite.cs`: "Null = anyone with the link"),
  `InviteService.CreateQuickAccessInviteAsync` (passwordless Client, addressed), `UserService.CreateAsync` (admin adds a member),
  `TenantService.CreateAsync` (super admin creates a workspace + admin), `DemoService.ProvisionAsync` (`demo-<slug>@demo.pointer`, `IsDemo`),
  `DemoService.UpgradeAsync` (sets the real address, `:288-300` after DB-11a: global uniqueness), `API/Seed/AdminSeeder.cs:128-160` (super admin from `ADMIN__EMAIL`).
- Password rules today: `MinimumLength(8)` in `Application/Validators/RegisterValidator.cs:15-17`, `RegisterAdminValidator.cs:15-17`, `CreateUserValidator.cs:15-17`,
  `CreateTenantValidator.cs:15-17`, `ChangePasswordRequestValidator.cs:14-16` (`NewPassword`), `ResetPasswordValidator.cs:14-16` (`NewPassword`),
  `UpdateUserValidator.cs:14-15` (optional `Password`), `AcceptInviteRequestValidator.cs:24-27`, `UpgradeDemoValidator.cs:15-18`; **three** inline service
  checks (GLM DB-14 #1 — the first draft listed one): `AuthService.ResetPasswordAsync :109-110` (`NewPassword.Length < 8`, literal message
  `"Password must be at least 8 characters."`), `TenantService.CreateAsync :168-169` (`request.Password.Length < 8`, **literal** message, not `MessageKeys`),
  `InviteService.AcceptAsync :513-514` (`request.Password.Length < 8`, `MessageKeys.User.PasswordWeak`). `grep -rn "Length < 8\|MinimumLength(8)\|at least 8" Application --include='*.cs'`
  → 13 lines today (9 validators + 3 services + `MessageKeys.cs:31`). Message `MessageKeys.User.PasswordWeak = "Password must be at least 8 characters."` (`MessageKeys.cs:31`).
  `UpgradeDemoValidator` is run inline by `DemoService.UpgradeAsync` (`:262-264`), not by auto-validation.
- Token rail (DB-11c §3.4a): `IResetTokenService.CreateScoped(Guid publicId, Guid stamp, string purpose, string? payload)` / `TryValidateScoped(token, purpose, out pid, out stamp, out payload)`,
  30-min TTL, purpose `^[a-z-]+$`; `Application/Common/TokenPurposes { Erase, ChangeEmail }`. Anonymous redeem endpoints carry `[EnableRateLimiting("signup")]`
  (`AuthController.cs:79-99` `forgot-password`/`reset-password`; policy = 5/h per IP, `RateLimitingExtensions.cs`) and return one indistinguishable failure.
- E-mail template precedent: `AuthService.RequestPasswordResetAsync :66-100` (`brand.Urls.App.TrimEnd('/')`, `{app}/reset?token=…`, `WorkspaceNameResolver.ResolveForEmailAsync`,
  best-effort `try/catch`). `IEmailService.SendAsync(to, subject, html)` capped per day.
- `MeController` (`API/Controllers/MeController.cs:10-19`: `[Route("api/me")]`, `[Authorize]`, tag `Me`), `AuthController` (`:11-17`, tag `Auth`) — both tags in `orval.config.ts:6`.
- **Identity from claims:** `User.PublicId` is a **`Guid`** (`Domain/Entity/User.cs:7`); the JWT `sub` is a string and — because the bearer handler maps inbound claims —
  may surface as `ClaimTypes.NameIdentifier` rather than `"sub"`. `Infrastructure/CurrentUser/HttpCurrentUser.cs:9-16` already does the right thing
  (`Guid.TryParse(FindFirst(NameIdentifier) ?? FindFirst("sub"))` → `Guid?`); `AuthenticationExtensions.cs:61-66` does the same with `ctx.Fail` on failure.
  **Any filter that needs the caller's identity resolves `ICurrentUser` from `RequestServices` and uses `.Id`** — never compares a claim string to `PublicId` (agy DB-14 #1).
- Authorization: `Policies.Admin` = claim `is_admin`, `Policies.SuperAdmin` = `is_super_admin` (`API/Extensions/AuthenticationExtensions.cs:110-113`). Global MVC filters:
  `Program.cs:49-52` (`options.Filters.Add(new ProducesAttribute(...))`; DB-12 adds `AuditCoverageFilter` there). `IMemoryCache` registered (`Infrastructure/DependencyInjection.cs:33`);
  the stamp validator's 60 s cache pattern `AuthenticationExtensions.cs:76-86`.
- Admin surface = every controller under namespace `Pointer.API.Controllers.Admin` (15 files, §DB-12 §2). Non-admin writes that stay open to unverified identities:
  comments/replies/uploads/verify, `POST /api/events`, `MeController.*`, `DemoController.Upgrade`, `AuthController.*`.
- `UserMapper.ToMeResponse(User, Role?, string? tenantName)` (DB-11a signature) → `MeResponse` (`Application/DTOs/Auth/MeResponse.cs`; DB-11b adds `WorkspaceId`, `Workspaces`).
- DB-02 guard (`Tests/MigrationSafetyTests.cs:75-87`): `.Sql(` ⇒ marker `R3 backfill` + `[ContractMigration]`. DB-10 CI applies from empty (0 `users` rows → the backfill is a no-op there).
- Tests: `Tests/UserGovernanceTests.cs:20-60` (InMemory + `IdentityHasher`, `NoopEmail`, `NoopBrandingService`, `FakeSettings`), `Tests/ChangePasswordTests.cs`, `Tests/NewValidatorsTests.cs` /
  `LoginValidatorTests.cs` (validator test shape), `Tests/AuthRateLimitingTests.cs:31-44` (`[Theory]` over `AuthController` action names, `PolicyName == "signup"`), `Tests/ResetTokenServiceTests.cs` (DB-11c),
  `CapturingEmail` double (DB-11c test 14), `TestSeed.Join` (DB-11a).
- Dashboard: `../pointer-dashboard/react/src/features/shell/Shell.tsx`, `features/signup/SignupPage.tsx`, `features/auth/{JoinPage,ResetPasswordPage,ForgotPasswordPage}.tsx`, `App.tsx:45` (`/reset` anonymous route precedent), `features/profile/ProfilePage.tsx`.
  Widget: `web-component/src/auth-ui.ts` (register/login forms; bundle budget 64 KB with ~1 KB headroom — no widget change in this doc).

## 3. Design

### 3.1 Schema — one column, one backfill

**`users.email_verified_at timestamptz NULL`** — `User.EmailVerifiedAt { get; set; }` with the doc-comment: "DB-14. Non-null = the identity proved
control of `Email` (verification link, addressed invite, operator-created, or grandfathered by the DB-14 backfill). Null = unverified: may read and comment,
may not act as an admin (`RequireVerifiedEmailFilter`). Reset to null by `DemoService.UpgradeAsync` (new address) and never by anything else — DB-11d's
confirm step sets it (the new address was just proven)." Mapping: `b.Property(x => x.EmailVerifiedAt).HasColumnName("email_verified_at");` after `RecipientEmail`
(DB-11c puts `ErasedAt` there too — order does not matter). No index (never filtered in bulk).

**Migration 1 — `AddUsersEmailVerifiedAt`** (scaffolded): exactly `AddColumn<DateTime>(name: "email_verified_at", table: "users", type: "timestamp with time zone", nullable: true)`.
Anything else → stop and report. No marker.

**Migration 2 — `BackfillUsersEmailVerifiedAt`** (scaffolded with **no** model change → empty; filled by hand). `[ContractMigration("DB-14")]` + marker verbatim:
`// DB-RULES: R3 backfill approved 2026-09-22 by Moamen (owner; D14.1 "existing identities are grandfathered as verified", relayed by the orchestrator; docs/db/execution/DB-14-email-verification-and-password-policy.md)`
`Up()` is one `migrationBuilder.Sql(...)`:
```sql
-- DB-14 D14.1: every identity that exists before this release is grandfathered. The literal is the cut-off written by the implementer on the day the
-- migration is authored (UTC midnight of that day); rows created after it are never touched, so a second run is a no-op (R3 idempotent).
UPDATE users SET email_verified_at = created_at
WHERE email_verified_at IS NULL AND deleted_at IS NULL AND created_at < TIMESTAMPTZ '<YYYY-MM-DD> 00:00:00+00';
```
The implementer replaces `<YYYY-MM-DD>` with **the authoring date + 1 day** (so every row created up to and including the deploy day is covered; the deploy must
happen on or before that date — release step 1 checks it). `Down()` stays **empty** with the comment `// No inverse: grandfathering is a one-time fact (DB-14 §8).`
`users` is far below the R3 batching threshold (one guarded UPDATE). Rehearsal check: `SELECT count(*) FROM users WHERE deleted_at IS NULL AND email_verified_at IS NULL` → 0.

**Every existing row:** every live `users` row gets `email_verified_at = created_at`; soft-deleted rows stay NULL (irrelevant — they cannot log in). No other table changes.

### 3.2 Verified at creation, or not — the rule per site

| Site (DB-11a version) | `EmailVerifiedAt` at creation | Verification mail? | Why |
|---|---|---|---|
| `RegisterAdminAsync` (self-serve workspace) | null | **yes** | nobody vouched for the address |
| `RegisterAsync` (widget stakeholder, pending) | null | **yes** (in addition to the approval flow) | same |
| `InviteService.Accept*` when `invite.Email != null` and `EmailNormalizer.Normalize(invite.Email) == identity e-mail` | `now` | no | the invite was addressed to this exact address and delivered to it — possession is proven by presenting the link (D14.2) |
| `InviteService.Accept*` when `invite.Email == null` ("anyone with the link") | null | **yes** | the link proves nothing about the typed address |
| `CreateQuickAccessInviteAsync` (passwordless Client) | `now` | no | the magic link is e-mailed to that address; a Client never acts as an admin anyway |
| `UserService.CreateAsync` (workspace admin adds a member) | null | **yes** | the admin typed it; the member proves it |
| `TenantService.CreateAsync` (super admin creates a workspace) | `now` | no | the operator vouches (D14.3); the created admin is usually the operator's own test/seed |
| `DemoService.ProvisionAsync` | null | no | fake address; **`IsDemo` identities are exempt from the gate** (§3.4) |
| `DemoService.UpgradeAsync` | set to **null** (new address) | **yes** | the convert step is exactly where the address becomes real (F4) |
| `AdminSeeder` super admin | `now` (set when creating; on reconcile set if null) | no | configured on the server |
| DB-11d `ConfirmEmailChangeAsync` step 5 | set to `now` | no | the link went to the new address (amend DB-11d §3.3 step 5: one line) |
| join-or-create onto an **existing** identity (DB-11a §3.3 rule 5) | unchanged — **except**: when the path is an invite accept, `invite.Email != null`, `EmailNormalizer.Normalize(invite.Email) == identity.Email` and `identity.EmailVerifiedAt == null` → set `now` (agy DB-14 #2) | no | a join never changes the identity — but D14.2's proof (an addressed link, delivered to that address, presented together with the account's password per DB-11a rule 3) holds for an existing identity exactly as for a new one; leaving it unverified would gate a person who just proved possession twice |

"Verification mail" = `EmailVerificationService.SendAsync(identity)` (§3.3) called best-effort right after the creating method's `SaveChangesAsync`.

### 3.3 The link — `IEmailVerificationService` (`Application/Services/Implementation/EmailVerificationService.cs`; Scrutor)

- `TokenPurposes.VerifyEmail = "verify-email"` (add to `Application/Common/TokenPurposes.cs`).
- `SendAsync(User identity)`: skip silently when `identity.EmailVerifiedAt != null || identity.IsDemo || identity.PasswordlessOnly || identity.Role.IsSuperAdmin`;
  `token = _resetTokens.CreateScoped(identity.PublicId, identity.SecurityStamp, TokenPurposes.VerifyEmail, payload: identity.Email)` (payload = the **normalised**
  address the link proves — a token minted before a DB-11d change cannot verify the new address);
  `link = $"{brand.Urls.App.TrimEnd('/')}/verify-email?token={Uri.EscapeDataString(token)}"`; subject `$"Verify your {brand.ProductName} e-mail address"`; body
  (copy the reset template shape; HTML-encode nothing dynamic but the link): "Confirm that {Email} is yours to unlock admin actions in {ProductName}. The
  link expires in 30 minutes. If you did not sign up, ignore this e-mail." Best-effort `try/catch` — **log the failure at `Warning`** (`"Verification mail to {PublicId} failed: {Reason}"`, no address), not `Information`: `IEmailService` is capped per day, so a signup burst can leave identities unverified with a throttled resend (GLM DB-14 #4); §9 step 5 watches this line. Records `_cache.Set($"verify_sent:{pid}", true, 5 min)`.
- `ResendAsync()` (`POST /api/me/verification/resend`, `MeController`, `[Authorize]` class-level, `[EnableRateLimiting("signup")]`, `[ProducesResponseType(typeof(Result), 200)]`):
  identity by `sub` (`FindIdentityByPublicIdAsync`); already verified → `Success(Auth.AlreadyVerified)`; demo/passwordless/super → `Failure(Auth.VerificationNotApplicable)`;
  `_cache.TryGetValue($"verify_sent:{pid}")` → `Failure(Auth.VerificationRecentlySent)` (one mail per identity per 5 min, on top of the per-IP budget);
  else `SendAsync` → `Success(Auth.VerificationSent)`.
- `ConfirmAsync(string token)` (`POST /api/auth/verify-email`, `AuthController`, `[AllowAnonymous]`, `[EnableRateLimiting("signup")]`, body `VerifyEmailRequest { string Token }`,
  validator `NotEmpty`): `TryValidateScoped(token, TokenPurposes.VerifyEmail, out pid, out stamp, out payload)` false → `Failure(Auth.VerificationLinkInvalid)`;
  identity live by `pid` (`IgnoreQueryFilters`, `DeletedAt == null`) and `identity.SecurityStamp == stamp` (a password change since minting kills the link — acceptable;
  the banner offers resend) and `EmailNormalizer.NormalizeRequired(payload) == identity.Email` — any failure → the same one message; already verified → `Success(Auth.EmailVerified)`
  (idempotent re-click; the stamp is **not** rotated by this action, so the link stays valid until expiry by design — it grants nothing beyond "verified");
  else `identity.EmailVerifiedAt = DateTime.UtcNow`, `Update`, `SaveChangesAsync`, `_cache.Remove($"emailverified:{pid}")` (§3.4), audit `auth.email.verified`
  (when `IAuditWriter` exists), return `Success(Auth.EmailVerified)`. Anonymous because the person clicks from a mail client; the token is the credential (DB-11d precedent).

### 3.4 The gate — `API/Auth/RequireVerifiedEmailFilter.cs : IAsyncActionFilter` (global, `Program.cs:49-52`)

Applies when **all** hold: the request is authenticated; the method is not GET/HEAD/OPTIONS; the controller type's namespace starts with
`Pointer.API.Controllers.Admin`; the action does not carry `[AllowUnverified]` (new attribute; none is needed today — it exists so a future exception is
explicit and greppable). **Namespace invariant (GLM DB-14 #3):** a new admin mutation **must** live under `API/Controllers/Admin/` or this filter will not gate
it (silently un-gated); a stakeholder-facing POST accidentally placed there is gated (fail-closed, safe). The DB-12 coverage test (`AuditCoverageTests`, same
namespace rule) is the twin check — a controller that trips one will trip the other. Record the invariant as a one-line comment on the filter class.

Exact mechanics (agy DB-14 #1 — the first draft compared a `string sub` to the `Guid PublicId`, which does not compile):
```csharp
public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
{
    var http = ctx.HttpContext;
    if (http.User?.Identity?.IsAuthenticated != true || HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method) || HttpMethods.IsOptions(http.Request.Method))
    { await next(); return; }
    var cad = ctx.ActionDescriptor as ControllerActionDescriptor;
    if (cad is null || cad.ControllerTypeInfo.Namespace?.StartsWith("Pointer.API.Controllers.Admin", StringComparison.Ordinal) != true
        || cad.MethodInfo.GetCustomAttribute<AllowUnverifiedAttribute>() is not null)
    { await next(); return; }

    var current = http.RequestServices.GetRequiredService<ICurrentUser>();          // Guid? Id — parses sub/NameIdentifier once, the codebase's one precedent
    if (current.IsSuperAdmin) { await next(); return; }                               // operator: seeded verified; never gated
    if (current.Id is not Guid publicId)
    {
        // An authenticated principal without a parsable sub cannot come from this API's own tokens (JwtTokenService always writes sub).
        // Fail OPEN like the lookup-exception path: the filter is not an authentication layer. Log once per request.
        _logger.LogWarning("RequireVerifiedEmailFilter: authenticated request without a parsable sub on {Method} {Path}; not gated", http.Request.Method, http.Request.Path);
        await next(); return;
    }

    bool verified;
    try
    {
        verified = await _cache.GetOrCreateAsync($"emailverified:{publicId}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
            var db = http.RequestServices.GetRequiredService<AppDbContext>();
            var row = await db.Users.IgnoreQueryFilters().AsNoTracking()
                .Where(u => u.PublicId == publicId && u.DeletedAt == null)
                .Select(u => new { u.EmailVerifiedAt, u.IsDemo })
                .FirstOrDefaultAsync();
            return row is null || row.EmailVerifiedAt != null || row.IsDemo;        // missing row → not gated (stamp validator already rejected deleted identities)
        });
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "RequireVerifiedEmailFilter: lookup failed; allowing request (fail-open).");
        await next(); return;
    }

    if (verified) { await next(); return; }
    http.Response.Headers["X-Email-Verification-Required"] = "true";
    ctx.Result = new ObjectResult(Result.Forbidden(MessageKeys.Auth.EmailNotVerified)) { StatusCode = StatusCodes.Status403Forbidden };
}
```
Constructor: `IMemoryCache cache, ILogger<RequireVerifiedEmailFilter> logger` (type-registered filter, DI-activated). `ConfirmAsync` removes the cache key
`emailverified:{pid}` (§3.3) so the unlock is immediate for the identity that just clicked. `AppDbContext` is referenced from `API` already (`Program.cs`,
`AuthenticationExtensions.cs`) — no new project reference.

What stays open to an unverified identity: every GET; every non-admin write (comments, replies, uploads, verify comment, events, `/api/me/*` incl. change-password,
leave, erase, resend; `POST /api/demo/upgrade`; the anonymous auth endpoints). Login succeeds with `Status = "ok"` — the *session* is not gated, the *admin actions* are.

### 3.5 `MeResponse` and login

`MeResponse` gains `public bool EmailVerified { get; set; }` (`EmailVerifiedAt != null || IsDemo || Role.IsSuperAdmin || PasswordlessOnly`) and
`public bool EmailVerificationRequired { get; set; }` (`!EmailVerified && role grants admin` — the banner is loud only for people the gate actually blocks; stakeholders
get a soft hint). `UserMapper.ToMeResponse` fills both. `LoginResponse.User` therefore carries them.

### 3.6 Password policy — `Application/Common/PasswordPolicy.cs`

```csharp
/// <summary>DB-14 D14.4. One policy for every place a password is set. Returns null when acceptable, else the MessageKeys.User.* message.
/// Rules: 10–128 chars; not in the embedded top-1000 list (case-insensitive exact match); not equal to the e-mail or its local part (case-insensitive).
/// Existing hashes are never re-checked (D14.5).</summary>
public static class PasswordPolicy
{
    public const int MinLength = 10;
    public const int MaxLength = 128;
    public static string? Validate(string? password, string? email);
    public static bool IsCommon(string password);   // HashSet<string> loaded once from the embedded resource
}
```
Embedded resource `Application/Resources/common-passwords.txt` — **exactly** the first 1 000 lines of SecLists
`Passwords/Common-Credentials/10-million-password-list-top-1000.txt` (MIT; commit the file with a two-line header comment naming source, commit hash and licence — the
loader skips lines starting with `#`). `<EmbeddedResource Include="Resources/common-passwords.txt" />` in `Application/Pointer.Application.csproj`. Test asserts 1 000 entries.

FluentValidation extension `Application/Validators/PasswordRules.cs`:
```csharp
public static IRuleBuilderOptions<T, string> StrongPassword<T>(this IRuleBuilder<T, string> rule, Func<T, string?> email) =>
    rule.NotEmpty().WithMessage(MessageKeys.User.PasswordRequired)
        .Custom((pw, ctx) => { var err = PasswordPolicy.Validate(pw, email(ctx.InstanceToValidate)); if (err != null) ctx.AddFailure(err); });
```
Replace the `MinimumLength(8)` rule at each site in §2 with `.StrongPassword(x => x.Email)` (`RegisterValidator`, `RegisterAdminValidator`, `CreateUserValidator`,
`CreateTenantValidator`, `AcceptInviteRequestValidator`, `UpgradeDemoValidator`), `.StrongPassword(_ => null)` for `ChangePasswordRequestValidator.NewPassword` and
`ResetPasswordValidator.NewPassword`, and `.StrongPassword(_ => null).When(x => x.Password != null)` for `UpdateUserValidator`.

**Service-layer re-validations — exactly five, all through `PasswordPolicy.Validate` (GLM DB-14 #1):** the pattern at every site is
`if (PasswordPolicy.Validate(pw, email) is string pwErr) return Result<…>.Failure(pwErr);` — never a length literal, never a literal message.
| Site | Password | E-mail passed | Replaces |
|---|---|---|---|
| `AuthService.ResetPasswordAsync :109-110` | `request.NewPassword` | the loaded identity's `Email` (after the token resolved the identity) | the `Length < 8` check + literal message |
| `AuthService.ChangePasswordAsync` (after the current-password check) | `request.NewPassword` | the loaded identity's `Email` | nothing (new — the validator has no address) |
| `UserService.UpdateAsync` (before hashing, only when `request.Password != null`) | `request.Password` | the member identity's `Email` | nothing (new) |
| `TenantService.CreateAsync :168-169` | `request.Password` | `request.Email` | the `Length < 8` check + **literal** `"Password must be at least 8 characters."` |
| `InviteService.AcceptAsync :513-514` | `request.Password` | `request.Email` | the `Length < 8` check + `MessageKeys.User.PasswordWeak` |
The last two are shadowed by their validators today (`CreateTenantValidator`, `AcceptInviteRequestValidator`) — they are kept as re-validation (the codebase's
"M2: guard nulls" convention keeps service-level guards) but must speak the **new** policy, otherwise the repo ends with a 10-char rule in validators and a stale
8-char fallback in two services and criterion 2 fails. Messages: `User.PasswordWeak = "Password must be at least 10 characters."`,
`User.PasswordTooLong = "Password must be 128 characters or fewer."`, `User.PasswordCommon = "That password is too common — choose something less guessable."`,
`User.PasswordIsEmail = "Your password must not be your e-mail address."`. Demo passwords (`DemoService.ProvisionAsync` generates 16 chars) pass; the seeded
super-admin password is **not** validated (config-owned; a warning is logged at boot if `PasswordPolicy.Validate` fails — `AdminSeeder`).

### 3.7 Message keys (`MessageKeys.Auth`)

`EmailNotVerified = "Verify your e-mail address to do this — check your inbox or resend the link from your profile."`,
`VerificationSent = "We've e-mailed you a verification link. It expires in 30 minutes."`, `VerificationRecentlySent = "A verification link was sent a moment ago — check your inbox (and spam) before requesting another."`,
`AlreadyVerified = "Your e-mail address is already verified."`, `VerificationNotApplicable = "This account does not need e-mail verification."`,
`VerificationLinkInvalid = "This verification link is invalid or has expired — request a new one from your profile."`, `EmailVerified = "Your e-mail address is verified."`.

### 3.8 Owner decisions encoded here (defaults apply unless the owner says otherwise before §9)

| # | Question | Default |
|---|---|---|
| D14.1 | Existing identities | **Grandfathered** — backfill `email_verified_at = created_at` for every live row (one real workspace today; no cohort split) |
| D14.2 | Accepting an **addressed** invite (`invite.Email` set and matching) counts as verification | **Yes** — the link was delivered to that address |
| D14.3 | Super-admin-created workspaces (`TenantService.CreateAsync`) | **Verified at creation** (operator vouches). Workspace-admin-added members are **not** |
| D14.4 | Password policy | 10–128 chars, not top-1000, not the address/local part. No composition rules (NIST 800-63B), no expiry, no history |
| D14.5 | Existing passwords | **Never re-checked**; enforced only when a password is set or changed |
| D14.6 | What an unverified identity may do | Everything a member does (read, comment, reply, upload, change own password/e-mail, leave, erase) **except** admin writes (`/api/admin/*` non-GET) |
| D14.7 *(added 2026-09-23, GLM DB-14 #2)* | Support remedy when the verification mail cannot arrive (typo at signup, provider cap) | **Change-e-mail is the remedy** — DB-11d's confirm step sets `EmailVerifiedAt` (§3.2 row 11), so the person fixes the address themselves; the banner text names it ("wrong address? change it in your profile"). **No operator "mark verified" endpoint by design** (it would let the operator vouch for an address nobody proved). Revisit if support tickets show the gap |

## 4. Safety classification

**Expand** (R1 column + R3 guarded, idempotent, literal-bounded backfill). Migration 2 carries the `R3 backfill` marker and `[ContractMigration("DB-14")]` →
explicit deploy (`pre-db14`, or `pre-db12-14` with DB-12 under R7.1). No data destroyed; `Down()` of Migration 2 is empty by design (§8). Tenancy: no
workspace-scoped data is added; the gate reads the caller's own identity row. Enumeration: the anonymous confirm endpoint returns one message for every failure
and is rate-limited (R16).

## 5. File-level tasks

1. `Domain/Entity/User.cs` — `EmailVerifiedAt` + doc-comment (§3.1). `Infrastructure/Mappings/UserMapping.cs` — the column line.
2. `just migrate name="AddUsersEmailVerifiedAt"` → one `AddColumn` (§3.1) or stop and report.
3. `just migrate name="BackfillUsersEmailVerifiedAt"` → must be **empty**; paste the §3.1 SQL with the date literal filled in (authoring date + 1); attribute + marker verbatim; empty `Down()` with the comment. `has-pending-model-changes` → "No changes".
4. `Application/Common/TokenPurposes.cs` — `public const string VerifyEmail = "verify-email";`.
5. `Application/Services/Interfaces/IEmailVerificationService.cs` + `Implementation/EmailVerificationService.cs` (§3.3; ctor `IUnitOfWork, ICurrentUser, IMembershipService, IResetTokenService, IEmailService, IBrandingService, IMemoryCache, IAuditWriter? (optional — omit if DB-12 is not merged and leave a `// DB-12:` comment)`).
6. `Application/DTOs/Auth/VerifyEmailRequest.cs` (`string Token`), `Application/Validators/VerifyEmailValidator.cs` (`NotEmpty`).
7. `API/Controllers/MeController.cs` — after `ChangePassword` (`:28`):
   ```csharp
   /// <summary>Re-sends the e-mail verification link (DB-14). One per 5 minutes per account; 5/h per IP.</summary>
   [HttpPost("verification/resend")]
   [EnableRateLimiting("signup")]
   [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
   public async Task<IActionResult> ResendVerification()
   {
       var result = await emailVerification.ResendAsync();
       return result.IsSuccess ? Ok(result) : BadRequest(result);
   }
   ```
   (constructor gains `IEmailVerificationService emailVerification`; `using Microsoft.AspNetCore.RateLimiting;` + `using Pointer.Application.Response;`).
   `API/Controllers/AuthController.cs` — after `ResetPassword` (`:99`):
   ```csharp
   /// <summary>Confirms an e-mail address with the token from the verification mail (DB-14). Anonymous — the token is the credential.</summary>
   [AllowAnonymous]
   [HttpPost("verify-email")]
   [EnableRateLimiting("signup")]
   [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
   [ProducesResponseType(typeof(Result), StatusCodes.Status400BadRequest)]
   public async Task<IActionResult> VerifyEmail([FromBody] VerifyEmailRequest request)
   {
       var result = await emailVerification.ConfirmAsync(request.Token);
       return result.IsSuccess ? Ok(result) : BadRequest(result);
   }
   ```
   Both `[NoAudit("…")]`/`[Audited(AuditActions.AuthEmailVerified)]` per DB-12 when it is merged (`VerifyEmail` audited; `ResendVerification` `[NoAudit("mail send, no state change")]`).
8. `API/Auth/AllowUnverifiedAttribute.cs`; `API/Auth/RequireVerifiedEmailFilter.cs` (§3.4 code **verbatim** — identity via `ICurrentUser.Id`, `Guid` compare, fail-open + Warning on a missing/unparsable sub, namespace-invariant comment); `Program.cs:49-52` → `options.Filters.Add<RequireVerifiedEmailFilter>();`.
9. Creation sites (§3.2): set `EmailVerifiedAt = DateTime.UtcNow` where the table says `now`; call `_emailVerification.SendAsync(identity)` where it says **yes** (constructor dependency in `AuthService`, `InviteService`, `UserService`, `DemoService`); `DemoService.UpgradeAsync` sets `EmailVerifiedAt = null` next to `user.Email = emailNormalized`; `AdminSeeder` sets it on create and on reconcile when null. **Join onto an existing identity via an addressed invite** (`InviteService.AcceptJoinExistingWorkspaceAsync` / `AcceptCreateNewWorkspaceAsync`, the `identity != null` branch of the DB-11a join-or-create): `if (invite.Email != null && EmailNormalizer.Normalize(invite.Email) == identity.Email && identity.EmailVerifiedAt == null) identity.EmailVerifiedAt = DateTime.UtcNow;` before the save (agy DB-14 #2). `docs/db/execution/DB-11d-change-email.md` §3.3 step 5 — add `identity.EmailVerifiedAt = DateTime.UtcNow;` (doc edit; DB-11d's implementer or this one, whichever lands second, adds the line to code).
10. `Application/Common/UserMapper.cs` + `Application/DTOs/Auth/MeResponse.cs` — §3.5.
11. `Application/Common/PasswordPolicy.cs`, `Application/Resources/common-passwords.txt` (+ csproj `EmbeddedResource`), `Application/Validators/PasswordRules.cs`; the nine validator edits and the **five** service re-validations of the §3.6 table (`AuthService.ResetPasswordAsync :109-110`, `AuthService.ChangePasswordAsync`, `UserService.UpdateAsync`, `TenantService.CreateAsync :168-169`, `InviteService.AcceptAsync :513-514`) — each via `PasswordPolicy.Validate`, no length literal, no literal message; `MessageKeys` (§3.6, §3.7).
12. Tests (§6); `just fmt`; `just test`; rehearsal (§3.1 query); `docs/db/SCHEMA.md` `users` row: add `email_verified_at`.

## 6. Tests

`Tests/EmailVerificationTests.cs` (InMemory fixture `UserGovernanceTests.cs:20-60`, `CapturingEmail`, `ResetTokenService` as in `ResetTokenServiceTests`, `TestSeed.Join`):
1. `RegisterAdmin_CreatesUnverified_SendsLink` — `EmailVerifiedAt == null`; one mail containing `verify-email?token=`; `TryValidateScoped(token, "verify-email")` true with payload == normalised e-mail; `TryValidateScoped(token, "change-email")` false; `TryValidate(token)` false.
2. `AcceptInvite_Addressed_VerifiedNoMail` (invite `Email = "A@x.com"`, accept as `"a@x.com"` → verified, zero mails); `AcceptInvite_Addressed_ExistingUnverifiedIdentity_BecomesVerified` (seed an unverified identity `a@x.com` with a membership in B; addressed invite to A for `A@x.com`; accept with the account's password → `EmailVerifiedAt != null`, zero mails; a **second** identity `c@x.com` accepting an addressed invite for `A@x.com` is refused by DB-11a's address lock, not by this doc); `AcceptInvite_Open_ExistingUnverifiedIdentity_StaysUnverified`; `AcceptInvite_Open_UnverifiedWithMail`; `QuickAccess_VerifiedAtCreation`; `TenantCreate_BySuperAdmin_Verified` (D14.3); `UserCreate_ByAdmin_UnverifiedWithMail`; `DemoUpgrade_ResetsVerification_SendsMail`; `JoinExistingIdentity_DoesNotTouchVerification`.
3. `Confirm_ValidToken_SetsVerifiedAt_Idempotent` (second click → success, timestamp unchanged); `Confirm_PayloadMismatch_AfterEmailChange_Invalid`; `Confirm_WrongPurpose_Invalid` (an `erase` token); `Confirm_StampRotated_Invalid`; `Confirm_Deleted_Invalid` — all failures return `Auth.VerificationLinkInvalid`.
4. `Resend_Throttled_5Minutes` (second call → `VerificationRecentlySent`, one mail); `Resend_AlreadyVerified`; `Resend_Demo_NotApplicable`.
5. `Tests/RequireVerifiedEmailFilterTests.cs` — build `ActionExecutingContext` (controller type `Pointer.API.Controllers.Admin.ProjectsController`, method POST, `RequestServices` providing a `FakeCurrentUser { Id = <guid> }`, an InMemory `AppDbContext`, a `MemoryCache`): unverified → result is `ObjectResult` 403 with header `X-Email-Verification-Required`; verified → `next` called; `IsDemo` → next; `FakeCurrentUser { IsSuperAdmin = true }` → next without a lookup; **`FakeCurrentUser { Id = null }` (unparsable/missing sub) → next, no lookup, one Warning logged** (agy DB-14 #1); missing `users` row → next; GET → next without a lookup; controller in `Pointer.API.Controllers` (non-admin) → next; `[AllowUnverified]` → next; cache: two calls, one query (count via a `DbCommandInterceptor` or assert the cache key `emailverified:{guid}` exists); after `ConfirmAsync` the key is gone and the next call passes.
6. `Tests/PasswordPolicyTests.cs` — `"short1"` → `PasswordWeak`; 129 chars → `PasswordTooLong`; `"password123"`, `"Password123"` → `PasswordCommon`; `"a@x.com"` and `"A"` (local part) with e-mail `a@x.com` → `PasswordIsEmail`; `"correct-horse-battery"` → null; embedded list has exactly 1 000 entries and contains `"123456"`.
7. `Tests/PasswordValidatorsTests.cs` — `[Theory]` over the nine validators with `"password1"` → invalid with `PasswordCommon`; `"long-enough-pw-1"` → valid; `UpdateUserValidator` with `Password = null` → valid. `ResetPassword_ServiceRejectsEmailAsPassword` (through `AuthService.ResetPasswordAsync` with a valid token); `ChangePassword_ServiceRejectsCommon`; `TenantCreate_ServiceRejectsCommon` and `AcceptInvite_ServiceRejectsCommon` (call the service directly with `"password1"` — bypassing the validator — → `Failure` with `PasswordCommon`; proves the two re-validations speak the new policy, GLM DB-14 #1); `UserUpdate_ServiceRejectsEmailAsPassword`.
8. `Tests/AuthRateLimitingTests.cs` — `[InlineData("VerifyEmail")]` on `SignupSurface_KeepsSignupRateLimit`; new `ResendVerification_HasSignupRateLimit` on `typeof(MeController).GetMethod("ResendVerification")`.
9. `Me_EmailVerifiedFlags` — unverified admin → `EmailVerified false, EmailVerificationRequired true`; unverified stakeholder → `false, false`; verified → `true, false`; demo → `true`.
10. Existing data survives: the rehearsal query (§3.1) prints 0 unverified live identities after Migration 2; the seeded super admin logs in and `POST /api/admin/projects` is not blocked. Tenancy: this doc adds no workspace-scoped rows; the R8 test is DB-11a's `InWorkspace_TenantB_SeesNothingOfTenantA` (unchanged).

## 7. Acceptance criteria

1. `dotnet ef migrations list -p Infrastructure -s API --no-connect` ends with `_AddUsersEmailVerifiedAt`, `_BackfillUsersEmailVerifiedAt`; `grep -c "ContractMigration(\"DB-14\")" Infrastructure/Migrations/*_BackfillUsersEmailVerifiedAt.cs` → 1; `grep -c "TIMESTAMPTZ '20" Infrastructure/Migrations/*_BackfillUsersEmailVerifiedAt.cs` → 1 and the date is ≥ today.
2. `grep -rn "MinimumLength(8)" Application --include='*.cs' | wc -l` → 0; `grep -rn "Length < 8" Application --include='*.cs' | wc -l` → 0; `grep -rn "at least 8" Application --include='*.cs' | wc -l` → 0 (the two literal messages in `AuthService`/`TenantService` and `MessageKeys.cs:31` are gone); `grep -rln "StrongPassword(" Application/Validators | wc -l` → 9; `grep -rl "PasswordPolicy.Validate" Application/Services/Implementation | sort` → exactly `AuthService.cs InviteService.cs TenantService.cs UserService.cs` (four files, five call sites).
2a. `grep -c "ICurrentUser" API/Auth/RequireVerifiedEmailFilter.cs` → ≥ 1; `grep -c 'FindFirst("sub")\|FindFirst(JwtRegisteredClaimNames.Sub)' API/Auth/RequireVerifiedEmailFilter.cs` → 0; `grep -c "Controllers.Admin" API/Auth/RequireVerifiedEmailFilter.cs` → ≥ 2 (the check and the invariant comment).
3. `wc -l Application/Resources/common-passwords.txt` → 1 002 (1 000 + 2 header lines); `grep -c "EmbeddedResource" Application/Pointer.Application.csproj` → ≥ 1.
4. `grep -c "verify-email" Application/Common/TokenPurposes.cs` → 1; `grep -c "TokenPurposes.VerifyEmail" Application/Services/Implementation/EmailVerificationService.cs` → 2.
5. `curl -s …/swagger.json | jq '.paths["/api/me/verification/resend"].post.tags, .paths["/api/auth/verify-email"].post.tags, .components.schemas.MeResponse.properties.emailVerified'` → `["Me"]`, `["Auth"]`, non-null.
6. `grep -c 'EnableRateLimiting("signup")' API/Controllers/MeController.cs` → 3 after DB-11c+DB-11d+this (1 if this lands first).
7. `just test` green with the 30+ new facts; DB-10 green (the backfill is a no-op on the empty CI database).
8. Manual on the rehearsal API with the local mail server: register a new workspace (self-serve) with `"password1"` → 400 `PasswordCommon`; with a strong password → 200, mail received; `POST /api/admin/projects` with that session → 403 + `X-Email-Verification-Required`; `POST /api/projects/{key}/comments` (as a stakeholder in a project) → 201; click the link (`POST /api/auth/verify-email`) → 200; `POST /api/admin/projects` → 200 within 60 s; re-click → 200 idempotent; `SELECT email_verified_at FROM users WHERE email = '<new>'` non-null; every pre-existing live identity has `email_verified_at = created_at`.

## 8. Rollback

Migration 1 `Down()` drops the column (**and the verification facts** — acceptable: re-running the backfill on redeploy grandfathers everyone again, which is
exactly the pre-DB-14 state). **Migration 2 has no `Down()`** (a data fact; nothing to invert). Code rollback = revert + ordinary redeploy; the column may stay.
Dump label `pre-db14` (or the R7.1 batch label) mandatory because Migration 2 is marked.

## 9. Release steps

1. Merge after DB-11a is verified in production and DB-11c is merged. **Check the date literal in Migration 2 is ≥ the planned deploy date**; if the deploy slips
   past it, amend the literal in a follow-up migration (`BackfillUsersEmailVerifiedAt2`, same marker) — never edit the merged one (R10).
2. R11 rehearsal on a same-day dump; paste the §3.1 count (0) and criterion 8 into the PR.
3. On the VM: `POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-db14 bash scripts/deploy-api.sh` (or the `pre-db12-14` batch). Expect two `Applying migration`
   lines and `DB-09: applying 1 contract migration(s)`.
4. Verify: `psql … -c "SELECT count(*) FROM users WHERE deleted_at IS NULL AND email_verified_at IS NULL"` → 0; log in as the real admin → `GET /api/auth/me` has
   `emailVerified: true`; rename the workspace → 200 (not gated); create a disposable identity via invite accept (open link) → banner path per criterion 8; erase it (DB-11c).
5. Watch `docker compose logs api` for `RequireVerifiedEmailFilter` warnings (lookup failures and "without a parsable sub" → fail-open; the latter must be zero) and for `Verification mail to … failed` at Warning (daily cap — if it appears during a signup burst, the affected people use change-e-mail or wait 5 min and resend, D14.7).
6. Dashboard: `dashboard-agent` regenerates the client from production once, then §11.

## 10. Out of scope

Change-e-mail (DB-11d — one forward reference here); MFA (§61, all-users trigger); forcing existing users to reset weak passwords (D14.5); password history/expiry;
CAPTCHA (trigger: first abuse); gating **non-admin** writes (comments stay open — the product's whole point is that a stakeholder can comment within a minute);
a `notifications` row for "verify your e-mail" (banner is enough); the widget's register form (server messages surface as-is; bundle budget); `RecordEventValidator`;
`clients/`; SSO.

## 11. Dashboard / widget / CLI tasks

**Dashboard** (after client regen: `usePostApiMeVerificationResend`, `usePostApiAuthVerifyEmail`, `MeResponse.emailVerified/emailVerificationRequired`):
1. `Shell.tsx`: when `me.emailVerificationRequired` render a top banner "Verify your e-mail address to manage this workspace — [Resend link] · Wrong address? [Change it]" (link to the DB-11d change-e-mail dialog; D14.7); when `!me.emailVerified && !required` a dismissible soft hint in the profile page only. Resend → toast `envelope.message` (429 → "Too many requests — try again later").
2. Axios mutator: on a 403 with header `X-Email-Verification-Required: true` show the banner's toast instead of the generic forbidden message.
3. New anonymous route `/verify-email` (`App.tsx`, next to `/reset` `:45`) → `features/auth/VerifyEmailPage.tsx` copied from `ResetPasswordPage.tsx`: reads `?token=`, **one button** "Verify" (never auto-submit — mail scanners pre-fetch links; DB-11d precedent), shows `envelope.message`, link to `/login` or `/`.
4. `SignupPage.tsx`, `JoinPage.tsx`, `ResetPasswordPage.tsx`, profile change-password dialog, users "add member" dialog, tenants "create" dialog: password helper text "At least 10 characters; avoid common passwords"; surface the server's message verbatim (the policy lives server-side; do not duplicate the list client-side). After self-serve signup show "Check your inbox to verify your address".
5. i18n (en + ar) for the banner, page and helper text.

**Widget:** none — the server returns the policy message on the widget's register form as it does for every validation error today; the bundle stays untouched (budget).
**CLI:** none (`login-with-key` unaffected; keys are minted from a verified-or-not identity — reading keys is not gated, and key creation is under `/api/me`, not gated by D14.6).

## 12. Cross-review adjudication (2026-09-22 reviews, folded 2026-09-23)

Reports: `docs/db/reviews/REVIEW-GLM-DB12-15-2026-09-22.md`, `docs/db/reviews/REVIEW-AGY-DB12-15-2026-09-22.md`. Every citation re-checked against the tree on 2026-09-23.

| Finding | Claim | Verdict | Where it landed |
|---|---|---|---|
| agy DB-14 #1 (**Blocker**) | `RequireVerifiedEmailFilter` compares `string sub` to `Guid PublicId` → does not compile | **Accepted** — `User.PublicId` is `Guid` (`User.cs:7`). Fixed by resolving `ICurrentUser.Id` (the codebase's one claim-parsing precedent, which also covers the `NameIdentifier` inbound mapping agy did not mention); missing/unparsable sub → fail-open + Warning (cannot come from this API's tokens) | §2 fact, §3.4 code verbatim, §5 task 8, §6 test 5, §7 crit. 2a, §9 step 5 |
| agy DB-14 #2 (Major) | Existing unverified identity accepting an addressed invite stays gated | **Accepted as Minor** (the banner + resend already existed, so "stuck" overstates it) — D14.2's proof holds for an existing identity too | §3.2 row 12, §5 task 9, §6 test 2 |
| GLM DB-14 #1 (Major) | Two inline length checks unlisted → criterion 2 fails | **Accepted** — `TenantService.cs:168-169` (literal message), `InviteService.cs:513-514` verified | §2, §3.6 five-site table, §5 task 11, §6 test 7, §7 crit. 2 |
| GLM DB-14 #2 (Minor) | No support override for an undeliverable address | **Accepted as a decision** — change-e-mail is the remedy; no operator override by design | D14.7, §9 step 5, §11.1 |
| GLM DB-14 #3 (Minor) | Namespace-based gate needs its invariant stated | **Accepted** | §3.4 first paragraph, §5 task 8, §7 crit. 2a |
| GLM DB-14 #4 (Minor) | Send failures need a Warning surface | **Accepted** | §3.3, §9 step 5 |
| GLM lockout analysis / agy "note on others" | No lockout for super admin, invitees, demo; backfill sound | **Confirmed** — no change |
