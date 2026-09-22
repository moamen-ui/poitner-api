# DB-10 — CI job: apply every migration to an empty PostgreSQL 15, snapshot drift check, Down/Up round-trip, guard tests

Review finding: P0-5 (open question **Q7 — answered yes** by the caller 2026-09-22; cross-review
GLM D1). Rules: R5, R11, R13 ([`DB-RULES.md`](../DB-RULES.md)). **Class: CI only — one new
workflow file. No code, no migration, no production change.** Recommended slot: immediately after
DB-05 (before DB-09/03/06, which all rely on hand-verified scaffolding).

## 1. Goal

Today the first time a new migration meets PostgreSQL is production boot (or a developer's local
`just up`); CI runs only InMemory/Sqlite tests (`.github/workflows/e2e.yml:28-29`). After this
doc, every pull request that touches the data layer fails in CI when: a migration does not apply
cleanly to an empty `postgres:15`; a mapping was changed without scaffolding a migration (snapshot
drift); the newest migration's `Down()` does not undo its `Up()`; or `MigrationSafetyTests`
(DB-02/DB-09) fails. User-visible reason: `20260915163653_FixPayloadFlagsJsonDefault` repaired 104
production rows broken by a raw-SQL default that this job would have caught.

## 2. Prerequisites (verified facts, 2026-09-22 @ 7075b39)

- Existing workflows: `.github/workflows/e2e.yml` (jobs `unit`, `e2e`, `fresh-app`; `unit` = `dotnet build` + `dotnet test --configuration Release`, `:23-29`) and `.github/workflows/publish-clients.yml`. No job starts PostgreSQL.
- Solution file `Pointer.sln` at the repo root (`Tests/RepoRoot.cs` walks up to it); `dotnet build` at the root builds API, Infrastructure, Domain, Application, Tests.
- EF tooling: `Microsoft.EntityFrameworkCore.Design` **8.0.11** referenced by `API/Pointer.API.csproj:22` and `Infrastructure/Pointer.Infrastructure.csproj:10`; Npgsql provider 8.0.11 (`Infrastructure/…csproj:14`). There is **no** `.config/dotnet-tools.json` manifest — the `dotnet-ef` CLI must be installed in the job. The repo's own commands are `dotnet ef migrations add … -p Infrastructure -s API` and `dotnet ef database update -p Infrastructure -s API` (`justfile:6-7`).
- There is **no** `IDesignTimeDbContextFactory` (`grep -rn IDesignTimeDbContextFactory --include='*.cs' .` → nothing). `dotnet ef` therefore builds the API host: it runs `Program.cs` up to `builder.Build()` and reads the configuration the services need. Two values are mandatory at that point:
  - `ConnectionStrings__Default` — `Infrastructure/DependencyInjection.cs:23-29` builds the Npgsql options from it (empty string is tolerated for model building but `database update` needs a real target);
  - `JWT__SigningKey` — `API/Extensions/AuthenticationExtensions.cs:23-28` **throws** at service registration if it is missing or shorter than 32 bytes. Any 32+ character string works in CI; it is not a secret.
  `DBMigrationEnabled` must be `false` (or absent) so nothing but `dotnet ef` touches the database; `ADMIN__EMAIL`/`ADMIN__PASSWORD` are only read by the seeder at runtime, not at design time.
- Migration files: `Infrastructure/Migrations/<yyyyMMddHHmmss>_<Name>.cs` + sibling `.Designer.cs` + `AppDbContextModelSnapshot.cs`; **59** migration files today (58 baseline + `20260922080137_DropShadowProjectAppUrlProjectId1`). Every historical migration has a `Down()`; `20260911170828_MigrateDefaultProjectAppUrlsToLocal.Down` is empty by design (R5) and its `Up()` is a guarded, idempotent `UPDATE` — so a Down→Up round-trip of any existing migration is a no-op or a clean re-apply.
- `dotnet ef migrations has-pending-model-changes` exists in EF Core 8 tooling: exit code 1 when the model differs from the snapshot; needs no database connection. `dotnet ef database update <MigrationName>` migrates **down** to that migration. `--no-build` and `--configuration` are accepted by every `dotnet ef` command.
- GitHub `ubuntu-latest` runners ship `psql` (postgresql-client) and have `~/.dotnet/tools` on `PATH` after `actions/setup-dotnet`.
- DB-02 test class: `Tests/MigrationSafetyTests.cs` (filter `FullyQualifiedName~MigrationSafetyTests`); DB-09 adds `Tests/MigrationGateTests.cs` (add its filter to the last step **when DB-09 merges**, not before).

## 3. Design

One new workflow, one job, a `postgres:15` service (same image tag as production,
`docker-compose.prod.yml:6`), five checks in order:

1. `dotnet build -c Release` (whole solution).
2. **Snapshot drift**: `dotnet ef migrations has-pending-model-changes` — fails when a mapping edit has no migration (R13; the class of bug that produced the shadow `ProjectId1`, DB-04).
3. **Apply from empty**: `dotnet ef database update` against the service container → every migration, in order, on real PostgreSQL 15 (R11 without production data — the rehearsal on a prod copy stays mandatory; this catches syntax/DDL errors earlier and on every PR).
4. **History count** equals the number of non-Designer migration files.
5. **Down/Up round-trip of the newest migration**: `database update <second-newest id>` then `database update` — proves the newest `Down()` is real (M-4, R5). Passes for an intentionally empty `Down()` too, because R3 requires the corresponding `Up()` to be idempotent; a DDL migration with a missing or wrong `Down()` fails here.
6. `dotnet test --filter MigrationSafetyTests` (DB-02; DB-09's `MigrationGateTests` appended later).

Triggers: pull requests touching the data layer or the workflow itself; pushes to `main`;
manual dispatch. Not scheduled (nothing time-dependent).

**`.github/workflows/db-migrations.yml` — verbatim:**

```yaml
name: DB migrations

on:
  pull_request:
    paths:
      - 'Infrastructure/**'
      - 'Domain/**'
      - 'API/Program.cs'
      - 'API/Startup/**'
      - 'Tests/Migration*.cs'
      - '.github/workflows/db-migrations.yml'
  push:
    branches: [main]
  workflow_dispatch:

jobs:
  migrate-from-empty:
    runs-on: ubuntu-latest
    timeout-minutes: 15
    services:
      postgres:
        image: postgres:15            # same major as production (docker-compose.prod.yml:6)
        env:
          POSTGRES_USER: pointer
          POSTGRES_PASSWORD: pointer
          POSTGRES_DB: pointer
        ports: ['5432:5432']
        options: >-
          --health-cmd "pg_isready -U pointer"
          --health-interval 5s --health-timeout 5s --health-retries 12
    env:
      # dotnet ef builds the API host (no IDesignTimeDbContextFactory): these two are required.
      ConnectionStrings__Default: "Host=localhost;Port=5432;Database=pointer;Username=pointer;Password=pointer"
      JWT__SigningKey: "ci-only-signing-key-not-a-secret-0123456789abcdef"   # >= 32 bytes or AddJwtAuth throws
      DBMigrationEnabled: "false"
      EF_ARGS: "-p Infrastructure -s API --no-build --configuration Release"
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'
      - run: dotnet tool install --global dotnet-ef --version 8.0.11
      - run: dotnet build -c Release
      - name: Snapshot matches the model (no forgotten migration — DB-RULES R13)
        run: dotnet ef migrations has-pending-model-changes $EF_ARGS
      - name: Apply every migration to an empty PostgreSQL 15
        run: dotnet ef database update $EF_ARGS
      - name: History row count equals migration file count
        run: |
          files=$(ls Infrastructure/Migrations/[0-9]*_*.cs | grep -vc '\.Designer\.cs$')
          rows=$(PGPASSWORD=pointer psql -h localhost -U pointer -d pointer -tAc 'SELECT count(*) FROM "__EFMigrationsHistory"')
          echo "migration files=$files history rows=$rows"
          [ "$files" = "$rows" ]
      - name: Newest migration Down/Up round-trip (DB-RULES R5)
        run: |
          prev=$(ls Infrastructure/Migrations/[0-9]*_*.cs | grep -v '\.Designer\.cs$' | xargs -n1 basename | sed 's/\.cs$//' | sort | tail -2 | head -1)
          echo "down to $prev"; dotnet ef database update "$prev" $EF_ARGS
          echo "up again";      dotnet ef database update $EF_ARGS
      - name: Migration guard tests (DB-02)
        run: dotnet test --no-build --configuration Release --filter "FullyQualifiedName~MigrationSafetyTests"
```

(41 lines of YAML excluding comments; the "~20 lines" estimate in Q7 covered steps 1-3 only —
steps 4-6 are the cheap additions the cross-review asked for.)

What happens to existing rows: nothing — CI-only, disposable database.

## 4. Safety classification

CI only. No migration, no marker, no owner approval. Rules exercised: R5 (round-trip), R11
(complements, does not replace, the prod-copy rehearsal), R13 (drift check).

## 5. File-level tasks

1. **`.github/workflows/db-migrations.yml`** (new) — paste §3 verbatim. Do not add it to `e2e.yml`; keep it a separate workflow so its status check has its own name.
2. Open a PR. In the PR description paste the job URL and the `migration files=59 history rows=59` line (or the current count).
3. **After DB-09 merges** (separate one-line PR, or inside DB-09's PR): change the last step's filter to `"FullyQualifiedName~MigrationSafetyTests|FullyQualifiedName~MigrationGateTests"`. The `paths` list already matches `Tests/Migration*.cs`, so nothing else changes.
4. Optional, same PR: in GitHub → Settings → Branches → `main` protection, add `DB migrations / migrate-from-empty` as a required status check (owner does this in the UI; not a file change).

## 6. Tests

This doc *is* a test. Sanity checks for the job (run on a branch, then revert — never merge them):

1. **Drift**: add `b.Property(x => x.Name).HasMaxLength(65);` to `Infrastructure/Mappings/PlanMapping.cs` without scaffolding → step "Snapshot matches the model" fails.
2. **Broken Down**: in a throwaway migration created with `just migrate name="Probe"`, put `migrationBuilder.AddColumn<int>("probe", "plans", nullable: true);` in `Up()` and leave `Down()` empty → "round-trip" step fails (second `update` tries to re-add an existing column). Remove the probe files and restore the snapshot afterwards (R10 — never commit the probe).
3. **Green**: revert both → all steps pass.

## 7. Acceptance criteria

1. The workflow appears under Actions as `DB migrations`; the job `migrate-from-empty` is green on `main`.
2. Job log shows `migration files=N history rows=N` with equal numbers (N = 59 today, or current).
3. Job log shows `down to 2026…_<second newest>` followed by `up again` and a successful second `database update`.
4. `dotnet test … --filter MigrationSafetyTests` step reports 2 passed (3 after DB-09).
5. Sanity check 1 (§6) fails the job on a branch; the revert passes.
6. `git diff --stat` for the PR touches exactly one file.

## 8. Rollback

Delete the workflow file. Nothing else changes; production is unaffected.

## 9. Release steps

Merge. No deploy. The next PR touching `Infrastructure/**` runs it. Watch the first three runs for
flakiness in the Postgres health-check wait (raise `--health-retries` to 20 if `database update`
ever fails with "connection refused").

## 10. Out of scope

`e2e.yml` and `publish-clients.yml`; any C# file; `Tests/`; `Program.cs` (a design-time factory is
**not** added — the env-var approach works and keeps one host-building path); running the job
against a production dump (R11 stays local — no prod data in GitHub); the dashboard repo;
`clients/`; branch-protection settings (owner, UI).
