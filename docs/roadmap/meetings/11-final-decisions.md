# Roadmap review meeting — final decisions (2026-09-11)

Participants: Claude (plan author, chair), GLM-5.2 via opencode, Gemini 3.1 Pro via agy.
Rounds: 01–03 GLM review + debate · 04 Claude position · 05 agy review · 06 GLM r4 · 07 agy r2 ·
08 chair synthesis · 09/10 last-call votes. **All S1–S14, D1–D3 and C1–C15 accepted by both reviewers.**

## Adopted from the last call

- **Effort**: C4 (`doctor` + `/api/meta`) re-estimated to **≤ 1 day** (agy). C15 (NEW-3 + §33-lite) budgeted **5–7 days** (GLM); may be split into two items if needed.
- **Release 1 realistic total**: 2.7–4.3 weeks. Cut line if it must fit 3 weeks:
  1. NEW-4 splits — *scheduling the existing `e2e/` suite* (½ d) stays in R1; the fresh-app init scenario + white-label CI job move to **day 1 of Release 2**.
  2. NEW-5 key hardening is the designated second slip — order-independent of §1, lands no later than **R2 week 1, before MCP**.
- **Added**: **NEW-6 public privacy / self-host page** (½ day of honest writing: what is captured, retention, self-host = your Postgres). Buyer-facing artifact for §33. Release 3.
- **npm package name** resolved to `pointer-feedback` (bin `pointer`) — `pointer` and `pointer-cli` are taken on npm (checked 2026-09-11); every `npx pointer …` mention in this record reads `npx -y pointer-feedback …`. (Execution-doc review, 2026-09-11.)
- **Rejected**: agy's "project deletion" add — already exists (`Admin/ProjectsController.cs:51`, soft-delete with filtered unique index per commit `7bede71`).

## Corrections to the original plan (facts)

| Original claim | Reality | Effect |
|---|---|---|
| §19 "introduce workspaces" | Tenants exist (`TenantService.cs`, `TenantStamp.cs`, query filters) | §19 → "add fields when needed"; rule: every new table has `OwnerId` |
| §20 "plan limits" new | `PlanEntitlements` + `EntitlementService`; `MaxCommentsPerMonth` enforced (`CommentService.cs:89-100`) | §20 dissolves into wiring attached to features |
| Email cap needs design | `EmailsPerMonth` entitlement exists | wiring only, still held |
| §35 needs new env model | `AppEnvironment` + `ProjectAppUrl` catalog exists | days, gate unchanged |
| §1 needs project list/create endpoints | plain `[Authorize]` list/create exist (`Admin/ProjectsController.cs:11-30`) | no new endpoints |
| §41 "anyone can post" | comment POST needs JWT | still do it — authenticated flood + widening funnel |
| Phase 4 stamps `data-pf` | widget reads `data-component-source` (`Program.cs:309`, `capture.ts:246`) | stamp the existing attr |
| §13 `IsQuickAccess` | `Role.QuickAccess` + `Invite` entity exist | cheaper |
| Keys safe | `User.ApiKey` plaintext, SQL equality (`AuthService.cs:213`) | **NEW-5 key hardening** |
| §40 assumes silent failure | init failures silent; submit → toast; 401 → login modal; disabled → `disableSilently()` | kill switch = flag → existing path |

## Final release plan

**Release 1 — install without an AI** (target 3 weeks)
| # | Item | Est. |
|---|---|---|
| R1.1 | NEW-1 on-disk contract freeze (decision doc) | ½ d |
| R1.2 | §1 `npx pointer init` — server → branding → key → project (existing endpoints) → env → tool → Vite/static inject (Next/monorepo via skill) → `/check` page; `--yes` non-interactive; §6 doc fix; §21 events | 1–2 w |
| R1.3 | §2 dashboard quick-start prints `npx pointer init --key …` | 1 d |
| R1.4 | §3 `doctor` + §4 `GET /api/meta` | ≤ 1 d |
| R1.5 | §41 allowed origins (`ProjectAppUrl`) + comment-POST rate limit | 1 d |
| R1.6 | NEW-5 API-key hardening (table, prefix, SHA-256, migration) — *slip candidate → R2 wk 1* | 2–3 d |
| R1.7 | NEW-4a schedule existing `e2e/` suite in CI | ½ d |
| R1.8 | **§50 tenant invitation by email (CRITICAL)** — added by the founder 2026-09-11, after the meeting; direct create-with-password demoted to a secondary path | 2–3 d |

**Release 2 — apply from anywhere**
| # | Item | Est. |
|---|---|---|
| R2.0 | NEW-4b fresh-app init E2E + white-label CI job | 2 d |
| R2.1 | §7 apply core lib + CLI (`--plan`) | 1–2 w |
| R2.2 | §24 MCP server, same package | 1–2 w |
| R2.3 | NEW-2 served-file version stamp + `pointer update` | ½ d |
| R2.4 | §10 in-app author notification + commit link | 2–3 d |
| R2.5 | §13 quick-access invites, link-copy delivery | 2–3 d |
| R2.6 | S6 secrets/payload advisory flag | 2 h |

**Release 3 — apply quality in production**
| # | Item | Est. |
|---|---|---|
| R3.1 | Phase 4 Vite plugin + gitignored manifest, opt-in, stamps `data-component-source`; §31 `CommitSha` + `data-build-sha`, staging first | 1–2 w |
| R3.2 | §45 design tokens → `stack.json` | 1–2 d |
| R3.3 | NEW-3 widget release engineering (`?v=`, `stable`, SRI, ≤ 60 KB gz, CSP note) | 3–4 d |
| R3.4 | §33-lite DOM-snapshot privacy (drop input values, `data-snapshot-mask`) | 2–3 d |
| R3.5 | NEW-6 public privacy / self-host page | ½ d |

**Hold list** (trigger →): §2b device-code login (key flow shows friction) · §9 `--pr` (§42 + a PR-based team) · §11 batch-by-file (manifest live in prod) · §14 @mentions · §15 template chips · §16 audit log (before first non-founder apply) · §18 scoping rules (first Client on prod) · §19/§44 workspace fields + clients (first external workspace; client isolation = filter discipline) · §20 dissolved · §25 full scoped keys UI (first key in CI/committed config) · §26 editor ext · §27 dedupe · §28 manual before/after · §29 multi-element · §30 changelog (§31 live) · §32 webhooks (first "notify my tool"; **before email**; **Jira/Slack/Linear ride on this via the tools' incoming-webhook automations — no first-class integrations**, decided 2026-09-21, spec in DX-UX-CX-PLAN §32) · §33 full (retention job on `RetentionDays`, blur toggle) · §35 preview envs (days; §42 + §9) · §36 board · §37 AI triage · §38 QR / §39 bookmarklet · §40 kill switch (flag → `disableSilently`) · §42 repo mapping (day before §9/§35/§43) · §43 cloud apply (CLI apply proven on 3+ repos; a quarter) · §46 nudge · §47 seeded demo (absorbs §23) · §48 docs site · §49 badge · email channel (first non-dev stakeholder outside founder workspace, or revisit p50 > 24 h).

**Cut**: §17 injection regex (→ S6 flag) · §23 fake terminal demo (→ §47).

**Added after the meeting** — 2026-09-22, founder: **§54 admin-defined comment fields** (Release 4, R4.1; execution `execution/R4-01-comment-fields.md`): optional extra fields on a comment (first: Jira ticket URL) with a suggested-tool hint for the AI; workspace scope; jsonb values + jsonb schema; no first-class Jira integration.

## Cross-cutting rules (apply to every item)
1. **Dashboard column**: every DTO/endpoint change lists its Orval regen + Angular UI task in the separate `pointer-dashboard` repo.
2. **Additive-only EF migrations** while self-hosters exist; migrations run on boot.
3. **Every new table has `OwnerId`** and the tenant query filter from day one; agency "client" isolation is filter discipline, never `IgnoreQueryFilters`.
4. **White-label**: server URL is the only input; `productName`/`urls` from `/api/branding`; one build-time `DEFAULT_SERVER`; no brand string in CLI output except what the server returned.
5. **On-disk names are frozen** (NEW-1); brand-neutral attribute names (`data-component-source`, `data-build-sha`, `data-snapshot-mask`).
6. **AI never pushes.** Commit per `CommitStyle`; push is the human's CLI.
