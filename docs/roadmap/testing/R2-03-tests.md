# R2-03-tests — Served-file version stamp + `pointer update`

Harness: [`00-HARNESS.md`](00-HARNESS.md). Execution doc: [`../execution/R2-03-served-version-stamp.md`](../execution/R2-03-served-version-stamp.md).

## Covers

AC-1 (line 1 `---`, stamp first line after frontmatter for both `.md`; `sed -n 2p` for both `.sh`) → R2-03-01 · AC-2 (`doctor` with `aiTool: cursor` checks `.cursor/rules/pointer-*.md`) → R2-03-03 steps 1–2, 5 · AC-3 (`/api/meta.skillVersion` identical to the stamp) → R2-03-02 · AC-4 (stale → `doctor` warns + file list; `pointer update` replaces + preserves `.agents/` symlinks; doctor green) → R2-03-03 · AC-5 (`install.sh` run twice prints `updated from <old>`) → R2-03-04 · AC-6 (unit + cli tests green) → CI, not a scenario.

## Preconditions

- Seed complete; personas: DEV (`USERS.developer` — CLI scenarios, key from `state/keys.json.developer`), no others needed.
- Prerequisites merged: R1-04 (`/api/meta`), R2-01 → R2-02 → R2-03 landing order (shared `skill.md`; the stamp sits after the frontmatter and cannot collide with the SECURITY drift test).
- Restart-dependent scenarios use `e2e/scripts/restart-api.mjs`: `docker compose up -d --force-recreate api` with env overrides, preserving the volume, re-waiting on `/swagger/v1/swagger.json` — nightly tier, grouped so the API restarts at most 3× per run (harness §8/§9). **The override is `Pointer__SkillVersion=<B>`** — ASP.NET maps hierarchical config keys over environment variables with a double underscore, so `Pointer:SkillVersion` is not a legal env-var name (the harness writes `Cli__MinVersion` for the same reason).
- Stamp parsing in specs mirrors `cli/src/lib/skill-stamp.ts`: for `.md`, scan for the second `---` line, take the first `pointer-skill-version:` line after it (never a fixed line number); for `.sh`, line 2.
- `runId`-scoped temp repos via `lib/git.mjs tempRepo()`; the shared helper `installPointerSh(repo)` — created here, reused by R2-06-02 — matches `install.sh`'s **real** interface (`API/wwwroot/install.sh:8-14,56-70`): the skills directory is the **positional argument**, not an env var, so it runs `curl -fsSL http://localhost:8090/install.sh | sh -s -- .agents` in `repo`. There is no `POINTER_AI_TOOL`. `install.sh` writes `.pointer/credentials.env` with an **empty** `POINTER_API_KEY=` and leaves an existing file untouched, so the helper afterwards writes the DEV key into `.pointer/credentials.env` itself (and exports `POINTER_SERVER`/`POINTER_PROJECT`/`POINTER_API_KEY` when it later invokes `./.pointer/pointer.sh`).

## Scenarios

| id | intent | tier | layer | role | steps | expected | evidence |
|---|---|---|---|---|---|---|---|
| R2-03-01 | served stamps on all four files | PR | api | — | Raw `fetch` (not envelope-parsed) of each: 1. `GET /skill.md` → text; line 1 === `---`; find closing frontmatter `---`; first line after it must match `/^<!-- pointer-skill-version: (.+) -->$/` → stamp `S`. 2. Same for `GET /pointer-init.md` → same `S`. 3. `GET /install.sh` line 2 matches `/^# pointer-skill-version: (.+)$/` === `S`. 4. `GET /pointer.sh` line 2 same. 5. All four bodies: count substrings `<POINTER_SKILL_VERSION>` and `<POINTER_SERVER>` → 0 (pointer.sh is now in `injectedFiles`). 6. `/skill.md` still contains `## ⚠️ SECURITY` and `AI RULES PRECEDENCE` (R2-01's drift anchors intact). | 1 → 200, frontmatter intact, stamp present exactly once; 2–4 → identical `S`; 5 → zero placeholders anywhere; 6 → both headings found | report row; the four `head`/line-2 outputs pasted |
| R2-03-02 | `/api/meta.skillVersion` equals the stamp | PR | api | — | 1. `GET /api/meta`. 2. Compare `data.skillVersion` with `S` from R2-03-01. 3. Assert `data.version` non-empty (default resolver = assembly informational version). | 200; `data.skillVersion === S`; `typeof skillVersion === 'string'` (DTO additive, Orval-safe) | report row |
| R2-03-03 | `doctor: flags stale skill copy` | nightly | cli + api | DEV | 1. `tempRepo()`; `spawnCli({ cwd: repo, args: ['init','--server','http://localhost:8090','--key',DEV_KEY,'--project','e2e-alpha','--tool','cursor','--yes','--json'] })` → exit 0. 2. Read stamp of `.cursor/rules/pointer-*.md` (glob) → `A` (=== served `S`). 3. `restart-api.mjs` with env `Pointer__SkillVersion=2026.09.12`; `GET /api/meta` → `data.skillVersion === '2026.09.12'`. 4. `spawnCli({ args: ['doctor'] })`; `spawnCli({ args: ['doctor','--json'] })`. 5. Run `installPointerSh(repo)` once so the **product's own** `.agents/` symlinks exist — `install.sh:29-34` creates `.agents/pointer-init/SKILL.md` and `.agents/pointer-feedback/SKILL.md` as symlinks to `../../<DIR>/…` — then `spawnCli({ args: ['update'] })`. 6. Re-read `.cursor/rules/pointer-*.md` stamp; `test -L .agents/pointer-feedback/SKILL.md` **and** `test -L .agents/pointer-init/SKILL.md` (assert the real product artifacts, not a hand-made link); `spawnCli({ args: ['doctor'] })` again. 7. `restart-api.mjs` **without** the override (restore, `finally`). | 1 → init exit 0, skill copies written under `.cursor/rules/` (cursor mapping — AC-2), **not** `.claude/skills/`; 4 → exit 0 with ⚠: stdout contains `stale` and lists `.cursor/rules/pointer-*.md` + `.pointer/pointer.sh` with `A → 2026.09.12`; `--json` → `skills[]` entry `{ file, installed: A, served: '2026.09.12', stale: true }`; 5 → `updated N files (skill version A → 2026.09.12)`; 6 → stamp `2026.09.12`, **both** `.agents/<skill>/SKILL.md` entries still **symlinks** (`update` rewrites the real file through the link, never replacing the link with a copy), doctor green (no stale entry, exit 0); 7 → `/api/meta.skillVersion === A` again | report row; doctor before/after output + `ls -l .agents/*/SKILL.md` |
| R2-03-04 | `install.sh` re-run prints `updated from <old>` | nightly | cli | DEV | **Straddles the restart window deliberately** — step 1 must run *before* R2-03-03's restart (served stamp still `A`), steps 2–3 *inside* it (served `2026.09.12`): 1. **(pre-window, before R2-03-03 step 3)** Fresh `tempRepo()`; `installPointerSh(repo)` → stdout contains `installed skill version A`; `.pointer/pointer.sh` exists with line-2 stamp `A`. 2. **(in-window)** With the override active: `installPointerSh(repo)` again on the **same** repo → stdout contains `updated from A`; line-2 stamp now `2026.09.12`. 3. Write the DEV key into `.pointer/credentials.env` (install.sh leaves it empty) and run `POINTER_SERVER=http://localhost:8090 POINTER_PROJECT=e2e-alpha ./.pointer/pointer.sh list` → exit 0, JSON array printed (install produced a working helper). | 1 → exit 0, `installed skill version A`; 2 → exit 0, `updated from A`; 3 → exit 0 | report row; both stdout excerpts |

## Spec files

- `e2e/api/served-stamp.spec.mjs` — R2-03-01/02 (raw `fetch`, substring counts; uses `lib/report.mjs`).
- `e2e/cli/skill-stamp.spec.mjs` — R2-03-03/04 (uses `spawnCli`, `restart-api.mjs`, new shared helper `installPointerSh(repo)` in `e2e/scripts/lib/install.mjs`).
- `run-e2e.sh` — restart-dependent rows grouped into the nightly `--restart-phase` group; 429 phase still last (harness §8).

## Not covered here

- `Tests/ServedSkillVersionTests.cs` — appsettings-override-wins cell and MVC-level placeholder assertions (xUnit, no browser/CLI).
- `cli/test/skill-stamp.test.ts` (stamp on `.md` line 1 is **not** recognised; missing → null), `skill-paths.test.ts` (mapping matrix for every `aiTool` + `skillsDir` override — E2E covers only `cursor` and `other`), `update.test.ts` (stub-server replace/symlink/`--check` semantics).
- Dashboard regeneration for `MetaResponse.skillVersion` — additive, verified by the contract guard (harness §10).
- Caddy/TLS-served stamping — R3-03's header matrix.
- **A registry-backed variant of `pointer update`** (harness §6 level 3) — deliberately **not added**.
  `update` re-downloads the skills from **the API** (`../execution/R2-03-served-version-stamp.md`
  §`pointer update`), not from npm, so running it out of a published tarball instead of `dist/cli.js`
  exercises packaging, which the nightly level-2 `packaging` job already covers. The registry buys
  nothing here; it earns its keep only where behaviour depends on *which version is published*
  (R1-04-06) or on a package forwarding to another name (the post-rebrand deprecate-stub, §52).

## Flake notes

- R2-03-01 must use the frontmatter scan, never `sed -n 5p` — R2-01/R2-02 edits shift line numbers.
- One restart window serves both rows, and the order is load-bearing: **R2-03-04 step 1 (pre-window, stamp `A`)** → R2-03-03 step 3 (restart with `Pointer__SkillVersion=2026.09.12`) → R2-03-03 steps 4–6 → R2-03-04 steps 2–3 → R2-03-03 step 7 (restore). Running R2-03-04 step 1 inside the window would read `2026.09.12` and the `updated from A` assertion could never hold. The restore restart runs in `finally` so a mid-phase failure cannot poison later phases.
- `doctor`/`update` locate skill files through the `aiTool` → directory mapping (R1-02 §G, plus an optional `config.json.skillsDir`), never a hard-coded `.claude/skills/**` glob — R2-03-03 deliberately installs with `--tool cursor` to prove that.
- After the phase, re-assert `GET /api/meta.skillVersion === A` (R2-03-03 step 7) before any other spec talks to the API — stamps leak into CLI handshakes.
- `.cursor/rules/pointer-*.md` is a glob — if init writes more than one file, assert **all** stamps equal `A`.
