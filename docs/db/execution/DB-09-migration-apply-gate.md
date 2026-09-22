# DB-09 — Mechanical R7 gate: `[ContractMigration]`, `Program.cs` refusal, explicit `deploy-api.sh` path

Review finding: P0-4 residual (cross-review GLM B2 / AGY 1.4). Rules: R2, R3, R5, R7, R10, R13
([`DB-RULES.md`](../DB-RULES.md)). **Class: Additive — code, one test file, one compose env line,
one script edit, one attribute added to an already-applied migration file. No migration.**
Must ship **before DB-07** (first Destructive migration) and before DB-03/DB-06 (they carry
markers and rely on the enforced path). Recommended slot: right after DB-10.
**Status: merged** `ff25a8d` (`ContractMigrationAttribute`, `API/Startup/MigrationGate.cs`,
`Program.cs` gate with `return 3`, `deploy-api.sh` pre-flight + `POINTER_APPLY_CONTRACT=1` path,
retroactive attributes on the DB-04/DB-05 migrations, `Tests/MigrationGateTests.cs`; 711 tests).
**Production deploy pending** — it is code only; ship it with an **ordinary** `bash
scripts/deploy-api.sh` *before* the DB-03..08 batch so the VM runs the new script (R7.1 point 7).
The batch run is then the production proof of the gate (§9 step 4).

## 1. Goal

Today a migration that carries a DB-RULES approval marker still auto-applies on the next ordinary
`bash scripts/deploy-api.sh` — the marker is checked by a unit test (DB-02), not by the process
that runs `MigrateAsync()` (`API/Program.cs:169`). After this doc: such a migration is also marked
with a runtime-visible attribute, `Program.cs` **refuses to migrate** while one is pending unless
the configuration flag `DBApplyContractMigrations=true` is present, and the only thing that sets
that flag is the explicit path `POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-<slug> bash
scripts/deploy-api.sh`, which stops the API first and takes a labelled dump — i.e. R7's written
procedure becomes the only way a contract migration can reach production. User-visible reason:
none directly; it removes the one path by which a column drop could still run unattended.

## 2. Prerequisites (verified facts, 2026-09-22 @ 7075b39)

- **Boot chain** `API/Program.cs:165-174`:
  ```csharp
  if (builder.Configuration.GetValue<bool>("DBMigrationEnabled"))
  {
      using var scope = app.Services.CreateScope();
      var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
      await db.Database.MigrateAsync();
      await AdminSeeder.SeedAsync(app.Services);
      // Moves any pre-hardening plaintext User.ApiKey into the hashed+encrypted api_keys table.
      // Idempotent and inline (not a hosted service) so it is guaranteed to follow the migration.
      await ApiKeyBackfill.RunAsync(app.Services);
  }
  ```
  `Program.cs` is top-level statements (`var builder = WebApplication.CreateBuilder(args);` at `:20`, `app.Run();` at `:417`); every existing `return` (`:73,160,311,328,332,379`) is inside a lambda, so a top-level `return 3;` is legal and makes the entry point return `int`. `using Pointer.API.Startup;` already exists (`:18`); `using Microsoft.EntityFrameworkCore;` at `:7`.
- **Startup-helper precedent**: `API/Startup/ApiKeyBackfillService.cs` — `public static class ApiKeyBackfill` in namespace `Pointer.API.Startup` with `RunAsync(IServiceProvider, CancellationToken)`.
- **Production config**: `docker-compose.prod.yml:26` `DBMigrationEnabled: "true"`; env block `:22-49`; `restart: unless-stopped` (`:20`) — a container that exits is restarted in a loop. `.env.prod` is docker `KEY=VALUE` (`.env.prod.example`). Compose interpolation: a variable exported in the calling shell **overrides** the same key in `--env-file`.
- **Local dev config**: `.env.example:7` `DBMigrationEnabled=true`; `docker-compose.yaml:12` `env_file: .env`. The e2e suite does **not** boot the API in CI (`.github/workflows/e2e.yml`, `e2e/run-e2e.sh:208` starts only Verdaccio; Playwright `baseURL` is a fixture server, `e2e/playwright.config.ts:14`) — the gate cannot break CI e2e.
- **Deploy script** `scripts/deploy-api.sh` (43 lines): `set -euo pipefail` (`:14`), `REPO` + `cd` (`:16-17`), pull (`:19-21`), `backup-db.sh pre-deploy` (`:23-24`), `up -d --build api` (`:26-27`), verify loop + smoke (`:29-43`). DB-01 inserts a freshness block **between line 17 and line 19**; this doc replaces **line 19 to the end**. The two do not overlap.
- **DB-02 test** `Tests/MigrationSafetyTests.cs`: `Baseline` 58 ids (`:13-73`); `RiskyOperation` (`:75-78`); `ApprovalMarker` regex (`:80-83`) `//\s*DB-RULES:\s*(R2 contract|R3 backfill|index change|R4 constraint)\s+approved\s+\d{4}-\d{2}-\d{2}\s+by\s+\S+`; `MigrationFiles()` (`:90-96`) excludes `.Designer.cs` and the snapshot; fact B (`:112-143`).
- **The one post-baseline migration today**: `Infrastructure/Migrations/20260922080137_DropShadowProjectAppUrlProjectId1.cs` — class line `:8` `public partial class DropShadowProjectAppUrlProjectId1 : Migration`, marker `:11`. Its `.Designer.cs` holds `[DbContext(typeof(AppDbContext))]` and `[Migration("20260922080137_DropShadowProjectAppUrlProjectId1")]` on the same partial class; attributes on partial declarations merge, so an attribute added in the hand-edited file is visible on the type. **Already applied in production** (2026-09-22 08:18 UTC) — adding an attribute to its class changes neither its id nor `__EFMigrationsHistory`. If DB-05's migration (`*_SoftDeleteAwareUniqueIndexes.cs`) has merged by the time this doc is implemented, it gets the same treatment (task 8).
- **EF APIs** (EF Core 8.0.11, `Infrastructure/Pointer.Infrastructure.csproj:14`): `db.Database.GetPendingMigrationsAsync(ct)` → `IEnumerable<string>` of ids; `db.GetService<IMigrationsAssembly>()` (`using Microsoft.EntityFrameworkCore.Infrastructure;` for `GetService`, `using Microsoft.EntityFrameworkCore.Migrations;` for the interface) → `.Migrations` is `IReadOnlyDictionary<string, TypeInfo>` keyed by migration id, built by reflection over the migrations assembly — **no database connection needed** for that dictionary.
- **`AppDbContext` constructor**: `(DbContextOptions<AppDbContext> options, ICurrentUser currentUser, IConfiguration configuration)` (`Infrastructure/AppDbContext.cs:9`). Test fixture shapes: `FakeCurrentUser` (`Tests/CommentFieldsTests.cs:28-36`), `BuildContext` (`:61-62`), `RepoRoot.Find()` (`Tests/RepoRoot.cs`). `Tests/Pointer.Tests.csproj` references Infrastructure, so `Npgsql.EntityFrameworkCore.PostgreSQL` is available transitively (`UseNpgsql` with a dead connection string is fine for building the model — nothing connects until a query runs).
- Commands: `just test` = `dotnet test`; `just fmt` = `dotnet csharpier .` (`justfile:5,8`).

## 3. Design

### 3.1 The signal: `ContractMigrationAttribute`

`Infrastructure/Migrations/ContractMigrationAttribute.cs`:

```csharp
namespace Pointer.Infrastructure.Migrations;

/// <summary>
/// Marks a migration that must never auto-apply on an ordinary API boot (DB-RULES R7). Every
/// migration that carries a <c>// DB-RULES: … approved …</c> marker (R2 contract, R3 backfill,
/// index change, R4 constraint — i.e. anything the DB-02 guard flags) also carries this attribute;
/// <c>Tests/MigrationSafetyTests</c> requires the two to agree. <c>API/Startup/MigrationGate</c>
/// refuses <c>MigrateAsync()</c> while such a migration is pending unless
/// <c>DBApplyContractMigrations=true</c>, which only <c>scripts/deploy-api.sh</c> sets, and only on
/// its <c>POINTER_APPLY_CONTRACT=1</c> path (API stopped, labelled dump taken).
/// </summary>
/// <param name="doc">The execution doc that approved it, e.g. <c>"DB-07"</c>.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class ContractMigrationAttribute(string doc) : Attribute
{
    public string Doc { get; } = doc;
}
```

Usage, in the hand-edited migration file only, directly above the class line:

```csharp
    /// <inheritdoc />
    [ContractMigration("DB-07")]
    public partial class DropUsersLegacyApiKey : Migration
    {
        /// <inheritdoc />
        // DB-RULES: R2 contract approved 2026-10-01 by <owner>
        protected override void Up(MigrationBuilder migrationBuilder)
```

"Contract" here is shorthand for "requires the R7 explicit step"; the attribute goes on **every**
marked migration regardless of which of the four marker kinds it carries. A migration without a
marker never carries the attribute.

### 3.2 The gate: `MigrationGate`

`API/Startup/MigrationGate.cs`:

```csharp
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Migrations;

namespace Pointer.API.Startup;

/// <summary>
/// DB-09 (DB-RULES R7): migrations marked [ContractMigration] never auto-apply on an ordinary boot.
/// Runs before MigrateAsync. Pure part (FindContractMigrations) is unit-tested; the async part is
/// exercised by the R11 rehearsal and by scripts/deploy-api.sh.
/// </summary>
public static class MigrationGate
{
    public const string ConfigKey = "DBApplyContractMigrations";

    /// <summary>Of the pending ids, those whose migration class carries [ContractMigration].</summary>
    public static IReadOnlyList<string> FindContractMigrations(
        IEnumerable<string> pendingIds,
        IReadOnlyDictionary<string, TypeInfo> migrations)
    {
        return pendingIds
            .Where(id =>
                migrations.TryGetValue(id, out var type)
                && type.GetCustomAttribute<ContractMigrationAttribute>() is not null)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>True = boot may continue into MigrateAsync. False = refuse; caller exits non-zero.</summary>
    public static async Task<bool> AllowMigrateAsync(
        AppDbContext db, IConfiguration config, ILogger logger, CancellationToken ct = default)
    {
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count == 0)
            return true;

        var marked = FindContractMigrations(pending, db.GetService<IMigrationsAssembly>().Migrations);
        if (marked.Count == 0)
            return true;

        var ids = string.Join(", ", marked);
        if (config.GetValue<bool>(ConfigKey))
        {
            logger.LogWarning(
                "DB-09: applying {Count} contract migration(s) because {Key}=true: {Ids}",
                marked.Count, ConfigKey, ids);
            return true;
        }

        logger.LogCritical(
            "DB-09 REFUSED: {Count} pending migration(s) carry [ContractMigration] and {Key} is not true: {Ids}. "
                + "Not migrating; exiting. Ship them with: POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-<slug> "
                + "bash scripts/deploy-api.sh (docs/db/DB-RULES.md R7, docs/db/execution/DB-09-migration-apply-gate.md).",
            marked.Count, ConfigKey, ids);
        return false;
    }
}
```

The log line **must** contain the literal `DB-09 REFUSED` — `deploy-api.sh` greps for it.

### 3.3 `Program.cs`

`API/Program.cs:165-174` becomes:

```csharp
if (builder.Configuration.GetValue<bool>("DBMigrationEnabled"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    // DB-09 / DB-RULES R7: a pending migration marked [ContractMigration] is applied only through the
    // explicit deploy path (API stopped, labelled dump, DBApplyContractMigrations=true). Otherwise
    // refuse and exit non-zero so scripts/deploy-api.sh fails loudly instead of migrating unattended.
    if (!await MigrationGate.AllowMigrateAsync(db, builder.Configuration, app.Logger))
        return 3;
    await db.Database.MigrateAsync();
    await AdminSeeder.SeedAsync(app.Services);
    // Moves any pre-hardening plaintext User.ApiKey into the hashed+encrypted api_keys table.
    // Idempotent and inline (not a hosted service) so it is guaranteed to follow the migration.
    await ApiKeyBackfill.RunAsync(app.Services);
}
```

Behaviour matrix:

| Pending migrations | `DBApplyContractMigrations` | Result |
|---|---|---|
| none | any | migrate (no-op), seed, boot — unchanged |
| only unmarked (R1 additive) | any | migrate, seed, boot — unchanged |
| ≥1 marked | unset / `false` | log Critical `DB-09 REFUSED …`, **exit code 3**, nothing migrated |
| ≥1 marked | `true` | log Warning `DB-09: applying …`, migrate, seed, boot |
| `DBMigrationEnabled=false` | any | gate never runs — unchanged |

### 3.4 Configuration

- `docker-compose.prod.yml`, `api.environment`, new line directly after `:26`
  `DBMigrationEnabled: "true"`:
  `DBApplyContractMigrations: "${DB_APPLY_CONTRACT:-false}"` with a one-line comment above it:
  `# DB-09: set to true only by scripts/deploy-api.sh on its POINTER_APPLY_CONTRACT=1 path. Never put DB_APPLY_CONTRACT in .env.prod.`
- `.env.example` (local only), after `:7` `DBMigrationEnabled=true`:
  `DBApplyContractMigrations=true` with comment `# local dev only: the dev database is disposable, so contract migrations may auto-apply here (DB-09)`.
- `.env.prod.example`: append a **comment only**:
  `# DB_APPLY_CONTRACT is deliberately absent: scripts/deploy-api.sh exports it per run (DB-09). Do not add it here.`

### 3.5 `scripts/deploy-api.sh`

Two layers. (a) A **pre-flight** after `git pull` that compares the migration files in the checkout
with `__EFMigrationsHistory` in the running database and refuses — *before rebuilding anything* —
if a not-yet-applied file contains `[ContractMigration`. (b) The **contract path**
(`POINTER_APPLY_CONTRACT=1`): stop api → `backup-db.sh $POINTER_CONTRACT_LABEL` → `up -d --build`
with `DB_APPLY_CONTRACT=true` exported. (c) The verify loop also recognises `DB-09 REFUSED` in the
logs (covers a manual `compose up` that bypassed the pre-flight). Lines 1-17 of the script are
unchanged (DB-01 adds its block after line 17). **Replace line 19 to the end of the file with
exactly:**

```bash
# DB-09 (DB-RULES R7): a migration whose class carries [ContractMigration] — every migration with a
# DB-RULES approval marker — never auto-applies on an ordinary deploy. It ships through this script as
#   POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-<slug> bash scripts/deploy-api.sh
# which stops the API first, dumps under that label, and boots with DBApplyContractMigrations=true.
APPLY_CONTRACT="${POINTER_APPLY_CONTRACT:-0}"
export DB_APPLY_CONTRACT=false
COMPOSE=(docker compose --env-file .env.prod -f docker-compose.prod.yml)

echo "== 1/5 pull =="
git pull --ff-only
git log --oneline -1

echo "== 2/5 contract-migration pre-flight =="
applied="$("${COMPOSE[@]}" exec -T db psql -U pointer -d pointer -tAc 'SELECT "MigrationId" FROM "__EFMigrationsHistory"' 2>/dev/null || true)"
pending_contract=""
for f in "$REPO"/Infrastructure/Migrations/[0-9]*_*.cs; do
  case "$f" in *.Designer.cs) continue ;; esac
  id="$(basename "$f" .cs)"
  grep -qx "$id" <<<"$applied" && continue
  grep -q '\[ContractMigration' "$f" && pending_contract+="$id"$'\n'
done
if [ -n "$pending_contract" ]; then
  if [ "$APPLY_CONTRACT" != "1" ]; then
    echo "deploy REFUSED (DB-09): pending migration(s) carry [ContractMigration]:" >&2
    printf '  %s\n' $pending_contract >&2
    echo "Nothing was rebuilt; the running API is unchanged (the checkout is now ahead of it)." >&2
    echo "Read the execution doc named in the attribute, run its pre-checks on prod, then:" >&2
    echo "  POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-<slug> bash scripts/deploy-api.sh" >&2
    exit 2
  fi
  : "${POINTER_CONTRACT_LABEL:?POINTER_APPLY_CONTRACT=1 requires POINTER_CONTRACT_LABEL=pre-<slug> (the dump label from the execution doc)}"
  echo "will apply contract migration(s):"; printf '  %s\n' $pending_contract
elif [ "$APPLY_CONTRACT" = "1" ]; then
  echo "note: POINTER_APPLY_CONTRACT=1 but no pending contract migration — proceeding as an ordinary deploy"
  APPLY_CONTRACT=0
fi

echo "== 3/5 backup =="
if [ "$APPLY_CONTRACT" = "1" ]; then
  "${COMPOSE[@]}" stop api                       # R7: no request may hit a half-migrated schema
  bash "$REPO/scripts/backup-db.sh" "$POINTER_CONTRACT_LABEL"
  export DB_APPLY_CONTRACT=true
else
  bash "$REPO/scripts/backup-db.sh" pre-deploy
fi

echo "== 4/5 rebuild api =="
"${COMPOSE[@]}" up -d --build api

echo "== 5/5 verify =="
for i in $(seq 1 30); do
  logs="$("${COMPOSE[@]}" logs --since 3m api 2>/dev/null || true)"
  grep -q "Now listening" <<<"$logs" && break
  if grep -q "DB-09 REFUSED" <<<"$logs"; then
    echo "deploy FAILED: the API refused to auto-apply a contract migration and is restarting in a loop." >&2
    grep "DB-09" <<<"$logs" | tail -3 >&2
    echo "Either re-run with POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-<slug>, or roll back:" >&2
    echo "  git checkout <previous commit> && ${COMPOSE[*]} up -d --build api" >&2
    exit 3
  fi
  sleep 2
done
"${COMPOSE[@]}" ps api
"${COMPOSE[@]}" logs --since 3m api | grep -iE "Now listening|migrat|DB-09|error|exception" | tail -20 || true
if [ "$DB_APPLY_CONTRACT" = "true" ]; then
  "${COMPOSE[@]}" exec -T db psql -U pointer -d pointer -c 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY 1 DESC LIMIT 3'
fi
for path in /api/branding /swagger/v1/swagger.json; do
  code=$(curl -s -o /dev/null -w '%{http_code}' "https://api.pointer.moamen.work$path")
  echo "$path $code"
  [ "$code" = "200" ] || { echo "smoke FAILED on $path" >&2; exit 1; }
done
echo "deploy OK"
```

Also update the header comment (`:10-13`) to say: "Migrations auto-apply on boot **except** those
marked `[ContractMigration]` (DB-09) — see the block below for how those ship."

### 3.6 DB-02 test: marker ⇔ attribute

Third fact in `Tests/MigrationSafetyTests.cs` (this is DB-02 §11 task 3, owned here):
for every non-baseline file returned by `MigrationFiles()`,
`ApprovalMarker.IsMatch(content)` must equal `content.Contains("[ContractMigration")`. Failure
message: `"{file}: has approval marker but no [ContractMigration] attribute"` or the converse.

### 3.7 What happens to existing rows

Nothing. No migration, no data change. The only migration file touched (`20260922080137_…`) gains
an attribute on its class; its id and `__EFMigrationsHistory` are untouched.

## 4. Safety classification

**Additive** (code + config). Rules: R7 (this doc is its enforcement), R10 (no migration id
touched — adding an attribute to an already-applied migration's class is allowed; renaming is not),
R13 (n/a — no generated migration). No marker, no owner approval needed. Local `just up` keeps
working because `.env.example` opts in.

## 5. File-level tasks

1. **`Infrastructure/Migrations/ContractMigrationAttribute.cs`** (new) — exactly §3.1.
2. **`API/Startup/MigrationGate.cs`** (new) — exactly §3.2.
3. **`API/Program.cs:165-174`** — replace with §3.3 (adds the comment, the `if (!await … ) return 3;` pair; everything else in the block stays). Do not touch any other line of `Program.cs`.
4. **`docker-compose.prod.yml`** — insert the two lines of §3.4 after `:26`. Indentation: six spaces, like the neighbours.
5. **`.env.example`** — add the comment + `DBApplyContractMigrations=true` after `:7`.
6. **`.env.prod.example`** — append the one comment line of §3.4.
7. **`scripts/deploy-api.sh`** — replace line 19 to end with §3.5 verbatim; update the header comment; `chmod` unchanged. `bash -n scripts/deploy-api.sh` must pass.
8. **`Infrastructure/Migrations/20260922080137_DropShadowProjectAppUrlProjectId1.cs`** — insert `    [ContractMigration("DB-04")]` on its own line between `:7` (`/// <inheritdoc />`) and `:8` (`public partial class …`). Same for `*_SoftDeleteAwareUniqueIndexes.cs` with `"DB-05"` **if that file exists** on `main` (check with `ls Infrastructure/Migrations/*_SoftDeleteAwareUniqueIndexes.cs`); if DB-05 is still an open PR, tell its author to add the attribute instead and note it in this PR. Never touch a `.Designer.cs`.
9. **`Tests/MigrationSafetyTests.cs`** — add the third fact of §3.6 (`ApprovalMarkerAndContractAttributeAgree`).
10. **`Tests/MigrationGateTests.cs`** (new) — §6.
11. **`DEPLOY.md`** § Updating — add a paragraph "Two deploy paths": ordinary `bash scripts/deploy-api.sh` (additive migrations auto-apply); contract `POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-<slug> bash scripts/deploy-api.sh` (stops the API, labelled dump, applies marked migrations); what `deploy REFUSED (DB-09)` means and that the running API is untouched when it appears. Cross-link `docs/db/DB-RULES.md` R7.
12. `just fmt`; `just test`; rehearsal (§7 criteria 6-8).

## 6. Tests

New `Tests/MigrationGateTests.cs` (copy `FakeCurrentUser` from `Tests/CommentFieldsTests.cs:28-36`;
build the context with `UseNpgsql("Host=localhost;Port=1;Database=gate;Username=x;Password=x")` —
never connected; empty `ConfigurationBuilder().Build()` as in `CommentFieldsTests.cs:61-62`):

1. `FindContractMigrations_ReturnsOnlyMarkedPendingIds_InOrder` — hand-built dictionary with three fake `TypeInfo`s: two private nested classes in the test file, one with `[ContractMigration("T")]`, one without, keyed `"20990101000000_Marked"`, `"20990101000001_Plain"`, plus a pending id not in the dictionary `"20990101000002_Unknown"`. Call with pending = all three in reverse order → result is exactly `["20990101000000_Marked"]`.
2. `FindContractMigrations_EmptyPending_ReturnsEmpty`.
3. `RealAssembly_DropShadowProjectId1_IsMarked` — `db.GetService<IMigrationsAssembly>().Migrations` contains key `"20260922080137_DropShadowProjectAppUrlProjectId1"` whose type has `ContractMigrationAttribute` with `Doc == "DB-04"`; and `FindContractMigrations(new[]{ that id, "20260623133436_InitialCreate" }, migrations)` returns exactly the first id. (Proves the attribute is visible through EF's discovery, i.e. the partial-class merge works.)
4. `RealAssembly_EveryBaselineMigration_IsUnmarked` — for every id in a copy of the DB-02 `Baseline` set (make `MigrationSafetyTests.Baseline` `internal static` and reuse it), the type has no `ContractMigrationAttribute`.

`AllowMigrateAsync` (needs a live Postgres) is covered by the rehearsal (criteria 6-8) and by
DB-10's CI job once both exist; say so in the PR. Tenancy invariant: unaffected (no filter, no
entity change) — reference `TenantQueryFilterTests` in the PR, no new test. "Existing data
survives": no data path is touched; criterion 7 shows the prod-copy boots and migrates normally
with the flag set.

## 7. Acceptance criteria

1. `dotnet test --filter "FullyQualifiedName~MigrationGateTests|FullyQualifiedName~MigrationSafetyTests"` → 7 passed (4 + 3).
2. `grep -c "ContractMigration(" Infrastructure/Migrations/*.cs` → 1 (or 2 if DB-05 merged), on the class line(s) of the post-baseline migration(s) only; `grep -L "ContractMigration" Infrastructure/Migrations/*.Designer.cs | wc -l` equals the number of Designer files (none touched).
3. `grep -n "DB-09 REFUSED" API/Startup/MigrationGate.cs scripts/deploy-api.sh` → one hit in each.
4. `grep -n "DBApplyContractMigrations" docker-compose.prod.yml .env.example API/Startup/MigrationGate.cs` → one hit in each; `grep -c "DB_APPLY_CONTRACT" .env.prod.example` → 1 (the comment).
5. `bash -n scripts/deploy-api.sh` passes; `git diff --stat` touches exactly the 11 files named in §5 tasks 1-11 (12 if the DB-05 migration file also receives the attribute) — `Tests/MigrationSafetyTests.cs` is one of them (third fact + `Baseline` made `internal static`).
6. **Rehearsal — refuse path** (R11 steps 1-2 restore the newest prod dump into `pointer_rehearsal`; then create a probe migration with `just migrate name="GateProbe"`, give it `[ContractMigration("PROBE")]` and a `// DB-RULES: index change approved <today> by <you> (probe)` marker, leave `Up()`/`Down()` empty):
   ```bash
   ConnectionStrings__Default="Host=localhost;Port=5433;Database=pointer_rehearsal;Username=pointer;Password=pointer" \
   DBMigrationEnabled=true JWT__SigningKey=<any 32+ chars> dotnet run --project API; echo "exit=$?"
   ```
   → log contains `DB-09 REFUSED: 1 pending migration(s) … GateProbe`, **`exit=3`**, and `SELECT count(*) FROM "__EFMigrationsHistory"` in `pointer_rehearsal` is unchanged.
7. **Rehearsal — apply path**: same command with `DBApplyContractMigrations=true` added → log contains `DB-09: applying 1 contract migration(s)` then `Now listening`; history count +1. Ctrl-C. Then **delete the probe** (`dotnet ef migrations remove -p Infrastructure -s API` against the rehearsal DB, or delete the two files and revert the snapshot) — it must never be committed (R10: a committed id is frozen).
8. **Rehearsal — unmarked path**: with the probe gone and no pending migrations, plain `dotnet run` boots with neither `DB-09` line.
9. On the VM after merge: ordinary `bash scripts/deploy-api.sh` prints `== 2/5 contract-migration pre-flight ==` with no `will apply`/`REFUSED` line and ends `deploy OK`; `docker compose -f docker-compose.prod.yml exec -T api printenv DBApplyContractMigrations` → `false`.

## 8. Rollback

`git revert` of the commit; redeploy with the ordinary path. No migration, no data. If the gate
misfires in production (refuses when it should not): `POINTER_APPLY_CONTRACT=1
POINTER_CONTRACT_LABEL=pre-gatefix bash scripts/deploy-api.sh` is itself the safe way through
(it dumps first), or `git checkout <previous commit> && docker compose --env-file .env.prod -f
docker-compose.prod.yml up -d --build api`. No dump requirement beyond the ordinary `pre-deploy`
one (nothing destructive).

## 9. Release steps

1. Merge (ordinary PR; the `unit` CI job covers the new tests; DB-10, if already merged, exercises the gate class compiling against the real migrations assembly).
2. On the VM: `bash scripts/deploy-api.sh` (ordinary path — no pending marked migration exists, DB-04 is applied). Watch for `== 2/5 …` and `deploy OK`.
3. Verify criterion 9.
4. Immediately afterwards, the next marked migration (DB-05 if not yet deployed, else DB-03) ships with `POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-db0N bash scripts/deploy-api.sh` — that deploy is the production proof of the gate; paste its `== 2/5` and `== 5/5` output into the corresponding doc's PR.
5. Watch: any `DB-09` line in `docker compose logs api` on an ordinary deploy is a bug report.

## 10. Out of scope

Any migration body or `Up()`/`Down()`; `.Designer.cs`; the snapshot; `MigrateAsync` itself (still
runs for unmarked migrations); `AdminSeeder`/`ApiKeyBackfill` (DB-07 removes the latter);
`backup-db.sh` and the DB-01 freshness block; CI YAML (DB-10); `Caddyfile`; the dashboard repo;
`clients/`; e2e. Do **not** make the gate environment-aware (`IsProduction()`) — the flag is the
only switch, so behaviour is identical everywhere.
