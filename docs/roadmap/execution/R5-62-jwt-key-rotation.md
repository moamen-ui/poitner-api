# R5-62 — JWT `kid` + two-key rotation window (§62 · Release 5 · 1 d)

## 1. Goal

Today rotating the JWT signing key means every outstanding token becomes invalid — a global logout
for every user and every CLI session. Add a `kid` (Key ID) header to issued tokens, configure
multiple signing keys with an active key pointer, and validate against all listed keys. This lets
the operator add a new key, switch the active key, and remove the old key after the max token
lifetime (12 h) — zero-downtime rotation with no user impact.

Effort: 1 d.

## 2. Prerequisites (verified facts)

- **Token issuance**: `Infrastructure/Auth/JwtTokenService.cs:11-42` — single `JwtOptions` with
  `SigningKey` (`:10`), creates `SymmetricSecurityKey` from `Encoding.UTF8.GetBytes(o.SigningKey)`
  (`:16`), `SigningCredentials` with `HmacSha256` (`:17`), issues `JwtSecurityToken` (`:38-39`).
  No `kid` header set.
- **Token validation**: `API/Extensions/AuthenticationExtensions.cs:42-53` —
  `TokenValidationParameters` with single `IssuerSigningKey = new SymmetricSecurityKey(keyBytes)` (`:49`).
  No `IssuerSigningKeys` (plural).
- **Config today**: `docker-compose.prod.yml:44` `JWT__SigningKey: "${JWT_SIGNING_KEY}"`;`.env.prod.example:9` `JWT_SIGNING_KEY=change-me-…`.
- **Key length check**: `AuthenticationExtensions.cs:23-28` — fails fast if < 32 bytes.
- **Lifetime**: `JwtTokenService.cs:10` `LifetimeHours = 12`; `docker-compose.prod.yml:46`
  `JWT__LifetimeHours: "12"`.
- **`JwtOptions`**: `JwtTokenService.cs:10` — `public class JwtOptions { public string SigningKey ...; public string Issuer ...; public int LifetimeHours ...; }`.
- **`ITokenService`**: `Application/Abstractions/ITokenService.cs:12` — `string Issue(User user, int? keyScopes = null)`.
- **Tests**: `Tests/TokenServiceTests.cs` tests token issuance.
- **Dependencies**: none for the key-rotation mechanism itself, but **coordinate with DB-11a/b** (in
  progress in `docs/db/execution/`, not yet landed in this tree): DB-11a changes `ITokenService.Issue`'s
  signature (adds a `WorkspaceMembership?` parameter) and DB-11b adds `IssueSelection`/
  `SelectionLifetimeMinutes` and a second `TokenValidationParameters` code path
  (`API/Extensions/SelectionScopeFence.cs`, `OnTokenValidated`). If DB-11a/b land first, rebase
  `ResolveActiveKey`/`ResolveAllKeys` onto the new signature rather than the one shown in §3.3/§3.4
  below (both still apply — DB-11a/b change *which claims* go in, not how the key/`kid` is chosen).
  Independent of DB-12/DB-13 (written, not yet implemented).

## 3. Design

### 3.1 Configuration

New config shape under `JWT:`:

```json
{
  "JWT": {
    "Issuer": "pointer-api",
    "LifetimeHours": 12,
    "SigningKey": "<legacy single key, backward compat>",
    "ActiveKeyId": "k1",
    "Keys": [
      { "Id": "k0", "Secret": "<the current JWT_SIGNING_KEY value>" },
      { "Id": "k1", "Secret": "<new 32+ byte key>" }
    ]
  }
}
```

**Backward compatibility**: when `JWT:Keys` is empty/missing, treat `JWT:SigningKey` as a single
key with id `"k0"` and `ActiveKeyId = "k0"`. This means existing `.env.prod` files work unchanged.

**Only two key slots are wired** (matching "two-key rotation window" — `JwtOptions.Keys` is a `List`
in code, but `docker-compose.prod.yml` only ever needs to pass through index 0 and 1). `.env.prod`
variable names are plain (compose substitutes them into `${VAR}` placeholders — it does not pass
`.env.prod` through to the container as-is via `env_file:`), so they cannot themselves use the `__`
ASP.NET Core config-key separator; that separator is only used on the *container-side* env var name
(the left side of each `docker-compose.prod.yml` line):
```yaml
JWT__ActiveKeyId: "${JWT_ACTIVE_KEY_ID:-k0}"
JWT__Keys__0__Id: "k0"
JWT__Keys__0__Secret: "${JWT_SIGNING_KEY}"
JWT__Keys__1__Id: "${JWT_KEY_1_ID:-}"
JWT__Keys__1__Secret: "${JWT_KEY_1_SECRET:-}"
# k0's secret is always the existing JWT_SIGNING_KEY var directly — no new .env.prod variable
# needed for it, and an unrotated deployment needs zero changes. Only k1 (the new key being
# rotated in) needs new vars. ResolveAllKeys (§3.4) must skip a Keys entry whose Secret is
# empty/missing (so an unset k1 does not fail the >= 32-byte check at startup).
```

### 3.2 `JwtOptions` changes

`Infrastructure/Auth/JwtTokenService.cs:10` — extend `JwtOptions`:

```csharp
public class JwtOptions
{
    public string SigningKey { get; set; } = "";
    public string Issuer { get; set; } = "pointer-api";
    public int LifetimeHours { get; set; } = 12;
    public string ActiveKeyId { get; set; } = "k0";
    public List<JwtKeyEntry> Keys { get; set; } = new();
}

public class JwtKeyEntry
{
    public string Id { get; set; } = "";
    public string Secret { get; set; } = "";
}
```

### 3.3 Token issuance with `kid`

In `JwtTokenService.Issue()`, after resolving the active key:

```csharp
var activeKey = ResolveActiveKey(o);
var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(activeKey.Secret)) { KeyId = activeKey.Id };
var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
// … existing claims …
var header = new JwtHeader(creds);
header["kid"] = activeKey.Id;
var token = new JwtSecurityToken(header, new JwtPayload(o.Issuer, o.Issuer, claims,
    null, DateTime.UtcNow.AddHours(o.LifetimeHours)));
```

`ResolveActiveKey(JwtOptions o)`: if `o.Keys` is non-empty, find the entry matching `o.ActiveKeyId`
(throw on miss). If `o.Keys` is empty, return `new JwtKeyEntry { Id = "k0", Secret = o.SigningKey }`.

### 3.4 Token validation with multiple keys

In `AuthenticationExtensions.AddJwtAuth()`, replace the single `IssuerSigningKey` with `IssuerSigningKeys`:

```csharp
var allKeys = ResolveAllKeys(config);
// …
options.TokenValidationParameters = new TokenValidationParameters
{
    // … existing …
    IssuerSigningKeys = allKeys.Select(k =>
        new SymmetricSecurityKey(Encoding.UTF8.GetBytes(k.Secret)) { KeyId = k.Id }).ToList(),
    ValidateIssuerSigningKey = true,
    // Remove the singular IssuerSigningKey
};
```

`ResolveAllKeys(IConfiguration config)`: reads `JWT:Keys` section. If empty, returns a single-entry
list with `Id = "k0"`, `Secret = config["JWT:SigningKey"]`. The key-length check (>= 32 bytes)
applies to **every** key in the list.

### 3.5 Rotation runbook

Add to `DEPLOY.md` under "Updating":

```markdown
### JWT key rotation (R5-62)

1. Generate a new key: `openssl rand -hex 32`
2. Add it to `.env.prod` (`JWT_SIGNING_KEY` — k0's secret — stays as-is):
   ```
   JWT_KEY_1_ID=k1
   JWT_KEY_1_SECRET=<new key>
   ```
3. Deploy: `bash scripts/deploy-api.sh`. Both keys validate; new tokens still use k0
   (`JWT_ACTIVE_KEY_ID` is still unset ⇒ defaults to `k0`).
4. Switch the active key: set `JWT_ACTIVE_KEY_ID=k1` in `.env.prod`. Deploy again. New tokens
   now carry `kid: k1`; existing k0 tokens still validate (k0 is still in the list).
5. Wait 12 hours (max token lifetime). All k0 tokens have expired.
6. Retire k0: set `JWT_SIGNING_KEY` to the same value as `JWT_KEY_1_SECRET` (k0's secret becomes
   the new key), then remove `JWT_KEY_1_ID`/`JWT_KEY_1_SECRET` and reset
   `JWT_ACTIVE_KEY_ID=k0` (or delete it — `k0` is the default) in `.env.prod`. Deploy. The config
   is back to a single active key — ready for the next rotation — and any token still carrying
   `kid: k1` is now rejected (that key id is no longer in the list), matching the intent of
   "remove the old key" without requiring `JWT_SIGNING_KEY` itself to ever be unset (the startup
   guard at `AuthenticationExtensions.cs:23-28` still requires a non-empty, >= 32-byte
   `JWT:SigningKey` — see the note in File-level task 2).
```

## 4. Safety / impact

**Additive** — no migration, no data change. The only behavioural change is the `kid` header in
new tokens (harmless — JWT consumers never inspect it). Backward compatibility: the legacy single
`JWT:SigningKey` config continues to work with `kid = "k0"`, so existing deployments and
`.env.prod` files require zero changes.

## 5. File-level tasks

1. **`Infrastructure/Auth/JwtTokenService.cs`** — extend `JwtOptions` (§3.2), add
   `ResolveActiveKey`, set `kid` header (§3.3), add `IssueMfaPending` if R5-61 needs it.
2. **`API/Extensions/AuthenticationExtensions.cs`** — replace `IssuerSigningKey` with
   `IssuerSigningKeys` (§3.4), add `ResolveAllKeys` helper, move the >= 32-byte check (currently
   lines 23-28, applied once to `JWT:SigningKey`) into `ResolveAllKeys` so it applies per key.
   `JWT:SigningKey` itself must stay required and non-empty (the top-level guard on lines 23-25
   still fires if it's blank) — the rotation runbook (§3.5) never unsets it, only repoints it, for
   exactly this reason.
3. **`docker-compose.prod.yml`** — add `JWT__ActiveKeyId`, `JWT__Keys__0__Id`,
   `JWT__Keys__0__Secret`, `JWT__Keys__1__Id`, `JWT__Keys__1__Secret` lines (after `:46`, §3.1).
4. **`.env.prod.example`** — add `JWT_KEY_1_ID` / `JWT_KEY_1_SECRET` (commented out / empty by
   default) plus a comment block documenting the multi-key config and pointing at the DEPLOY.md
   rotation runbook.
5. **`DEPLOY.md`** — add the rotation runbook (§3.5).
6. **`Tests/JwtKeyRotationTests.cs`** (new) — §6.

## 6. Tests

New `Tests/JwtKeyRotationTests.cs`:

1. **`Issue_SetsKidHeader`** — issue a token, decode the JWT header, assert `kid` == the active key id.
2. **`Validate_AcceptsTokenFromActiveKey`** — issue with key k1, validate with keys [k0, k1] → valid.
3. **`Validate_AcceptsTokenFromOldKeyDuringOverlap`** — issue with key k0, validate with keys
   [k0, k1] and active = k1 → still valid (the old key is in the list).
4. **`Validate_RejectsTokenAfterKeyRemoval`** — issue with key k0, validate with keys [k1] only
   → invalid (signature mismatch).
5. **`LegacyConfig_SingleSigningKey_WorksAsK0`** — `JwtOptions { SigningKey = "...", Keys = [] }`
   → `ResolveActiveKey` returns id `"k0"` with that secret; issued token validates.
6. **`AllKeys_MustBe32Bytes`** — a key shorter than 32 bytes in the list →
   `InvalidOperationException` at startup.
7. **`ActiveKeyId_NotInList_Throws`** — `ActiveKeyId = "k2"` with keys [k0, k1] → throw.

## 7. Acceptance criteria

1. `dotnet test --filter JwtKeyRotationTests` → green (7 tests).
2. Issue a token, decode with `jq`: `echo '<token>' | cut -d. -f1 | base64 -d | jq .kid` →
   `"k0"` (legacy config).
3. With two keys configured and `ActiveKeyId=k1`: new token has `kid: "k1"`; a token issued
   before the switch (with `kid: "k0"`) still authenticates.
4. After removing k0 from the list: the old token returns 401.
5. Existing `.env.prod` with only `JWT_SIGNING_KEY` (no `Keys` section) → API boots and works.
6. `grep -c 'IssuerSigningKeys' API/Extensions/AuthenticationExtensions.cs` → 1.
7. `grep -c 'kid' Infrastructure/Auth/JwtTokenService.cs` → at least 1.
8. `DEPLOY.md` contains "JWT key rotation".

## 8. Rollback

`git revert`; redeploy. No migration. Tokens issued with a `kid` header still validate against
the old single-key path (the header is ignored by the old validator). The only risk is if the
operator already rotated keys in `.env.prod`; in that case, revert the `.env.prod` to the single
`JWT_SIGNING_KEY` value.

## 9. Release steps

1. Merge PR. `unit` CI covers new tests.
2. Deploy: `bash scripts/deploy-api.sh`. No config change needed — legacy single-key works.
3. Verify: decode a fresh token’s header → `kid: "k0"`.
4. When ready to rotate: follow the runbook in DEPLOY.md.

## 10. Out of scope

Asymmetric keys (RSA/ECDSA), JWKS endpoint (`/.well-known/jwks.json`), automatic key rotation
via a background job, key rotation UI in the dashboard, refresh tokens, session listing,
token revocation list, `kid` in the Swagger security scheme.
