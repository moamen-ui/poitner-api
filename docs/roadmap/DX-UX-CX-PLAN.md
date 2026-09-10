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

## Suggested order

1. Workspaces (§19) — schema first.
2. CLI `init` + pre-filled command (§1–2) + `doctor` + `/api/meta`.
3. `apply` / `--plan` / `--pr` (§7–9) + audit log (§16) + injection guard (§17).
4. In-app notifications + author loop (§10), passwordless invites (§13).
5. Vite plugin + manifest (Phase 4).
6. Usage events, plan limits, widget versioning, extension demo.

## Verification (per phase, high level)

- CLI: fresh Vite + Angular + static sample apps; `npx pointer init` end-to-end with **no AI tool**, first comment within 5 minutes; `doctor` green; non-interactive flags in CI.
- Apply: 2+ comments with `CommitStyle=Separate` and `Single`; `--plan` makes no edits; `--pr` pushes only via the human CLI.
- Manifest: same repo on two machines → identical `manifest.json`; rename a component → stale hash falls back with a warning.
- White-label: run the whole flow against a second server URL with different `/api/branding`; no "Pointer" string in CLI output except what the server returned.

## Related

- `docs/rebranding-plan` branch — `docs/rebranding/REBRANDING-PLAN.md` (§3.1: branding is data, not code; CLI gets one `NAME_LOWER` row).
- `API/wwwroot/install.sh`, `API/wwwroot/pointer-init.md`, `API/wwwroot/skill.md` — current install/apply flow this plan replaces.
- `docs/AI_AGENT_TOKEN_OPTIMIZATION.md` — the token-cost rationale behind Phase 4.
