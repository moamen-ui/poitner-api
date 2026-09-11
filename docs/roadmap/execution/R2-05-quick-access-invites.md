# R2-05 — Passwordless quick-access invites (link-copy delivery)  (§13 · Release 2 · 2–3 d)

## Goal
An admin invites a client to comment on one project and gets a **magic link** to paste into
chat/email themselves. The client opens the app URL with that link and the widget signs them in — no
password ever exists for them to type, receive or lose. Email delivery stays off (held); the existing
password-email path is kept only as a fallback behind a setting.

## Out of scope
- Email sending (held; `EmailsPerMonth` wiring later). Non-quick-access invites (`/join?code=` flow,
  `AcceptAsync`) — unchanged. Reviewer share link without the widget installed (§39).

## Prerequisites
- None hard; R2-04 recommended (a client who gets notified needs a way back in — the link).
- Facts: quick-access invites already **eagerly provision** a `User` with a generated password and
  write an `Invite` row with `Uses = MaxUses = 1` purely for audit
  (`InviteService.CreateQuickAccessInviteAsync`, `InviteService.cs:507-601`); the response `Url` is the
  project's `AppUrl` (`InviteResponse.Url`, `Application/DTOs/Invite/InviteResponse.cs`); email is sent via
  `_emailService.SendAsync` (`:593-597`) with the password in the body (`BuildQuickAccessInviteEmailHtml`,
  `:701-720`); `Project.AppUrl` required (`:525-526`); seat entitlement checked (`:538-543`);
  `Role.QuickAccess` (`Domain/Entity/Role.cs:28`); widget stores `pointer_token`/`pointer_user`
  (`web-component/src/element.ts:424-445`, `saveAuth`); the widget reads `window.location` for
  `pageUrl` (`element.ts:1115`, `window.location.href`) **and** `route` (`element.ts:1118`) — both are
  persisted in every comment's element capture, which is why the invite token must be stripped from
  the URL **before any capture can happen, on success and on failure alike**; the JWT carries
  `is_quick_access` (`JwtTokenService.cs`). Widget boot order: `connectedCallback` calls
  `this.loadAuth()` at `element.ts:168`, then `if (this.token) this.init(); else this.renderChrome();`
  at `element.ts:266-267` — `init()` (`element.ts:454`) is a *separate* method that only runs when a
  token exists, so the invite hook must sit **before `loadAuth()` in `connectedCallback`**, not inside `init()`.
- **Security fix carried by this doc:** today `CreateQuickAccessInviteAsync` generates a password
  (`InviteService.cs:546`) and emails it in plaintext (`:586`/`:593-597`, template `:701-719`, `<b>Password:</b>`
  at `:715`) — CWE-319, severity Medium–High (low-privilege tenant-scoped account, only when SMTP is
  configured). The passwordless design below removes that path by default.

## Design

### Magic-link token (new, distinct from the audit `Invite.Code`)
Table `quick_access_links` (strict-own) — `Domain/Entity/QuickAccessLink.cs : BaseEntity`
| column | type | notes |
|---|---|---|
| `OwnerId` | Guid? | tenant |
| `UserId` | Guid | the provisioned quick-access `User.PublicId` |
| `ProjectId` | int | target project (for the redirect URL and audit) |
| `InviteId` | int | FK to the audit `Invite` row |
| `TokenHash` | string(64) | SHA-256 hex of the raw token; **raw token never stored** |
| `ExpiresAt` | DateTime | Decision: 14 days default (`CreateInviteRequest.ExpiresInDays` if given) |
| `MaxUses` | int | Decision: **unlimited within TTL** (`0` = unlimited) — confirmed in review: the link is bound to exactly one provisioned low-privilege account (own comments only, no status changes — `CommentService.cs:203-204`, `:482-483`), the token is 256-bit and stored only as SHA-256, redemption is rate-limited, rotate/revoke exist; single-use would break the silent re-sign-in after the 12 h JWT expires, which is the core UX. A per-invite `MaxUses` remains available for later. Document to admins: "treat the link like a password". |
| `Uses` | int | |
| `LastUsedAt` | DateTime? | |
| `RevokedAt` | DateTime? | |
Raw token: 32 random bytes, base64url (43 chars). Magic link = `{project.AppUrl}` with query
`?pointer_invite=<token>` appended (preserve existing query/hash). Index `(TokenHash)` unique.

### Flow
1. `POST /api/admin/invites` with a QuickAccess role (existing) → `CreateQuickAccessInviteAsync`:
   - keep user provisioning **but** set `PasswordHash` to a random unusable value and a new
     `User.PasswordlessOnly = true` (additive bool column) so password login returns
     `MessageKeys.Auth.InvalidCredentials` for these accounts;
   - create the `QuickAccessLink`; return `InviteResponse.Url` = the **magic link**, plus new fields
     `MagicLink: string` (same value, explicit) and `LinkExpiresAt`;
   - email: send **only if** setting `ISettingsService.QuickAccessInviteEmailEnabled` (new key,
     default `false`) is true — and then the email carries the magic link, **not a password**
     (rewrite `BuildQuickAccessInviteEmailHtml` accordingly). `EmailSent` stays accurate.
2. Widget boot — in **`connectedCallback`, immediately before the existing `this.loadAuth()` call at
   `element.ts:168`** (not in `init()`, which only runs when a token already exists — `element.ts:266`):
   `await this.consumeInviteToken()`: if `location.search` has `pointer_invite`, **first** strip the
   param from the URL via `history.replaceState` (preserving all other query params and the hash), **then**
   call `POST /api/auth/login-with-invite { token }` → `LoginResponse`; on `ok` → `saveAuth(token, user)`
   and let the existing `if (this.token) this.init(); else this.renderChrome();` (`:266-267`) take the
   `init()` branch — no login modal. On failure → the param is already gone (so a later comment capture
   can never persist the token into `pageUrl`/`route`, `element.ts:1115,1118`), fall through to the
   normal login modal and show a one-line notice `This invite link is invalid or expired — ask for a new one.`
   `connectedCallback` becomes async-safe: wrap the pre-`loadAuth` part in an async IIFE that awaits the
   invite exchange before continuing the existing sequence (a 3 s timeout on the exchange → treat as failure).
3. `POST /api/auth/login-with-invite` (anonymous, **rate-limit policy `login`: 60 requests / 1 min per IP**
   — a new policy added to `API/Extensions/RateLimitingExtensions.cs` (R1-05 owns that file; add it there
   or in this doc's branch with a rebase note). **Do not reuse `signup`** (5/h/IP, `00-API-INVENTORY.md`
   §5): an office or agency behind one NAT IP would be locked out after five clients open their links):
   hash token → find link (not revoked, not expired, uses ok) → load user (active, approved, role
   QuickAccess) → increment `Uses`, set `LastUsedAt` → issue the normal JWT (`JwtTokenService`) →
   `LoginResponse { Status: "ok", Token, User }`. Constant-time compare not needed (hash lookup), but do
   not reveal which check failed (one message key `Invite.LinkInvalid` for all failures).
4. Admin list (`GET /api/admin/invites`) rows for quick-access invites gain `MagicLinkActive: bool`,
   `LinkExpiresAt`, `LinkUses`; new `POST /api/admin/invites/{id}/quick-link/rotate` → new token,
   revokes the old, returns the new `MagicLink`; `DELETE /api/admin/invites/{id}` (existing revoke)
   also revokes the link (`RevokedAt`).
5. Dashboard "Invite client" dialog: shows the magic link with a **Copy** button and the expiry;
   "Rotate link" action; no password anywhere.

### Session lifetime for clients
JWT lifetime is 12 h (`JwtOptions.LifetimeHours`). A client returning after 12 h with a still-valid
link is re-signed-in silently (step 2). After link expiry they need a new link — the admin rotates.

## Tasks
0. `e2e/scripts/seed.mjs` — the provisioned client becomes `PasswordlessOnly`, so replace the password login at `seed.mjs:104-106` with a magic-link redemption (`POST /api/auth/login-with-invite { token }`) and store the invite token in `state/credentials.json.client.inviteToken`.
1. `Domain/Entity/QuickAccessLink.cs`; `User.PasswordlessOnly` (bool, default false); mapping `Infrastructure/Mappings/QuickAccessLinkMapping.cs`; `AppDbContext` strict-own filter; `just migrate name="AddQuickAccessLinksAndPasswordlessOnly"`.
2. `Application/Common/TokenGenerator.cs` — `NewUrlSafeToken(32)` + `Sha256Hex(string)` (or reuse an existing helper if one exists — grep `RandomNumberGenerator` first and cite it).
3. `Application/DTOs/Auth/LoginWithInviteRequest.cs { Token }`; `InviteResponse` + `MagicLink`, `LinkExpiresAt`, `MagicLinkActive`, `LinkUses`.
4. `ISettingsService.QuickAccessInviteEmailEnabled` const + default false (follow the existing settings pattern in `Application/Services/Interfaces/ISettingsService.cs`).
5. `InviteService.CreateQuickAccessInviteAsync` — passwordless provisioning, link creation, conditional email with the new template; `RevokeAsync` also revokes the link; `RotateQuickLinkAsync(id)`; `ListAsync` fills the new fields.
6. `AuthService.LoginWithInviteAsync` + `AuthController` `POST login-with-invite` (`[AllowAnonymous]`, `[EnableRateLimiting("login")]`, `[Tags("Invites")]`, `[ProducesResponseType(typeof(LoginResponse), 200)]`); new `login` policy 60/min/IP in `RateLimitingExtensions.cs` (coordinate with R1-05, which also edits that file); `AuthService.LoginAsync` rejects `PasswordlessOnly` users.
7. `API/Controllers/Admin/InvitesController.cs` — `POST {id}/quick-link/rotate`.
8. Widget: `element.ts` — `consumeInviteToken()` in `connectedCallback` **before** `this.loadAuth()` (`:168`); `history.replaceState` strip **before** the network call (so it happens on success and failure); 3 s timeout; notice text; `apiLoginWithInvite()`; `auth-ui.ts` notice slot. `npm run build`; commit artifacts.
9. `MessageKeys.Invite.LinkInvalid`, `Invite.LinkRotated`; `Auth.PasswordlessAccount`.
10. `pointer-init.md` / dashboard help text: "Invite a client" paragraph describing the link.

## Dashboard tasks
Regenerate for `InviteResponse` (new fields), `LoginWithInviteRequest`, rotate endpoint. UI: invite dialog shows/copies the magic link + expiry, "Rotate link", removes any password text.

## Tests
- `Tests/QuickAccessLinkTests.cs`: create → link row with hash, raw token not persisted; `login-with-invite` valid → JWT with `is_quick_access=true`, `Uses` +1; expired/revoked/unknown → same failure message; rotate → old fails, new works; `DELETE` invite → link revoked; password login for `PasswordlessOnly` user → invalid credentials; seat entitlement still enforced; tenant isolation (link from tenant A cannot be listed by B).
- `Tests/InviteServiceTests.cs` — existing tests updated for `EmailSent=false` by default; email path covered with the setting on (mock `IEmailService` receives a body containing the link and **no** password).
- `cli`/widget unit: none (widget has no test runner yet — see R3-03/R3-04 for adding vitest; until then the strip-on-failure behaviour is covered by E2E only).
- E2E (`e2e/widget/quick-access.spec.ts`): admin creates client invite via API → open `FIXTURE_URL?pointer_invite=<token>` → widget signed in as client (name shown, no modal), URL cleaned → client posts a comment → API shows it with the client's `AuthorId` **and its `element.pageUrl` does not contain `pointer_invite`**; second visit with the same link works; after rotate, open the **old** link → invalid notice + login modal **and** `location.search` no longer contains `pointer_invite` (strip-on-failure). Scenario names: `quick-access: magic link signs in`, `quick-access: url param stripped on success`, `quick-access: url param stripped on failure`, `quick-access: rotated link rejected`, `quick-access: password login blocked`, `quick-access: 61st login-with-invite in a minute is 429`.

## Acceptance criteria
- [ ] Creating a Client invite returns `magicLink` of the form `<AppUrl>?pointer_invite=<43-char token>` and sends no email by default.
- [ ] Opening the link signs the client into the widget with no password step; the address bar no longer contains `pointer_invite` after load — **on success and on failure**; no stored comment's `pageUrl`/`route` ever contains `pointer_invite`.
- [ ] `login-with-invite` is limited by the `login` policy (60/min/IP), not `signup`.
- [ ] `POST /api/auth/login` with the provisioned client's email fails regardless of password.
- [ ] Rotate invalidates the previous link; revoke invalidates the link.
- [ ] The raw token appears nowhere in the DB (`SELECT * FROM quick_access_links` shows only hashes).
- [ ] `just test`, widget build green; artifacts committed.

## Rollout / compatibility
Existing quick-access users (created with passwords) keep working — `PasswordlessOnly` defaults false for them; admins can rotate to give them a link. Additive migration only.

## Report template
Files; migration name; test results; e2e summary; a screenshot/DOM dump of the widget signed in via link; confirmation of default `EmailSent=false`.
