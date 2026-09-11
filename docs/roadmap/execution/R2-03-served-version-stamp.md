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
  (`API/wwwroot/pointer.sh`, not in `injectedFiles`). **Both `.md` skills start with YAML frontmatter**
  (`API/wwwroot/skill.md:1` and `pointer-init.md:1` are `---`; the block closes at the second `---`
  line) — AI tools parse that block, so nothing may be inserted before it.
- **Shared file: `API/wwwroot/skill.md`** — also edited by R2-01 (rewrite) and R2-02 (MCP paragraph).
  Land in order R2-01 → R2-02 → **R2-03**, or rebase before touching it. The stamp is inserted after the
  frontmatter and does not intersect the `## ⚠️ SECURITY` section, so R2-01's heading-scoped drift test
  is unaffected.

## Design
- **Stamp**: the middleware also replaces a second placeholder `<POINTER_SKILL_VERSION>` with
  `skillVersion`. Placement per file type:
  - `skill.md`, `pointer-init.md`: `<!-- pointer-skill-version: <POINTER_SKILL_VERSION> -->` on the
    **first line after the closing `---` of the frontmatter** (i.e. line 5 today for both files — but
    parsers must locate it by scanning for the second `---`, never by fixed line number). **Never line 1.**
  - `install.sh`, `pointer.sh`: `# pointer-skill-version: <POINTER_SKILL_VERSION>` as line 2 (after shebang).
  Add `/pointer.sh` to `injectedFiles` (it has no `<POINTER_SERVER>` today, harmless).
- **Value**: `skillVersion` = the API assembly informational version (same as `/api/meta.version`)
  by default; overridable via `Pointer:SkillVersion` in appsettings so skill-only edits can bump it
  without a deploy of code (Decision: string, e.g. `2026.09.11`).
- **`/api/meta`** gains `skillVersion: string` (R1-04's DTO `MetaResponse`).
- **`doctor`** (R1-04) adds a check: read the stamp of each installed skill copy → compare with
  `/api/meta.skillVersion` → `stale` warning listing files. **Which files:** derive the skill
  directory from `.pointer/config.json.aiTool` through the tool→directory mapping in R1-02 §G
  (`claude-code` → `.claude/skills/pointer-*/SKILL.md`; `cursor` → `.cursor/rules/pointer-*.md`;
  `windsurf` → `.windsurf/rules/pointer-*.md`; others → `.agents/pointer-*/SKILL.md`), plus
  `--skills-dir` if `init` recorded one (`config.json.skillsDir`, optional key — add it to R1-02 §C step 7
  and to the on-disk contract as an additive key), plus `.pointer/pointer.sh`. `config.json` records
  the tool *name*, not a directory.
- **`pointer update`** (`cli/src/commands/update.ts`): re-downloads `skill.md`, `pointer-init.md`,
  `pointer.sh` from the configured server into the paths resolved by the same mapping, preserves the
  `.agents/` symlinks, prints `updated N files (skill version X → Y)`. `--check` only reports.
- **`install.sh`**: after downloading, print `installed skill version <POINTER_SKILL_VERSION>`; when
  re-run, compare the existing stamp and print `updated from <old>`. Parsing: for `.md` files
  `awk 'BEGIN{f=0} /^---$/{f++; next} f==2 && /pointer-skill-version:/{print; exit}'`-style scan for the
  first `pointer-skill-version:` line after the second `---` (never `sed -n 1p`); for `.sh` files `sed -n 2p`.

## Tasks
1. `API/Program.cs:192-223` — add `/pointer.sh` to `injectedFiles`; replace `<POINTER_SKILL_VERSION>`; resolve value from `IConfiguration["Pointer:SkillVersion"]` ?? assembly informational version.
2. `API/wwwroot/skill.md`, `pointer-init.md` — insert the stamp line immediately after the closing `---` of the frontmatter; `install.sh`, `pointer.sh` — line 2 after the shebang.
3. `Application/DTOs/Meta/MetaResponse.cs` (R1-04) — add `SkillVersion`; `API/Controllers/MetaController.cs` fills it from the same resolver (extract `SkillVersionResolver` static helper in `API/Extensions/`).
4. `cli/src/lib/skill-stamp.ts` — `readStamp(path): string | null`: for `.md` skip the frontmatter (first line `---` … next `---`) and read the first `pointer-skill-version:` HTML comment after it; for `.sh` read line 2; anything else → `null`.
4b. `cli/src/lib/skill-paths.ts` — `skillFilesFor(config): string[]` implementing the R1-02 §G mapping (+ `skillsDir` override, + `.pointer/pointer.sh`); shared by `doctor` and `update`.
5. `cli/src/commands/doctor.ts` — `skills: { file, installed, served, stale }[]` check over `skillFilesFor(config)`.
6. `cli/src/commands/update.ts` — download/replace/symlink-preserve; `--check`.
7. `install.sh` — print installed/updated version lines.
8. `cli/README.md`, `AGENTS.md` — document `pointer update`.

## Dashboard tasks
Regenerate for `MetaResponse.skillVersion` (additive; no UI).

## Docs
**Updates `landing/docs/cli-reference.html`** with an `update` section: what `pointer update` refreshes (the served skills), how `doctor` reports a stale copy, and why a skill file installed months ago can drift from the server. No new page.

## Tests
- `Tests/ServedSkillVersionTests.cs`: GET `/skill.md`, `/pointer-init.md`, `/install.sh`, `/pointer.sh` contain the stamp and no leftover `<POINTER_SKILL_VERSION>`; **for the two `.md` files line 1 is still `---` and the stamp is the first line after the frontmatter**; appsettings override wins; `/api/meta.skillVersion` equals the stamp.
- `cli/test/skill-stamp.test.ts`: parses md (after frontmatter) and sh forms; a stamp wrongly placed on line 1 of an `.md` is **not** recognised (guards the frontmatter rule); missing → null.
- `cli/test/skill-paths.test.ts`: mapping for each `aiTool` value and the `skillsDir` override.
- `cli/test/update.test.ts`: against a stub server, replaces files, keeps `.agents/*` symlinks, reports versions; `--check` writes nothing.
- E2E scenario `doctor: flags stale skill copy` — install with server stamp A, set `Pointer:SkillVersion=B`, `doctor` warns, `update` fixes, `doctor` green.

## Acceptance criteria
- [ ] `curl -s <server>/skill.md | head -1` prints `---` (frontmatter intact) and `curl -s <server>/skill.md | awk '/^---$/{c++;next} c==2{print;exit}'` prints `<!-- pointer-skill-version: … -->`; same for `pointer-init.md`; `curl -s <server>/install.sh | sed -n 2p` and `…/pointer.sh | sed -n 2p` print `# pointer-skill-version: …`.
- [ ] `doctor` with `aiTool: cursor` in `config.json` checks `.cursor/rules/pointer-*.md`, not `.claude/skills/…`.
- [ ] `GET /api/meta` includes `skillVersion` identical to the stamp.
- [ ] `pointer doctor` in a repo with an older stamp prints `stale` with the file list; `pointer update` replaces them and preserves `.agents/` symlinks; `doctor` is then green.
- [ ] `curl -fsSL <server>/install.sh | sh` twice prints `updated from <old>` on the second run.
- [ ] `just test` and `cli` tests green.

## Rollout / compatibility
Existing installed copies have no stamp → `doctor` reports `unknown (pre-versioning) — run pointer update`.

## Report template
Files; the four `head` outputs; `doctor` before/after output; test results.
