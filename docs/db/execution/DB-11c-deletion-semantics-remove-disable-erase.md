# DB-11c — Remove / disable / leave / erase: who may end what, and the sole-admin guard

Depends on [DB-11a](DB-11a-identity-and-workspace-memberships.md) in production; independent of
[DB-11b](DB-11b-login-workspace-picker-and-switch.md) (either order). Owner requirements 2026-09-22:
"super admin can delete the user and the user can delete his account while the workspace admin can
move away the user out the workspace or disable/enable it but can't delete the user while it might
be in other workspaces"; per-user erase (GDPR) must exist; revoking an already-accepted invite should
offer disabling the invitee; bug **S-13** (super admin can demote a tenant's only Workspace Admin).
Rules: R1 (one nullable column), R5, R7 (additive → ordinary deploy), R8, R10, R16.
**Class: Additive** (one migration: `users.erased_at`) **+ code that destroys per-user secrets on
explicit request** (erase deletes API keys, device logins, quick-access links, the person's inbox and
personal AI rules, and scrubs the person's address from `invites.email` — by design, on the account
holder's explicit action (password, or a one-time e-mailed link for magic-link accounts) or a super
admin's explicit call, never by a schema step). No approval line needed: the migration is additive; the destructive behaviour is a
product feature the owner asked for.
**Status 2026-09-22: written; not implemented.** Owner decisions D8, D9, D10, D12 and F5 have defaults (§3.8).
**Amended 2026-09-22 (evening)** after the cross-review (`docs/roadmap/meetings/2026-09-22-foundations/04-chair-synthesis.md`
§1 rows D2/D3/D9/D10 = GLM A2, GLM A3, agy A1, agy A3; cited below by finding id because D8–D12 here are
owner decisions): **GLM A2** erase tombstones `invites.email` for the person's address inside the
transaction (§3.4 step 2b, inventory table); **GLM A3** magic-link identities erase themselves through a
one-time e-mailed link (§3.4a/§3.4b, two endpoints); **agy A1** screenshots — founder default **F5 = keep**
(§3.4 step 4, §3.8, privacy sentence in §11); **agy A3** legal hold — one forward-reference sentence (§3.4).

## 1. Goal

Give each actor exactly the verbs the owner described, on the membership model of DB-11a:

| Actor | Verb | Effect |
|---|---|---|
| Workspace Admin / Deputy | **remove from workspace** (`DELETE /api/admin/users/{id}`) | ends the membership (kept for audit), revokes **that workspace's** sessions, API keys and magic links; the identity and its other memberships are untouched |
| Workspace Admin / Deputy | **disable / enable** (`PATCH /api/admin/users/{id}` `isActive`) | flips the membership; disabled = that workspace's sessions stop within 60 s, its key stops logging in, notifications stop; reversible |
| any member | **leave workspace** (`POST /api/me/leave-workspace`) | ends own membership; same revocations |
| any member | **delete my account** (`DELETE /api/me`, password confirmed; a magic-link account instead confirms through a one-time e-mailed link — `POST /api/me/request-erase` → `POST /api/auth/confirm-erase`, §3.4b) | **erase**: identity becomes a tombstone (`Deleted user`, `erased+<id>@tombstone.invalid`), secrets cleared, every membership ended, keys/logins/links/inbox deleted; **comments and replies stay**, attributed to the tombstone |
| super admin | **erase any identity** (`DELETE /api/admin/identities/{publicId}`) | same erase |
| nobody | delete an identity that is the **sole Workspace Admin** anywhere, or demote / remove / disable / leave as the sole admin | blocked with the list of workspaces; **transfer first** (`POST /api/admin/users/{deputyPublicId}/promote`) — this closes S-13 for super admins too |

A Workspace Admin who has joined other workspaces is one identity with several memberships; each
verb above acts on one membership except erase, which acts on all of them and is blocked while any
of them is a sole-admin membership. `workspaces.id` is unrelated to any `public_id` since DB-11a, so
erasing the founding admin never touches the workspace row.

## 2. Prerequisites (verified facts, 2026-09-22 @ `2756bd6`, plus DB-11a's additions)

- `UserService.DeleteAsync` (`UserService.cs:312-350`): guards `CannotDeleteSelf` `:322-323`,
  `CannotDeleteAdmin` `:325-326`, deputy matrix `:328-338`; after DB-11a it ends the membership.
  `UpdateAsync` `:249-308`: escalation guard `:263-264`, self-demotion guard `:270-276`
  (`CannotChangeSelfFromAdmin` — only protects the caller's **own** row, not another admin's, and not
  against a super admin — that is S-13), stamp rotation `:281-282,294-295`.
  `TransferOwnershipAsync` `:357-409`. Message keys `MessageKeys.User.*` (`MessageKeys.cs:25-44`).
- `TenantService.SetStatusAsync` (`TenantService.cs:255-297`) — super admin approve/enable/disable of
  a tenant's admin (after DB-11a: of the admin's membership).
- `ApiKey.RevokedAt` (`Domain/Entity/ApiKey.cs`, "regeneration revokes rather than deletes");
  `QuickAccessLink.RevokedAt`; `InviteService.RevokeLinksForInviteAsync` (`InviteService.cs:405-418`)
  and `RevokeAsync` `:302-321` (returns `Result.Success(MessageKeys.Invite.Revoked_Ok)`);
  `API/Controllers/Admin/InvitesController.cs:39-51 Revoke` returns `Result`.
  `TenantInviteService.RevokeAsync` (`:107-`) is the super-admin new-workspace invite — untouched.
- `DeviceLogin` rows: `user_id` uuid, `device_code_hash`; swept inline > 1 day past expiry
  (`DeviceLoginService.cs:47-56`). `Notification.UserId` (recipient), `ActorId`. `AiRule.UserId`
  (personal rule when non-null). `PredefinedAction.UserId` (personal action when non-null).
- `MeController` (`API/Controllers/MeController.cs`): `[Route("api/me")]`, `[Authorize]`,
  `[Produces]`, no `[Tags]` → tag `Me` (in `orval.config.ts:6`). `UsersController`
  (`API/Controllers/Admin/UsersController.cs`): `[Route("api/admin/users")]`, `Policies.Admin`, tag `Users`.
  `TenantsController` `Policies.SuperAdmin`, `[Tags("Tenants")]`.
- Retention job `API/Hosted/RetentionService.cs` (DB-08) — `RetentionOptions` with `*Days` knobs,
  `Retention:*` config, sweeps in `SweepXAsync` methods (precedent if D8 ever changes).
- After DB-11a: `WorkspaceMembership { IsActive, LeftAt, LeftReason, SecurityStamp, InviteId, Role, User }`,
  `MembershipEndReason { Removed=1, Left=2, AccountErased=3 }`, `IMembershipService`
  (`InWorkspace`, `CurrentAdminAsync`, `GetMembershipAsync`, `ListForIdentityAsync`), `UserNameResolver`,
  `users.merged_into_user_id`, `ux_users_email_live (lower(email)) WHERE deleted_at IS NULL` (expression
  index, DB-11a GLM A1), `EmailNormalizer` (DB-11a §3.3a), stamp validator checking `mstamp` + membership liveness.
- `Invite.Email` (`Domain/Entity/Invite.cs:37-38`): "Optional email lock: only this (normalized) email may
  accept. **Null = anyone with the link**" — so scrubbing must overwrite, never null. Super-admin
  new-workspace invites are the same entity (`TenantInviteService.cs:37,62`). DB-08 sweeps only
  never-used expired/revoked invites (`RetentionService`), so an accepted invite keeps its e-mail forever.
- `IResetTokenService` (`Application/Abstractions/IResetTokenService.cs:14,21`) / `Infrastructure/Auth/ResetTokenService.cs:25-50`:
  stateless HMAC-SHA256 token `{publicId:N}.{stamp:N}.{expUnix}.{sig}` (4 parts, signed over `id|stamp|exp`
  with `JWT:SigningKey`, TTL 30 min, constant-time compare), registered singleton
  (`Infrastructure/DependencyInjection.cs:45`). Callers: `AuthService.RequestPasswordResetAsync` `:69`
  (`Create`) and `ResetPasswordAsync` `:113-135` (`TryValidate` + stamp equality = single-use). The reset
  e-mail is the template to copy: `AuthService.cs:66-100` (`_branding.BuildResponseAsync("", …)`,
  `brand.Urls.App`, link `{app}/reset?token=…`, `WorkspaceNameResolver.ResolveForEmailAsync`,
  `_emailService.SendAsync(to, subject, html)` in a best-effort `try/catch`). `IEmailService`
  (`Application/Services/Interfaces/IEmailService.cs:10`) is capped per day; sends are best-effort.
- Screenshots: `Comment.Element.ScreenshotUrl` (`Domain/ValueObjects/ElementCapture.cs:12`) → files under
  `wwwroot/uploads/<ownerN>/<project>/` (`Infrastructure/Storage/LocalFileStorage.cs:14,26`). Deleted only
  with the comment (`CommentService.cs:821-825`, `_fileStorage.DeleteAsync`) or the workspace
  (`TenantService.cs:456`, `DeleteOwnerFilesAsync`). There is no per-author file path.
- Rate limiting: `forgot-password`/`reset-password` carry `[EnableRateLimiting("signup")]`
  (`AuthController.cs:81,92`; 5/h per IP); `Tests/AuthRateLimitingTests.cs:31-44` is a `[Theory]` over
  `AuthController` action names. `MeController` actions carry no rate limit today.
- Dashboard anonymous token page precedent: route `/reset` (`../pointer-dashboard/react/src/App.tsx:45`) →
  `features/auth/ResetPasswordPage.tsx`; profile page `features/profile/ProfilePage.tsx`. Widget: sign-out
  wiring `web-component/src/element.ts:1283-1285`; `this.user.isQuickAccess` `element.ts:2600`.
- Dashboard users page actions (`../pointer-dashboard/react/src/features/users/UsersPage.tsx:251-260,478-480`):
  enable/disable via `PATCH isActive`, delete via `DELETE`. Invites page calls `DELETE /api/admin/invites/{id}`.

## 3. Design

### 3.1 Schema — one column

`users.erased_at timestamptz NULL` (`User.ErasedAt`; mapping `HasColumnName("erased_at")`).
Non-null ⇔ the row is a tombstone (§3.4). No index. Migration `AddUsersErasedAt`: exactly one
`AddColumn`; `Down()` drops it. Additive → auto-applies on boot (R7).

### 3.2 The sole-admin guard (S-13) — `MembershipService`

```csharp
/// <summary>S-13 (DB-11c). A workspace must never lose its last Workspace Admin: returns the
/// workspaces (id, name) in which <paramref name="membershipIds"/> are the ONLY live Workspace Admin
/// membership. Empty = safe. Applies to every actor, super admins included; the way out is
/// TransferOwnershipAsync. Tenant suspension by a super admin (TenantService.SetStatusAsync) is
/// exempt by design (D10): a disabled admin is recoverable, an admin-less workspace is not.</summary>
Task<List<(Guid WorkspaceId, string Name)>> SoleAdminWorkspacesAsync(IEnumerable<int> membershipIds);
```
Implementation: for each membership id with `LeftAt == null` and `Role.Name == "Workspace Admin"`,
count live Workspace Admin memberships in the same `OwnerId`; keep those with count == 1; join names
from `Workspaces` (IgnoreQueryFilters). Failure shape used everywhere (one helper in `MembershipService`):
`Result.Conflict(string.Format(MessageKeys.User.SoleAdminBlocked, string.Join(", ", names)))`
with `MessageKeys.User.SoleAdminBlocked = "Blocked: this person is the only Workspace Admin of {0}. Promote a deputy there first."`

Applied at: `UserService.UpdateAsync` when `request.RoleId` changes a membership **away from**
Workspace Admin (any caller — replaces the self-only `CannotChangeSelfFromAdmin` check; keep that
key for the self case's message) and when `request.IsActive == false`; `UserService.DeleteAsync`
(replaces `CannotDeleteAdmin`); `LeaveWorkspaceAsync`; `EraseAsync` (all of the identity's live
memberships). **Not** applied at `TenantService.SetStatusAsync` (D10) nor `TransferOwnershipAsync`
(it is the resolution).

### 3.3 Ending a membership — one routine

`IMembershipService.EndAsync(WorkspaceMembership m, MembershipEndReason reason, Guid actor)`:
1. `m.LeftAt = UtcNow; m.LeftReason = reason; m.IsActive = false; m.SecurityStamp = Guid.NewGuid();` (`Update`).
2. Revoke this workspace's credentials of this identity — all with `IgnoreQueryFilters()` and an explicit `OwnerId == m.OwnerId` predicate:
   `ApiKeys.Where(k => k.UserId == m.UserId && k.OwnerId == m.OwnerId && k.RevokedAt == null)` → `RevokedAt = now`;
   `QuickAccessLinks.Where(l => l.UserId == m.User.PublicId && l.OwnerId == m.OwnerId && l.RevokedAt == null)` → `RevokedAt = now`;
   `DeviceLogins.Where(d => d.UserId == m.User.PublicId && d.OwnerId == m.OwnerId && d.Status == Approved)` → `Status = Denied` (an approved-not-yet-consumed code must not hand out a key later).
3. Does **not** `SaveChanges` (caller decides the transaction). Comments, replies, notifications
   already delivered, audit columns: untouched.

Disable (`IsActive = false`) is **not** `EndAsync`: it flips `IsActive`, rotates the membership
stamp, and leaves keys/links in place — the membership check in `login-with-key`, the magic-link
path and the stamp validator make them inert while disabled and live again on enable.

### 3.4 Erase — `IdentityEraseService.EraseAsync(User identity, Guid actor)`

(new `Application/Services/Implementation/IdentityEraseService.cs : IIdentityEraseService`; Scrutor.)
Precondition (caller checks and returns the Conflict): `SoleAdminWorkspacesAsync(all live memberships)` is empty.
Capture first: `var pid = identity.PublicId; var originalEmail = identity.Email; var tombstoneEmail = $"erased+{pid:N}@tombstone.invalid";`.
Inside `ExecuteInTransactionAsync`:
1. `EndAsync(m, AccountErased, actor)` for every live membership.
2. Hard-delete (`RemoveRange`, `IgnoreQueryFilters`): `ApiKeys.Where(k => k.UserId == identity.Id)` (all, including revoked — they are secrets);
   `DeviceLogins.Where(d => d.UserId == pid)`; `QuickAccessLinks.Where(l => l.UserId == pid)`;
   `Notifications.Where(n => n.UserId == pid)` (the person's inbox); `AiRules.Where(r => r.UserId == pid)` (personal rules, D9).
2b. **Invites (GLM A2).** `Invites.IgnoreQueryFilters().Where(i => i.Email == originalEmail)` → `i.Email = tombstoneEmail`, `Update`
   — every row, whether accepted, open, expired or revoked (DB-08 never sweeps a used invite, so the address
   would otherwise survive indefinitely). **Overwrite, never null:** `Invite.Email == null` means "anyone
   with the link may accept" (`Invite.cs:37`), so nulling would *unlock* a still-open invite. The tombstone
   address keeps it locked (no live identity can ever present it) and removes the person's address from the
   row. Covers admin invites, quick-access invites and super-admin new-workspace invites (same entity).
3. Tombstone the row: `Email = tombstoneEmail` (unique by construction; `.invalid` is reserved, RFC 2606; lower-case,
   so `ux_users_email_live` is satisfied), `DisplayName = "Deleted user"`, `PasswordHash = <hash of a random 64-char secret>`,
   `PasswordlessOnly = true`, `Language = Theme = AddCommentShortcut = null`, `RecipientEmail = null`,
   `IsActive = false`, `SecurityStamp = Guid.NewGuid()`, `ErasedAt = now`, `DeletedAt = now` (so every
   `DeletedAt == null` read — login, reset, `AdminSeeder` e-mail match, `ListForIdentityAsync` — skips it).
   **`PublicId` is kept** so `comments.author_id`, `replies.author_id`, `notifications.actor_id` and
   audit columns keep resolving (to "Deleted user"). `user_aliases` rows pointing at it stay.
4. Kept on purpose: `comments`, `replies` (content the workspace owns), `predefined_actions` with
   `user_id` (workspace content; D9), `usage_events.user_id` (analytics; the tombstone id is not
   personal data), `notifications` where `actor_id == pid` (other people's inboxes; render "Deleted user"),
   **and the screenshots attached to the person's comments (owner decision F5, default *keep*, §3.8; agy A1).**
   A screenshot (`comments.element.screenshot_url` → `wwwroot/uploads/<workspace>/<project>/…`) depicts the
   *customer's application*, not the author; it is the workspace's asset exactly like the comment text, and
   it is deleted only with the comment (`CommentService.cs:821-825`) or the workspace (`TenantService.cs:456`).
   `IdentityEraseService` therefore does **not** take an `IFileStorage` dependency (acceptance criterion 7).
5. `UserNameResolver`: no change needed — the tombstone row resolves by `PublicId` (it is soft-deleted;
   the resolver must **not** filter on `DeletedAt`; DB-11a's resolver does not).

**Erase inventory** — every column that can carry the person's address or name, and what erase does with it.
A new column that carries a person's e-mail or name is added here **and** to `EraseAsync` in the same PR
(DB-RULES R14, amended); test 11 mechanises the e-mail half.

| Column(s) | Holds | On erase |
|---|---|---|
| `users.email`, `users.display_name`, `users.password_hash`, `users.security_stamp` | the identity | overwritten (step 3) |
| `users.recipient_email` (demo real address) | e-mail | nulled (step 3) |
| `invites.email` | accept lock = the person's address | tombstoned (step 2b) |
| `api_keys` rows with `user_id = users.id` | secrets | deleted (step 2) |
| `device_logins.user_id = pid`, `quick_access_links.user_id = pid` | credentials | deleted (step 2) |
| `notifications.user_id = pid` | the person's inbox (may quote their name) | deleted (step 2) |
| `ai_rules.user_id = pid` | personal rules | deleted (step 2, D9) |
| `comments.author_id/applied_by/edited_by`, `replies.author_id`, `notifications.actor_id`, `predefined_actions.user_id`, `predefined_action_suggestions.reviewed_by`, `usage_events.user_id`, every `created_by/updated_by/deleted_by` | a uuid only | kept; resolves to "Deleted user" |
| `comments.body`, `replies.body` | text the person wrote (may contain names) | kept — workspace content |
| screenshots (`comments.element.screenshot_url` → files) | images of the customer's app | kept (F5) |
| `usage_events.meta` (jsonb) | today nothing personal — no production writer passes `meta` (`UsageEventService.cs:40-44`; GLM E4) | kept; a future writer that stores PII must add a scrub line here |
| `user_aliases` | old uuids → identity | kept (audit of the merge) |

**Legal hold (forward reference — agy A3, chair D10).** A later doc adds a per-workspace flag in
`workspace_settings` that makes `EraseAsync` refuse with `Conflict` for any identity holding a live
membership in that workspace and pauses the DB-08 sweeps for it; the flag, its endpoint and its audit
row are that doc, not this one — this doc only keeps `EraseAsync` the single erase routine so the check
has one place to live.

Retention of tombstones: **forever** (D8). If the owner later wants a purge, it is a `RetentionService`
sweep that hard-deletes tombstones older than N days **and** rewrites their `author_id`s to a single
shared sentinel — a separate doc.

### 3.4a Scoped one-time tokens — `IResetTokenService` extension (used by §3.4b and by DB-11d)

Today's token has no notion of *purpose* (§2). Add two members to `Application/Abstractions/IResetTokenService.cs`
and implement them in `Infrastructure/Auth/ResetTokenService.cs`; existing `Create`/`TryValidate` are untouched,
so outstanding reset links keep working:
```csharp
/// <summary>
/// Purpose-bound variant (DB-11c). Format "{publicId:N}.{stamp:N}.{expUnix}.{purpose}.{payload}.{sig}" — SIX
/// parts, signed over "id|stamp|exp|purpose|payload"; payload is base64url(UTF-8) or empty. A scoped token never
/// validates through TryValidate (which requires four parts) and a reset token never validates through
/// TryValidateScoped (six parts + purpose match), so an e-mailed link can only do the one thing it was minted
/// for. Same 30-minute TTL; same stamp binding — the caller rotates users.security_stamp on success, which makes
/// the token single-use. purpose must match ^[a-z-]+$ (a '.' would break the split; assert it).
/// </summary>
string CreateScoped(Guid userPublicId, Guid securityStamp, string purpose, string? payload = null);
bool TryValidateScoped(string token, string purpose, out Guid userPublicId, out Guid securityStamp, out string? payload);
```
`TryValidateScoped`: `Split('.')` → exactly 6 parts; `parts[3]` equals `purpose` (ordinal); `parts[2]` parses and is
in the future; signature over `$"{parts[0]}|{parts[1]}|{exp}|{parts[3]}|{parts[4]}"` compared with
`CryptographicOperations.FixedTimeEquals` (copy `:43-46`); then `Guid.TryParseExact(…, "N")` for id and stamp;
`payload = parts[4].Length == 0 ? null : UTF8(base64url-decode(parts[4]))`. Purpose constants:
`Application/Common/TokenPurposes.cs` — `public static class TokenPurposes { public const string Erase = "erase"; public const string ChangeEmail = "change-email"; }`
(`ChangeEmail` is consumed by DB-11d).

### 3.4b Erase confirmation for magic-link identities (GLM A3, chair D3)

A `PasswordlessOnly` identity (a quick-access Client living in the widget inside a customer's app) has no
password to present, and a Workspace Admin can only *remove*, never erase — so without this rail the
erasure right of the least technical users is "e-mail the operator". The rail reuses the stateless token
service and the `reset-password` shape (`AuthController.cs:79-99`):

1. **`POST /api/me/request-erase`** — `MeController`, `[Authorize]` (class-level), `[EnableRateLimiting("signup")]`
   (it sends e-mail; same 5/h-per-IP budget as `forgot-password`), no body, `[ProducesResponseType(typeof(Result), 200)]`.
   → `IIdentityEraseService.RequestEraseLinkAsync()`: identity by `sub` (live, `IgnoreQueryFilters`); super admin →
   `Forbidden(User.CannotEraseSuperAdmin)`; `!PasswordlessOnly` → `Failure(User.EraseUsePassword)` (password accounts
   confirm with the password — one rail per account kind); else `token = _resetTokens.CreateScoped(pid, identity.SecurityStamp, TokenPurposes.Erase)`,
   `link = $"{brand.Urls.App.TrimEnd('/')}/delete-account?token={Uri.EscapeDataString(token)}"`, and
   `_emailService.SendAsync(identity.Email, $"Confirm deleting your {brand.ProductName} account", html)` inside the
   same best-effort `try/catch` as `AuthService.cs:83-100`. Body text (verbatim, HTML-encode nothing dynamic but the
   link): "You asked to delete your account. Click the link below to confirm — it expires in 30 minutes. Your
   feedback stays with the workspaces you commented in and is shown as 'Deleted user'. If you did not ask for
   this, ignore this e-mail; nothing happens." Return `Success(User.EraseLinkSent)`. The sole-admin guard is
   **not** evaluated here (nothing is destroyed yet; it runs at step 2).
2. **`POST /api/auth/confirm-erase`** — `AuthController`, `[AllowAnonymous]`, `[EnableRateLimiting("signup")]`,
   body `ConfirmEraseRequest { string Token }` (validator `NotEmpty`), `[ProducesResponseType(typeof(Result), 200)]`
   + 400 + 409. → `IIdentityEraseService.EraseByTokenAsync(string token)`:
   `TryValidateScoped(token, TokenPurposes.Erase, out pid, out stamp, out _)` else `Failure(User.EraseLinkInvalid)`;
   identity live by `pid` (`IgnoreQueryFilters`, `DeletedAt == null`) **and** `identity.SecurityStamp == stamp`, else
   the same failure (single-use: §3.4 step 3 rotates the stamp); `!identity.PasswordlessOnly` → the same failure
   (a password account never erases by link); super admin → the same failure; sole-admin → `Conflict` (§3.2);
   `EraseAsync(identity, actor: pid)`; `Success(User.Erased)`. Every failure is the one message (as
   `LoginWithInviteAsync`, `AuthService.cs:573-574`: a guessed token must not learn which check failed).
3. **`DELETE /api/me`** (password path, §3.5) is unchanged for password accounts; for a `PasswordlessOnly`
   identity it now returns `Failure(User.EraseNeedsEmailConfirmation)` pointing at step 1 instead of the former
   `Forbidden(EraseNeedsPassword)` (that key is dropped).

Why anonymous: the Client has no dashboard session; the e-mailed token *is* the credential, exactly as
`reset-password`. Why the dashboard hosts the landing page: `brand.Urls.App` is the only URL the API knows
that is ours (the widget is on the customer's origin); the page is anonymous and tiny (§11). Why one message:
enumeration resistance, as everywhere else on the anonymous auth surface.

### 3.5 Endpoints

| Route | Policy | Service | Result |
|---|---|---|---|
| `DELETE /api/admin/users/{id}` (exists) | Admin | `UserService.DeleteAsync(int id)` → **RemoveFromWorkspaceAsync**: membership `(users.id == id, caller tenant)`; guards: self → `CannotRemoveSelf` (renamed message: "Use Leave workspace instead."); sole admin (§3.2); deputy matrix as today; then `EndAsync(Removed)` | `Result` (unchanged shape) |
| `PATCH /api/admin/users/{id}` (exists) | Admin | `UpdateAsync`: role change or `isActive=false` on a sole admin → §3.2 Conflict; `isActive` flips membership + membership stamp | `UserResponse` |
| `POST /api/me/leave-workspace` (new) | any authenticated tenant user | `UserService.LeaveWorkspaceAsync()`: membership `(caller, tenant)`; sole admin → Conflict; `EndAsync(Left)`; response tells the client to drop its token | `Result` |
| `DELETE /api/me` (new) | any authenticated tenant user | body `DeleteMyAccountRequest { string Password }`; `IdentityEraseService.EraseSelfAsync(request)`: identity by `sub`; super admin → `Forbidden(User.CannotEraseSuperAdmin)`; `PasswordlessOnly` identity → `Failure(User.EraseNeedsEmailConfirmation)` (§3.4b step 3); password verify else `Failure(CurrentPasswordIncorrect)`; sole-admin Conflict; erase | `Result` |
| `POST /api/me/request-erase` (new) | any authenticated tenant user | `IdentityEraseService.RequestEraseLinkAsync()` — §3.4b step 1 (`PasswordlessOnly` only; e-mails a 30-min scoped token); `[EnableRateLimiting("signup")]` | `Result` |
| `POST /api/auth/confirm-erase` (new) | anonymous, `[EnableRateLimiting("signup")]` | body `ConfirmEraseRequest { string Token }`; `IdentityEraseService.EraseByTokenAsync(token)` — §3.4b step 2 | `Result` (200 / 400 / 409) |
| `DELETE /api/admin/identities/{publicId}` (new, `API/Controllers/Admin/IdentitiesController.cs`, `[Route("api/admin/identities")]`, `[Tags("Identities")]`, `[Produces]`) | SuperAdmin | `IdentityEraseService.EraseByPublicIdAsync(Guid)`: identity live; not a super admin (`Forbidden`); sole-admin Conflict; erase | `Result` |
| `DELETE /api/admin/invites/{id}` (exists) | Admin | `InviteService.RevokeAsync(int id)` now returns `Result<InviteRevokeResponse>` | see §3.6 |

`orval.config.ts` `filters.tags`: add `'Identities'` (CLAUDE.md rule 1b — a missing tag generates nothing).

### 3.6 Revoke an accepted invite → offer "also disable"

`Application/DTOs/Invite/InviteRevokeResponse.cs`:
```csharp
public class InviteRevokeResponse
{
    public int InviteId { get; set; }
    /// <summary>Live memberships this invite created (workspace_memberships.invite_id). Empty when the
    /// invite was never accepted. The dashboard offers "also disable these members".</summary>
    public List<InviteeMembership> Invitees { get; set; } = new();
}
public class InviteeMembership { public int UserId { get; set; } public Guid PublicId { get; set; } public string Email { get; set; } = ""; public string DisplayName { get; set; } = ""; public string RoleName { get; set; } = ""; public bool IsActive { get; set; } }
```
`RevokeAsync`: after the existing revoke + `RevokeLinksForInviteAsync`, load
`InWorkspace(invite.OwnerId!.Value).Where(m => m.InviteId == invite.Id && m.LeftAt == null)` and map.
Controller: `[ProducesResponseType(typeof(InviteRevokeResponse), 200)]`. The dashboard then calls the
existing `PATCH /api/admin/users/{userId} { isActive: false }` per invitee the admin ticks — no new
endpoint. (Memberships created before DB-11a have `invite_id = NULL` except quick-access ones, so
older invites return an empty list; that is expected.)

### 3.7 Message keys

`User.SoleAdminBlocked` (§3.2), `User.CannotRemoveSelf = "You cannot remove yourself — use Leave workspace instead."`,
`User.LeftWorkspace = "You have left the workspace."`, `User.Erased = "Your account has been deleted."`,
`User.CannotEraseSuperAdmin = "Super-admin accounts cannot be erased here."`,
`User.EraseNeedsEmailConfirmation = "Magic-link accounts confirm deletion by e-mail — request a deletion link first."`,
`User.EraseUsePassword = "Your account has a password — confirm deletion with it instead."`,
`User.EraseLinkSent = "We've e-mailed you a link to confirm deleting your account. It expires in 30 minutes."`,
`User.EraseLinkInvalid = "This deletion link is invalid or has expired — request a new one."` (GLM A3; no `EraseNeedsPassword` key). Retire `CannotDeleteAdmin`
(replace its two uses with `SoleAdminBlocked`).

### 3.8 Owner decisions

| # | Question | Default encoded |
|---|---|---|
| D8 | How long are tombstones kept? | Forever (no purge). |
| D9 | What does erase delete besides secrets? | Deletes: API keys, device logins, magic links, the person's inbox, **personal AI rules**; **scrubs** the person's address from `invites.email` (tombstone, GLM A2). Keeps: comments, replies, **their screenshots (F5)**, personal predefined actions, usage events, notifications they triggered for others. Full list: §3.4 inventory. |
| F5 *(founder default, chair §4)* | On erase, delete the screenshots attached to the person's comments? | **Keep.** They depict the customer's application, not the author; they are the customer's asset like the comment text and are deleted only with the comment or the workspace. The privacy text says so (§11 Docs). |
| D10 | Does the sole-admin guard apply to a super admin suspending a tenant (`PATCH /api/admin/tenants/{id}` `disable`)? | **No** — suspension is reversible; the guard blocks only demote / remove / leave / erase. |
| D12 | Self-service "leave workspace"? | Yes, `POST /api/me/leave-workspace` (the same routine as removal, actor = self). |

## 4. Safety classification

**Additive** migration (R1) — auto-applies on an ordinary deploy (R7). Code destroys per-user secrets
and inbox rows, and scrubs `invites.email`, **only** inside `EraseAsync`, on the account holder's
password-confirmed request, the account holder's one-time e-mailed link (§3.4b — the token is bound to
identity, stamp and purpose, so a reset link cannot erase and an erase link cannot reset), or a super
admin's explicit call; all three paths are transactional and guarded by S-13. Tenancy: every
membership lookup is `(identity, explicit workspace)`; §6 tests 1 and 6 prove tenant B cannot remove
or disable A's members.

## 5. File-level tasks

1. `Domain/Entity/User.cs` — `public DateTime? ErasedAt { get; set; }` with the doc-comment "Non-null = tombstone (DB-11c): e-mail/name/secrets replaced, memberships ended, `DeletedAt` set; `PublicId` kept so authored content still resolves to 'Deleted user'." `Infrastructure/Mappings/UserMapping.cs` — `b.Property(x => x.ErasedAt).HasColumnName("erased_at");` after `RecipientEmail`.
2. `just migrate name="AddUsersErasedAt"` → exactly one `AddColumn`; anything else → stop and report.
3. `IMembershipService`/`MembershipService` — `SoleAdminWorkspacesAsync`, `EndAsync`, and `Result SoleAdminConflict(IEnumerable<(Guid, string)>)` per §3.2–3.3.
4. `Application/Abstractions/IResetTokenService.cs` + `Infrastructure/Auth/ResetTokenService.cs` — §3.4a (`CreateScoped`, `TryValidateScoped`; existing members untouched). `Application/Common/TokenPurposes.cs` (new, §3.4a verbatim).
4a. `Application/Services/Interfaces/IIdentityEraseService.cs` + `Implementation/IdentityEraseService.cs` — §3.4 (incl. step 2b invites, **no** `IFileStorage`) + the four entry points (`EraseSelfAsync(DeleteMyAccountRequest)`, `EraseByPublicIdAsync(Guid)`, `RequestEraseLinkAsync()`, `EraseByTokenAsync(string)` — §3.4b). Constructor: `IUnitOfWork`, `IMembershipService`, `ICurrentUser`, `IPasswordHasher`, `IResetTokenService`, `IEmailService`, `IBrandingService` (the same set `AuthService` uses).
5. `UserService.cs` — `DeleteAsync` → remove-from-workspace (§3.5), `UpdateAsync` guards (§3.2), new `LeaveWorkspaceAsync()`; `IUserService` gains `LeaveWorkspaceAsync`.
6. `Application/DTOs/User/DeleteMyAccountRequest.cs` (`string Password`), `Application/Validators/DeleteMyAccountValidator.cs` (`NotEmpty`); `Application/DTOs/Auth/ConfirmEraseRequest.cs` (`string Token`), `Application/Validators/ConfirmEraseValidator.cs` (`NotEmpty`).
7. `API/Controllers/MeController.cs` — add:
   ```csharp
   /// <summary>Leaves the current workspace. Blocked while the caller is its only Workspace Admin.</summary>
   [HttpPost("leave-workspace")]
   [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
   [ProducesResponseType(typeof(Result), StatusCodes.Status409Conflict)]
   public async Task<IActionResult> LeaveWorkspace() { var r = await users.LeaveWorkspaceAsync(); if (r.IsConflict) return Conflict(r); return r.IsSuccess ? Ok(r) : BadRequest(r); }

   /// <summary>Deletes the caller's account everywhere (GDPR erase). Password required. Comments stay, attributed to "Deleted user".</summary>
   [HttpDelete]
   [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
   [ProducesResponseType(typeof(Result), StatusCodes.Status403Forbidden)]
   [ProducesResponseType(typeof(Result), StatusCodes.Status409Conflict)]
   public async Task<IActionResult> DeleteMyAccount([FromBody] DeleteMyAccountRequest request) { var r = await erase.EraseSelfAsync(request); if (r.IsForbidden) return StatusCode(403, r); if (r.IsConflict) return Conflict(r); return r.IsSuccess ? Ok(r) : BadRequest(r); }
   ```
   and, directly after `DeleteMyAccount` (GLM A3):
   ```csharp
   /// <summary>Magic-link accounts only: e-mails a one-time link that confirms deleting the account (30 min). Password accounts use DELETE /api/me.</summary>
   [HttpPost("request-erase")]
   [EnableRateLimiting("signup")]
   [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
   [ProducesResponseType(typeof(Result), StatusCodes.Status403Forbidden)]
   public async Task<IActionResult> RequestErase() { var r = await erase.RequestEraseLinkAsync(); if (r.IsForbidden) return StatusCode(403, r); return r.IsSuccess ? Ok(r) : BadRequest(r); }
   ```
   (inject `IUserService users`, `IIdentityEraseService erase` in the primary constructor; add `using Microsoft.AspNetCore.RateLimiting;`; check how `Result.IsConflict` is exposed — `Application/Response/Result.cs:14,28` sets it.)
7a. `API/Controllers/AuthController.cs` — add after `ResetPassword` (`:92-100`), same shape:
   ```csharp
   /// <summary>Confirms deleting a magic-link account with the token e-mailed by POST /api/me/request-erase. Anonymous by necessity — the token is the credential.</summary>
   [AllowAnonymous]
   [HttpPost("confirm-erase")]
   [EnableRateLimiting("signup")]
   [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
   [ProducesResponseType(typeof(Result), StatusCodes.Status400BadRequest)]
   [ProducesResponseType(typeof(Result), StatusCodes.Status409Conflict)]
   public async Task<IActionResult> ConfirmErase([FromBody] ConfirmEraseRequest request)
   {
       var result = await eraseService.EraseByTokenAsync(request.Token);
       if (result.IsConflict) return Conflict(result);
       return result.IsSuccess ? Ok(result) : BadRequest(result);
   }
   ```
   (add `IIdentityEraseService eraseService` to the primary constructor at `:13-17`.)
8. `API/Controllers/Admin/IdentitiesController.cs` — new, one action `Delete(Guid publicId)` per §3.5 (`[Authorize(Policy = Policies.SuperAdmin)]`).
9. `InviteService.RevokeAsync` + `IInviteService` + `API/Controllers/Admin/InvitesController.cs:39-51` — §3.6; new DTO file.
10. `orval.config.ts:6` — add `'Identities'` to `filters.tags`.
11. `Application/Resources/MessageKeys.cs` — §3.7.
12. Tests (§6), incl. the `AuthRateLimitingTests` additions. 13. `just fmt`, `just test`. 14. PR **Dashboard tasks** = §11 (dashboard, widget **and** the Docs sentence).

## 6. Tests

`Tests/DeletionSemanticsTests.cs` (fixture + `TestSeed.Join` from DB-11a; Sqlite `TestDb` where FKs matter):

1. `Remove_EndsMembership_RevokesOnlyThatWorkspacesKeysAndLinks` — identity in A and B with a key in each; remove from A → A's key `RevokedAt != null`, B's key untouched, membership A `LeftAt/LeftReason == Removed`, B live, `users` row intact.
2. `Remove_SoleAdmin_Conflict_NamesWorkspace`; `Demote_SoleAdmin_BySuperAdmin_Conflict` (S-13 regression: `FakeCurrentUser { IsSuperAdmin = true }` + `UpdateAsync(adminId, { RoleId = deputyRole })` → `IsConflict`, role unchanged); `Disable_SoleAdmin_Conflict`; `Demote_AdminWhenAnotherAdminExists_Allowed`.
3. `Disable_KeepsKeys_ButLoginWithKeyReturnsDisabled_AndEnableRestores`.
4. `Leave_Works_ForNonAdmin`; `Leave_SoleAdmin_Conflict`; `Leave_ThenLogin_ReturnsNoWorkspace_WhenLastMembership` (DB-11b status, or `"disabled"` if DB-11b is not merged yet — assert on `!= "ok"`).
5. `Erase_Self_Tombstone_KeepsComments_ResolvesDeletedUser` — seed a comment and a reply by the identity in A; erase → `users` row: `ErasedAt/DeletedAt` set, `Email` starts with `erased+`, `DisplayName == "Deleted user"`, `PublicId` unchanged; `ApiKeys/DeviceLogins/QuickAccessLinks/Notifications(user_id)/AiRules(user_id)` for it → 0; comment and reply still present with the same `author_id`; `UserNameResolver` returns `"Deleted user"` for it; both memberships `LeftReason == AccountErased`.
6. `Erase_BlockedWhileSoleAdminAnywhere_ListsWorkspaces`; `Erase_WrongPassword_Fails_NothingChanged`; `Erase_Passwordless_ByPassword_Fails_PointsToEmailRail` (`Message == MessageKeys.User.EraseNeedsEmailConfirmation`, nothing changed); `Erase_BySuperAdmin_OnSuperAdmin_Forbidden`; `Erase_TenantB_CannotRemoveAsMember` (tenant B admin calling `DeleteAsync(users.id of an A-only member)` → NotFound).
7. `Login_AfterErase_InvalidCredentials`; `PasswordReset_AfterErase_NoEmail` (`RequestPasswordResetAsync` finds nothing).
8. `RevokeInvite_Accepted_ReturnsInvitees`; `RevokeInvite_NeverAccepted_EmptyList`.
9. `TenantSetStatus_Disable_SoleAdmin_Allowed` (D10).
10. Existing `Tests/UserGovernanceTests.cs`: update the `CannotDeleteAdmin` assertions to `IsConflict` + `SoleAdminBlocked`; keep every other assertion.
11. **GLM A2** `Erase_ScrubsInviteEmail_KeepsInviteLocked` — seed three invites: one accepted by the identity (locked to its e-mail, `Uses = 1`), one still open and locked to the same e-mail, one locked to another address; erase → the first two have `Email == "erased+…@tombstone.invalid"` and unchanged `RevokedAt`/`Uses`, the third is untouched; `InviteService.AcceptAsync` with the open invite's code and the original e-mail → fails (lock no longer matches). `Erase_LeavesNoEmailBehind` — after erase, `db.Users.IgnoreQueryFilters().Any(u => u.Email == original || u.RecipientEmail == original)` and `db.Invites.IgnoreQueryFilters().Any(i => i.Email == original)` are both false (this is the mechanised half of the §3.4 inventory).
12. **F5** `Erase_KeepsScreenshotFile` — a `RecordingFileStorage : IFileStorage` test double that lists every `DeleteAsync`/`DeleteOwnerFilesAsync` call; seed a comment by the identity with `Element.ScreenshotUrl = "uploads/x/y/z.png"`; erase → the recorder is empty and the comment's `ScreenshotUrl` is unchanged. (Also proves `IdentityEraseService` has no `IFileStorage` dependency: constructing it does not need the double at all — assert via the DI-free constructor.)
13. **GLM A3** new `Tests/ResetTokenServiceTests.cs` (build `ResetTokenService` with `new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["JWT:SigningKey"] = "test-key-…32+ chars" }).Build()`): `Scoped_RoundTrip_WithPayload` (`CreateScoped(id, stamp, "change-email", "new@x.com")` → `TryValidateScoped(…, "change-email")` true, payload equal); `Scoped_WrongPurpose_Fails`; `Scoped_RejectedByPlainTryValidate` (six-part token → `TryValidate` false); `Plain_RejectedByTryValidateScoped` (`Create(id, stamp)` → `TryValidateScoped(…, "erase")` false); `Scoped_TamperedPayload_Fails`; `Scoped_PurposeWithDot_Throws`.
14. **GLM A3** in `DeletionSemanticsTests` with a `CapturingEmail : IEmailService` double (stores `to`, `subject`, `html`; `NoopEmail` in `UserGovernanceTests.cs:20-60` is the shape to extend): `RequestErase_Passwordless_SendsScopedToken` (one e-mail to the identity's address; extract `token=` from the link; `TryValidateScoped(token, "erase")` true; `TryValidate(token)` false); `RequestErase_PasswordAccount_Failure_NoEmail` (`EraseUsePassword`, zero e-mails); `RequestErase_SuperAdmin_Forbidden`; `ConfirmErase_ValidToken_Tombstones_ScrubsInvites` (same assertions as test 5 + test 11); `ConfirmErase_ReusedToken_Fails` (second call → `EraseLinkInvalid`, stamp rotated); `ConfirmErase_ResetTokenRejected` (`Create(pid, stamp)` reset token → `EraseLinkInvalid`, nothing changed); `ConfirmErase_PasswordAccountToken_Fails` (mint a scoped erase token for a password identity by hand → `EraseLinkInvalid`); `ConfirmErase_SoleAdmin_Conflict`.
15. **GLM A3** `Tests/AuthRateLimitingTests.cs`: add `[InlineData("ConfirmErase")]` to `SignupSurface_KeepsSignupRateLimit`; add `RequestErase_HasSignupRateLimit` (reflection on `typeof(MeController).GetMethod("RequestErase")`, `PolicyName == "signup"`).

## 7. Acceptance criteria

1. `dotnet ef migrations list … --no-connect` shows one new id ending `_AddUsersErasedAt`; `grep -c "AddColumn" Infrastructure/Migrations/*_AddUsersErasedAt.cs` → 1; no `ContractMigration`.
2. `grep -rn "CannotDeleteAdmin" Application | wc -l` → 0 (retired); `grep -c "SoleAdminWorkspacesAsync" Application/Services/Implementation/UserService.cs Application/Services/Implementation/IdentityEraseService.cs` → ≥2 and ≥1.
3. `curl -s …/swagger.json | jq '.paths["/api/me/leave-workspace"].post.tags, .paths["/api/me"].delete.tags, .paths["/api/me/request-erase"].post.tags, .paths["/api/auth/confirm-erase"].post.tags, .paths["/api/admin/identities/{publicId}"].delete.tags'` → `["Me"]`, `["Me"]`, `["Me"]`, `["Auth"]`, `["Identities"]`; `jq '.paths["/api/admin/invites/{id}"].delete.responses["200"].content["application/json"].schema."$ref"'` → `InviteRevokeResponse`; `grep -c "'Identities'" orval.config.ts` → 1.
4. `just test` green incl. the facts above.
5. Manual on the rehearsal API (restored prod dump): as the production admin, create a second workspace admin via promote on a test deputy, then erase the test deputy's account via `DELETE /api/me` → its comments still list with author "Deleted user"; `SELECT email, display_name, erased_at, deleted_at FROM users WHERE erased_at IS NOT NULL;` → one tombstone; `SELECT count(*) FROM api_keys k JOIN users u ON u.id = k.user_id WHERE u.erased_at IS NOT NULL;` → 0.
6. As super admin: `PATCH /api/admin/users/{soleAdminId} { roleId: <Deputy> }` → 409 with the workspace name (S-13 closed).
7. **GLM A2 / F5 / GLM A3 greps:** `grep -c "Invite" Application/Services/Implementation/IdentityEraseService.cs` → ≥ 2; `grep -c "IFileStorage\|_fileStorage" Application/Services/Implementation/IdentityEraseService.cs` → 0; `grep -c "TokenPurposes.Erase" Application/Services/Implementation/IdentityEraseService.cs` → 2; `grep -c "EraseNeedsPassword" Application` (recursive) → 0; `grep -c "CreateScoped\|TryValidateScoped" Application/Abstractions/IResetTokenService.cs` → 2.
8. Manual on the rehearsal API with the local mail server (`local-mail-server/`, `Email:Provider=smtp`): a quick-access Client (created via a client invite) opens the widget, uses "Delete my account", receives the e-mail, opens `/delete-account?token=…` in the dashboard, confirms → `SELECT email, erased_at FROM users WHERE erased_at IS NOT NULL;` shows the tombstone; `SELECT count(*) FROM invites WHERE email = '<the client's address>';` → 0; the client's comments still list as "Deleted user" with their screenshots still served; re-using the link → 400 `EraseLinkInvalid`.

## 8. Rollback

The migration's `Down()` drops `erased_at` (no data of value — tombstones remain soft-deleted rows
without the marker). Revert the commit and redeploy. **Erasures performed while the code was live
are not reversible by rollback** — the secrets are gone and the e-mail/name are overwritten; that is
the feature. Ordinary `pre-deploy` dump suffices; no labelled dump.

## 9. Release steps

1. Merge after DB-11a is verified in production (DB-11b optional). 2. `bash scripts/deploy-api.sh`
(ordinary; the additive migration auto-applies). 3. Verify criteria 5–6 against production with a
disposable test identity (invite → accept → erase). 4. Watch `docker compose logs api` for `Conflict`
bursts on `PATCH /api/admin/users` (would mean the dashboard is still offering demotion of sole
admins — dashboard task 2). 5. Dashboard: client regen once (adds `Identities`, `InviteRevokeResponse`,
`DeleteMyAccountRequest`), then §11.

## 10. Out of scope

Tombstone purge (D8 = never; a future retention doc); FK from `comments.author_id` to
`users(public_id)` (Q5 — now feasible; separate doc); erasing **workspace** content on request
(that is `TenantService.HardDeleteAsync`); "disable identity everywhere" for super admins (erase or
per-tenant suspension cover the need); the widget (a stakeholder erases from the dashboard profile
page; the widget only needs to handle 401 after erase — it already does); the CLI (its key is revoked
→ `login-with-key` fails with the existing message; the widget's only new control is the quick-access
"Delete my account" entry of §11); dropping legacy `users` columns (**DB-11e**); changing an identity's
e-mail (`POST /api/me/change-email`, [DB-11d](DB-11d-change-email.md), which consumes §3.4a's
`TokenPurposes.ChangeEmail`); the legal-hold flag (§3.4 forward reference); e-mail verification at signup
(DB-14); deleting screenshot files on erase (F5 = keep; revisit only if the owner overrides F5).

## 11. Dashboard / widget / CLI tasks

**Dashboard** (after client regen):
1. `UsersPage`: rename the row action "Delete" → "Remove from workspace" (`DELETE /api/admin/users/{id}` unchanged); on 409 show the server message (sole admin). Enable/disable unchanged (`PATCH isActive`); on 409 show the message.
2. `UsersPage` role editor: on 409 from `PATCH roleId` show the message (do not pre-filter — the server is the guard).
3. Invites page: on `DELETE /api/admin/invites/{id}` read `invitees`; when non-empty show a dialog "This invite was already used by N people — also disable them?" with checkboxes → `PATCH /api/admin/users/{userId} { isActive: false }` per ticked row; invalidate users + invites queries.
4. Profile page: "Leave this workspace" (`POST /api/me/leave-workspace`; on success clear the token, go to login) and a danger-zone "Delete my account" (password field → `DELETE /api/me`; on 409 show the message with the workspace names; on success clear the token).
5. `TenantsPage` (super admin): per-row "Erase admin identity…" → `DELETE /api/admin/identities/{publicId}` with a confirm; on 409 show the message.
6. **GLM A3** new anonymous route `/delete-account` (`App.tsx`, next to `/reset` at `:45`) → `features/auth/DeleteAccountPage.tsx`, copied from `ResetPasswordPage.tsx`: reads `?token=`, shows "Delete my account permanently — your feedback stays, shown as 'Deleted user'" with one confirm button → `POST /api/auth/confirm-erase { token }`; on 200 show `envelope.message` and a link to the login page; on 400/409 show `envelope.message`. No session is required or used.

**Widget** (GLM A3): when `this.user.isQuickAccess` (`element.ts:2600`), add a "Delete my account…" entry beside the sign-out control (`element.ts:1283-1285`; template in `templates.ts`) → confirm dialog → `POST /api/me/request-erase` with the widget's bearer token → toast `envelope.message` (`EraseLinkSent`); on 400 show `envelope.message`. i18n strings en + ar. `npm run build`, commit `API/wwwroot/pointer.*`, stay inside the 64 KB budget (memory note: ~1 KB headroom — if the new strings do not fit, ship the widget entry as a link to the dashboard profile page instead and say so in the PR).

**Docs** (F5 — the privacy sentence this default implies; goes to whoever writes the privacy page and the DSAR runbook, foundations #10/#13, not to the API implementer): add verbatim — "Deleting your account removes your name and e-mail address from our systems, including invitations addressed to you; feedback you wrote stays with the workspace that owns it and is shown as 'Deleted user'. Screenshots attached to feedback depict the customer's application, not you; they are retained with that feedback and are deleted when the comment or the workspace is deleted, not when you delete your account." Draft privacy page: `landing-redesign/glm/privacy.html`.

**CLI**: none (document in `cli/README.md` that a revoked key means the account was removed from that workspace — one sentence).
