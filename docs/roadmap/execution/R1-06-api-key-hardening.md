# R1-06 — API-key hardening (NEW-5 · Release 1, slip → R2 week 1 · 2–3 days)

## Goal
API keys stop living in plaintext in the database. Lookups use a SHA-256 hash; the dashboard can still
**re-display** a user's key (an explicit product requirement — `pointer-init.md:254-258`: "always
re-viewable there afterward, not a one-time reveal") via authenticated encryption at rest. The key string
a user holds does not change, so nothing the CLI writes to disk changes. Lays the table that §25
(multiple scoped keys) will extend.

**Chair decision (review, 2026-09-11):** keep hash-for-lookup **plus** AES-GCM-for-display rather than
hash-only/one-time-reveal. Rationale: exploiting the reversible copy requires **both** the DB and the
environment; an environment-only leak already exposes `JWT__SigningKey`, with which an attacker mints
valid 12-hour tokens for *any* user (`Infrastructure/Auth/JwtTokenService.cs:11-39`) — strictly worse than
reading comment/apply keys. A DB-only leak reveals only hash + AES-GCM blob. The re-viewable promise is
load-bearing: it is served in `pointer-init.md` and R1-03's quick-start pre-fills the key by re-fetching
it. See "Alternative" at the end for what flips if the founder prefers one-time reveal.

## Out of scope
- Multiple keys per user, scope enforcement, per-project keys, dashboard key management UI (§25, held).
- Rotating existing keys (users keep their current key; it is migrated in place).
- Dropping the `User.ApiKey` column (R2, `DropUserApiKeyColumn` — must not ship before §25 is done with the legacy column).

## Prerequisites
- None (independent of R1-02; must land **before R2-02 MCP** — R2-02 lists this as a hard prerequisite).
- Facts: `User.ApiKey` (string?, `Domain/Entity/User.cs:40`); generation `ProfileService.cs:66-75`
  (`"ptr_" + 40 lowercase hex chars`); `GetOrCreateApiKeyAsync`/`RegenerateApiKeyAsync`
  (`ProfileService.cs:37-60`); login `AuthService.LoginWithApiKeyAsync` (`AuthService.cs:201-240`,
  `IgnoreQueryFilters`, equality on `u.ApiKey`); JWT issue `Infrastructure/Auth/JwtTokenService.cs`,
  `ITokenService.Issue(User)` (`Application/Abstractions/ITokenService.cs:7`); JWT options bound from
  configuration section **`JWT`** (`Infrastructure/DependencyInjection.cs:33`,
  `s.Configure<JwtOptions>(c.GetSection("JWT"))`; env form `JWT__SigningKey`, see `.env.example`,
  `docker-compose.prod.yml`); tests `Tests/ApiKeyAuthTests.cs` (construct `AuthService`/`ProfileService`
  directly, `:84-86`, with a `FakeToken : ITokenService`, `:37`; EF **InMemory** provider, `:80`).
- Id types: callers hold the user's **Guid `PublicId`** (`currentUser.Id`, e.g. `MeController.cs:47`);
  the `User` row's PK and the new `ApiKey.UserId` FK are **int**. Service methods take the Guid and
  resolve the int internally.

## Design

### A. Entity `Domain/Entity/ApiKey.cs`
| Column | Type | Notes |
|---|---|---|
| `Id` | int | PK (BaseEntity) |
| `UserId` | int | FK → `User.Id`; index |
| `OwnerId` | Guid? | tenant (strict-own filter bucket, same as User) |
| `Prefix` | string(16) | first 12 chars of the key, e.g. `ptr_0a1b2c3d` — display/masking only |
| `Hash` | string(64) | lowercase hex SHA-256 of the full key; **unique index** |
| `Encrypted` | string | base64 of AES-256-GCM(nonce ‖ ciphertext ‖ tag) of the full key |
| `Scopes` | int | flags: `Read=1, Apply=2, Manage=4`; `Full = 7`. R1 always writes `Full`; not enforced yet |
| `Label` | string(60)? | null in R1 ("Default") |
| `LastUsedAt` | DateTime? | updated at most once per minute |
| `RevokedAt` | DateTime? | non-null = dead |
| `CreatedAt` | DateTime | BaseEntity |

Invariant in R1: **at most one non-revoked row per user** — enforced in the service **and** by a
Postgres partial unique index `(UserId) WHERE "RevokedAt" IS NULL` in the migration. Note: the test
fixtures use EF InMemory, which enforces **neither** the unique `Hash` index nor the partial index — service
tests assert the one-active-row invariant through the service; DB-level uniqueness is Postgres-only and
is verified by the acceptance SQL, not by unit tests.

### B. Crypto — `Infrastructure/Security/ApiKeyProtector.cs` (`IApiKeyProtector`)
- `string Hash(string key)` → SHA-256 hex.
- `string Encrypt(string key)` / `string Decrypt(string blob)` → AES-256-GCM, 12-byte random nonce, 16-byte tag.
- Key material: `Auth:ApiKeyEncryptionKey` (base64, 32 bytes). **Decision:** if absent, derive with
  HKDF-SHA256 from **`JWT:SigningKey`** (the existing `JwtOptions.SigningKey`, section `JWT`) with info
  `"pointer-apikey-v1"` and log a startup **warning** ("set Auth:ApiKeyEncryptionKey for production").
  The derived path exists for zero-config boots (dev, first self-host boot); **production must set
  `Auth:ApiKeyEncryptionKey`**. Coupling note: with the derived key, one env leak yields both the JWT
  signing secret and the key-encryption secret — acceptable because env-only or DB-only compromise
  still reveals no key material, but it is why production sets a dedicated key. Rotating `JWT:SigningKey`
  (or the dedicated key) breaks *display* only — lookups still work via `Hash` — documented in DEPLOY.md.
- `.env.prod.example` + `docker-compose.prod.yml`: add `Auth__ApiKeyEncryptionKey`.

### C. Service changes
- `IApiKeyService` (`Application/Services/Interfaces`) with `GetOrCreateAsync(Guid publicId)`,
  `RegenerateAsync(Guid publicId)`, `RevealAsync(Guid publicId)`, `ResolveUserAsync(string rawKey)`,
  `TouchLastUsedAsync(int apiKeyId)`.
- `ProfileService.GetOrCreateApiKeyAsync` / `RegenerateApiKeyAsync` delegate to it: regenerate = set
  `RevokedAt` on the current row, insert a new one, return the new raw key. `ApiKeyResponse { ApiKey }`
  unchanged (decrypted).
- **Decrypt-failure rule (mandatory):** `RevealAsync`/`GetOrCreateAsync` on a row whose `Encrypted` blob
  fails to decrypt (wrong/rotated key, tamper) returns `Result.Failure("key display unavailable")` and
  **MUST NOT create, revoke or rotate anything**. `GetOrCreateAsync` treats "row exists but undecryptable"
  as *exists* — never as *missing* — otherwise a rotated encryption key would silently regenerate every
  viewed key. Only `RegenerateAsync` (an explicit user action) mints a new key.
- `AuthService.LoginWithApiKeyAsync`: `h = protector.Hash(key)` → `ApiKey` row with
  `Hash == h && RevokedAt == null` (`IgnoreQueryFilters`, include `User.Role`) → same approval/active checks
  → issue JWT with claim `key_scopes` = `Scopes` → `TouchLastUsedAsync`. **Stop reading `User.ApiKey`.**
- `key_scopes` mechanism: `ITokenService.Issue(User)` has no scopes channel. Add an overload
  `string Issue(User user, int? keyScopes = null)` (interface + `JwtTokenService` + the test `FakeToken`);
  emit the `key_scopes` claim only when non-null; password login passes null.
- `TouchLastUsedAsync` and the backfill (D) run without a tenant context → they must use
  `IgnoreQueryFilters()` (strict-own filters would otherwise return zero rows).
- Generation stays `ptr_` + 40 hex (`ProfileService.cs:73`) — moved into `ApiKeyService`; uniqueness
  check now on `Hash`.

### D. Migration `AddApiKeysTable` (additive)
1. Create `api_keys` table + indexes (unique `Hash`; partial unique `(UserId) WHERE "RevokedAt" IS NULL`).
2. Data step (raw SQL is not possible for AES — do it in code): an `IHostedService` `ApiKeyBackfillService`
   runs once at startup **after** migrations, with `IgnoreQueryFilters()`: for every user with non-empty
   `ApiKey` and no `api_keys` row → insert `{Prefix, Hash, Encrypted, Scopes=Full, OwnerId = user.OwnerId}`;
   then set `User.ApiKey = null`. Idempotent; logs the count. (Additive rule: the `User.ApiKey` column is
   **kept** this release and dropped in R2 by `DropUserApiKeyColumn`.)
3. `AppDbContext`: `ApiKey` in the strict-own filter bucket (`Infrastructure/AppDbContext.cs:58-125`).

### E. Exposure
- No endpoint shape changes. `GET /api/me/api-key` still returns the full key (decrypted).
- `MeResponse` unchanged. Optional (cheap, do it): `ApiKeyResponse` gains `Prefix` and `LastUsedAt?`
  so the dashboard can show "last used" (§25 will build on it) — additive.

### Alternative (if the founder prefers one-time reveal)
Not chosen; recorded so the decision is flippable in one PR:
- Drop the `Encrypted` column and `RevealAsync`; `GET /api/me/api-key` returns `Prefix` + `LastUsedAt` only.
- The raw key is returned exactly once by `POST /api/me/api-key/regenerate` (and by first creation); it is
  never persisted in reversible form.
- R1-03's quick-start loses the re-fetch: it becomes an inline "Generate key" action that injects the key
  into the shown `npx -y pointer-feedback init --key …` command once (kept in component memory for the
  session, never server-side after display); the profile page shows `Prefix` + `LastUsedAt`.
- `pointer-init.md:254-258` is rewritten ("shown once at generation — regenerate to get a new one").
- `Auth:ApiKeyEncryptionKey`, the HKDF fallback and the decrypt-failure rule disappear.

## Tasks
1. Entity, mapping `Infrastructure/Mappings/ApiKeyMapping.cs` (unique `Hash`, partial unique active-per-user, FK), query-filter registration, migration.
2. `IApiKeyProtector` + implementation + `Tests/ApiKeyProtectorTests.cs` (round-trip, tamper → throws, hash stable, HKDF fallback from `JWT:SigningKey`).
3. `ITokenService.Issue(User, int? keyScopes = null)` overload; update `JwtTokenService` and the test double `FakeToken` (`Tests/ApiKeyAuthTests.cs:37`).
4. `IApiKeyService`/`ApiKeyService` + `Tests/ApiKeyServiceTests.cs` (create once; regenerate revokes old and old key no longer resolves; resolve by hash; last-used throttled; **decrypt failure returns Failure and does not mint/revoke**; one-active-row invariant asserted through the service).
5. Rewire `ProfileService` and `AuthService`. `Tests/ApiKeyAuthTests.cs`: **fixtures updated, assertions preserved** — the tests construct `AuthService(...)` / `ProfileService(...)` directly (`:84-86`), so constructor wiring (new `IApiKeyService`/`IApiKeyProtector` deps) and `FakeToken` change; every existing assertion about raw-key login behaviour must still hold.
6. `ApiKeyBackfillService` + `Tests/ApiKeyBackfillTests.cs` (users with legacy key get a row and `User.ApiKey` nulled; rerun is a no-op; legacy raw key still logs in afterwards; runs with `IgnoreQueryFilters`).
7. Config: `Auth:ApiKeyEncryptionKey` in `appsettings*.json` (empty), `.env.prod.example`, `docker-compose.prod.yml`, `DEPLOY.md` paragraph (generate with `openssl rand -base64 32`; rotation consequences; production must set it).
8. `ApiKeyResponse.Prefix/LastUsedAt` (additive) + mapping.
9. `AGENTS.md`: security note "API keys are hashed for lookup and encrypted for display; `Auth:ApiKeyEncryptionKey` required in production".

## Dashboard tasks
- Regenerate services (`ApiKeyResponse` gains `prefix`, `lastUsedAt`). Optional: show "Last used" under the key on the Profile page.

## Tests
- `Tests/ApiKeyProtectorTests.cs`, `Tests/ApiKeyServiceTests.cs`, `Tests/ApiKeyBackfillTests.cs`; existing `Tests/ApiKeyAuthTests.cs` with fixtures updated and all assertions preserved.
- InMemory caveat: unique/partial indexes are not enforced by the InMemory provider — do not write tests that "prove" uniqueness there; the DB-level guarantee is covered by the acceptance SQL below.
- E2E scenarios `legacy-key-still-logs-in-after-upgrade`, `regenerated-key-old-one-rejected` — defined in `docs/roadmap/testing/R1-06-tests.md` (R2-00 remains the driver/CI-matrix host only).

## Acceptance criteria
- [ ] `SELECT "ApiKey" FROM users` is all NULL after first boot; `api_keys."Hash"` populated; no plaintext key anywhere in the DB (`SELECT * FROM api_keys` shows only prefix/hash/base64 blob).
- [ ] `\d api_keys` shows the unique `Hash` index and the partial unique `(UserId) WHERE "RevokedAt" IS NULL` index.
- [ ] `POST /api/auth/login-with-key` with a pre-upgrade key → `status:"ok"`.
- [ ] `GET /api/me/api-key` returns the same key the user had before the upgrade.
- [ ] Regenerate → old key → `InvalidApiKey`; new key works; exactly one non-revoked row per user.
- [ ] Booting without `Auth:ApiKeyEncryptionKey` logs the warning and works. Then change the key (simulating rotation): login still works; `GET /api/me/api-key` returns `Result.Failure("key display unavailable")`; `SELECT count(*) FROM api_keys WHERE "RevokedAt" IS NULL` is unchanged (nothing minted or revoked).
- [ ] `just test` green.

## Rollout / compatibility
Zero client-visible change; CLI/skills/`pointer.sh` unaffected. Backfill is idempotent and runs on every boot in O(users-with-legacy-key). Column drop deferred to R2 (`DropUserApiKeyColumn`). Self-hosters: set `Auth:ApiKeyEncryptionKey` before first boot of this version or accept the derived default.
**Rollback hazard:** the backfill nulls `User.ApiKey` while the column still exists. A self-hoster who boots this version once and then rolls back to the previous binary has no keys left in `User.ApiKey` — every user must regenerate. Document in DEPLOY.md: "back up the DB before upgrading; rollback after first boot requires key regeneration."

## Report template
Files changed · `just test` line · SQL evidence for AC-1/AC-2 · curl transcript for legacy-key login, regenerate, and the rotated-key display failure.
