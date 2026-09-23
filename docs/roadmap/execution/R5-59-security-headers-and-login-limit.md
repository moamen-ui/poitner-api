# R5-59 — Security headers + login rate limit + `security.txt` (§59 · Release 5 · 1 d)

**Status (2026-09-23):** shipped `5a37117` + `c55dc46` (per-host `X-Frame-Options`, heredoc
`security.txt`); **§12 amendment shipped `6ca148b`**: password login now locks after 10 failed
attempts per e-mail for 15 min (success resets), 429 + `Retry-After`; per-IP floor `login-ip`
60/min; verified live (10×400 → 429, Retry-After 900, other e-mail unaffected). CSP is
report-only; enforcement is this doc's follow-up (§10).

**Hardening `bc5e9b4` shipped** (GLM review findings M1/F5/F6/F7): e-mail max length capped at 254,
cache key hashed with SHA-256, login request body limited to 64 KB, `X-Request-Id` validated,
recipients pseudonymised in mail logs; live-verified. Follow-ups F3 (dummy-hash timing) and F4
(`KnownProxies`) remain open.

## 1. Goal

Harden the public-facing surface before any customer data arrives: add security headers to every
response via Caddy, apply a per-e-mail rate limit on password login (the only unlimited auth
endpoint today — `AuthController.Login` at `AuthController.cs:19-29` has NO `[EnableRateLimiting]`
attribute, confirmed by `Tests/AuthRateLimitingTests.cs:22-29` which asserts `Login_IsNotRateLimited`),
and serve `/.well-known/security.txt`.

Effort: 1 d.

## 2. Prerequisites (verified facts)

- **Login has no rate limit**: `AuthController.cs:19-20` — `[AllowAnonymous]`, `[HttpPost("login")]`,
  no `[EnableRateLimiting]`. `Tests/AuthRateLimitingTests.cs:22-29` `Login_IsNotRateLimited` asserts
  `Assert.Empty(rateLimits)`. The CORS comment at `Program.cs:90-91` explicitly says "Login is
  deliberately NOT rate-limited" — this was correct when the widget was the only consumer, but
  password login must be limited before public launch.
- **Existing rate limit policies**: `RateLimitingExtensions.cs:22-140` — `signup` (5/h/IP),
  `login` (60/min/IP, for magic-link redemption), `device-start`, `device-poll`, `demo`, `plans`,
  `meta`, `events`, `comments`, `builds`. The `login` policy at `:124-132` is per-IP via `ClientIp`.
  The `signup` policy is also per-IP.
- **Partition helpers**: `ClientIp` (`:42-43`) returns `RemoteIpAddress?.ToString() ?? "unknown"`;
  `PartitionKeyFor` (`:191-199`) returns `user:{id}` or `ip:{address}`.
- **Caddy config**: `Caddyfile:1-71` — four site blocks: `api.pointer.moamen.work` (`:25-52`),
  `app.pointer.moamen.work` (`:55-58`), `demo.pointer.moamen.work` (`:62-65`),
  `pointer.moamen.work` (`:68-71`). No `header` security directives exist. Shared snippet
  `(dashboard)` at `:13-23`.
- **Dashboard `index.html`**: The dashboard source (`pointer-dashboard/react`) is **not vendored into
  this repo** (`scripts/deploy-dashboards.sh` clones it separately to `~/pointer-dashboard` on the
  VM and copies its `dist/` into `~/pointer-api/dashboard/react`), so this claim cannot be verified
  file:line against this tree — treat the CSP `script-src`/`connect-src`/font assumptions below as
  unverified-in-this-repo and confirm them against the dashboard repo (or the built `dist/index.html`
  on the VM) before enforcing (non-report-only) CSP. The dashboard loads a Pointer widget script from
  the API origin (`Pointer__Server`/`VITE_POINTER_SERVER`-style config) per the dashboard repo's own
  README; fonts/CDN usage should be re-checked there.
- **Dashboard origins**: `Program.cs:98-104` — `https://app.pointer.moamen.work`,
  `https://app-react.pointer.moamen.work`, `https://demo.pointer.moamen.work`,
  `https://pointer.moamen.work`.
- **Landing**: served from `landing/` by Caddy at `pointer.moamen.work`.
- **Widget host**: the widget is embedded on arbitrary customer sites; the API serves
  `/widget.js` and `/widget.css` from the `api.pointer.moamen.work` origin.
- **Contact email**: `moamen.ui@gmail.com` (from `landing/privacy.html:151`, the file actually
  served at `pointer.moamen.work`; the three `landing-redesign/{claude,agy,glm}/privacy.html`
  drafts carry the identical line but are not live).
- **On-disk contract**: `/.well-known/security.txt` is not in `ON-DISK-CONTRACT.md` — it is not
  customer-facing, so no contract addition needed.
- **Dependencies**: independent of DB-11a/b/c/d, DB-12 (`docs/db/execution/DB-12-audit-log.md`,
  status "written; not implemented") and DB-13 (`docs/db/execution/DB-13-operator-impersonation.md`,
  same status) — headers/CSP, `security.txt`, and the login rate limit touch neither identity nor
  the audit log.

## 3. Design

### 3.1 Security headers via Caddy `header` blocks

Add a shared snippet `(security-headers)` to `Caddyfile` and `import` it in every site block.

```caddyfile
(security-headers) {
    header {
        Strict-Transport-Security "max-age=63072000; includeSubDomains; preload"
        X-Content-Type-Options "nosniff"
        X-Frame-Options "DENY"
        Referrer-Policy "strict-origin-when-cross-origin"
        Permissions-Policy "camera=(), microphone=(), geolocation=(), payment=()"
        -Server
    }
}
```

**CSP for the dashboard hosts** (`app.pointer`, `demo.pointer`):

The dashboard loads:
- Self-origin JS/CSS (fingerprinted Vite bundles)
- The Pointer widget from `api.pointer.moamen.work` (`/widget.js`, `/widget.css`)
- Images: self and `data:` (inline SVG icons)
- Fonts: self (bundled by Vite)
- API: `https://api.pointer.moamen.work`
- No external CDN, no Google Fonts, no `unsafe-eval`

Start with **report-only** CSP to avoid breaking anything:

```caddyfile
header Content-Security-Policy-Report-Only "default-src 'self'; script-src 'self' https://api.pointer.moamen.work; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self'; connect-src 'self' https://api.pointer.moamen.work; frame-ancestors 'none'; base-uri 'self'; form-action 'self'"
```

(The widget injects inline styles, so `'unsafe-inline'` is needed for `style-src`. No `unsafe-eval`.)

**For `api.pointer`**: frame-ancestors allows embedding the widget:
```caddyfile
header >X-Frame-Options "SAMEORIGIN"
header Content-Security-Policy-Report-Only "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self'; connect-src 'self'; frame-ancestors *; base-uri 'self'"
```

**For `pointer.moamen.work`** (landing): same as dashboard but self-only connect-src.

After one deploy cycle with no CSP violations in browser console, switch from `Report-Only` to
enforced `Content-Security-Policy`. Document this as a follow-up task.

### 3.2 `X-Frame-Options` / `frame-ancestors`

- Dashboard + landing: `X-Frame-Options: DENY` and `frame-ancestors 'none'` in CSP.
- API: `X-Frame-Options: SAMEORIGIN` (the `/check` page and Swagger are framed by themselves;
  the widget is loaded via `<script>`, not `<iframe>`).

### 3.3 `/.well-known/security.txt`

Served by Caddy via `respond` in the `api.pointer.moamen.work` block (simplest — no file needed):

```caddyfile
handle /.well-known/security.txt {
    respond "Contact: mailto:moamen.ui@gmail.com\nPreferred-Languages: en, ar\nExpires: 2027-09-22T00:00:00.000Z\n" 200 {
        close
    }
    header Content-Type "text/plain; charset=utf-8"
}
```

Also serve it from the landing host (`pointer.moamen.work`) and dashboard hosts.

### 3.4 Login rate limit — per-e-mail partition

Add a new policy `"password-login"` in `RateLimitingExtensions.cs`:

```csharp
o.AddPolicy("password-login", ctx =>
{
    // Partition by the email in the JSON body. The body has already been buffered by
    // ASP.NET model binding, so we parse it from the request. Fallback: partition by IP
    // if the body is unreadable.
    string partitionKey;
    ctx.Request.EnableBuffering();
    try
    {
        ctx.Request.Body.Position = 0;
        using var reader = new System.IO.StreamReader(ctx.Request.Body, leaveOpen: true);
        var body = reader.ReadToEndAsync().GetAwaiter().GetResult();
        ctx.Request.Body.Position = 0;
        var doc = System.Text.Json.JsonDocument.Parse(body);
        var email = doc.RootElement.TryGetProperty("email", out var e) ? e.GetString() : null;
        partitionKey = !string.IsNullOrWhiteSpace(email)
            ? $"email:{email.Trim().ToLowerInvariant()}"
            : $"ip:{ClientIp(ctx)}";
    }
    catch
    {
        partitionKey = $"ip:{ClientIp(ctx)}";
    }
    return RateLimitPartition.GetFixedWindowLimiter(
        partitionKey,
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(15),
            QueueLimit = 0
        });
});
```

**10 attempts per 15 minutes per email address** — generous enough for a real human who fat-fingers
their password, tight enough to make brute-force uneconomical. Falls back to per-IP when the body
is unreadable.

On `AuthController.cs:20`, add `[EnableRateLimiting("password-login")]`:
```csharp
    [AllowAnonymous]
    [HttpPost("login")]
    [EnableRateLimiting("password-login")]
```

Also apply `[EnableRateLimiting("login")]` on `LoginWithKey` (`:48-55`) — API-key login should have
the same magic-link rate limit (it was missing).

### 3.5 Update the test

`Tests/AuthRateLimitingTests.cs:22-29` `Login_IsNotRateLimited` — **rename** to
`Login_HasPasswordLoginRateLimit` and assert that the `[EnableRateLimiting]` attribute is present
with policy `"password-login"`. Add a new test `LoginWithKey_HasLoginRateLimit` asserting policy
`"login"`.

**Also update the class-level XML doc comment** (`AuthRateLimitingTests.cs:10-17`) — it currently
reads "BINDING: the per-IP `signup` limiter exists to throttle account-creation and password-email
abuse — NOT login… These assertions fail loudly if the limiter ever creeps back onto login" — i.e.
it documents the *old* decision this doc reverses, in language ("BINDING", "fail loudly if… creeps
back onto login") that will actively mislead the next reader once `Login_IsNotRateLimited` is gone.
Rewrite it to state the new binding: login IS rate-limited, per-e-mail (not per-IP, to avoid
locking out a shared NAT), while `signup` remains the separate per-IP account-creation guard.

Also update the CORS comment in `Program.cs:90-91` to remove the statement "Login is deliberately
NOT rate-limited" — replace with "Login is rate-limited per e-mail (R5-59)".

## 4. Safety / impact

**Additive** — Caddy config changes (headers, security.txt respond), one new rate-limit policy,
one attribute on two controller methods, test updates. No migration, no data change.

**Risk**: CSP in report-only mode cannot break anything. The login rate limit is 10/15min per email,
which is very generous for normal use. The `EnableBuffering` + body parse in the rate limiter is a
known ASP.NET pattern and does not interfere with model binding.

## 5. File-level tasks

1. **`Caddyfile`** — add `(security-headers)` snippet after `(dashboard)` (`:23`); `import
   security-headers` in each of the four site blocks; add per-host CSP `Content-Security-Policy-Report-Only`
   headers and `X-Frame-Options` overrides; add `handle /.well-known/security.txt` in each site block.
2. **`API/Extensions/RateLimitingExtensions.cs`** — add `"password-login"` policy after `:132`.
3. **`API/Controllers/AuthController.cs`** — add `[EnableRateLimiting("password-login")]` on `Login`
   (`:20`); add `[EnableRateLimiting("login")]` on `LoginWithKey` (`:48`).
4. **`API/Program.cs`** — update the CORS comment at `:90-92`.
5. **`Tests/AuthRateLimitingTests.cs`** — rename `Login_IsNotRateLimited` → `Login_HasPasswordLoginRateLimit`,
   assert policy = `"password-login"`; add `LoginWithKey_HasLoginRateLimit`.

## 6. Tests

Updated `Tests/AuthRateLimitingTests.cs`:

1. **`Login_HasPasswordLoginRateLimit`** — `typeof(AuthController).GetMethod("Login")` has
   `[EnableRateLimiting]` with `PolicyName == "password-login"`.
2. **`LoginWithKey_HasLoginRateLimit`** — `typeof(AuthController).GetMethod("LoginWithKey")` has
   `[EnableRateLimiting]` with `PolicyName == "login"`.
3. Existing `SignupSurface_KeepsSignupRateLimit`, `DeviceCodeSurface_HasItsOwnRateLimit`,
   `RateLimiter_RejectsWith429_NotDefault503`, `RateLimiter_OnRejected_SetsRetryAfterSeconds`
   remain unchanged.

## 7. Acceptance criteria

1. `curl -sI https://app.pointer.moamen.work/ | grep -i strict-transport-security` → header present.
2. `curl -sI https://api.pointer.moamen.work/api/branding | grep -i x-frame-options` → `SAMEORIGIN`.
3. `curl -sI https://app.pointer.moamen.work/ | grep -i x-frame-options` → `DENY`.
4. `curl -sI https://app.pointer.moamen.work/ | grep -i content-security-policy` → `Report-Only` CSP
   present with `script-src 'self' https://api.pointer.moamen.work`.
5. `curl -s https://api.pointer.moamen.work/.well-known/security.txt` → contains
   `Contact: mailto:moamen.ui@gmail.com`.
6. `curl -s https://pointer.moamen.work/.well-known/security.txt` → same.
7. `dotnet test --filter AuthRateLimitingTests` → green (≥6 tests).
8. 11th `POST /api/auth/login` with the same email in 15 min → 429.
9. `grep -c EnableRateLimiting API/Controllers/AuthController.cs` → 10 (was 8).
10. `curl -sI https://api.pointer.moamen.work/ | grep -i permissions-policy` → present.

## 8. Rollback

`git revert` the code changes; revert the `Caddyfile` to the previous version and
`docker compose -f docker-compose.prod.yml up -d --force-recreate caddy`. The API
`git revert` + `deploy-api.sh` removes the rate limit.

## 9. Release steps

1. Merge PR. `unit` CI covers the test changes.
2. On the VM: `git pull --ff-only`. Deploy API first (`bash scripts/deploy-api.sh`).
3. Then recreate Caddy for the Caddyfile changes:
   `docker compose -f docker-compose.prod.yml up -d --force-recreate caddy`.
4. Verify criteria 1–6 from any machine.
5. Monitor browser console on `app.pointer.moamen.work` for CSP violations over 24 h.
6. If no violations: follow-up PR to change `Content-Security-Policy-Report-Only` → enforced
   `Content-Security-Policy`.

## 10. Out of scope

Enforced CSP (report-only first), CORS changes, CSRF tokens (bearer API, no cookies), HPKP
(deprecated), Expect-CT (deprecated), Subresource Integrity on the widget (the widget is served
from the same origin), rate limiting on other endpoints, IP-based login blocking,
account-lockout after N failures, CAPTCHA.

## 11. Follow-up — e2e suite adjustment (2026-09-23)

The `password-login` policy (10 req / 15 min, per normalised e-mail) broke the e2e suite's `api`
phase in CI: the shared login helper (`e2e/scripts/lib/api.mjs`'s `login()`) re-authenticated every
seeded persona once per spec file (dozens of times per run for `wsAdmin`/`superAdmin`), which blew
through the new budget and turned into a wall of `429`s (run `35787191644`, 14 failed tests). Fixed
in the suite, not here: `login()` now caches the JWT per `(baseUrl, email)` for the life of the
Playwright process (single-worker config, so this is a real per-phase cache), with a `forceFresh`
escape hatch for a spec that ever needs a guaranteed-real round trip, and an explicit
rate-limited error message when a login does still get a `429`. After caching, the `api` phase logs
each persona in exactly once (≤ 10/15 min per e-mail, comfortably under the policy). See
`e2e/api/key-store.spec.mjs` (DB-07 dropped `users.api_key` — unrelated column-removal drift caught
in the same pass) and `e2e/api/origins.spec.mjs`/`e2e/api/tenant-invite.spec.mjs` for two further
spec-side fixes unrelated to this policy (a pre-existing wildcard-app-url seed collision, and an
`emailSent` assertion that depends on a mail transport CI does not configure).

## 12. Amendment 2026-09-23 — password login limits FAILED attempts, not logins

**Why.** The shipped `password-login` fixed-window policy (10 requests / 15 min per normalised e-mail)
counted successful logins too. The first full e2e run on `main` proved the consequence: browser-driven
specs sign in through the real form and the same account hit 429 after ten sign-ins; a stakeholder
signing in from several devices, or a QA person re-testing a flow, would hit exactly the same wall.
Brute-force protection must count failures.

**Design (replaces §3's policy for `POST /api/auth/login` only; the Caddy headers and `security.txt`
are unchanged).**
1. Remove `[EnableRateLimiting("password-login")]` from `AuthController.Login`; delete the
   `password-login` policy and its tests. Add `[EnableRateLimiting("login-ip")]` instead: a lenient
   per-IP fixed window (60 requests / 1 min, `QueueLimit = 0`) as a flood floor — partition on
   `ClientIp` exactly like the existing `signup` policy.
2. New `ILoginAttemptLimiter` (Application) + `LoginAttemptLimiter` (Infrastructure or API/Auth) backed
   by `IMemoryCache`: key `login-fail:<normalised e-mail>`, sliding 15-minute window, threshold 10.
   `AuthService.LoginAsync`: before verifying the password call `IsLockedAsync(email)` → if locked
   return `Result.Failure(MessageKeys.Auth.TooManyAttempts)` mapped to HTTP **429** with
   `Retry-After: <seconds left>`; on wrong password call `RecordFailureAsync(email)`; on success call
   `ResetAsync(email)`. Unknown e-mails count as failures too (no enumeration difference).
   Configuration `Auth:LoginLockout:{Threshold=10, WindowMinutes=15}`; a single-process in-memory store
   is acceptable today (one API instance); note the Redis swap point for the future.
3. Response shape: the existing `Result` envelope with `status: "locked"`; the dashboard shows the
   existing generic error plus the retry hint (no new client type — `LoginResponse.Status` already
   carries `"disabled"`, add `"locked"`).
4. Tests: `Tests/LoginAttemptLimiterTests.cs` (10 failures → locked; success resets; window expiry
   unlocks; e-mail normalisation shares the counter; unknown e-mail counts) and update
   `Tests/AuthRateLimitingTests.cs` (`Login_HasPasswordLoginRateLimit` → `Login_HasIpFloorRateLimit`
   asserting `login-ip`; keep `LoginWithKey_HasLoginRateLimit`).
5. Acceptance: 10 wrong passwords for one e-mail → the 11th returns 429 with `Retry-After`, while a
   different e-mail from the same IP still gets 400; one correct login after 9 failures resets the
   counter; 61 requests in a minute from one IP → 429 regardless of e-mail; e2e widget phase green.
6. Release: ordinary deploy (`scripts/deploy-api.sh`); no migration.
