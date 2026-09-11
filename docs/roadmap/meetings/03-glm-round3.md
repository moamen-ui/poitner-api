
> build · glm-5.2

# 1. FINAL ORDER

1. **§41** — allowed origins (extend `ProjectAppUrl` rows) + comment rate limit. No gate.
2. **NEW-1: on-disk contract freeze** — decide §2's list below before anything writes into customer repos. Gate: before §1.
3. **§1** — `npx pointer init`: server/branding → key → user-scoped project list/create (new endpoints) → env → **Vite+static injected deterministically; Next/monorepo routed to AI skill** → `/check` verification. Absorbs §6 doc fix (kill pointer-init.md:12 self-register lie) + §21-lite (`installed`/`first_comment` events, TenantId at birth).
4. **§2** — dashboard pre-filled `npx pointer init --key …` only. Gate: §1. (Device-code flow held.)
5. **§3+§4** — `doctor` + `/api/meta`, shipped inside the CLI release. Gate: §1.
6. **§7** — apply core as shared lib + CLI entry (`--plan` from §8 is a free prompt flag). Gate: §1 config.
7. **§24** — MCP server, same release as §7. Gate: §7.
8. **NEW-2: served-file version stamp** — stamp version line via existing `<POINTER_SERVER>` middleware (Program.cs:197-223); `doctor` compares, `pointer update` refreshes, curl|sh warns. Gate: §3.
9. **Phase 4** — Vite plugin + manifest, stamping **`data-component-source`** (not `data-pf`); Vite only. Angular/Next effort-flagged (§4 below).
10. **§45** — design tokens into `stack.json`. Rides §1's detection pass.
11. **§10** — in-app author notification with commit link, at commit time. Gate: §7.
12. **NEW-3: widget release engineering** — merge §22+§34: immutable `pointer.js?v=<sha>` + short-TTL `stable` + SRI snippet + deploy smoke + KB/ms budget.
13. **§33-lite** — DOM snapshot privacy only: auto-drop `value` of input/textarea/select in `shallowSnapshot` (capture.ts:139-146), neutral mask attr. Gate: before first real customer.
14. **§13** — quick-access invites (Role.cs:28 + Invite.cs:31 exist); link-copy delivery first, email delivery held per F.
15. **NEW-4: continuous verification CI** — scheduled fresh Vite + Angular app: init → comment → apply E2E; plus white-label job (second server URL + custom branding, assert zero hard-coded name).

# 2. ON-DISK CONTRACT FREEZE

| Written into customer repos | Verdict |
|---|---|
| `.pointer/` dir (`config.json`, `credentials.env`, `stack.json`, `pointer.sh`, `manifest.json`) | **Keep as-is** — permanent path; post-rebrand CLI dual-reads old+new |
| Env vars `POINTER_API_KEY`, `VITE_/NEXT_PUBLIC_/REACT_APP_/bare POINTER_*` (pointer-init.md:53-67) | **Keep as-is** — dual-read both names forever post-rebrand |
| Gitignore lines `.pointer/`, `!.pointer/*.example`, `!.pointer/stack.json`, `!.pointer/pointer.sh` (install.sh:77-80) | **Keep as-is** — derivative of dir name |
| `<pointer-feedback>` custom element tag | **Keep as-is** — frozen v1 DOM API; dual-register at rebrand if renamed |
| `window.__pointerEmbedded` (Program.cs:298) | **Keep as-is** — server-served embed.js can set both flags at rebrand |
| `source-attr` value `data-component-source` (Program.cs:309) | **Keep as-is** — already neutral; **plan's `data-pf` must be renamed to this before Phase 4 ships** |
| Plan's `data-pf-build` | **Rename to `data-build-sha` before shipping** |
| Plan's `data-pf-mask` (§33) | **Rename to `data-snapshot-mask` before shipping** |
| npm bin/package name `pointer` in `.mcp.json` + printed commands | **Keep as-is** — permanent deprecate-stub package post-rebrand; never unpublish |
| Skill dirs `pointer-init/`, `pointer-feedback/` under `.claude/skills/`, `.agents/` (install.sh:18-33) | **Keep as-is** — content is server-served and rebrandable; dir names cosmetic |
| Served URLs `/pointer.js`, `/pointer.css`, `/embed.js`, `/install.sh`, `/skill.md` (in customer HTML) | **Keep as-is** — serve old paths permanently as aliases post-rebrand |
| localStorage key `pointer_token` (pointer-init.md:347) | **Keep as-is** — accept one-time re-login at rebrand |
| `stack.json` keys, element attrs (`project`/`server`/`environment`/`fixed-environment`/`screenshot`), `.agents/` dir | **Keep as-is** — already neutral |

# 3. HOLD LIST

- §2b device-code login — `--key` flow shows friction
- §9 `apply --pr` — §42 done + a PR-based team asks
- §11 batch by file — Phase 4 manifest live in prod
- §14 @mentions — multiple active repliers per thread
- §15 template chips — widget polish sprint
- §16 audit log — before first non-founder apply
- §17 — **cut, replaced by** secrets/payload flag (`sk-`/`AKIA`/`ghp_`/base64/`<script>`/`curl|sh`), 2h
- §18 scoping rules — first Client-role commenter on prod
- §19/§44 workspace extensions — first external workspace
- §20 plan limits — first paid plan
- §23 — **cut, merged into §47** (no fake output)
- §25 scoped keys — first key leaves a laptop (CI/MCP)
- §26 editor extensions — Phase 4 stable + in-editor demand
- §27 dup detection — queue noise reported
- §28 auto re-screenshot — redesign as manual attach; stakeholders demand proof
- §29 multi-element — Phase 4 hashes stable in prod
- §30 changelog — §31 live
- §31 deploy awareness — **fast-follow to order #9**; CommitSha column lands with Phase 4; staging/preview scope (widget-off-in-prod blindness, pointer-init.md:29,356)
- §32 webhooks — first "notify my tool" request; **before email**
- §33 full (retention, blur toggle) — first privacy-question customer
- §34/§22 — folded into NEW-3 (order #12)
- §35 preview environments — §42 + §9 live; budget schema change (H3)
- §38 QR / §39 bookmarklet — §13 adopted; CSP kills bookmarklets anyway
- §37 AI triage — comment volume justifies cost
- §42 repo mapping — day before §9/§35/§43 work starts
- §43 cloud apply — CLI apply proven on 3+ real repos
- §47 seeded demo — first signup without hand-holding
- §48 docs site — first human can't find docs via search
- §49 badge — paid plans exist
- Email channel (§10b) — first non-dev stakeholder outside founder workspace, or comment→revisit p50 > 24h

# 4. EFFORT FLAGS — weeks, not days

- **§1 full init** — 1–2 w even with Next/monorepo on the skill path: new user-scoped project endpoints, `/check`, non-interactive mode, doctor wiring.
- **§24 MCP** — 1–2 w: tool schemas/pagination, skill.md rewrite (security/aiRules prose survives), validation across Claude Code + Cursor + opencode + Windsurf.
- **Phase 4 Angular builder** — 2–4 w: compiler/host-attribute surgery. **Next RSC plugin** — 2–4 w, no DOM roots to stamp; may need wrapper-component strategy.
- **§35 ephemeral environments** — 1–2 w: `EnvironmentTag` fixed enum (Comment.cs:9) + 3 fixed activation bools (Project.cs:13-15) + role-gated switcher (Project.cs:36-39) = schema + widget + dashboard migration, not a CI flag.
- **§9 `--pr`** — 1–2 w: repo mapping, push rights, gh auth, host variance.
- **§13 email-delivered variant** — couples to email infra/deliverability; link-copy variant is days.
- **§28 before/after** — 2+ w and highest failure rate in the plan (prod auth, selector drift, deploy detection).
- **§43 cloud apply** — a quarter: GitHub App permissions, sandboxed runner, secret handling, queueing.
