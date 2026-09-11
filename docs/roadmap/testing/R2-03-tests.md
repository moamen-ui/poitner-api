# R2-03-tests — Served-file version stamp + `pointer update`

Harness: [`00-HARNESS.md`](00-HARNESS.md). Execution doc: [`../execution/R2-03-served-version-stamp.md`](../execution/R2-03-served-version-stamp.md).

## Covers

AC-1 (line 1 `---`, stamp first line after frontmatter for both `.md`; `sed -n 2p` for both `.sh`) → R2-03-01 · AC-2 (`doctor` with `aiTool: cursor` checks `.cursor/rules/pointer-*.md`) → R2-03-03 steps 1–2, 5 · AC-3 (`/api/meta.skillVersion` identical to the stamp) → R2-03-02 · AC-4 (stale → `doctor` warns + file list; `pointer update` replaces + preserves `.agents/` symlinks; doctor green) → R2-03-03 · AC-5 (`install.sh` run twice prints `updated from <old>`) → R2-03-04 · AC-6 (unit + cli tests green) → CI, not a scenario.

## Preconditions

- Seed complete; personas: DEV (`USERS.developer` — CLI scenarios, key from `state/keys.json.developer`), no others needed.
- Prerequisites merged: R1-04 (`/api/meta`), R2-01 → R2-02 → R2-03 landing order (shared `skill.md`; the stamp sits after the frontmatter and cannot collide with the SECURITY drift test).
- Restart-dependent scenarios use `e2e/scripts/restart-api.mjs`: `docker compose up -d --force-recreate api` with env overrides (here `Pointer:SkillVersion=<B>`), preserving the volume, re-waiting on `/swagger/v1/swagger.json` — nightly tier, grouped so the API restarts at most 3× per run (harness §8/§9).
- Stamp parsing in specs mirrors `cli/src/lib/skill-stamp.ts`: for `.md`, scan for the second `---` line, take the first `pointer-skill-version:` line after it (never a fixed line number); for `.sh`, line 2.
- `runId`-scoped temp repos via `lib/git.mjs tempRepo()`; the shared helper `installPointerSh(repo)` = `curl -fsSL http://localhost:8090/install.sh | sh` with `POINTER_SERVER/POINTER_PROJECT/POINTER_API_KEY` (DEV key) and `POINTER_AI_TOOL=other` exported — created here, reused by R2-06-02.

## Scenarios

| id | intent | tier | layer | role | steps | expected | evidence |
|---|---|---|---|---|---|---|---|
| R2-03-01 | served stamps on all four files | PR | api | — | Raw `fetch` (not envelope-parsed) of each: 1. `GET /skill.md` → text; line 1 === `---`; find closing frontmatter `---`; first line after it must match `/^<!-- pointer-skill-version: (.+) -->$/` → stamp `S`. 2. Same for `GET /pointer-init.md` → same `S`. 3. `GET /install.sh` line 2 matches `/^# pointer-skill-version: (.+)$/` === `S`. 4. `GET /pointer.sh` line 2 same. 5. All four bodies: count substrings `<POINTER_SKILL_VERSION>` and `<POINTER_SERVER>` → 0 (pointer.sh is now in `injectedFiles`). 6. `/skill.md` still contains `## ⚠️ SECURITY` and `AI RULES PRECEDENCE` (R2-01's drift anchors intact). | 1 → 200, frontmatter intact, stamp present exactly once; 2–4 → identical `S`; 5 → zero placeholders anywhere; 6 → both headings found | report row; the four `head`/line-2 outputs pasted |
| R2-03-02 | `/api/meta.skillVersion` equals the stamp | PR | api | — | 1. `GET /api/meta`. 2. Compare `data.skillVersion` with `S` from R2-03-01. 3. Assert `data.version` non-empty (default resolver = assembly informational version). | 200; `data.skillVersion === S`; `typeof skillVersion === 'string'` (DTO additive, Orval-safe) | report row |
| R2-03-03 | `doctor: flags stale skill copy` | nightly | cli + api | DEV | 1. `tempRepo()`; `spawnCli({ cwd: repo, args: ['init','--server','http://localhost:8090','--key',DEV_KEY,'--project','e2e-alpha','--tool','cursor','--yes','--json'] })` → exit 0. 2. Read stamp of `.cursor/rules/pointer-*.md` (glob) → `A` (=== served `S`). 3. `restart-api.mjs` with env `Pointer:SkillVersion=2026.09.12`; `GET /api/meta` → `data.skillVersion === '2026.09.12'`. 4. `spawnCli({ args: ['doctor'] })`; `spawnCli({ args: ['doctor','--json'] })`. 5. `ln -s ../.pointer/pointer.sh .agents/pointer.sh` (if `.agents` absent, create); `spawnCli({ args: ['update'] })`. 6. Re-read `.cursor/rules/pointer-*.md` stamp; `test -L .agents/pointer.sh`; `spawnCli({ args: ['doctor'] })` again. 7. `restart-api.mjs` **without** the override (restore, `finally`). | 1 → init exit 0, skill copies written under `.cursor/rules/` (cursor mapping — AC-2), **not** `.claude/skills/`; 4 → exit 0 with ⚠: stdout contains `stale` and lists `.cursor/rules/pointer-*.md` + `.pointer/pointer.sh` with `A → 2026.09.12`; `--json` → `skills[]` entry `{ file, installed: A, served: '2026.09.12', stale: true }`; 5 → `updated N files (skill version A → 2026.09.12)`; 6 → stamp `2026.09.12`, `.agents/pointer.sh` still a **symlink** (preserved, not overwritten), doctor green (no stale entry, exit 0); 7 → `/api/meta.skillVersion === A` again | report row; doctor before/after output pasted |
| R2-03-04 | `install.sh` re-run prints `updated from <old>` | nightly | cli | DEV | Runs **inside the same restart window as R2-03-03 steps 3–6** (no extra restart): 1. Fresh `tempRepo()`; with default server stamp `A`: `installPointerSh(repo)` → stdout contains `installed skill version A`; `.pointer/pointer.sh` exists with line-2 stamp `A`. 2. While the override is active (served `2026.09.12`): `installPointerSh(repo)` again → stdout contains `updated from A`; line-2 stamp now `2026.09.12`. 3. `./.pointer/pointer.sh list` smoke → exit 0, JSON array printed (install produced a working helper). | 1 → 200/exit 0 both runs; the two version lines exact (`installed skill version A`, `updated from A`); 3 → exit 0 | report row; both stdout excerpts |

## Spec files

- `e2e/api/served-stamp.spec.mjs` — R2-03-01/02 (raw `fetch`, substring counts; uses `lib/report.mjs`).
- `e2e/cli/skill-stamp.spec.mjs` — R2-03-03/04 (uses `spawnCli`, `restart-api.mjs`, new shared helper `installPointerSh(repo)` in `e2e/scripts/lib/install.mjs`).
- `run-e2e.sh` — restart-dependent rows grouped into the nightly `--restart-phase` group; 429 phase still last (harness §8).

## Not covered here

- `Tests/ServedSkillVersionTests.cs` — appsettings-override-wins cell and MVC-level placeholder assertions (xUnit, no browser/CLI).
- `cli/test/skill-stamp.test.ts` (stamp on `.md` line 1 is **not** recognised; missing → null), `skill-paths.test.ts` (mapping matrix for every `aiTool` + `skillsDir` override — E2E covers only `cursor` and `other`), `update.test.ts` (stub-server replace/symlink/`--check` semantics).
- Dashboard regeneration for `MetaResponse.skillVersion` — additive, verified by the contract guard (harness §10).
- Caddy/TLS-served stamping — R3-03's header matrix.

## Flake notes

- R2-03-01 must use the frontmatter scan, never `sed -n 5p` — R2-01/R2-02 edits shift line numbers.
- One restart window serves both R2-03-03 and R2-03-04 (order: restart → doctor stale → update → install.sh runs → restore); the restore restart runs in `finally` so a mid-phase failure cannot poison later phases.
- After the phase, re-assert `GET /api/meta.skillVersion === A` (R2-03-03 step 7) before any other spec talks to the API — stamps leak into CLI handshakes.
- `.cursor/rules/pointer-*.md` is a glob — if init writes more than one file, assert **all** stamps equal `A`.
