# R1-01 — On-disk contract freeze (NEW-1 · Release 1 · ½ day)

## Goal
Before the CLI writes anything into customer repositories, fix the list of names that land there and
declare them stable across the planned rebrand. Output is a **committed document + a guard test**, not
a feature. After this, no implementer may introduce a new customer-visible name without adding it here.

## Out of scope
- Renaming anything that already ships (`.pointer/`, `POINTER_*`, `<pointer-feedback>`) — all kept.
- The npm package name (decided in `01-OVERVIEW.md`: `pointer-feedback`, bin `pointer`).
- The rebrand itself (`docs/rebranding-plan` branch).

## Prerequisites
None. Facts: `00-API-INVENTORY.md` §5–§8 (served files, `pointer.sh`, `pointer-init.md`, `install.sh`).

## Design

### The frozen contract (`docs/ON-DISK-CONTRACT.md`)
Create `docs/ON-DISK-CONTRACT.md` with this table. Column "post-rebrand" states the compatibility
promise the future rename must honour.

| Surface | Name(s) | Written by | Post-rebrand promise |
|---|---|---|---|
| Directory | `.pointer/` | CLI, `install.sh`, `pointer-init.md` | keep; new CLI dual-reads `.pointer/` and `.<newname>/` forever |
| Files in it | `config.json` (new, committable), `credentials.env` (gitignored), `credentials.env.example`, `stack.json`, `pointer.sh`, `manifest.json` (gitignored), `.token_cache` (gitignored) | CLI | keep names |
| `config.json` keys | `server`, `project`, `environment` (`local`\|`staging`\|`production`), `aiTool`, `cliVersion` | CLI | additive only |
| `.gitignore` lines | `.pointer/`, `!.pointer/credentials.env.example`, `!.pointer/stack.json`, `!.pointer/pointer.sh`, `!.pointer/config.json` | CLI, `install.sh` | keep |
| Env vars | `POINTER_API_KEY` (credentials.env); `POINTER_SERVER`, `POINTER_PROJECT`, `POINTER_ENV`, `POINTER_ENABLED` with framework prefixes `VITE_`, `NEXT_PUBLIC_`, `REACT_APP_` (see `pointer-init.md:55-65`) | CLI, skill | dual-read old + new names |
| Custom element | `<pointer-feedback>` + attributes `project`, `server`, `environment`, `fixed-environment`, `screenshot`, `source-attr` | host HTML | keep; dual-register if ever renamed |
| Global | `window.__pointerEmbedded`, `window.__POINTER_CONFIG__`, `window.__POINTER_FETCH__` | `/embed.js`, extension | keep |
| DOM attributes | `data-component-source` (source hint; existing), `data-build-sha` (R3-01), `data-snapshot-mask` (R3-04) | build plugin / host | **brand-neutral by design — never rename** |
| Served URLs | `/pointer.js`, `/pointer.css`, `/embed.js`, `/install.sh`, `/skill.md`, `/pointer-init.md`, `/pointer.sh`, `/vendor/snapdom.js` | API | keep as permanent aliases |
| Skill directories | `.claude/skills/pointer-init/`, `.claude/skills/pointer-feedback/`, `.agents/pointer-init/`, `.agents/pointer-feedback/` | `install.sh`, CLI | keep |
| Browser storage | `localStorage` `pointer_token`, `pointer_env_<project>`, `pointer_toolbar_pos`; `sessionStorage` `pointer_visible` | widget | keep (or accept one re-login) |
| MCP config | `.mcp.json` entry `"pointer": { "command": "npx", "args": ["-y", "pointer-feedback", "mcp"] }` — **user-level**, not repo-committed | user, docs | deprecate-stub package forwards forever |
| npm | package `pointer-feedback`, bin `pointer` | — | never unpublish; permanent deprecate-stub printing the new command |

### Guard test
A unit test in `Tests/OnDiskContractTests.cs` that reads `docs/ON-DISK-CONTRACT.md` and asserts each
string in a hard-coded `FrozenNames` array appears in it, and that `API/wwwroot/install.sh`,
`pointer-init.md`, `Program.cs` (embed.js) contain no `data-pf` / `pointer_*` names outside the table.
Purpose: an implementer who adds a new customer-visible name gets a failing test pointing here.

## Tasks
1. Create `docs/ON-DISK-CONTRACT.md` with the table above verbatim plus a 3-line preamble ("why frozen", "how to add a name: PR must update this file and the test").
2. Add `Tests/OnDiskContractTests.cs`:
   - `FrozenNames_AreDocumented` — every entry of the `FrozenNames` string array (all names in the table) is present in the doc text.
   - `ServedFiles_UseOnlyFrozenNames` — regex `data-pf[a-z-]*` has zero matches in `API/wwwroot/*.md`, `API/wwwroot/*.sh`, `API/Program.cs`, `web-component/src/**/*.ts`.
   - Locate the repo root from the test assembly path by walking up to the directory containing `Pointer.sln` (same technique as any existing test that reads files; if none, implement a small `RepoRoot.Find()` helper in `Tests/`).
3. Add a "Customer-visible names" paragraph to `AGENTS.md` (3 lines) linking to the contract doc.
4. Add `!.pointer/config.json` to the gitignore block in `API/wwwroot/install.sh:76-80` (the CLI will write `config.json`; `install.sh` must not ignore it).

## Dashboard tasks
None.

## Tests
- Unit: `Tests/OnDiskContractTests.cs` (2 tests above).

## Acceptance criteria
- [ ] `docs/ON-DISK-CONTRACT.md` exists with all 13 rows.
- [ ] `just test` green including the two new tests; deliberately adding `data-pf-x` to `pointer-init.md` makes `ServedFiles_UseOnlyFrozenNames` fail (verify, then revert).
- [ ] `install.sh` gitignore block contains `!.pointer/config.json`.
- [ ] `AGENTS.md` links to the contract.

## Rollout / compatibility
No runtime change. Existing installs unaffected.

## Report template
Files changed · `just test` summary line · confirmation of the deliberate-failure check.
