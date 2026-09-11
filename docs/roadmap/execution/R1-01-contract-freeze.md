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
| `config.json` keys | `server`, `project`, `environment` (`local`\|`staging`\|`production`), `aiTool`, `skillsDir` (optional), `cliVersion` | CLI | additive only |
| `.gitignore` lines | `.pointer/`, `!.pointer/credentials.env.example`, `!.pointer/stack.json`, `!.pointer/pointer.sh`, `!.pointer/config.json` | CLI, `install.sh` | keep |
| Env vars | `POINTER_API_KEY` (credentials.env); `POINTER_SERVER`, `POINTER_PROJECT`, `POINTER_ENV`, `POINTER_ENABLED` with framework prefixes `VITE_`, `NEXT_PUBLIC_`, `REACT_APP_` (see `pointer-init.md:55-65`) | CLI, skill | dual-read old + new names |
| Custom element | `<pointer-feedback>` + attributes `project`, `server`, `environment`, `fixed-environment`, `screenshot`, `source-attr` | host HTML | keep; dual-register if ever renamed |
| Global | `window.__pointerEmbedded`, `window.__POINTER_CONFIG__`, `window.__POINTER_FETCH__` | `/embed.js`, extension | keep |
| DOM attributes | `data-component-source` (source hint; existing), `data-build-sha` (R3-01), `data-snapshot-mask` (R3-04) | build plugin / host | **brand-neutral by design — never rename** |
| Served URLs | `/pointer.js`, `/pointer.css`, `/embed.js`, `/install.sh`, `/skill.md`, `/pointer-init.md`, `/pointer.sh`, `/vendor/snapdom.js` | API | keep as permanent aliases |
| Skill directories | `.claude/skills/pointer-init/`, `.claude/skills/pointer-feedback/`, `.agents/pointer-init/`, `.agents/pointer-feedback/` | `install.sh`, CLI | keep |
| Browser storage | `localStorage` `pointer_token`, `pointer_user` (`element.ts:413`), `pointer_env_<project>`, `pointer_toolbar_pos`; `sessionStorage` `pointer_visible`, `pointer_page_session_id` (`pagecontext.ts:179`) | widget | keep (or accept one re-login) |
| MCP config | `.mcp.json` entry `"pointer": { "command": "npx", "args": ["-y", "pointer-feedback", "mcp"] }` — **user-level**, not repo-committed | user, docs | deprecate-stub package forwards forever |
| npm | package `pointer-feedback`, bin `pointer` | — | never unpublish; permanent deprecate-stub printing the new command |

### Guard test
A unit test in `Tests/OnDiskContractTests.cs` that reads `docs/ON-DISK-CONTRACT.md` and asserts each
string in a hard-coded `FrozenNames` array appears in it, and that every customer-visible name found in
the served/injected surfaces belongs to an **allowlist** (not a denylist — a denylist of one dead prefix
guards nothing):
- **attributes** — two scopes, so the test passes against today's tree (verified 2026-09-11: `skill.md:324`
  has `data-testid` in an example snapshot; `landing/index.html` uses `data-theme`, `data-step`,
  `data-brand-logo`, `data-brand-name`, `data-i`; the widget's shadow DOM uses `data-id`, `data-act`,
  `data-toggle`, `data-placement`, `data-private`, `data-c`):
  1. in `API/wwwroot/*.md`, `API/wwwroot/*.sh`, `API/Program.cs`, `web-component/src/**/*.ts`: every
     `data-[a-z-]+` literal ∈ Frozen = {`data-component-source`, `data-build-sha`, `data-snapshot-mask`} ∪
     `InternalAttributes` = {`data-id`, `data-act`, `data-toggle`, `data-placement`, `data-private`,
     `data-c`, `data-i`, `data-path`, `data-testid`, `data-theme`, `data-step`, `data-brand-logo`,
     `data-brand-name`} (the list lives in the test and in `docs/ON-DISK-CONTRACT.md` as a footnote);
  2. in `landing/**/*.html` (excluding `landing/v2/`): only attributes whose name contains `pointer` or
     `pf` are checked, and they must be ∈ Frozen (marketing markup is otherwise free).
  Rule of thumb the test encodes: **no `data-pointer*`, `data-pf*`, or any new pointer-namespace
  attribute anywhere outside the three frozen ones.**
- every `localStorage`/`sessionStorage` key literal in `web-component/src/**/*.ts` ∈ the storage row;
- every `window.__[A-Za-z]+` global in `API/Program.cs` and `web-component/src/**/*.ts` ∈ the globals row.
Purpose: an implementer who adds a new customer-visible name gets a failing test pointing here.

## Tasks
1. Create `docs/ON-DISK-CONTRACT.md` with the table above verbatim plus a 3-line preamble ("why frozen", "how to add a name: PR must update this file and the test").
2. Add `Tests/OnDiskContractTests.cs`:
   - `FrozenNames_AreDocumented` — every entry of the `FrozenNames` string array (all names in the table) is present in the doc text.
   - `ServedFiles_UseOnlyFrozenNames` — collect every `data-[a-z-]+` literal (scope 1) / every `data-*` containing `pointer`|`pf` (scope 2), every `localStorage`/`sessionStorage` key literal and every `window.__X` global from the paths above and assert each is in its allowlist; the failure message names the offending literal and file. Must be **green against the current tree** before any deliberate-failure check.
   - Locate the repo root from the test assembly path by walking up to the directory containing `Pointer.sln` (same technique as any existing test that reads files; if none, implement a small `RepoRoot.Find()` helper in `Tests/`).
3. Add a "Customer-visible names" paragraph to `AGENTS.md` (3 lines) linking to the contract doc.
4. Add `!.pointer/config.json` to the gitignore block in `API/wwwroot/install.sh:76-80` (the CLI will write `config.json`; `install.sh` must not ignore it).

## Dashboard tasks
None.

## Docs
**None — internal only.** The frozen names are already visible to users in what `init` writes; the contract document exists so *implementers* cannot rename them. No public page changes.

## Tests
- Unit: `Tests/OnDiskContractTests.cs` (2 tests above).

## Acceptance criteria
- [ ] `docs/ON-DISK-CONTRACT.md` exists with all 13 rows.
- [ ] `just test` green including the two new tests **against the unmodified tree**; then deliberately adding `data-pointer-x` to `pointer-init.md` makes `ServedFiles_UseOnlyFrozenNames` fail naming that literal (verify, then revert).
- [ ] `install.sh` gitignore block contains `!.pointer/config.json`.
- [ ] `AGENTS.md` links to the contract.

## Rollout / compatibility
No runtime change. Existing installs unaffected.

## Report template
Files changed · `just test` summary line · confirmation of the deliberate-failure check.
