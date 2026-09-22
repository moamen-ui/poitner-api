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
personal AI rules — by design, on the account holder's or a super admin's explicit action, never by a
schema step). No approval line needed: the migration is additive; the destructive behaviour is a
product feature the owner asked for.
**Status 2026-09-22: written; not implemented.** Owner decisions D8, D9, D10, D12 have defaults (§3.8).

## 1. Goal

Give each actor exactly the verbs the owner described, on the membership model of DB-11a:

| Actor | Verb | Effect |
|---|---|---|
| Workspace Admin / Deputy | **remove from workspace** (`DELETE /api/admin/users/{id}`) | ends the membership (kept for audit), revokes **that workspace's** sessions, API keys and magic links; the identity and its other memberships are untouched |
| Workspace Admin / Deputy | **disable / enable** (`PATCH /api/admin/users/{id}` `isActive`) | flips the membership; disabled = that workspace's sessions stop within 60 s, its key stops logging in, notifications stop; reversible |
| any member | **leave workspace** (`POST /api/me/leave-workspace`) | ends own membership; same revocations |
| any member | **delete my account** (`DELETE /api/me`, password confirmed) | **erase**: identity becomes a tombstone (`Deleted user`, `erased+<id>@tombstone.invalid`), secrets cleared, every membership ended, keys/logins/links/inbox deleted; **comments and replies stay**, attributed to the tombstone |
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
  `users.merged_into_user_id`, `ux_users_email_live (email) WHERE deleted_at IS NULL`, stamp validator
  checking `mstamp` + membership liveness.
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
Inside `ExecuteInTransactionAsync`:
1. `EndAsync(m, AccountErased, actor)` for every live membership.
2. Hard-delete (`RemoveRange`, `IgnoreQueryFilters`): `ApiKeys.Where(k => k.UserId == identity.Id)` (all, including revoked — they are secrets);
   `DeviceLogins.Where(d => d.UserId == pid)`; `QuickAccessLinks.Where(l => l.UserId == pid)`;
   `Notifications.Where(n => n.UserId == pid)` (the person's inbox); `AiRules.Where(r => r.UserId == pid)` (personal rules, D9).
3. Tombstone the row: `Email = $"erased+{pid:N}@tombstone.invalid"` (unique by construction; `.invalid`
   is reserved, RFC 2606), `DisplayName = "Deleted user"`, `PasswordHash = <hash of a random 64-char secret>`,
   `PasswordlessOnly = true`, `Language = Theme = AddCommentShortcut = null`, `RecipientEmail = null`,
   `IsActive = false`, `SecurityStamp = Guid.NewGuid()`, `ErasedAt = now`, `DeletedAt = now` (so every
   `DeletedAt == null` read — login, reset, `AdminSeeder` e-mail match, `ListForIdentityAsync` — skips it).
   **`PublicId` is kept** so `comments.author_id`, `replies.author_id`, `notifications.actor_id` and
   audit columns keep resolving (to "Deleted user"). `user_aliases` rows pointing at it stay.
4. Kept on purpose: `comments`, `replies` (content the workspace owns), `predefined_actions` with
   `user_id` (workspace content; D9), `usage_events.user_id` (analytics; the tombstone id is not
   personal data), `notifications` where `actor_id == pid` (other people's inboxes; render "Deleted user").
5. `UserNameResolver`: no change needed — the tombstone row resolves by `PublicId` (it is soft-deleted;
   the resolver must **not** filter on `DeletedAt`; DB-11a's resolver does not).

Retention of tombstones: **forever** (D8). If the owner later wants a purge, it is a `RetentionService`
sweep that hard-deletes tombstones older than N days **and** rewrites their `author_id`s to a single
shared sentinel — a separate doc.

### 3.5 Endpoints

| Route | Policy | Service | Result |
|---|---|---|---|
| `DELETE /api/admin/users/{id}` (exists) | Admin | `UserService.DeleteAsync(int id)` → **RemoveFromWorkspaceAsync**: membership `(users.id == id, caller tenant)`; guards: self → `CannotRemoveSelf` (renamed message: "Use Leave workspace instead."); sole admin (§3.2); deputy matrix as today; then `EndAsync(Removed)` | `Result` (unchanged shape) |
| `PATCH /api/admin/users/{id}` (exists) | Admin | `UpdateAsync`: role change or `isActive=false` on a sole admin → §3.2 Conflict; `isActive` flips membership + membership stamp | `UserResponse` |
| `POST /api/me/leave-workspace` (new) | any authenticated tenant user | `UserService.LeaveWorkspaceAsync()`: membership `(caller, tenant)`; sole admin → Conflict; `EndAsync(Left)`; response tells the client to drop its token | `Result` |
| `DELETE /api/me` (new) | any authenticated tenant user | body `DeleteMyAccountRequest { string Password }`; `IdentityEraseService.EraseSelfAsync(request)`: identity by `sub`; `PasswordlessOnly` identity → `Forbidden(MessageKeys.User.EraseNeedsPassword)` ("Magic-link accounts are removed by the workspace admin."); password verify else `Failure(CurrentPasswordIncorrect)`; super admin → `Forbidden`; sole-admin Conflict; erase | `Result` |
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
`User.LeftWorkspace = "You have left the workspace."`, `User.EraseNeedsPassword`, `User.Erased = "Your account has been deleted."`,
`User.CannotEraseSuperAdmin = "Super-admin accounts cannot be erased here."`. Retire `CannotDeleteAdmin`
(replace its two uses with `SoleAdminBlocked`).

### 3.8 Owner decisions

| # | Question | Default encoded |
|---|---|---|
| D8 | How long are tombstones kept? | Forever (no purge). |
| D9 | What does erase delete besides secrets? | Deletes: API keys, device logins, magic links, the person's inbox, **personal AI rules**. Keeps: comments, replies, personal predefined actions, usage events, notifications they triggered for others. |
| D10 | Does the sole-admin guard apply to a super admin suspending a tenant (`PATCH /api/admin/tenants/{id}` `disable`)? | **No** — suspension is reversible; the guard blocks only demote / remove / leave / erase. |
| D12 | Self-service "leave workspace"? | Yes, `POST /api/me/leave-workspace` (the same routine as removal, actor = self). |

## 4. Safety classification

**Additive** migration (R1) — auto-applies on an ordinary deploy (R7). Code destroys per-user secrets
and inbox rows **only** inside `EraseAsync`, on the account holder's password-confirmed request or a
super admin's explicit call; both paths are transactional and guarded by S-13. Tenancy: every
membership lookup is `(identity, explicit workspace)`; §6 tests 1 and 6 prove tenant B cannot remove
or disable A's members.

## 5. File-level tasks

1. `Domain/Entity/User.cs` — `public DateTime? ErasedAt { get; set; }` with the doc-comment "Non-null = tombstone (DB-11c): e-mail/name/secrets replaced, memberships ended, `DeletedAt` set; `PublicId` kept so authored content still resolves to 'Deleted user'." `Infrastructure/Mappings/UserMapping.cs` — `b.Property(x => x.ErasedAt).HasColumnName("erased_at");` after `RecipientEmail`.
2. `just migrate name="AddUsersErasedAt"` → exactly one `AddColumn`; anything else → stop and report.
3. `IMembershipService`/`MembershipService` — `SoleAdminWorkspacesAsync`, `EndAsync`, and `Result SoleAdminConflict(IEnumerable<(Guid, string)>)` per §3.2–3.3.
4. `Application/Services/Interfaces/IIdentityEraseService.cs` + `Implementation/IdentityEraseService.cs` — §3.4 + the two entry points of §3.5 (`EraseSelfAsync(DeleteMyAccountRequest)`, `EraseByPublicIdAsync(Guid)`).
5. `UserService.cs` — `DeleteAsync` → remove-from-workspace (§3.5), `UpdateAsync` guards (§3.2), new `LeaveWorkspaceAsync()`; `IUserService` gains `LeaveWorkspaceAsync`.
6. `Application/DTOs/User/DeleteMyAccountRequest.cs` (`string Password`), `Application/Validators/DeleteMyAccountValidator.cs` (`NotEmpty`).
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
   (inject `IUserService users`, `IIdentityEraseService erase` in the primary constructor; check how `Result.IsConflict` is exposed — `Application/Response/Result.cs:28` sets it.)
8. `API/Controllers/Admin/IdentitiesController.cs` — new, one action `Delete(Guid publicId)` per §3.5 (`[Authorize(Policy = Policies.SuperAdmin)]`).
9. `InviteService.RevokeAsync` + `IInviteService` + `API/Controllers/Admin/InvitesController.cs:39-51` — §3.6; new DTO file.
10. `orval.config.ts:6` — add `'Identities'` to `filters.tags`.
11. `Application/Resources/MessageKeys.cs` — §3.7.
12. Tests (§6). 13. `just fmt`, `just test`. 14. PR **Dashboard tasks** = §11.

## 6. Tests

`Tests/DeletionSemanticsTests.cs` (fixture + `TestSeed.Join` from DB-11a; Sqlite `TestDb` where FKs matter):

1. `Remove_EndsMembership_RevokesOnlyThatWorkspacesKeysAndLinks` — identity in A and B with a key in each; remove from A → A's key `RevokedAt != null`, B's key untouched, membership A `LeftAt/LeftReason == Removed`, B live, `users` row intact.
2. `Remove_SoleAdmin_Conflict_NamesWorkspace`; `Demote_SoleAdmin_BySuperAdmin_Conflict` (S-13 regression: `FakeCurrentUser { IsSuperAdmin = true }` + `UpdateAsync(adminId, { RoleId = deputyRole })` → `IsConflict`, role unchanged); `Disable_SoleAdmin_Conflict`; `Demote_AdminWhenAnotherAdminExists_Allowed`.
3. `Disable_KeepsKeys_ButLoginWithKeyReturnsDisabled_AndEnableRestores`.
4. `Leave_Works_ForNonAdmin`; `Leave_SoleAdmin_Conflict`; `Leave_ThenLogin_ReturnsNoWorkspace_WhenLastMembership` (DB-11b status, or `"disabled"` if DB-11b is not merged yet — assert on `!= "ok"`).
5. `Erase_Self_Tombstone_KeepsComments_ResolvesDeletedUser` — seed a comment and a reply by the identity in A; erase → `users` row: `ErasedAt/DeletedAt` set, `Email` starts with `erased+`, `DisplayName == "Deleted user"`, `PublicId` unchanged; `ApiKeys/DeviceLogins/QuickAccessLinks/Notifications(user_id)/AiRules(user_id)` for it → 0; comment and reply still present with the same `author_id`; `UserNameResolver` returns `"Deleted user"` for it; both memberships `LeftReason == AccountErased`.
6. `Erase_BlockedWhileSoleAdminAnywhere_ListsWorkspaces`; `Erase_WrongPassword_Fails_NothingChanged`; `Erase_Passwordless_Forbidden`; `Erase_BySuperAdmin_OnSuperAdmin_Forbidden`; `Erase_TenantB_CannotRemoveAsMember` (tenant B admin calling `DeleteAsync(users.id of an A-only member)` → NotFound).
7. `Login_AfterErase_InvalidCredentials`; `PasswordReset_AfterErase_NoEmail` (`RequestPasswordResetAsync` finds nothing).
8. `RevokeInvite_Accepted_ReturnsInvitees`; `RevokeInvite_NeverAccepted_EmptyList`.
9. `TenantSetStatus_Disable_SoleAdmin_Allowed` (D10).
10. Existing `Tests/UserGovernanceTests.cs`: update the `CannotDeleteAdmin` assertions to `IsConflict` + `SoleAdminBlocked`; keep every other assertion.

## 7. Acceptance criteria

1. `dotnet ef migrations list … --no-connect` shows one new id ending `_AddUsersErasedAt`; `grep -c "AddColumn" Infrastructure/Migrations/*_AddUsersErasedAt.cs` → 1; no `ContractMigration`.
2. `grep -rn "CannotDeleteAdmin" Application | wc -l` → 0 (retired); `grep -c "SoleAdminWorkspacesAsync" Application/Services/Implementation/UserService.cs Application/Services/Implementation/IdentityEraseService.cs` → ≥2 and ≥1.
3. `curl -s …/swagger.json | jq '.paths["/api/me/leave-workspace"].post.tags, .paths["/api/me"].delete.tags, .paths["/api/admin/identities/{publicId}"].delete.tags'` → `["Me"]`, `["Me"]`, `["Identities"]`; `jq '.paths["/api/admin/invites/{id}"].delete.responses["200"].content["application/json"].schema."$ref"'` → `InviteRevokeResponse`; `grep -c "'Identities'" orval.config.ts` → 1.
4. `just test` green incl. the facts above.
5. Manual on the rehearsal API (restored prod dump): as the production admin, create a second workspace admin via promote on a test deputy, then erase the test deputy's account via `DELETE /api/me` → its comments still list with author "Deleted user"; `SELECT email, display_name, erased_at, deleted_at FROM users WHERE erased_at IS NOT NULL;` → one tombstone; `SELECT count(*) FROM api_keys k JOIN users u ON u.id = k.user_id WHERE u.erased_at IS NOT NULL;` → 0.
6. As super admin: `PATCH /api/admin/users/{soleAdminId} { roleId: <Deputy> }` → 409 with the workspace name (S-13 closed).

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
→ `login-with-key` fails with the existing message); dropping legacy `users` columns (DB-11d).

## 11. Dashboard / widget / CLI tasks

**Dashboard** (after client regen):
1. `UsersPage`: rename the row action "Delete" → "Remove from workspace" (`DELETE /api/admin/users/{id}` unchanged); on 409 show the server message (sole admin). Enable/disable unchanged (`PATCH isActive`); on 409 show the message.
2. `UsersPage` role editor: on 409 from `PATCH roleId` show the message (do not pre-filter — the server is the guard).
3. Invites page: on `DELETE /api/admin/invites/{id}` read `invitees`; when non-empty show a dialog "This invite was already used by N people — also disable them?" with checkboxes → `PATCH /api/admin/users/{userId} { isActive: false }` per ticked row; invalidate users + invites queries.
4. Profile page: "Leave this workspace" (`POST /api/me/leave-workspace`; on success clear the token, go to login) and a danger-zone "Delete my account" (password field → `DELETE /api/me`; on 409 show the message with the workspace names; on success clear the token).
5. `TenantsPage` (super admin): per-row "Erase admin identity…" → `DELETE /api/admin/identities/{publicId}` with a confirm; on 409 show the message.

**Widget**: none. **CLI**: none (document in `cli/README.md` that a revoked key means the account was removed from that workspace — one sentence).
