# R5-61 — Operator MFA (§61 · Release 5 · 1–2 d)

**Status (2026-09-23):** implemented on `feat/r5-61-operator-mfa` (rebased onto main = DB-11a/b/c/d,
DB-12, DB-13, R5-62 in production) — build green, full suite green (1070/1070, +22 new), migration
`AddOperatorMfa` proven on a rehearsal Postgres. Not yet merged/deployed; see AGENT-REPORT.md in the
worktree for the full verification record.

## 1. Goal

Add TOTP-based MFA for super-admin identities only. The single env-seeded super-admin account
(`ADMIN__EMAIL` / `ADMIN__PASSWORD`, `docker-compose.prod.yml:47-48`) can read every tenant’s data
— it must require a second factor before launch. Non-super-admin users are unaffected.

Effort: 1–2 d.

## 2. Prerequisites (verified facts)

- **Super-admin account**: seeded by `API/Seed/AdminSeeder.SeedAsync` (called from `Program.cs:176`).
  `ADMIN__EMAIL` and `ADMIN__PASSWORD` in `docker-compose.prod.yml:47-48`.
- **IsSuperAdmin claim**: `JwtTokenService.cs:27` `new Claim("is_super_admin", …)`;
  `AuthenticationExtensions.cs:113` `.AddPolicy(Policies.SuperAdmin, p => p.RequireClaim("is_super_admin", "true"))`.
- **Login flow**: `AuthController.Login` (`AuthController.cs:19-29`) → `AuthService.LoginAsync`
  returns `LoginResponse { Status, Token, User }`. Status can be `"ok"`, `"pending"`, `"rejected"`,
  `"disabled"` (`00-API-INVENTORY.md:10`).
- **JWT lifetime**: 12 h (`JwtTokenService.cs:10` `LifetimeHours = 12`).
- **ApiKeyProtector**: `Infrastructure/Security/ApiKeyProtector.cs` — AES-256-GCM encryption with
  `Auth:ApiKeyEncryptionKey` config or HKDF-derived fallback from JWT signing key. `Encrypt(string)`,
  `Decrypt(string)` methods. Uses `IApiKeyProtector` interface at `Application/Abstractions/IApiKeyProtector.cs`.
- **Users entity**: `Domain/Entity/User.cs` — has `SecurityStamp` (Guid), `PublicId`, `Email`,
  `OwnerId`, `RoleId`. No `totp_secret` or MFA columns exist.
- **DB-11c / DB-12 status**: `docs/db/execution/DB-11c-deletion-semantics-remove-disable-erase.md`
  is **written 2026-09-22; not implemented**. It does define a scoped-token primitive
  (`IResetTokenService.CreateScoped`/`TryValidateScoped`, `DB-11c.md:206-223`) but that rail is a
  **string token mailed as a one-time link** (purpose + payload, validated against `SecurityStamp`),
  not a JWT claim — the wrong shape for an in-band "enter your 6-digit code now" step. `docs/db/execution/DB-12-audit-log.md`
  is also **written 2026-09-22; not implemented** (`IAuditWriter`, `audit_events` table). The
  foundations report's sequence (`docs/roadmap/meetings/2026-09-22-foundations/07-final-report.md`
  §3 step 5) places operator MFA in the **ops pack, parallel** with DB-11b/c — i.e. this doc is
  **independent of DB-11c and DB-12** by design; it does not wait on either. Design below uses a
  **self-contained JWT claim** (`mfa_pending`), not DB-11c's token rail.
- **Migrations**: `just migrate name="..."` (`justfile:6`). Auto-apply on boot (`Program.cs:175`).
  DB-02 guard: `Tests/MigrationSafetyTests.cs`. This is additive (R1) — new nullable columns, no
  existing column touched.
- **Dashboard**: separate repo `pointer-dashboard/react`; changes described but not implemented here.

## 3. Design

### 3.1 Database columns (additive migration)

On the `users` table (not a new table — super-admin is a User row):

| Column | Type | Notes |
|---|---|---|
| `totp_secret` | `text NULL` | AES-256-GCM encrypted via `ApiKeyProtector.Encrypt()`. NULL = MFA not enrolled. |
| `totp_enabled_at` | `timestamptz NULL` | Set when TOTP is verified and enabled. NULL = not enabled. |

New table `user_recovery_codes`:

| Column | Type | Notes |
|---|---|---|
| `id` | `int` (PK, identity) | |
| `user_id` | `int` (FK → users.id) | |
| `code_hash` | `text NOT NULL` | SHA-256 hash of the recovery code (same `IApiKeyProtector.Hash()`). |
| `used_at` | `timestamptz NULL` | Set when consumed; row kept for audit. |

Migration name: `AddOperatorMfa`. Additive only (R1): new nullable columns on `users`, new table.
No existing column modified. The DB-02 guard applies normally (no approval marker needed for R1).

### 3.2 Enrol flow

**`POST /api/me/mfa/enrol`** `[Authorize]`:
- Returns 403 if `!IsSuperAdmin`.
- Returns 409 if `totp_enabled_at IS NOT NULL` (already enrolled).
- Generates a 20-byte random TOTP secret (RFC 6238, SHA-1, 6 digits, 30 s step).
- Stores `ApiKeyProtector.Encrypt(base32secret)` in `users.totp_secret`.
- Returns `{ secret: "<base32>", otpauthUrl: "otpauth://totp/Pointer:<email>?secret=<base32>&issuer=Pointer&algorithm=SHA1&digits=6&period=30" }`.
  The dashboard renders a QR code from `otpauthUrl` using a JS QR library (or the `<canvas>` approach — no new server dependency).

**`POST /api/me/mfa/verify`** `[Authorize]`:
- Body: `{ code: "123456" }`.
- Decrypts `totp_secret`, validates the TOTP code (allow ±1 time step for clock skew).
- If valid: sets `totp_enabled_at = UtcNow`, generates 8 recovery codes (8-char alphanumeric),
  hashes each with `ApiKeyProtector.Hash()`, stores in `user_recovery_codes`, returns the plain
  codes **once**.
- If invalid: 400.

**`POST /api/me/mfa/disable`** `[Authorize]`:
- Body: `{ code: "123456" }` (current TOTP or a recovery code).
- Returns 403 if `!IsSuperAdmin`.
- Validates code. If valid: NULLs `totp_secret`, `totp_enabled_at`, deletes recovery code rows.
- Returns 200.

### 3.3 Login flow change

In `AuthService.LoginAsync`, after successful password verification, before issuing the JWT:
- If `user.Role.IsSuperAdmin && user.TotpEnabledAt != null`:
  - Do NOT issue the normal 12 h JWT.
  - Instead, issue a **short-lived scoped JWT** (5-minute lifetime) with an extra claim
    `mfa_pending: "true"` and reduced claims (only `sub`, `stamp`, `mfa_pending`). This is the
    "MFA-required" token.
  - Return `{ status: "mfa_required", token: "<scoped JWT>" }`. The dashboard stores it and
    redirects to the MFA input screen.

**Independent of DB-11c/DB-12**: the `mfa_pending` claim approach above is self-contained — no DB
entity, no dependency on DB-11c's `IResetTokenService.CreateScoped`/`TryValidateScoped` (that rail
mails a one-time link; this is an inline code-entry step), and no dependency on DB-12's audit log.
This lets MFA ship in the ops pack without waiting on either (see Prerequisites). Once DB-12 lands,
add `IAuditWriter` calls for `mfa.enrolled` / `mfa.disabled` / `mfa.login_failed` at the call sites
in §5 (a follow-up task, not a blocker — MFA enrollment/disable on the one super-admin account is
exactly the kind of security-relevant mutation DB-12's goal statement calls out).

**`POST /api/auth/mfa`** `[AllowAnonymous]` (the caller has the scoped token, not a full session):
- Reads `Authorization: Bearer <scoped JWT>`. Validates it normally (signature, expiry).
- Requires `mfa_pending == "true"` claim. If absent or expired: 401.
- Body: `{ code: "123456" }`.
- Decrypts `totp_secret`, validates the code (or matches a recovery code hash).
- If valid: issues a **normal 12 h JWT** (full claims). Returns `{ status: "ok", token, user }`.
  If a recovery code was used, marks `used_at`.
- If invalid: 400.

### 3.4 Enforcement scope

MFA is enforced **only** when `IsSuperAdmin && totp_enabled_at IS NOT NULL`. A super-admin who has
not enrolled sees no change (normal login). The `POST /api/me/mfa/enrol` endpoint is the opt-in.
This matches the plan's scope for §61: "Operator MFA (TOTP on the super-admin account) — one
env-seeded account reads every tenant" (`DX-UX-CX-PLAN.md:408`) — the item exists specifically
because there is exactly one super-admin account, not a general MFA-for-all-users feature.

Non-super-admin users are completely unaffected. The `totp_secret` and `totp_enabled_at` columns
are on the `users` table but only read/written by MFA-specific code paths that gate on `IsSuperAdmin`.

### 3.5 TOTP implementation

Use `System.Security.Cryptography.HMACSHA1` directly (no new NuGet). TOTP (RFC 6238) is:
```
counter = floor(unixTime / 30)
hmac = HMACSHA1(secret, counter as 8-byte big-endian)
offset = hmac[19] & 0x0f
code = (hmac[offset..offset+4] as uint32 & 0x7fffffff) % 1_000_000
```
Validate current step ± 1 (3 windows). Encapsulate in `Infrastructure/Auth/TotpService.cs`.

## 4. Safety / impact

**Additive migration** (R1). New nullable columns on `users`, new table `user_recovery_codes`.
No existing column modified. DB-02 guard applies. Auto-applies on boot.

**Login flow**: only changes when `IsSuperAdmin && totp_enabled_at != null`. All other users see
identical behaviour. The `mfa_pending` scoped token has a 5-minute lifetime and is only valid for
`POST /api/auth/mfa`.

**Encryption-key note**: neither `docker-compose.prod.yml` nor `.env.prod.example` currently sets
`Auth__ApiKeyEncryptionKey`, so `ApiKeyProtector` falls back to an HKDF-derived key from
`JWT__SigningKey` today (`ApiKeyProtector.cs:41-63`, logs a warning on every boot). This doc reuses
`ApiKeyProtector.Encrypt`/`Decrypt` for `totp_secret`, so TOTP secrets would ride on that same
fallback-derived key unless the operator sets `Auth__ApiKeyEncryptionKey` explicitly — worth doing
before enabling MFA in production (rotating the JWT signing key would otherwise make both API keys
and TOTP secrets undisplayable/undecryptable at once), but not a blocker since logins still work
either way (TOTP validation only needs to decrypt the stored secret, not re-derive it).

## 5. File-level tasks

1. **`Domain/Entity/User.cs`** — add `public string? TotpSecret { get; set; }` and
   `public DateTime? TotpEnabledAt { get; set; }`.
2. **`Domain/Entity/UserRecoveryCode.cs`** (new) — `Id`, `UserId`, `CodeHash`, `UsedAt`.
3. **`Infrastructure/Mappings/UserMapping.cs`** — map `totp_secret` and `totp_enabled_at`.
4. **`Infrastructure/Mappings/UserRecoveryCodeMapping.cs`** (new) — table `user_recovery_codes`,
   FK to `users`.
5. **`Infrastructure/AppDbContext.cs`** — add `DbSet<UserRecoveryCode> UserRecoveryCodes`.
6. **Migration** `AddOperatorMfa` — `just migrate name="AddOperatorMfa"`.
7. **`Infrastructure/Auth/TotpService.cs`** (new) — `GenerateSecret()`, `ValidateCode(secret, code)`,
   `GenerateOtpAuthUrl(email, secret, issuer)`.
8. **`Application/Services/Implementation/MfaService.cs`** (new) + interface in
   `Application/Services/Interfaces/IMfaService.cs`.
9. **`API/Controllers/MfaController.cs`** (new) — `[Route("api/me/mfa")]`, `[Authorize]`,
   **`[Tags("Me")]`** (Swashbuckle tags an action by controller name by default — `MfaController`
   would otherwise tag as `"Mfa"`, which is not in `orval.config.ts` `filters.tags` and would
   silently generate nothing for the dashboard; reusing the existing `"Me"` tag needs no
   `orval.config.ts` change since it's already listed and the routes live under `api/me/*`):
   `POST enrol`, `POST verify`, `POST disable` — each needs
   `[ProducesResponseType(typeof(<InnerResponseType>), 200)]` on the inner DTO (never `Result<T>`).
10. **`API/Controllers/AuthController.cs`** — add `POST mfa` action (`:29`, after Login).
11. **`Application/Services/Implementation/AuthService.cs`** — in `LoginAsync`, after password
    check: if `IsSuperAdmin && TotpEnabledAt != null`, issue scoped token, return `mfa_required`.
12. **`Infrastructure/Auth/JwtTokenService.cs`** — add `IssueMfaPending(User)` method that
    issues a 5-min JWT with `mfa_pending: "true"`, `sub`, `stamp` only.
13. **DI registration**: `MfaService` needs **no manual step** — `Application/DependencyInjection.cs`'s
    `AddApplication()` uses Scrutor (`s.Scan(...).AddClasses(c => c.Where(t => t.Name.EndsWith("Service")))`)
    to auto-register every `*Service` class in the Application assembly against its interface, and
    `MfaService`/`IMfaService` matches that convention. `TotpService` (in `Infrastructure/Auth/`) is
    **not** covered by that scan (it only scans the Application assembly) — add an explicit
    `s.AddSingleton<TotpService>();` (no interface needed, per §5 task 7) to
    `Infrastructure/DependencyInjection.cs`'s `AddInfrastructure()` method, alongside the other
    manual `s.Add*<...>` lines there.
14. **`docker-compose.prod.yml`** — no new env var needed (encryption key is `Auth:ApiKeyEncryptionKey`).

## 6. Tests

New `Tests/MfaServiceTests.cs`:

1. **`TotpService_GenerateAndValidate`** — generate a secret, compute the expected code, validate.
2. **`TotpService_RejectsWrongCode`** — wrong code returns false.
3. **`TotpService_AcceptsAdjacentTimeStep`** — code from ±30s window is accepted.
4. **`Enrol_Forbidden_ForNonSuperAdmin`** — non-SA user → 403.
5. **`Enrol_Conflict_WhenAlreadyEnabled`** — SA with `TotpEnabledAt` set → 409.
6. **`Verify_EnablesMfa_ReturnsRecoveryCodes`** — valid code → `TotpEnabledAt` set, 8 codes returned.
7. **`Login_SuperAdmin_WithMfa_ReturnsMfaRequired`** — SA with MFA enabled → `status: "mfa_required"`.
8. **`MfaEndpoint_CompletesLogin_WithValidCode`** — scoped token + valid TOTP → normal JWT.
9. **`MfaEndpoint_RejectsExpiredScopedToken`** — expired scoped token → 401.
10. **`RecoveryCode_WorksOnce`** — recovery code accepted; second use rejected.
11. **`Disable_ClearsMfa`** — disable with valid code → `TotpEnabledAt` null.
12. **`Login_SuperAdmin_WithoutMfa_NormalLogin`** — SA who hasn’t enrolled → normal `status: "ok"`.

## 7. Acceptance criteria

1. `dotnet test --filter MfaServiceTests` → green (12 tests).
2. `POST /api/me/mfa/enrol` with a non-SA token → 403.
3. `POST /api/me/mfa/enrol` with SA token → 200 + `otpauthUrl` starting with `otpauth://totp/`.
4. After verify: `POST /api/auth/login` with SA creds → `{ status: "mfa_required" }`.
5. `POST /api/auth/mfa` with scoped token + valid TOTP → `{ status: "ok", token: "<12h JWT>" }`.
6. `POST /api/auth/mfa` with scoped token + wrong code → 400.
7. Non-SA login is completely unchanged.
8. Migration `AddOperatorMfa` applies on boot; `just test` green; `just fmt` clean.
9. `grep -c 'totp_secret' Infrastructure/Mappings/UserMapping.cs` → 1.
10. Recovery codes: 8 returned on verify; each works once.

## 8. Rollback

`git revert` the commit; the migration added nullable columns and a new table, so revert is safe
(the columns stay in the DB but are unused). If a full schema rollback is needed:
`dotnet ef migrations remove` then `dotnet ef database update <previous migration>` on the
rehearsal DB first.

## 9. Release steps

1. Merge PR. `unit` CI covers tests. Migration auto-applies on next `deploy-api.sh`.
2. Deploy: `bash scripts/deploy-api.sh`.
3. The operator (super-admin) logs in, navigates to Settings → MFA, scans the QR with an
   authenticator app, enters the code to verify. Recovery codes are shown once.
4. All subsequent super-admin logins require the TOTP code.

## 10. Out of scope

MFA for non-super-admin users, WebAuthn/FIDO2, SMS-based 2FA, email-based 2FA, remember-device,
trusted-device list, session list, MFA enforcement policy (e.g. "require MFA for all admins"),
backup phone number, MFA for API-key login (`login-with-key`), MFA for device-code login.

## 11. Dashboard tasks

In `pointer-dashboard/react` (after API merge + client regen):

- New `src/features/settings/MfaCard.tsx`, shown only for `isSuperAdmin`.
- If not enrolled: "Set up two-factor authentication" button → calls `POST /api/me/mfa/enrol` →
  shows QR code (render `otpauthUrl` as a QR using a `<canvas>` QR generator or inline SVG — no
  new npm dependency if possible; a small `qrcode` package is acceptable). Input for the 6-digit
  code → calls `POST /api/me/mfa/verify` → shows recovery codes in a `<pre>` block with a "Copy"
  button. Remind to save them.
- If enrolled: "Disable MFA" button → prompts for current code → calls `POST /api/me/mfa/disable`.
- Login page: when `status === "mfa_required"`, store the scoped token, show a 6-digit input,
  submit to `POST /api/auth/mfa/verify` → on success, store the normal token and redirect.
- i18n: `mfa.setup`, `mfa.disable`, `mfa.enterCode`, `mfa.recoveryCodes`, `mfa.saved`, etc. in
  both `en.json` and `ar.json`.

**Implementation consequences (as built, AGENT-TASK.md deltas over the design above):**

- The login-completion route is `POST /api/auth/mfa/verify`, not the bare `POST /api/auth/mfa`
  this section originally named — the scoped token's fence
  (`API/Extensions/MfaPendingScopeFence.cs`) is an EXACT path match, so the dashboard must call
  `/api/auth/mfa/verify` precisely (no trailing slash).
- The scoped token uses claim `scope: "mfa_pending"` (not a separate `mfa_pending: "true"` claim),
  matching the existing `select_workspace`/`impersonate` scope-claim convention so the same
  `OnTokenValidated` dispatch mechanism fences it — irrelevant to the dashboard (it only ever
  forwards the opaque token string) but noted for anyone reading the JWT payload while debugging.
- `MeResponse` gains `mfaEnabled: boolean` (true once TOTP is enabled) for the Settings page to
  decide "Set up" vs "Disable".
- Audit actions actually used: `auth.mfa.enrolled` (POST verify, once enabled),
  `auth.mfa.disabled` (POST disable), `auth.mfa.challenge_failed` (any wrong TOTP/recovery code,
  including at login) — `POST /api/me/mfa/enrol` itself is deliberately unaudited (secret
  generated, not yet enabled).
