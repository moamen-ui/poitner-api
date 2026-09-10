# R2-03 — Served-file version stamp + `pointer update`  (NEW-2 · Release 2 · ½ d)

## Goal
Every skill/script the server serves carries the server's version; `doctor` tells the developer when
their installed copy is stale; `pointer update` refreshes it. Closes the "frozen `skill.md` in a repo
forever" gap for the `curl|sh` path too.

## Out of scope
- Auto-updating anything silently. Semantic versioning of skill *content*.

## Prerequisites
- R1-04 (`GET /api/meta` returns `{ version, minCliVersion }`) — this doc adds `skillVersion`.
- Facts: placeholder middleware `API/Program.cs:192-223` rewrites `<POINTER_SERVER>` in
  `/pointer-init.md`, `/skill.md`, `/install.sh`; `pointer.sh` is served as a plain static file
  (`API/wwwroot/pointer.sh`, not in `injectedFiles`).

## Design
- **Stamp**: the middleware also replaces a second placeholder `<POINTER_SKILL_VERSION>` with
  `skillVersion`. Each served file gets a first-line comment containing it:
  - `skill.md`, `pointer-init.md`: `<!-- pointer-skill-version: <POINTER_SKILL_VERSION> -->` as line 1.
  - `install.sh`, `pointer.sh`: `# pointer-skill-version: <POINTER_SKILL_VERSION>` as line 2 (after shebang).
  Add `/pointer.sh` to `injectedFiles` (it has no `<POINTER_SERVER>` today, harmless).
- **Value**: `skillVersion` = the API assembly informational version (same as `/api/meta.version`)
  by default; overridable via `Pointer:SkillVersion` in appsettings so skill-only edits can bump it
  without a deploy of code (Decision: string, e.g. `2026.09.11`).
- **`/api/meta`** gains `skillVersion: string` (R1-04's DTO `MetaResponse`).
- **`doctor`** (R1-04) adds a check: read line-1/2 stamp of each installed skill copy
  (`.claude/skills/pointer-*/SKILL.md`, `.pointer/pointer.sh`) → compare with `/api/meta.skillVersion`
  → `stale` warning listing files.
- **`pointer update`** (`cli/src/commands/update.ts`): re-downloads `skill.md`, `pointer-init.md`,
  `pointer.sh` from the configured server into the same paths `install.sh:18-43` uses (respecting the
  skills dir recorded in `.pointer/config.json.aiTool`), preserves the `.agents/` symlinks, prints
  `updated N files (skill version X → Y)`. `--check` only reports.
- **`install.sh`**: after downloading, print `installed skill version <POINTER_SKILL_VERSION>`; when
  re-run, compare the existing stamp and print `updated from <old>` (pure `sed -n 1p`/`2p` parsing).

## Tasks
1. `API/Program.cs:192-223` — add `/pointer.sh` to `injectedFiles`; replace `<POINTER_SKILL_VERSION>`; resolve value from `IConfiguration["Pointer:SkillVersion"]` ?? assembly informational version.
2. `API/wwwroot/skill.md`, `pointer-init.md`, `install.sh`, `pointer.sh` — add the stamp lines.
3. `Application/DTOs/Meta/MetaResponse.cs` (R1-04) — add `SkillVersion`; `API/Controllers/MetaController.cs` fills it from the same resolver (extract `SkillVersionResolver` static helper in `API/Extensions/`).
4. `cli/src/lib/skill-stamp.ts` — `readStamp(path): string | null` (parse first two lines).
5. `cli/src/commands/doctor.ts` — `skills: { file, installed, served, stale }[]` check.
6. `cli/src/commands/update.ts` — download/replace/symlink-preserve; `--check`.
7. `install.sh` — print installed/updated version lines.
8. `cli/README.md`, `AGENTS.md` — document `pointer update`.

## Dashboard tasks
Regenerate for `MetaResponse.skillVersion` (additive; no UI).

## Tests
- `Tests/ServedSkillVersionTests.cs`: GET `/skill.md`, `/install.sh`, `/pointer.sh` contain the stamp and no leftover `<POINTER_SKILL_VERSION>`; appsettings override wins; `/api/meta.skillVersion` equals the stamp.
- `cli/test/skill-stamp.test.ts`: parses md and sh forms; missing → null.
- `cli/test/update.test.ts`: against a stub server, replaces files, keeps `.agents/*` symlinks, reports versions; `--check` writes nothing.
- E2E scenario `doctor: flags stale skill copy` — install with server stamp A, set `Pointer:SkillVersion=B`, `doctor` warns, `update` fixes, `doctor` green.

## Acceptance criteria
- [ ] `curl -s <server>/skill.md | head -1` prints `<!-- pointer-skill-version: … -->`; same for the other three files.
- [ ] `GET /api/meta` includes `skillVersion` identical to the stamp.
- [ ] `pointer doctor` in a repo with an older stamp prints `stale` with the file list; `pointer update` replaces them and preserves `.agents/` symlinks; `doctor` is then green.
- [ ] `curl -fsSL <server>/install.sh | sh` twice prints `updated from <old>` on the second run.
- [ ] `just test` and `cli` tests green.

## Rollout / compatibility
Existing installed copies have no stamp → `doctor` reports `unknown (pre-versioning) — run pointer update`.

## Report template
Files; the four `head` outputs; `doctor` before/after output; test results.
