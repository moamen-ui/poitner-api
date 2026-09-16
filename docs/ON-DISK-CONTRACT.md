# On-disk contract (frozen names)

These are the names that land in customer repositories and customer browsers; they are frozen so the
planned rebrand can honour them (see the "post-rebrand" column). To add a new customer-visible name,
the same PR must add it to this file and to the allowlists in `Tests/OnDiskContractTests.cs` — the
guard test fails otherwise.

| Surface | Name(s) | Written by | Post-rebrand promise |
|---|---|---|---|
| Directory | `.pointer/` | CLI, `install.sh`, `pointer-init.md` | keep; new CLI dual-reads `.pointer/` and `.<newname>/` forever |
| Files in it | **Committed (team config):** `config.json`, `stack.json` (detected stack — not a secret), and — in a multi-project (monorepo) repo — every file under `projects/` (`projects/<key>.stack.json`, one per app). **Gitignored (derived / per-machine — a fresh clone re-installs its own copy):** `credentials.env` (secret), `credentials.env.example`, `pointer.sh`, `manifest.json`, `.token_cache` | CLI | keep names |
| `config.json` keys | `server`, `project`, `environment` (`local`\|`staging`\|`production`), `aiTool`, `skillsDir` (optional), `cliVersion`, `projects` (optional — a multi-project repo's `{ key: { path, environment, environments, htmlPath, delivery } }` map, replacing `project`/`environment`/`htmlPath` for that repo) | CLI | additive only |
| `.gitignore` lines (current) | `.pointer/*`, `!.pointer/stack.json`, `!.pointer/config.json`, `!.pointer/projects/` — only these are re-included; everything else in `.pointer/` stays covered by the `.pointer/*` wildcard | CLI | keep |
| `.gitignore` lines (skill dirs, current) | `.claude/skills/pointer-init/`, `.claude/skills/pointer-feedback/`, `.agents/skills/pointer-init/`, `.agents/skills/pointer-feedback/`, `.cursor/rules/pointer-init.md`, `.cursor/rules/pointer-feedback.md`, `.windsurf/rules/pointer-init.md`, `.windsurf/rules/pointer-feedback.md` — gitignored as of this version; a fresh clone has none of these until `init`/`update` (re-)installs them. Also still ignored: `.agents/pointer-init/`, `.agents/pointer-feedback/` — the **legacy location (pre-2026-09-16)**, superseded by `.agents/skills/pointer-*/` (the Agent Skills standard layout); `installSkills` removes this pair on every install, so it only lingers in a repo that has not re-run `init`/`update` since | CLI | keep |
| Env vars | `POINTER_API_KEY` (credentials.env); `POINTER_SERVER`, `POINTER_PROJECT`, `POINTER_ENV`, `POINTER_ENABLED` with framework prefixes `VITE_`, `NEXT_PUBLIC_`, `REACT_APP_` (see `pointer-init.md:55-65`) | CLI, skill | dual-read old + new names |
| Custom element | `<pointer-feedback>` + attributes `project`, `server`, `environment`, `fixed-environment`, `screenshot`, `source-attr` | host HTML | keep; dual-register if ever renamed |
| Global | `window.__pointerEmbedded`, `window.__POINTER_CONFIG__`, `window.__POINTER_FETCH__` | `/embed.js`, extension | keep |
| DOM attributes | `data-component-source` (source hint; existing), `data-build-sha` (R3-01), `data-snapshot-mask` (R3-04) | build plugin / host | **brand-neutral by design — never rename** |
| Served URLs | `/pointer.js`, `/pointer.css`, `/embed.js`, `/install.sh`, `/skill.md`, `/pointer-init.md`, `/pointer.sh`, `/vendor/snapdom.js` | API | keep as permanent aliases |
| Skill directories | `.claude/skills/pointer-init/`, `.claude/skills/pointer-feedback/`, `.agents/skills/pointer-init/`, `.agents/skills/pointer-feedback/` (current, 2026-09-16+ — the Agent Skills standard layout, used by `other`/`antigravity` and symlinked into from the three tools above) — **gitignored** (see the `.gitignore` row above); every clone/machine gets its own copy via `init`/`update`/`install.sh`. `.agents/pointer-init/`, `.agents/pointer-feedback/` — **legacy location (pre-2026-09-16), removed on the next `init`/`update`** | `install.sh`, CLI | keep |
| Browser storage | `localStorage` `pointer_token`, `pointer_user` (`element.ts:413`), `pointer_env_<project>`, `pointer_toolbar_pos`; `sessionStorage` `pointer_visible`, `pointer_page_session_id` (`pagecontext.ts:179`) | widget | keep (or accept one re-login) |
| MCP config | `.mcp.json` entry `"pointer": { "command": "npx", "args": ["-y", "pointer-feedback", "mcp"] }` — **user-level**, not repo-committed | user, docs | deprecate-stub package forwards forever |
| npm | package `pointer-feedback`, bin `pointer` | — | never unpublish; permanent deprecate-stub printing the new command |

---

**Footnote — `.gitignore` history.** Earlier CLI versions committed more than `config.json`/
`stack.json`: the block also re-included `!.pointer/credentials.env.example` and
`!.pointer/pointer.sh`, and the skill directories were not gitignored at all — every consumer repo
ended up committing pointer.sh and ~800 lines of server-served skill markdown that went stale the
moment the server changed it. `upsertGitignore` (`cli/src/config.ts`) migrates a `.gitignore`
written by any of those versions on the next `init`/`doctor --fix`: it drops both `!` lines above
and adds the four skill-directory ignore lines from the `.gitignore` rows above. `pointer.sh` and
the skill files are still written to the same paths (see "Files in it" and "Skill directories"
above) — they are simply installed fresh into every clone by `init`/`update` instead of being
shared via git.

**Footnote — internal `data-*` attributes (allowlist, not contract).** The guard test
(`Tests/OnDiskContractTests.cs`) also permits these non-customer-facing `data-*` names, all internal
to the widget's shadow DOM, the served skill docs, or the marketing pages — never part of the
host-facing contract:

`data-id`, `data-act`, `data-toggle`, `data-placement`, `data-private`, `data-c`, `data-i`,
`data-path`, `data-testid`, `data-theme`, `data-step`, `data-brand-logo`, `data-brand-name`,
`data-pf-left`, `data-pf-top`

`data-pf-left` / `data-pf-top` carry the toolbar's position on the host element so the widget can
place it from a stylesheet rule instead of an inline `style` attribute, which a host page's
Content-Security-Policy blocks. They live inside the widget's own shadow DOM and no host ever reads
them.

Any other `data-*` name — in particular any `data-pointer*` or `data-pf*` — must be added to the
frozen table above and to the test's allowlists in the same PR that introduces it.
