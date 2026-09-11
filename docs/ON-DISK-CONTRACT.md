# On-disk contract (frozen names)

These are the names that land in customer repositories and customer browsers; they are frozen so the
planned rebrand can honour them (see the "post-rebrand" column). To add a new customer-visible name,
the same PR must add it to this file and to the allowlists in `Tests/OnDiskContractTests.cs` — the
guard test fails otherwise.

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

---

**Footnote — internal `data-*` attributes (allowlist, not contract).** The guard test
(`Tests/OnDiskContractTests.cs`) also permits these non-customer-facing `data-*` names, all internal
to the widget's shadow DOM, the served skill docs, or the marketing pages — never part of the
host-facing contract:

`data-id`, `data-act`, `data-toggle`, `data-placement`, `data-private`, `data-c`, `data-i`,
`data-path`, `data-testid`, `data-theme`, `data-step`, `data-brand-logo`, `data-brand-name`

Any other `data-*` name — in particular any `data-pointer*` or `data-pf*` — must be added to the
frozen table above and to the test's allowlists in the same PR that introduces it.
