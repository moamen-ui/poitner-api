# DB-07 — Drop the legacy plaintext `users.api_key` column

Review finding: S-10. Rules: R2 (contract), R5, R7, R10, R11, R13. **Class: Destructive** (drops a
column). One migration + removal of the one-shot backfill code. Independent of every other doc;
schedule any time after DB-01.

**Owner approval (required before task 1):** `Approved to drop users.api_key on ____-__-__ by ________` — fill in and paste into the PR description.

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

**Destructive** (R2 contract, R5). Marker: `// DB-RULES: R2 contract approved <date> by <owner>`.
Dump `pre-db07` immediately before; R7 explicit step. Owner approval line above must be filled.

## 5. File-level tasks

1. Delete `API/Startup/ApiKeyBackfillService.cs`; remove `API/Program.cs:171-173` (the comment and the `await ApiKeyBackfill.RunAsync(app.Services);` line) — `MigrateAsync` and `SeedAsync` stay.
2. `Domain/Entity/User.cs:33-40` — delete the property and its doc-comment.
3. `Infrastructure/Mappings/UserMapping.cs:37-38` — delete both lines.
4. `Domain/Entity/ApiKey.cs:10-12` — replace the sentence "Replaces the plaintext `User.ApiKey` column, which is kept for one release … dropped in R2." with "Replaces the plaintext `User.ApiKey` column (dropped by DB-07)." `Application/Services/Interfaces/IAuthService.cs:10` — replace "(User.ApiKey)" with "(an `api_keys` row)".
5. `dotnet build` — fix any compile error **only** by deleting code that referenced `User.ApiKey` (backfill tests); if a non-test, non-backfill file references it, stop and report.
6. `just migrate name="DropUsersLegacyApiKey"`; verify `Up()` is exactly the two operations in §3; add marker; anything else → stop.
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

Owner approval line filled → merge → on the VM `git pull` → `stop api` → `backup-db.sh pre-db07` →
prod pre-check `SELECT count(*) FROM users WHERE api_key IS NOT NULL;` = 0 (else abort: some user
row was written with a plaintext key by a path this review did not find — report) → `up -d --build
api` → log grep → smoke: `POST /api/auth/login-with-key` with the owner's key returns a token;
profile page shows the key.

## 10. Out of scope

`api_keys` table and `ApiKeyService`, scoped keys (§25), `Cli__MinVersion`, the CLI's credential
store (`ON-DISK-CONTRACT.md` rows are untouched — nothing customer-visible changes), `clients/`,
dashboard.
