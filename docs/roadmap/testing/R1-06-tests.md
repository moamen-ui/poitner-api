# R1-06-tests — API-key hardening (hash lookup · AES-GCM display · backfill)

Harness: [`00-HARNESS.md`](00-HARNESS.md). Execution doc: [`../execution/R1-06-api-key-hardening.md`](../execution/R1-06-api-key-hardening.md).

## Covers

- AC-1 (`users` legacy key column all NULL; `api_keys` hash-only, no plaintext anywhere) and AC-2
  (unique `Hash` index + partial unique active-per-user index) → **R1-06-01**.
- AC-3 (pre-upgrade key logs in) + AC-4 (`GET /api/me/api-key` returns the same key after the
  upgrade) → **R1-06-02** (two-image `upgrade` job).
- AC-5 (regenerate → old key rejected, new works, exactly one non-revoked row) → **R1-06-03**.
- AC-6 (boot warning; rotate `Auth:ApiKeyEncryptionKey` → login still works, display fails, nothing
  minted/revoked) → **R1-06-01** step 1 (warning) + **R1-06-04** (rotation).
- AC-7 (`just test` green) — unit-level.

## Preconditions

- Seed complete (`keys.json` holds one raw key per persona; H-03's super-admin caveat applies).
- Nightly tier for 01/02/04; 03 is PR.
- `docker compose exec -T db psql -U pointer -d pointer` is the SQL channel (compose env:
  `POSTGRES_USER=pointer`, `POSTGRES_DB=pointer`). **Decision:** all SQL below uses this repo's
  snake_case migration naming (`users.api_key`, `api_keys.prefix/hash/encrypted/revoked_at`) — the
  execution doc's acceptance SQL quotes PascalCase (`"ApiKey"`, `"Hash"`), which does not match the
  naming convention every existing migration uses (`20260831183037_AddUserApiKey.cs` adds
  `users.api_key`); the assertions are identical either way.
- `e2e/scripts/restart-api.mjs` (R1-04) reused for R1-06-04; the two-image job (02) needs a compose
  override that drops the dev bind mount — see the scenario.

## Scenarios

| id | intent | tier | layer | role | steps | expected | evidence |
|---|---|---|---|---|---|---|---|
| R1-06-01 | DB is hash-only + indexes + boot warning | nightly | api | — | After reset+seed: 1. `docker compose logs api 2>&1 \| grep -c 'set Auth:ApiKeyEncryptionKey for production'`. 2. `psql -tAc "SELECT count(*) FROM users WHERE api_key IS NOT NULL"`. 3. `psql -tAc "SELECT count(*) FROM api_keys"`. 4. With `K = keys.json.wsAdmin.apiKey` (node): `psql -tAc "SELECT prefix, hash, scopes, revoked_at FROM api_keys WHERE prefix = '${K.slice(0,12)}'"`; compute `sha256hex(K)` locally (`crypto.createHash('sha256')`). 5. Plaintext scan: `psql -tAc "SELECT count(*) FROM api_keys WHERE prefix ~ 'ptr_' OR hash ~ 'ptr_' OR encrypted ~ 'ptr_[0-9a-f]{40}'"`. 6. `psql -tAc "SELECT indexdef FROM pg_indexes WHERE tablename = 'api_keys'"`. | 1 → ≥ 1 (derived-key boot warning, §B). 2 → `0` (backfill nulled the legacy column). 3 → ≥ 8 (one row per seeded persona key; super-admin per H-03 decision). 4 → one row: `hash === sha256hex(K)`, `scopes === 7` (Full), `revoked_at` NULL. 5 → `0`. 6 → one def contains `UNIQUE` + `(hash)`; one contains `UNIQUE` + `(user_id)` + `WHERE (revoked_at IS NULL)`. | report row; all six outputs in `detail` |
| R1-06-02 | `legacy-key-still-logs-in-after-upgrade` | nightly (`upgrade` job; manual fallback `run-e2e.sh --upgrade`) | api | wsAdmin | Dedicated nightly job `upgrade` (extends the R1-07 workflow). **Decision: pin the legacy ref** — workflow env `LEGACY_REF` = the last `main` commit before R1-06's `AddApiKeysTable` migration merged (recorded when the job is created; the job fails with instructions if that ref no longer builds). 1. `git fetch --depth 1 origin "$LEGACY_REF" && git worktree add ../legacy "$LEGACY_REF"`; `docker build -t pointer-api:legacy --target final ../legacy`; `docker build -t pointer-api:candidate --target final .` (the published stage — the dev target bind-mounts source, which would defeat the two-image test). 2. Write `e2e/state/upgrade/compose.upgrade.yml`: `services: { api: { image: pointer-api:${API_TAG}, volumes: [], environment: ["Email__Provider=smtp","Email__Smtp__Host=mailpit","Email__Smtp__Port=1025","Email__Enabled=true"] } }` — the empty `volumes` **drops the `./:/src` bind mount** so the image's code is what runs. 3. Initial wipe (the only `down -v` of the job): `API_TAG=legacy docker compose -f docker-compose.yaml -f e2e/state/upgrade/compose.upgrade.yml down -v --remove-orphans || true`, then `API_TAG=legacy … up -d`; poll `GET /swagger/v1/swagger.json` until 200 (same loop as `reset.sh:34`). 4. **Reset + seed on the legacy image:** `node e2e/scripts/seed.mjs` (its logins run against the legacy API); `rawLegacyKey = state/keys.json.wsAdmin.apiKey` — minted by the legacy image, i.e. stored the pre-upgrade way. 5. Swap images preserving everything: `docker compose -f docker-compose.yaml -f e2e/state/upgrade/compose.upgrade.yml stop api` (db + mailpit + `pgdata` volume untouched) → `API_TAG=candidate docker compose -f docker-compose.yaml -f e2e/state/upgrade/compose.upgrade.yml up -d api` — same compose project, **same volume, never `down -v` between the halves** (00-HARNESS §9). 6. Re-wait `/swagger/v1/swagger.json` (≤ 120 s — the candidate boots, migrates, and runs `ApiKeyBackfillService`). 7. `POST /api/auth/login-with-key {apiKey: rawLegacyKey}` → `data.token`. 8. With that token: `GET /api/me/api-key`. 9. `psql -tAc "SELECT count(*) FROM users WHERE api_key IS NOT NULL"` and `… FROM api_keys WHERE prefix = '<rawLegacyKey.slice(0,12)>' AND revoked_at IS NULL`. Steps 7–9 live in `e2e/scripts/upgrade-assert.mjs` (records into `state/report.md`). | 7 → 200, `data.status === 'ok'`, `data.user.email === 'e2e-owner@example.com'` (AC-3). 8 → 200, `data.apiKey === rawLegacyKey` (AC-4 — backfill encrypted the same raw key). 9 → `0` and `1`. | job log + `report.md` artifact with the row |
| R1-06-03 | `regenerated-key-old-one-rejected` | PR | api | pm | **Decision: use `pm`** — regenerating `wsAdmin`'s key would invalidate `keys.json` for every later spec. 1. `POST /api/auth/login` as pm → `GET /api/me/api-key` → `oldKey` (should equal `keys.json.pm.apiKey`). 2. `POST /api/me/api-key/regenerate` → `newKey`. 3. `POST /api/auth/login-with-key {apiKey: oldKey}`. 4. `POST /api/auth/login-with-key {apiKey: newKey}` → token; then `GET /api/me/api-key` with it. 5. `psql -tAc "SELECT count(*) FROM api_keys WHERE prefix = '<newKey.slice(0,12)>' AND revoked_at IS NULL"` and the same for `oldKey`'s prefix. 6. Rewrite `state/keys.json → pm.apiKey = newKey` (later phases keep working). | 2 → 200; `newKey` matches `/^ptr_[0-9a-f]{40}$/`, ≠ `oldKey`; `data.prefix === newKey.slice(0,12)`; `data.lastUsedAt` present after step 4 (additive fields, R1-06 task 8). 3 → **400**, envelope `isSuccess === false` (old key revoked; status-based assert, AC-5's `InvalidApiKey`). 4 → `status === 'ok'`; reveal returns `newKey`. 5 → `1` (new) and `0` for the old prefix's non-revoked count — exactly one active row. | report row |
| R1-06-04 | reveal round-trip + encryption-key rotation | nightly | api | deputy | **Decision: use `deputy`** (low-reuse persona; `wsAdmin`/`pm` keys are load-bearing for other specs). Restart-grouped with R1-04-04 (≤ 3 recreates — see that doc's Flake notes; this scenario's step 2 recreate also restores `minCliVersion` to default). 1. `POST /api/auth/login` as deputy → `GET /api/me/api-key` → `key1`; `POST /api/auth/login-with-key {apiKey: key1}` → ok (reveal + login round-trip). Baseline: `psql -tAc "SELECT count(*) FROM api_keys"` and `… WHERE revoked_at IS NULL"`. 2. `node e2e/scripts/restart-api.mjs --env Auth__ApiKeyEncryptionKey=$(openssl rand -base64 32)` (override file + `up -d --force-recreate api`; volume/db preserved; re-wait `/swagger`). 3. `POST /api/auth/login-with-key {apiKey: key1}` → token (hash lookup — unaffected by the rotation). 4. With that token: `GET /api/me/api-key` (expect failure). 5. Repeat the two `psql` counts — nothing minted, nothing revoked. 6. Restore (the group's final clean recreate): `restart-api.mjs` with no `--env` → `GET /api/me/api-key` (fresh deputy password login) → `apiKey === key1` (**Decision:** the HKDF-from-`JWT:SigningKey` fallback is deterministic, so returning to the derived key re-enables decryption of the old blob — assert it). | 3 → **200** `status === 'ok'`. 4 → **400** with envelope `message === 'key display unavailable'` (AC-6's decrypt-failure rule). 5 → both counts identical to baseline. 6 → 200, key unchanged. | report row; `docker compose logs api --tail 50` on failure |

## Spec files

- `e2e/api/key-store.spec.mjs` — R1-06-01 (node `child_process.execFile` for `docker compose exec`;
  `lib/report.mjs`).
- `e2e/api/api-keys.spec.mjs` — R1-06-03 (rewrites `keys.json` — see step 6).
- `e2e/api/key-rotation.spec.mjs` — R1-06-04 (uses `restart-api.mjs`; **nightly-only flag**, grouped
  restart phase).
- `e2e/scripts/upgrade-assert.mjs` + `.github/workflows/e2e.yml` job `upgrade` — R1-06-02 (the job
  generates `e2e/state/upgrade/compose.upgrade.yml`; `run-e2e.sh --upgrade` wraps the same sequence
  locally as the manual fallback).
- New helper: none beyond R1-04's `restart-api.mjs`; a tiny `psql(sql)` wrapper inside
  `key-store.spec.mjs` is enough.

## Not covered here

- Crypto round-trip, tamper → throw, HKDF derivation, hash stability —
  `Tests/ApiKeyProtectorTests.cs`.
- Service invariants (create-once, regenerate revokes, resolve-by-hash, last-used throttle,
  decrypt-failure never mints/revokes, one-active-row) — `Tests/ApiKeyServiceTests.cs` (InMemory
  enforces neither unique nor partial indexes — DB-level uniqueness is exactly R1-06-01 step 6,
  per the execution doc's InMemory caveat).
- Backfill idempotency + rerun no-op — `Tests/ApiKeyBackfillTests.cs`; the *live* one-shot backfill
  on real data is R1-06-02 step 9.
- Existing raw-key login assertions with updated fixtures — `Tests/ApiKeyAuthTests.cs` (fixtures
  change, assertions preserved — R1-06 task 5).
- Dashboard "Last used" display — dashboard repo (the API surface it reads is asserted in
  R1-06-03 step 2).
- `DropUserApiKeyColumn` and §25 multi-key scopes — R2, out of scope here.
- Rollback hazard (boot-once-then-rollback needs regeneration) — documented in DEPLOY.md, not
  automatable without a third image; manual.

## Flake notes

- R1-06-02's two halves share one compose project and one `pgdata` volume; any accidental
  `down -v` between steps 4 and 5 destroys the fixture — the job script greps itself for `down -v`
  occurring after step 3 and fails fast if reordered (cheap belt-and-braces for a nightly).
- R1-06-02 timing: the candidate's first boot runs migrations + backfill; the 120 s `/swagger`
  re-wait covers it — do not assert on backfill logs before the re-wait completes.
- R1-06-03 must rewrite `keys.json` **before** any later phase reads `pm`'s key; it runs in the api
  phase, ahead of cli/widget phases (00-HARNESS §8 order).
- R1-06-04 shares the 3-restart budget with R1-04-04 (sequence ① Cli override → ② this rotation →
  ③ clean restore); running it standalone locally (`--upgrade`-style partial run, `E2E_REUSE=1`) is
  fine — it performs its own steps 2 and 6.
- `openssl rand -base64 32` output can contain `/` and `+` — the override file quotes the value
  (`Auth__ApiKeyEncryptionKey=<v>` in YAML list form needs no quoting, but keep the value on one
  line).
