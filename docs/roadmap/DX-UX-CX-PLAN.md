# Pointer — DX / UX / CX improvement plan

> Agreed 2026-09-10 in a brainstorming session. Status: **plan only, nothing implemented.**
> Scope of this doc: everything discussed and approved so far. Later ideas get appended.

## Context

The core loop works (comment on a running app → AI applies the queue → commit/commit-URL), but the
**first steps are not straight**: skills are installed via `curl | sh`, the API key is a "fill this
in later" file, the project key is typed by hand, and finishing the install requires running an AI
skill. The goal is: **first comment in ≤ 5 minutes, without needing an AI agent to install**, and a
loop the whole team lives in — not just the developer.

## Decisions taken

| Topic | Decision |
|---|---|
| Distribution of install | **npm CLI** (`npx pointer …`) is the primary path. `curl \| sh` stays as fallback for non-Node stacks. |
| Package name | Keep `pointer*` for now — experimental; the rebranding plan (`docs/rebranding-plan` branch) templates the rename. Check `npm view` for a free name (`pointer-cli` / `@pointer-app/cli`). |
| OSS vs SaaS | Undecided → **design white-label first.** Server URL is the *only* input; name/logo/urls come from `GET /api/branding`; skills and widget are served by the server. One build-time `DEFAULT_SERVER` constant, never hardcoded in code paths. |
| Self-hosting boundary | Self-hosted = **API + Postgres** (and the **dashboard, deployed separately** as today). Thin clients — **npm CLI, browser extension, landing** — are published centrally and take `--server`. Self-hosting bootstrap (`server up`) is **out of scope**. |
| Email notifications | **Admin-configured per workspace** (cost control): in-app notifications (widget badge + dashboard bell) are the free default; email is an opt-in channel with a monthly cap. Same switch later fits Slack/webhooks. |
| Source-path manifest | **Never committed** — deterministic, regenerated locally (see §4). |

## Phase 1 — Install & first comment (CLI)

1. **`npx pointer init`** — replaces `install.sh` + manual steps. Interactive, in order:
   1. Server URL (flag `--server` / env `POINTER_SERVER` / existing `.pointer/config.json` / prompt, default = build-time constant).
   2. Fetch `GET /api/branding` → use `productName`, `urls.app` in all output.
   3. API key → validate live (`/api/auth/me`), write `.pointer/credentials.env`, ensure `.gitignore`. Fail fast if invalid.
   4. Project → **list the user's projects from the API**, pick or "create new". Project key is generated, never typed.
   5. Environment (Local / Staging / Production).
   6. AI tool (Claude Code / Cursor / opencode / other) → skills directory.
   7. Install skills (downloaded from the server, pre-filled), detect stack, **inject the widget tag directly** (Vite / Angular / Next / static — logic exists in `API/wwwroot/pointer-init.md`). The AI skill becomes the fallback for exotic setups.
   8. End with a **verification**, not a to-do list: served check page `<server>/check?project=…` → green = widget loads, key works.
   - Non-interactive: `npx pointer init --key … --project … --server … -y` (CI / agents).
2. **API key hand-off, both options:**
   - Dashboard quick-start shows one pre-filled command: `npx pointer init --key ptr_…`.
   - `npx pointer login` → browser device-code flow (like `gh auth login`), key lands in the terminal.
3. **`npx pointer list | apply | status | reply | doctor`** — replaces `.pointer/pointer.sh`.
   - `doctor`: server reachable, key valid, project exists, widget tag present, skills installed, `.gitignore` correct. AI skill calls it first.
4. **Version handshake**: `GET /api/meta` → `{ version, minCliVersion }`; CLI warns on mismatch.
5. **Widget empty-state onboarding**: first open with zero comments → 3-step tooltip.
6. **One doc path**: dashboard quick-start is the single source; `install.sh`, `pointer-init.md`, landing all link to it.

## Phase 2 — The apply loop (DX)

7. **`npx pointer apply`** is the entry point: fetches the queue, builds the prompt, hands off via `--tool claude|cursor|opencode` (or prints / copies to clipboard). `skill.md` becomes an implementation detail.
8. **`apply --plan`** (dry run): AI lists file(s) it would change per comment, no edits.
9. **`apply --pr`**: branch → commit(s) per project `CommitStyle` → **push by the human's CLI** (AI still never pushes) → PR body lists comments + pin screenshots.
10. **Close the loop to the author**: on `applied`, notify author (in-app by default, email if workspace-enabled) with commit/PR link + 👍/👎 → 👎 reopens.
11. **Batch by file**: group queue items touching the same component into one prompt.
12. **Dev feedback on AI work**: 👍/👎 per applied comment → dataset for which comment phrasings apply cleanly.

## Phase 3 — Stakeholder side (CX)

13. **Passwordless quick-access**: invite by email → magic link → widget authed (`IsQuickAccess` role already exists).
14. **Threaded replies with @mention** from terminal/dashboard (`npx pointer reply 42 "which blue?"`) → author pinged.
15. **Comment templates as chips** in the widget (existing `PredefinedAction`s): "Fix typo", "Make responsive", "Wrong data".

## Phase 4 — Source-path resolution in production (token cost)

Today (`web-component/src/capture.ts:242-257`): tier 1 custom `data-source` attr → tier 2 React/Vue **dev-mode** internals → tier 3 null → AI greps. In production tiers 1–2 vanish (minified), so every prod comment pays the grep.

**Design — stable ID, not a path:**
- **Build plugin** (installed by `init`; **Vite first** — covers React/Vue/Svelte/Solid — then Angular builder) stamps each component root with `data-pf="<hash>"`.
- `hash = hash(repo-relative file path + component export name)` — no line numbers, no content. Deterministic across machines and builds; changes only on file/component rename.
- Plugin writes **`.pointer/manifest.json`** `{ hash → "src/…/File.tsx" }`. **Gitignored, regenerated** by every dev-server/build run and by `apply`/`doctor` if missing. Production DOM exposes only meaningless hashes.
- Widget captures `data-pf` as `sourcePath` (tier 1 — zero widget changes). CLI/AI resolves the hash locally; line numbers found at apply time.
- **Stale hash** (file renamed after capture): fall back to today's grep, seeded by the component name stored in the manifest; CLI reports "source renamed since capture".
- Optional later: `data-pf-build="<git sha>"` on `<html>` → CLI warns "captured on a build N commits behind".
- **No-plugin fallbacks** (static / Razor / Blazor): `npx pointer map` builds a selector/text → file index once; source maps as second choice.

## Phase 5 — Trust, safety, business

16. **Apply audit log** per comment: who, tool/model, commit, diff summary.
17. **Prompt-injection guard**: `skill.md` wraps comments as *data*; API flags suspicious patterns (`ignore previous`, `curl`, `rm`).
18. **Per-project scoping rules**: e.g. Client comments can't target `/admin`; Production comments need PM approval before `ready-to-apply`.
19. **Workspaces before billing**: org boundary owning members, billing, email budget, integrations. Everything in §2–§5 hangs off it — introduce while the schema is cheap to change.
20. **Plan limits enforced in the API** (projects, comments/month, seats, applies/month) — add a limits object to `/api/plans`, check in the service layer.
21. **Usage events**: `installed`, `first_comment`, `first_apply`, `apply_failed` per project + one dashboard tile. Target metric: time-to-first-comment.
22. **`pointer.js` versioning**: `/pointer.js?v=` + `stable` alias, or at minimum a deploy smoke test — one bad deploy must not break every customer site.
23. **Extension as the demo**: default sandbox project with no signup; shows the apply-terminal with fake output. Delivers the landing's "no install" promise.

## Phase 6 — AI-tool interface & editor

24. **Pointer MCP server** (`npx pointer mcp`): typed tools `list_comments`, `get_comment`, `mark_applied`, `reply`, `resolve_source(hash)` for Claude Code / Cursor / Windsurf / opencode at once. Fewer tokens than prose, no drift between skill copies, the key never leaves the CLI process. `skill.md` shrinks to "use the Pointer tools." **Biggest DX multiplier in this plan.**
25. **Scoped API keys**: per project / per tool, revocable, optional expiry, "last used" in the dashboard (needed once keys live in CI and MCP configs).
26. **VS Code / JetBrains extension** (thin): pending comments as gutter markers on the manifest-resolved file, pin screenshot on hover, "apply with AI" calls the CLI. Depends on Phase 4.

## Phase 7 — Comment quality & the after-apply story

27. **Duplicate detection**: same selector + similar text within N days → "3 people reported this" (one comment, a counter). Saves apply tokens; PM signal.
28. **Before/after screenshots**: after apply **and deploy**, the widget re-screenshots the same selector and attaches it — the proof stakeholders want.
29. **Multi-element comments** (shift-click): "all these cards" is one comment, not four.
30. **Auto-changelog per project**: applied comments grouped by deploy (`data-pf-build`) → shared "What changed" page; doubles as the verification prompt for §10.
31. **Deploy awareness**: new build sha detected → `applied` → `deployed`; §10 notifications and §28 re-screenshots fire **then**, not at commit time (commit ≠ live).
32. **Outbound webhooks first** (`comment.created|applied|deployed`), workspace-configured like email; Slack, Jira/Linear, GitHub Issues are thin consumers of the webhook. One mechanism, cost stays with the customer.

## Phase 8 — Privacy & security

33. **Capture privacy** — two separate concerns:
    - **Screenshot (image, opt-in per comment as today).** On the *first* check per user per project, one-time notice: *"Screenshots capture exactly what you see, including filled form data. Only take it when the comment needs it."* → remembered at account level (same sync as `addCommentShortcut`). **No masking by default** — filled data is often the point. Optional per-comment **"blur inputs"** toggle, off by default. **Delete**: author, admin, project owner can delete the image without deleting the comment → card shows "screenshot removed by X"; blob physically deleted. Workspace **retention** (e.g. images auto-deleted after 90 days, comments stay).
    - **DOM snapshot (text, captured on every comment, no checkbox)** — `capture.ts:139-145` sends 160 chars of `textContent` + attribute values ≤120 chars, which can carry customer names / `value="…"`. Auto-drop `value` of `input/textarea/select`; `data-pf-mask` on any subtree → `•••` in the snapshot; per-project toggle "don't capture text content" (selector + classes still work for the AI).
    - **Server**: deleting a screenshot/comment deletes the blob; `DELETE project` cascades everything; workspace-level retention job; short privacy note on the landing.
34. **Widget hardening**: SRI hashes for the versioned `pointer.js` (§22), documented CSP for hosts, never auto-upgrade a pinned version.
40. **Status page + widget kill switch**: widget fails silently when the API is down (verify with a test); server-side per-project flag disables the widget instantly (leaked key, runaway bot).

## Phase 9 — Review workflow & reach

35. **Preview deployments = automatic environments.** Vercel / Netlify / Cloudflare PR URLs registered as ephemeral environments (by `init` or a CI flag), auto-archived on merge; comments on previews → same PR (§9). The most common modern review flow — own it.
36. **Assignment + triage board**: assign a comment to a dev, kanban by status, saved filters per user. Status exists today; ownership doesn't.
37. **AI triage (server-side, opt-in, cheap model)**: classify *style / data / logic / bug / question*, effort S/M/L, flag "ambiguous → ask author". Feeds §27, §11, §36. Cost lands on the workspace budget like email.
38. **Mobile capture via QR**: dashboard QR → phone opens the app with the widget authed → comment from a real device (viewport/UA already captured). Responsive bugs are half of all feedback.
39. **Reviewer share link (no install anywhere)**: invited guest opens a site with the extension-less widget — via the browser extension today, a bookmarklet later. Removes "ask the dev to add the widget first" for agencies.

## Phase 10 — Abuse, multi-repo, cloud apply, agencies

41. **Per-project allowed origins.** The project key is public in the host HTML → anyone can post (or flood) comments into the queue. Project setting: allowed domains (`app.acme.com`, `*.vercel.app`), enforced on the widget's comment endpoints via `Origin`/`Referer`. Rate limiting exists (`API/Program.cs:46`) but only for auth surfaces — extend to comment creation. **Closable in a day.**
42. **Project ↔ repo mapping + monorepos.** A comment knows its project, not its repo. Add `repoUrl` + `rootPath` per project (set by `init`) so `apply`/MCP know which checkout and sub-package; one repo can host several projects; manifest lives per package. Prerequisite silently assumed by §9 (PR), §30 (changelog), §43.
43. **Cloud apply via GitHub App** (the parked local-apply-bridge, done right): PM clicks "Apply" in the dashboard → server-side runner checks out the repo, runs the AI (customer's key or workspace budget), opens a PR. No local CLI. **The SaaS differentiator** — the one thing the OSS build won't have. Build *after* the CLI, on the same `apply` core, so both paths share one code base.
44. **Agency model**: workspace → **clients** → projects. Client-scoped guests, per-client branding, per-client report/changelog. Agencies are the natural buyer (many sites, non-technical commenters); small schema addition now if §19 is designed with it.

## Phase 11 — AI quality, growth, docs

45. **Design-system awareness**: `init` detects tokens (Tailwind config, CSS vars, `_variables.scss`) → summary in `.pointer/stack.json`; the AI is told "use existing tokens" so "make it blue" becomes `var(--primary)`, not `#0000ff`. Almost free, directly improves apply quality.
46. **Comment quality nudge** in the widget: client-side heuristics while typing ("bigger" → "bigger how? e.g. 18px"). No AI cost; halves downstream ambiguity.
47. **Seeded demo project on signup**: sample app with 5 comments, one already applied — dashboard never empty, the apply story visible in 30 seconds.
48. **Docs as a real site** (`docs.<domain>`, generated from the same markdown the API serves): quick-start, CLI reference, MCP setup per tool, privacy, self-hosting. Today docs live inside skills/README — fine for AIs, invisible to humans searching.
49. **"Powered by" badge** on the free plan (widget footer, off on paid) — zero-cost distribution.

## Suggested order

0. Allowed origins + comment rate limiting (§41) — security hole, do first.
1. Workspaces with client grouping (§19, §44) — schema first.
2. CLI `init` + pre-filled command (§1–2) + `doctor` + `/api/meta` + repo mapping (§42) + design tokens (§45).
3. `apply` / `--plan` / `--pr` (§7–9) + audit log (§16) + injection guard (§17) → cloud apply (§43) on the same core.
4. In-app notifications + author loop (§10), passwordless invites (§13).
5. Vite plugin + manifest (Phase 4) → MCP server (§24) → scoped keys (§25).
6. Capture privacy (§33) + kill switch (§40) — before the first real customer.
7. Deploy awareness (§31) + webhooks (§32) + preview environments (§35).
8. Usage events, plan limits, widget versioning, extension demo, then the rest of Phases 7 and 9.

## Verification (per phase, high level)

- CLI: fresh Vite + Angular + static sample apps; `npx pointer init` end-to-end with **no AI tool**, first comment within 5 minutes; `doctor` green; non-interactive flags in CI.
- Apply: 2+ comments with `CommitStyle=Separate` and `Single`; `--plan` makes no edits; `--pr` pushes only via the human CLI.
- Manifest: same repo on two machines → identical `manifest.json`; rename a component → stale hash falls back with a warning.
- White-label: run the whole flow against a second server URL with different `/api/branding`; no "Pointer" string in CLI output except what the server returned.
- MCP: Claude Code + one non-Anthropic tool list/apply/reply through the MCP tools with `skill.md` reduced to a pointer; token count per apply lower than the prose flow.
- Privacy: comment on a filled form → snapshot contains no input values; `data-pf-mask` subtree shows `•••`; author deletes the screenshot → blob gone, comment intact, "removed by" shown; kill-switch flag → widget renders nothing, no console errors.
- Deploy awareness: new build sha → `applied` comments become `deployed`, notification fires once, re-screenshot attached.
- Allowed origins: comment POST from a non-listed origin → 403; burst of comments from one IP → 429; listed origin unaffected.
- Cloud apply: dashboard "Apply" on a test repo → PR opened with the same commits/commit URLs the CLI path produces.

## Related

- `docs/rebranding-plan` branch — `docs/rebranding/REBRANDING-PLAN.md` (§3.1: branding is data, not code; CLI gets one `NAME_LOWER` row).
- `API/wwwroot/install.sh`, `API/wwwroot/pointer-init.md`, `API/wwwroot/skill.md` — current install/apply flow this plan replaces.
- `docs/AI_AGENT_TOKEN_OPTIMIZATION.md` — the token-cost rationale behind Phase 4.
