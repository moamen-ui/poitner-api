# R1-06 — API-key hardening (NEW-5 · Release 1, slip → R2 week 1 · 2–3 days)

## Goal
API keys stop living in plaintext in the database. Lookups use a SHA-256 hash; the dashboard can still
**re-display** a user's key (an explicit product requirement — `pointer-init.md:252-255`: "always
re-viewable, not a one-time reveal") via authenticated encryption at rest. The key string a user holds
does not change, so nothing the CLI writes to disk changes. Lays the table that §25 (multiple scoped
keys) will extend.

## Out of scope
- Multiple keys per user, scope enforcement, per-project keys, dashboard key management UI (§25, held).
- Rotating existing keys (users keep their current key; it is migrated in place).

## Prerequisites
- None (independent of R1-02; must land **before R2-02 MCP**).
- Facts: `User.ApiKey` (string?, `Domain/Entity/User.cs:40`); generation `ProfileService.cs:66-75`
  (`"ptr_" + 40 lowercase hex chars`); `GetOrCreateApiKeyAsync`/`RegenerateApiKeyAsync`
  (`ProfileService.cs:37-60`); login `AuthService.LoginWithApiKeyAsync` (`AuthService.cs:201-240`,
  `IgnoreQueryFilters`, equality on `u.ApiKey`); JWT issue `Infrastructure/Auth/JwtTokenService.cs`;
  tests `Tests/ApiKeyAuthTests.cs`.

## Design

### A. Entity `Domain/Entity/ApiKey.cs`
| Column | Type | Notes |
|---|---|---|
| `Id` | int | PK (BaseEntity) |
| `UserId` | int | FK → User; index |
| `OwnerId` | Guid? | tenant (strict-own filter bucket, same as User) |
| `Prefix` | string(16) | first 12 chars of the key, e.g. `ptr_0a1b2c3d` — display/masking only |
| `Hash` | string(64) | lowercase hex SHA-256 of the full key; **unique index** |
| `Encrypted` | string | base64 of AES-256-GCM(nonce ‖ ciphertext ‖ tag) of the full key |
| `Scopes` | int | flags: `Read=1, Apply=2, Manage=4`; `Full = 7`. R1 always writes `Full`; not enforced yet |
| `Label` | string(60)? | null in R1 ("Default") |
| `LastUsedAt` | DateTime? | updated at most once per minute |
| `RevokedAt` | DateTime? | non-null = dead |
| `CreatedAt` | DateTime | BaseEntity |

Invariant in R1: **at most one non-revoked row per user** (enforced in service; partial unique index
`(UserId) WHERE RevokedAt IS NULL` in the migration).

### B. Crypto — `Infrastructure/Security/ApiKeyProtector.cs` (`IApiKeyProtector`)
- `string Hash(string key)` → SHA-256 hex.
- `string Encrypt(string key)` / `string Decrypt(string blob)` → AES-256-GCM, 12-byte random nonce, 16-byte tag.
- Key material: `Auth:ApiKeyEncryptionKey` (base64, 32 bytes). **Decision:** if absent, derive with
  HKDF-SHA256 from `Jwt:Key` with info `"pointer-apikey-v1"` and log a startup **warning** ("set
  Auth:ApiKeyEncryptionKey for production"). Rationale: self-hosters get a working default; rotating
  `Jwt:Key` then breaks *display* only (lookups still work via `Hash`) — documented in DEPLOY.md.
- `.env.prod.example` + `docker-compose.prod.yml`: add `Auth__ApiKeyEncryptionKey`.

### C. Service changes
- `IApiKeyService` (`Application/Services/Interfaces`) with `GetOrCreateAsync(userId)`, `RegenerateAsync(userId)`, `RevealAsync(userId)`, `ResolveUserAsync(rawKey)`, `TouchLastUsedAsync(id)`.
- `ProfileService.GetOrCreateApiKeyAsync` / `RegenerateApiKeyAsync` delegate to it: regenerate = set `RevokedAt` on the current row, insert a new one, return the new raw key. `ApiKeyResponse { ApiKey }` unchanged (decrypted).
- `AuthService.LoginWithApiKeyAsync`: `h = protector.Hash(key)` → `ApiKey` row with `Hash == h && RevokedAt == null` (IgnoreQueryFilters, include `User.Role`) → same approval/active checks → issue JWT (add claim `key_scopes` = `Scopes`) → `TouchLastUsedAsync`. **Stop reading `User.ApiKey`.**
- Generation stays `ptr_` + 40 hex (`ProfileService.cs:73`) — moved into `ApiKeyService`; uniqueness check now on `Hash`.

### D. Migration `AddApiKeysTable` (additive)
1. Create `api_keys` table + indexes.
2. Data step in the same migration (raw SQL is not possible for AES — do it in code): an
   `IHostedService` `ApiKeyBackfillService` runs once at startup **after** migrations: for every user with
   non-empty `ApiKey` and no `api_keys` row → insert `{Prefix, Hash, Encrypted, Scopes=Full}`; then set
   `User.ApiKey = null`. Idempotent; logs the count. (Additive rule: the `User.ApiKey` column is **kept**
   this release and dropped in R2 by a follow-up migration `DropUserApiKeyColumn`.)
3. `AppDbContext`: `ApiKey` in the strict-own filter bucket (`Infrastructure/AppDbContext.cs:58-125`).

### E. Exposure
- No endpoint shape changes. `GET /api/me/api-key` still returns the full key (decrypted).
- `MeResponse` unchanged. Optional (cheap, do it): `ApiKeyResponse` gains `Prefix` and `LastUsedAt?`
  so the dashboard can show "last used" (§25 will build on it) — additive.

## Tasks
1. Entity, mapping `Infrastructure/Mappings/ApiKeyMapping.cs` (unique `Hash`, partial unique active-per-user, FK), query-filter registration, migration.
2. `IApiKeyProtector` + implementation + `Tests/ApiKeyProtectorTests.cs` (round-trip, tamper → throws, hash stable, HKDF fallback path).
3. `IApiKeyService`/`ApiKeyService` + `Tests/ApiKeyServiceTests.cs` (create once, regenerate revokes old and old key no longer resolves, resolve by hash, last-used throttled).
4. Rewire `ProfileService` and `AuthService`; keep `Tests/ApiKeyAuthTests.cs` green (they exercise the raw-key login path — they must pass unchanged).
5. `ApiKeyBackfillService` + `Tests/ApiKeyBackfillTests.cs` (users with legacy key get a row and `User.ApiKey` nulled; rerun is a no-op; legacy raw key still logs in afterwards).
6. Config: `Auth:ApiKeyEncryptionKey` in `appsettings*.json` (empty), `.env.prod.example`, `docker-compose.prod.yml`, `DEPLOY.md` paragraph (generate with `openssl rand -base64 32`; rotation consequences).
7. `ApiKeyResponse.Prefix/LastUsedAt` (additive) + mapping.
8. `AGENTS.md`: security note "API keys are hashed for lookup and encrypted for display".

## Dashboard tasks
- Regenerate services (`ApiKeyResponse` gains `prefix`, `lastUsedAt`). Optional: show "Last used" under the key on the Profile page.

## Tests
- `Tests/ApiKeyProtectorTests.cs`, `Tests/ApiKeyServiceTests.cs`, `Tests/ApiKeyBackfillTests.cs`; existing `Tests/ApiKeyAuthTests.cs` unchanged and green.
- E2E (R2-00): `legacy-key-still-logs-in-after-upgrade`, `regenerated-key-old-one-rejected`.

## Acceptance criteria
- [ ] `SELECT "ApiKey" FROM users` is all NULL after first boot; `api_keys.Hash` populated; no plaintext key anywhere in the DB (`SELECT * FROM api_keys` shows only prefix/hash/base64 blob).
- [ ] `POST /api/auth/login-with-key` with a pre-upgrade key → `status:"ok"`.
- [ ] `GET /api/me/api-key` returns the same key the user had before the upgrade.
- [ ] Regenerate → old key → `InvalidApiKey`; new key works; exactly one non-revoked row per user.
- [ ] Booting without `Auth:ApiKeyEncryptionKey` logs the warning and works; with a wrong key later, login still works and `GET /api/me/api-key` fails cleanly (500 → mapped to a `Result.Failure` "key display unavailable"), never leaks.
- [ ] `just test` green.

## Rollout / compatibility
Zero client-visible change; CLI/skills/`pointer.sh` unaffected. Backfill is idempotent and runs on every boot in O(users-with-legacy-key). Column drop deferred to R2 (`DropUserApiKeyColumn`). Self-hosters: set the encryption key before first boot of this version or accept the derived default.

## Report template
Files changed · `just test` line · SQL evidence for AC-1 · curl transcript for legacy-key login and regenerate.
