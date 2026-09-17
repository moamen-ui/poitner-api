# On-disk contract (frozen names)

These are the names that land in customer repositories and customer browsers; they are frozen so the
planned rebrand can honour them (see the "post-rebrand" column). To add a new customer-visible name,
the same PR must add it to this file and to the allowlists in `Tests/OnDiskContractTests.cs` — the
guard test fails otherwise.

| Surface | Name(s) | Written by | Post-rebrand promise |
|---|---|---|---|
| Directory | `.pointer/` | CLI, `install.sh`, `pointer-init.md` | keep; new CLI dual-reads `.pointer/` and `.<newname>/` forever |
| Files in it | **Committed (team config):** `config.json`, `stack.json` (detected stack — not a secret), and — in a multi-project (monorepo) repo — every file under `projects/` (`projects/<key>.stack.json`, one per app). **Gitignored (derived / per-machine — a fresh clone re-installs its own copy):** `credentials.env` (secret — only written by the CLI when the key is kept repo-local: `init --local-credentials`, answering "no" to `init`'s save-globally prompt, or `install.sh`'s no-Node scaffold; the default since the global credential store is to save nothing here at all), `pointer.sh`, `manifest.json`. `credentials.env.example` and `.token_cache` are **legacy names, no longer created** by the CLI (dropped alongside the global store / global cache — see the two new rows below) but stay frozen here since an older install may still have one on disk; both are harmless and gitignored either way | CLI | keep names |
| Global credential store | `~/.config/pointer/credentials.json` (honours `$XDG_CONFIG_HOME`; Windows `%APPDATA%\pointer\credentials.json`; `$POINTER_CONFIG_DIR` overrides the whole directory, for tests) — one entry per server origin: `{ "<server origin>": { "apiKey", "email", "displayName", "savedAt" } }`, file mode `0600`. Outside any repo, per machine, never committed. Written by `pointer login` and by `init` (default, unless `--local-credentials`); read by `resolveApiKey` (`cli/src/credentials.ts`) — the one resolver every command, and `pointer.sh`, uses | CLI | keep; new CLI keeps the directory name `pointer` inside `.config`/`.cache` even after a rebrand, dual-reading a new name alongside it |
| Global cache dir | `${XDG_CACHE_HOME:-~/.cache}/pointer/<hash of server+key>.json` — the cached login JWT, replacing the old repo-local `.token_cache`; keyed by server+key so it is shared across every repo authenticated the same way, not per repo | CLI, `pointer.sh` | keep |
| `config.json` keys | `server`, `project`, `aiTool`, `skillsDir` (optional), `cliVersion`, `delivery`, `projects` (optional — a multi-project repo's `{ key: { path, htmlPath, delivery } }` map, replacing `project`/`htmlPath` for that repo). `environment` (`local`\|`staging`\|`production`) and `environments` are **legacy — no longer written by `init`** since environments and their activation moved to the dashboard; both names stay frozen here (still read for backward compatibility on a config written by an older CLI) and the `<pointer-feedback>` `environment` attribute below is unaffected — it remains a supported, opt-in pin (`init --environment <list>`) | CLI | additive only |
| `.gitignore` lines (current) | `.pointer/*`, `!.pointer/stack.json`, `!.pointer/config.json`, `!.pointer/projects/` — only these are re-included; everything else in `.pointer/` stays covered by the `.pointer/*` wildcard | CLI | keep |
| `.gitignore` lines (skill dirs, current) | `.claude/skills/pointer-init/`, `.claude/skills/pointer-feedback/`, `.agents/skills/pointer-init/`, `.agents/skills/pointer-feedback/`, `.cursor/rules/pointer-init.md`, `.cursor/rules/pointer-feedback.md`, `.windsurf/rules/pointer-init.md`, `.windsurf/rules/pointer-feedback.md` — gitignored as of this version; a fresh clone has none of these until `init`/`update` (re-)installs them. Also still ignored: `.agents/pointer-init/`, `.agents/pointer-feedback/` — the **legacy location (pre-2026-09-16)**, superseded by `.agents/skills/pointer-*/` (the Agent Skills standard layout); `installSkills` removes this pair on every install, so it only lingers in a repo that has not re-run `init`/`update` since | CLI | keep |
| Env vars | `POINTER_API_KEY` — resolved in order from the env var itself, `credentials.env`, or the global credential store above (`resolveApiKey`); `POINTER_SERVER`, `POINTER_PROJECT`, `POINTER_ENV`, `POINTER_ENABLED` with framework prefixes `VITE_`, `NEXT_PUBLIC_`, `REACT_APP_` (see `pointer-init.md:55-65`); `POINTER_CONFIG_DIR` overrides the global store/cache directory (tests only) | CLI, skill | dual-read old + new names |
| Custom element | `<pointer-feedback>` + host-set attributes `project`, `server`, `environment`, `fixed-environment`, `screenshot`, `source-attr`; widget-set (output) attribute `data-fbk-theme` (`light`\|`dark`, resolved by the widget itself — see footnote) | host HTML | keep; dual-register if ever renamed |
| Global | `window.__pointerEmbedded`, `window.__POINTER_CONFIG__`, `window.__POINTER_FETCH__` | `/embed.js`, extension | keep |
| DOM attributes | `data-component-source` (source hint; existing), `data-build-sha` (R3-01), `data-snapshot-mask` (R3-04) | build plugin / host | **brand-neutral by design — never rename** |
| Served URLs | `/widget.js`, `/widget.css` (current, 2026-09-17+ — the bundled file names, matching the widget's own naming), `/pointer.js`, `/pointer.css` (pre-rename names, served byte-identical forever), `/embed.js`, `/install.sh`, `/skill.md`, `/pointer-init.md`, `/pointer.sh`, `/vendor/snapdom.js` | API | keep as permanent aliases |
| Skill directories | `.claude/skills/pointer-init/`, `.claude/skills/pointer-feedback/`, `.agents/skills/pointer-init/`, `.agents/skills/pointer-feedback/` (current, 2026-09-16+ — the Agent Skills standard layout, used by `other`/`antigravity` and symlinked into from the three tools above) — **gitignored** (see the `.gitignore` row above); every clone/machine gets its own copy via `init`/`update`/`install.sh`. `.agents/pointer-init/`, `.agents/pointer-feedback/` — **legacy location (pre-2026-09-16), removed on the next `init`/`update`** | `install.sh`, CLI | keep |
| Browser storage | `localStorage` `pointer_token`, `pointer_user` (`element.ts:413`), `pointer_env_<project>`, `pointer_toolbar_pos`, `pointer_widget_theme` (widget's own light/dark override — see the `data-fbk-theme` footnote), `pointer_widget_language` (widget's own en/ar override, same reasoning — never written to `User.language`); `sessionStorage` `pointer_visible`, `pointer_page_session_id` (`pagecontext.ts:179`) | widget | keep (or accept one re-login) |
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
`data-fbk-left`, `data-fbk-top`, `data-fbk-act`, `data-fbk-drag`, `data-fbk-count`, `data-fbk-unread`,
`data-fbk-tip-side`, `data-ids`

`data-fbk-left` / `data-fbk-top` carry the toolbar's position on the host element so the widget can
place it from a stylesheet rule instead of an inline `style` attribute, which a host page's
Content-Security-Policy blocks. `data-fbk-act` / `data-fbk-drag` / `data-fbk-count` /
`data-fbk-unread` are the toolbar's own selector hooks for element.ts's click wiring.
`data-fbk-tip-side` flips a pin's hover tooltip below the pin when it's too close to the top of the
viewport for the tooltip to open upward. `data-ids` is the comma-joined comment id list on a merged
pin cluster wrapper (element.ts's toggleClusterMenu). All of these live inside the widget's own
shadow DOM and no host ever reads them.

Any other `data-*` name — in particular any `data-pointer*` or `data-pf*` — must be added to the
frozen table above and to the test's allowlists in the same PR that introduces it.

**Footnote — `data-fbk-theme` (frozen, not internal).** Unlike the attributes above, this one is
set by the widget on the `<pointer-feedback>` element itself (light DOM, not shadow DOM), so it's
inspectable from the host page and part of the frozen contract, not the internal allowlist. The
widget resolves it once at boot to `light` or `dark` — an explicit per-browser override (set from
the widget's own account menu, stored in `localStorage` under `pointer_widget_theme`) wins;
otherwise the host page's own rendered background decides; otherwise the OS preference. This is
deliberately NOT the same field as `User.theme`/`/api/me/preferences` (the dashboard's own
site-wide theme toggle) — persisting the widget's choice there would silently flip the dashboard's
theme too, so the widget's theme choice stays local to the browser it was made in and never
touches the account. A consuming app can override any single design token for one mode from its
own CSS without waiting on the widget, e.g.
`pointer-feedback[data-fbk-theme="dark"] { --fbk-primary: #0aa36e; }`.
