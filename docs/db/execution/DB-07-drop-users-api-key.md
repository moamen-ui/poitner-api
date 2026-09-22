# DB-07 — Drop the legacy plaintext `users.api_key` column

Review finding: S-10. Rules: R2 (contract), R5, R7, R10, R11, R13. **Class: Destructive** (drops a
column). One migration + removal of the one-shot backfill code. Independent of every other doc's
*schema*, but **must not start before [DB-09](DB-09-migration-apply-gate.md) has shipped**: this is
the first Destructive migration and it has to go through the enforced explicit path
(`POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-db07 bash scripts/deploy-api.sh`) rather than
the written-only R7 procedure. The migration class also carries `[ContractMigration("DB-07")]`
next to the marker (DB-09 §3).

**Owner approval:** `Approved to drop users.api_key — approved 2026-09-22 by Moamen (owner; instruction "choose the best for clean db", relayed by the orchestrator)`. Paste this line into the PR description verbatim. **Status 2026-09-22 (evening): approved; DB-09 merged (`ff25a8d`) — ready to implement.** May ship in the DB-03..08 batch deploy (DB-RULES R7.1, label `pre-db03-08`) or alone with `pre-db07`.

**Deployed 2026-09-22 12:05 UTC**, batched with DB-03/DB-06/DB-03b(API)/DB-08 in one contract
deploy per R7.1 (dump `pre-db03-08`), after the pre-check (non-null `api_key` count `0`) and a
combined rehearsal on a same-day dump (`pre-batch-rehearsal`). After: `users.api_key` is gone.
Implementation note: the §7.1 snapshot grep can never read `0` because of the unrelated
`api_keys` table name — intent was verified manually instead.

## 1. Goal

Finish R1-06 (`docs/roadmap/execution/R1-06-api-key-hardening.md`): keys have lived hashed +
encrypted in `api_keys` since `20260911170058_AddApiKeysTable`; the plaintext column was kept "for
one release" and nulled on every boot. Remove the column, its unique index, the `User.ApiKey`
property and the startup backfill. User-visible reason: none; it closes the "are API keys stored in
plaintext?" question permanently.

## 2. Prerequisites (verified facts)

- `Domain/Entity/User.cs:33-40` — `public string? ApiKey { get; set; }` with its doc-comment.
- `Infrastructure/Mappings/UserMapping.cs:37-38` — `b.Property(x => x.ApiKey).HasColumnName("api_key").HasMaxLength(64); b.HasIndex(x => x.ApiKey).IsUnique();`
- Column added by `20260831183037_AddUserApiKey` (`api_key varchar(64)`, unique index `IX_users_api_key`).
- `API/Startup/ApiKeyBackfillService.cs` (static class `ApiKeyBackfill`, `RunAsync`, `:20-75`): reads `u.ApiKey` (`:31-34,52`), inserts `api_keys` rows, then `user.ApiKey = null` (`:69`). Called from `API/Program.cs:171-173` after `MigrateAsync`/`SeedAsync`.
- Other mentions: `Application/Services/Interfaces/IAuthService.cs:10` (doc-comment only, "User.ApiKey"), `Domain/Entity/ApiKey.cs:10-12` (doc-comment, "dropped in R2"), `docs/roadmap/execution/00-API-INVENTORY.md` §1 bullet "API key storage today". `AuthService.cs:232` `request.ApiKey` is the **request DTO**, not the column — unrelated.
- Login by key uses `api_keys.hash` (`ApiKeyMapping.cs:39`; `IApiKeyService`), not `users.api_key`.
- Tests: `grep -rn "\.ApiKey\b" Tests/` — any hit that sets `User.ApiKey` belongs to the backfill's tests and is removed with it; hits on `ApiKey` **entities** or request DTOs stay. (Not enumerated here — the implementer runs the grep and lists the hits in the PR.)
- Pre-check (rehearsal and prod; must be `0`): `SELECT count(*) FROM users WHERE api_key IS NOT NULL;` — true after any boot since R1-06 shipped (backfill nulls the column).

## 3. Design

Expected migration `DropUsersLegacyApiKey`, `Up()`:
1. `DropIndex(name: "IX_users_api_key", table: "users")`
2. `DropColumn(name: "api_key", table: "users")`

`Down()`: `AddColumn<string>("api_key", "users", type: "character varying(64)", maxLength: 64, nullable: true)` + `CreateIndex("IX_users_api_key", "users", "api_key", unique: true)` (values are **not** recoverable — see §8).

Existing rows: lose a column that is NULL in every row (pre-check). No other change.

## 4. Safety classification

**Destructive** (R2 contract, R5). Marker, **verbatim**, on the line above `Up(`:
`// DB-RULES: R2 contract approved 2026-09-22 by Moamen (owner; instruction "choose the best for clean db", relayed by the orchestrator; docs/db/execution/DB-07-drop-users-api-key.md)`
and `[ContractMigration("DB-07")]` on the class (both required together, `Tests/MigrationSafetyTests.cs:158-172`).
Dump `pre-db07` (or the batch's `pre-db03-08`) immediately before; R7 explicit path only.

## 5. File-level tasks

1. Delete `API/Startup/ApiKeyBackfillService.cs`; remove the two comment lines and the `await ApiKeyBackfill.RunAsync(app.Services);` line from `API/Program.cs` (`:175-177` as of `ff25a8d`, directly after `AdminSeeder.SeedAsync`; re-locate by text if moved) — `MigrationGate.AllowMigrateAsync` (`:172-173`), `MigrateAsync` and `SeedAsync` stay.
2. `Domain/Entity/User.cs:33-40` — delete the property and its doc-comment.
3. `Infrastructure/Mappings/UserMapping.cs:37-38` — delete both lines.
4. `Domain/Entity/ApiKey.cs:10-12` — replace the sentence "Replaces the plaintext `User.ApiKey` column, which is kept for one release … dropped in R2." with "Replaces the plaintext `User.ApiKey` column (dropped by DB-07)." `Application/Services/Interfaces/IAuthService.cs:10` — replace "(User.ApiKey)" with "(an `api_keys` row)".
5. `dotnet build` — fix any compile error **only** by deleting code that referenced `User.ApiKey` (backfill tests); if a non-test, non-backfill file references it, stop and report.
6. `just migrate name="DropUsersLegacyApiKey"`; verify `Up()` is exactly the two operations in §3; add the §4 marker and `[ContractMigration("DB-07")]`; anything else → stop.
7. `docs/roadmap/execution/00-API-INVENTORY.md` §1 — change the "API key storage today" bullet to point at `api_keys` (one line). 8. `just fmt`, `just test`. 9. Rehearsal with the pre-check.

## 6. Tests

No new test file. Existing coverage stays: `Tests/ApiKeyAuthTests.cs`, `Tests/ApiKeyServiceTests.cs`, `Tests/ApiKeyProtectorTests.cs` must still pass unchanged (they exercise `api_keys`). Add one line to the PR: output of `grep -rn "\.ApiKey\b" Tests/ Domain/ Application/ Infrastructure/ API/ | grep -v "request\.\|Request\|ApiKeyService\|ApiKeyProtector\|ApiKeys\b\|Prefix\|Hash"` → expected empty.

## 7. Acceptance criteria

1. `grep -rn "api_key\b" Infrastructure/Mappings Domain` → no output; `grep -c "api_key" Infrastructure/Migrations/AppDbContextModelSnapshot.cs` → 0.
2. `API/Program.cs` contains no `ApiKeyBackfill`; the file `API/Startup/ApiKeyBackfillService.cs` is gone.
3. Migration `Up()` = `DropIndex` + `DropColumn` only, with marker.
4. `just test` green; the three `ApiKey*Tests` files unchanged.
5. Rehearsal: pre-check 0; after update `\d users` shows no `api_key`; `POST /api/auth/login-with-key` with a real key against the rehearsal API returns 200 (proves login never needed the column).

## 8. Rollback

`Down()` restores an **empty** column and index. **Plaintext values cannot be restored** (they were
already nulled by the backfill on the first boot after R1-06; nothing depends on them). The dump
`pre-db07` is the only copy of the column's (NULL) contents. This is acceptable by design.

## 9. Release steps

1. Prod pre-check **before** the deploy: `docker compose --env-file .env.prod -f docker-compose.prod.yml exec -T db psql -U pointer -d pointer -tAc "SELECT count(*) FROM users WHERE api_key IS NOT NULL;"` → `0` (else abort: some user row was written with a plaintext key by a path this review did not find — report).
2. Merge (approval line in the PR) → `POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-db07 bash scripts/deploy-api.sh` alone, or the batch's single `pre-db03-08` run (R7.1). The script stops the API, dumps, rebuilds; the pre-flight lists `…_DropUsersLegacyApiKey`.
3. Log grep → `Applying migration '…_DropUsersLegacyApiKey'`; `\d users` shows no `api_key`.
4. Smoke: `POST /api/auth/login-with-key` with the owner's key returns a token; profile page shows the key.

## 10. Out of scope

`api_keys` table and `ApiKeyService`, scoped keys (§25), `Cli__MinVersion`, the CLI's credential
store (`ON-DISK-CONTRACT.md` rows are untouched — nothing customer-visible changes), `clients/`,
dashboard.
