# Pointer — DX / UX / CX improvement plan

> Brainstormed 2026-09-10; reviewed 2026-09-11 in a three-expert meeting (Claude · GLM-5.2 · Gemini
> 3.1 Pro — transcripts in [`meetings/`](meetings/), outcome in
> [`meetings/11-final-decisions.md`](meetings/11-final-decisions.md)).
> Implementer-grade specs live in [`execution/`](execution/).
>
> Item numbers §1–§49 are stable identifiers used across all documents — never renumber.

## Status — 2026-09-13

**Releases 1, 2 and 3 are built and verified.** Current state, from the suite rather than from
memory (`node e2e/scripts/coverage.mjs`):

| Release | Items | Scenarios | State |
|---|---|---|---|
| R1 — foundations | R1-01 … R1-10 | 58/58 | shipped |
| R2 — the apply loop | R2-00 … R2-07 | 52/52 | shipped |
| R3 — production hardening | R3-01 … R3-06 | 35/35 | shipped |

**145/145 automatable scenarios**, plus 7 that are manual by design (R1-07 drives GitHub Actions
rather than the stack; R3-05-02 is a Lighthouse audit through an MCP tool). Verified alongside:
569 .NET tests, 109 CLI tests, and a two-image upgrade rehearsal that seeds a database through an
image built from a pre-migration commit and then upgrades onto it.

Two things this table deliberately does not hide:

- **R2-00-06** (white-label: widget text carries no brand leak) is red. It is a two-browser-context
  sequencing problem in the scenario, not the product; the guarantee itself is covered by
  R2-00-08, which passes.
- **The CLI is not published to npm.** `npx pointer-feedback` resolves only against the local
  Verdaccio registry the nightly stack runs — a deliberate decision, not an oversight. Installs go
  through `install.sh` and `.pointer/pointer.sh` today.

Per-item status lives with each spec in [`execution/`](execution/); per-scenario status is in
[`testing/`](testing/) and is regenerated on every suite run.

## Context

The core loop works (comment on a running app → AI applies the queue → commit + commit URL), but the
**first steps are not straight**: skills are installed via `curl | sh`, the API key is a "fill this
in later" file, the project key is typed by hand, and finishing the install requires running an AI
skill. Goal: **first comment in ≤ 5 minutes, without needing an AI agent to install**, then a loop
the whole team lives in — not just the developer.

## Decisions taken

| Topic | Decision |
|---|---|
| Distribution of install | **npm CLI** (`npx -y pointer-feedback …`) is the primary path. `curl \| sh` stays as fallback for non-Node stacks. |
| Package name | npm package **`pointer-feedback`** (bin `pointer`; `pointer`/`pointer-cli` were taken — checked 2026-09-11); brand stays `Pointer` for now (experimental; rebrand templated on the `docs/rebranding-plan` branch). **What is frozen is the on-disk contract, not the npm name** — see NEW-1. Post-rebrand: permanent deprecate-stub package, dual-read old names forever. |
| OSS vs SaaS | Undecided → **white-label first.** Server URL is the *only* input; name/logo/urls from `GET /api/branding`; skills and widget served by the server; one build-time `DEFAULT_SERVER`. |
| Self-hosting boundary | Self-hosted = **API + Postgres** (+ **dashboard, deployed separately**). Thin clients — **npm CLI, extension, landing** — are published centrally and take `--server`. Self-hosting bootstrap is out of scope. |
| Notifications | In-app (widget badge + dashboard bell) is the free default. **Email is held** until a real trigger (first non-dev stakeholder outside the founder workspace, or comment→revisit p50 > 24 h); when un-held it is admin-configured per workspace and capped by the existing `EmailsPerMonth` entitlement. **Webhooks (§32) before email.** |
| Source-path manifest | **Never committed** — deterministic, regenerated locally (Phase 4). |
| API keys | Hash for lookup + encrypt for display at rest **now** (NEW-5; keeps the served "always re-viewable" promise); full scoped-key UI (§25) later. |
| AI and git | AI commits per project `CommitStyle`; **AI never pushes** — the human's CLI does. |

## What the code already has (corrections found in review)

Read before estimating anything — several items are *wiring*, not features.

- **Tenants/workspaces exist** — `Application/Services/Implementation/TenantService.cs`, `TenantStamp.cs`, EF query filters. Rule: every new table is born with `OwnerId`.
- **Plan entitlements exist** — `Domain/ValueObjects/PlanEntitlements.cs` (`MaxProjects`, `MaxSeats`, `MaxCommentsPerMonth`, `MaxEnvironments`, `MaxActiveInvites`, `EmailsPerMonth`, `RetentionDays`, `ExtensionEnabled`, …); `EntitlementService`; `MaxCommentsPerMonth` enforced at `CommentService.cs:89-100`. `EmailsPerMonth`/`RetentionDays`/`MaxEnvironments` are catalog-only (nothing consumes them yet).
- **Two environment concepts** — fixed `EnvironmentTag` enum (Local/Staging/Production, comment tagging + 3 activation bools on `Project`) **vs** tenant-defined `AppEnvironment` catalog + `ProjectAppUrl` rows (named stage → URL, used for extension origin matching).
- **Project list/create are plain `[Authorize]`** (`API/Controllers/Admin/ProjectsController.cs:11-30`) — stakeholders can already list/create their own projects; delete exists (`:51`).
- **API keys are one-per-user and stored in plaintext** — `User.ApiKey`, compared by SQL equality in `AuthService.cs:213`. `MeController` exposes `api-key` + `api-key/regenerate`.
- **Widget already reads `data-component-source`** (`Program.cs:309`, `capture.ts:246-254`, ancestor walk) as tier 1; React/Vue **dev-mode** fiber internals as tier 2 (`framework-source.ts`); null → AI greps.
- **Widget failure behaviour** — init network/5xx: silent; comment submit failure: toast; 401: login modal; project disabled/404/409: `disableSilently()` teardown.
- **`Role.QuickAccess` + `Invite` entity exist** (`Role.cs:28`, `Invite.cs`).
- **E2E suite exists** — `e2e/` (Playwright, `run-e2e.sh`, seeds two tenants/projects, optional AI layer).
- **`AiRule`, `StatusPresentation`, `PredefinedAction(Suggestion)`, `ExportImport`, `DemoService`, `ExtensionSite`** exist.
- **`pointer-init.md:12` claims projects self-register on first comment — the code says no** (`:25` contradicts it). Fix in §6.
- `pointer.js` = 118 KB raw on every page; snapdom (126 KB) is lazy-loaded only at capture time.

## Cross-cutting rules (every item)

1. **Dashboard column** — any DTO/endpoint change lists its client + UI task for the separate `pointer-dashboard` repo (**three** apps at parity: angular, react, vue). Clients are generated *here* (`npm run generate-clients` → `@moamen-ui/pointer-*`), not there. Those sections are the input to a **once-per-phase** `dashboard-agent` run that happens after the phase's backend is done and before its e2e; the `rebranding-agent` is invoked **eagerly** instead, whenever a tracked surface changes. Protocol: [`execution/01-OVERVIEW.md` § Cross-repo sync agents](execution/01-OVERVIEW.md).
2. **Additive-only EF migrations** while self-hosters exist (migrations run on boot).
3. **`OwnerId` + tenant filter on every new table**; agency "client" isolation is filter discipline, never `IgnoreQueryFilters`.
4. **White-label** — no brand string in CLI/widget output except what `/api/branding` returned.
5. **On-disk contract frozen** (NEW-1) — brand-neutral attribute names: `data-component-source`, `data-build-sha`, `data-snapshot-mask`.
6. **Docs are written with the feature, never after.** Every execution doc has a `## Docs` section
   naming the public page it creates or updates (or "none — internal only" and why), and that page is
   part of the item's definition of done. Written later, by someone re-reading code they have forgotten,
   a docs page costs several times as much and comes out wrong. Site shell:
   [`execution/R2-07-docs-site.md`](execution/R2-07-docs-site.md).
7. **AI never pushes.**

---

## Release plan (reviewed order)

### Release 1 — install without an AI (target 3 weeks; realistic 2.7–4.3)

| # | Item | Est. |
|---|---|---|
| R1.1 | **NEW-1 on-disk contract freeze** — decision doc listing every name written into customer repos (`.pointer/`, `POINTER_*` env vars, gitignore lines, `<pointer-feedback>` tag, `data-component-source`, skill dirs, `.mcp.json` entry, served URLs, `pointer_token`); all "keep as-is, dual-read after rebrand". | ½ d |
| R1.2 | **§1 `npx -y pointer-feedback init`** (below) incl. §6 doc fix and §21 events | 1–2 w |
| R1.3 | **§2** dashboard quick-start prints `npx -y pointer-feedback init --key ptr_…` | 1 d |
| R1.4 | **§3 `doctor` + §4 `GET /api/meta`** `{ version, minCliVersion }` | ≤ 1 d |
| R1.5 | **§41** allowed origins (enforce `ProjectAppUrl` on widget comment endpoints) + comment-POST rate limit | 1 d |
| R1.6 | **NEW-5 API-key hardening** — `ApiKey` table (`UserId`, `Prefix` 12 chars indexed, `Hash` SHA-256, `Scopes` default Full, `LastUsedAt`, `RevokedAt`, `OwnerId`); migration hashes + encrypts existing keys into it; `User.ApiKey` column **kept this release (additive rule), dropped in R2**; key string format unchanged; key stays re-viewable (AES-GCM for display, SHA-256 for lookup — see `execution/R1-06`). *Slip candidate → R2 week 1, before MCP.* | 2–3 d |
| R1.7 | **NEW-4a** harness runner + CI schedule — rewrite `run-e2e.sh` into a phase/tier runner (`--pr/--nightly/--only/--list`, today it parses one flag) and add `lib/report.mjs` so every phase writes `state/report.md` (today only the AI phase does), then schedule the suite. Everything downstream — tiers, the evidence artifact, the `e2e-tester-agent`'s targeted re-runs — depends on these two. | 2 d |
| R1.8 | **§50 tenant invitation (CRITICAL)** — a super admin invites a workspace owner **by email**; the invitee sets their own password from the link. Direct create-with-password stays as a secondary, explicitly-labelled path. | 2–3 d |
| R1.9 | **§51 project URLs per enabled environment** — every project URL attaches to a named environment from the workspace catalog, and only to one the workspace has **enabled**; the seeded `default` environment is retired in favour of `local` (old rows migrate). Create still takes one URL in one step, with the environment-URL table shown from the start. **Must land before or together with R1.2** — it changes `CreateProjectRequest`, which `npx pointer-feedback init` calls. | 3–4 d |
| R1.10 | **NEW-4d fully-local client loop** — generate the API clients against the **local** API (`generate-clients` already defaults to `:8090`), build them, publish to the local Verdaccio (NEW-4c) and install them into the three dashboard apps with `--no-save`, so a phase's endpoints reach the UI **before any deploy**. Unblocks the `dashboard-agent`, which otherwise waits on `publish-clients.yml` generating from production. The registry is hard-coded in two scripts today and `publishConfig` outranks `--registry`, so this is a real (small) code change, not just docs. Spec: [`execution/R1-10-local-client-loop.md`](execution/R1-10-local-client-loop.md) · Tests: [`testing/R1-10-tests.md`](testing/R1-10-tests.md). | 1 d |

### Release 2 — apply from anywhere

| # | Item | Est. |
|---|---|---|
| R2.0 | **NEW-4b** fresh-app init E2E (Vite + Angular + static) + white-label CI job (second server URL, custom branding, zero hard-coded name) | 2 d |
| R2.0b | **§52 mock-domain rebrand rehearsal** — the same stack served as **PickIt at pick-it.test**: widget, served skills, `/embed.js`, CLI output, landing page and **e-mails** all follow the new name *and* domain, then teardown restores both. No product code — `Pointer__PublicUrl` + DB branding + a Chromium resolver rule. **Run this and `verify-no-pointer.sh` green as the gate before executing the rebrand.** Folded into R2.0 (shares its branding helpers, teardown and CI job). | 1 d |
| R2.0c | **NEW-4c local npm registry** — a Verdaccio container in the nightly stack so the CLI's *published* behaviour is testable without publishing for real: `@latest` resolution, the `minCliVersion` upgrade hint against a genuinely older CLI (R1-04-06), and the post-rebrand deprecate-stub. Closes the last surface that still needed a public deploy to verify. Zero extra API restarts — it reuses the `Cli__MinVersion` window. | ½ d |
| R2.1 | **§7 apply core** as shared lib + `npx -y pointer-feedback apply` (`--plan` dry run from §8 rides along; `--tool claude\|cursor\|opencode` hand-off or print/clipboard) | 1–2 w |
| R2.2 | **§24 MCP server** (`npx -y pointer-feedback mcp`, same package): `list_comments`, `get_comment`, `mark_applied`, `reply`, `resolve_source`; `.mcp.json` documented as user-level config | 1–2 w |
| R2.3 | **NEW-2** served-file version stamp via existing `<POINTER_SERVER>` middleware; `doctor` compares; `pointer update` refreshes; `curl\|sh` warns | ½ d |
| R2.4 | **§10** in-app author notification on `applied` with commit link + 👍/👎 (👎 reopens) | 2–3 d |
| R2.5 | **§13** quick-access invites, passwordless magic link, **link-copy delivery** (email delivery held) | 2–3 d |
| R2.6 | **S6** secrets/payload advisory flag on comment card (`sk-`, `AKIA`, `ghp_`, long base64, `<script>`, `curl … \| sh`); never included in the apply payload | 2 h |
| R2.7 | **§48** public docs site — `landing/docs/` served at `<domain>/docs/` (no Caddy or build change: the bare domain's `try_files` already resolves subdirectories, as `landing/v2/` proves). Ships **first in Release 2** so every item's page has a home; the landing footer's Docs link starts following `/api/branding`'s `urls.docs`. Page *content* is not this item — each execution doc's `## Docs` section owns its own page, written by whoever builds the feature. | 1–2 d |

### Release 3 — apply quality in production

| # | Item | Est. |
|---|---|---|
| R3.1 | **Phase 4** Vite plugin + gitignored manifest, **opt-in flag first**, stamps `data-component-source` on every top-level host element a component returns; `hash(repo-relative path + export name)`; + **§31** `Comment.CommitSha`, `data-build-sha` on `<html>`, `(projectId, sha, firstSeenAt)` table, staging first | 1–2 w |
| R3.2 | **§45** design tokens (Tailwind config / CSS vars / `_variables.scss`) → `stack.json`; AI told "use existing tokens" | 1–2 d |
| R3.3 | **NEW-3** widget release engineering — immutable `pointer.js?v=<sha>`, short-TTL `stable`, SRI snippet, deploy smoke, **≤ 60 KB gz** budget in CI, CSP note (constructed `CSSStyleSheet` for nonce-strict hosts) | 3–4 d |
| R3.4 | **§33-lite** DOM-snapshot privacy — drop `value` of `input/textarea/select` in `shallowSnapshot`; `data-snapshot-mask` → `•••`; per-project "no text content" toggle | 2–3 d |
| R3.5 | **NEW-6** public privacy / self-host page (what is captured, retention, deletion, self-host = your Postgres) | ½ d |
| R3.6 | **§53 landing-page refresh** — rebuild the page's *argument* around the real thesis (an AI tool is only as good as the feedback it receives): the structured brief a comment carries as the second-screen proof, the loop ending at a commit URL, objection handling, and a token claim with **no invented figure**. PRD: [`prd/LANDING-PRD.md`](prd/LANDING-PRD.md). Also retires `landing/v2/`. | 3–4 d |

### Hold list (item → un-hold trigger)

§9 `apply --pr` → §42 done + a PR-based team · §11 batch-by-file → manifest live in prod · §14 @mentions → multiple repliers per thread · §15 template chips → widget polish sprint · §16 audit log → before first non-founder apply · §18 scoping rules → first Client-role commenter on prod · §19/§44 workspace fields + clients → first external workspace · §20 → **dissolved** into feature-attached wiring (email→`EmailsPerMonth`, retention job→`RetentionDays`, §35→`MaxEnvironments`) · §25 full scoped keys UI → first key in CI / committed config · §26 editor ext → Phase 4 stable + demand · §27 dedupe → queue noise reported · §28 before/after → **redesigned as manual attach**; stakeholders demand proof · §29 multi-element → hashes stable in prod · §30 changelog → §31 live · §32 webhooks → first "notify my tool" (**before email**) · §33 full (retention job, blur toggle, image delete UI) → first privacy-question customer · **project purge job** (physical delete of soft-deleted projects' rows + screenshot files, `RetentionDays`-driven; today delete is soft-only, `ProjectService.DeleteAsync`) → before NEW-6 can promise deletion · §35 preview environments (**days**, `ProjectAppUrl` rows) → §42 + §9 live · §36 board → ownership requested · §37 AI triage → comment volume · §38 QR / §39 bookmarklet → §13 adopted; CSP kills bookmarklets · §40 kill switch (flag → `disableSilently`) → first leaked key · §42 repo mapping → day before §9/§35/§43 · §43 cloud apply → CLI apply proven on 3+ repos (**a quarter**) · §46 nudge → vague-comment rate measured · §47 seeded demo (absorbs §23) → first signup without hand-holding · **full-app dashboard UX audit** (the React app (Angular/Vue retired 2026-09-15, branch `legacy/angular-vue`), every screen, `impeccable`-driven, incl. dark mode and RTL parity) → first external workspace, or the first user-reported usability complaint — until then each phase's dashboard-agent run does a UX pass scoped to the screens it touched · §49 badge → paid plans · email channel → trigger above.

**Cut:** §17 injection regex (→ S6) · §23 fake-output terminal demo (→ §47).

### Effort flags — weeks, not days
Full §1 init (even with Next/monorepo on the skill path) · §24 MCP · Phase 4 **Angular builder** and **Next RSC** plugins (2–4 w each, no DOM roots to stamp in RSC) · §9 `--pr` · §28 · §43 (a quarter).

---

## Item catalog (stable numbering)

### Phase 1 — Install & first comment (CLI)

1. **`npx -y pointer-feedback init`** — replaces `install.sh` + manual steps. Interactive, in order:
   1. Server URL: `--server` / `POINTER_SERVER` / existing `.pointer/config.json` / prompt; default = build-time `DEFAULT_SERVER`.
   2. `GET /api/branding` → `productName`, `urls.app` used in all output.
   3. API key → validate live (`login-with-api-key` + `me`), write `.pointer/credentials.env`, ensure `.gitignore`. Fail fast.
   4. Project → **list from existing `GET /api/admin/projects`** (plain `[Authorize]`), pick or create (`POST`). Key is generated, never typed.
   5. Environment (Local / Staging / Production).
   6. AI tool (Claude Code / Cursor / opencode / other) → skills directory.
   7. Install skills (server-served, pre-filled), detect stack, **inject the widget tag deterministically for Vite + static**; **Next/App-Router and monorepos are routed to the AI skill** with an explicit message.
   8. End with verification: served `<server>/check?project=…` page → widget loads + key works.
   - Non-interactive: `--key … --project … --server … --yes`.
2. **API key hand-off** — (a) dashboard quick-start shows `npx -y pointer-feedback init --key ptr_…` pre-filled; (b) *done (2026-09-16):* `npx -y pointer-feedback login` browser/device-code flow (`gh auth login`-style — `POST /api/auth/device/start|poll|approve|deny`), also offered from `init`'s first-run sign-in prompt.
3. **`npx -y pointer-feedback list | status | reply | doctor`** — replaces `.pointer/pointer.sh`. `doctor`: server reachable, key valid, project exists, widget tag present, skills installed and current, `.gitignore` correct.
4. **`GET /api/meta`** → `{ version, minCliVersion }`; CLI warns on mismatch.
5. **Widget empty-state onboarding** — first open with zero comments → 3-step tooltip. *(unscheduled, small)*
6. **One doc path** — dashboard quick-start is the source; fix `pointer-init.md:12` self-register claim; `install.sh`, landing link to it.

### Phase 2 — The apply loop (DX)

7. **`npx -y pointer-feedback apply`** — fetch queue, build prompt, hand off (`--tool`) or print/copy; `skill.md` becomes an implementation detail.
8. **`apply --plan`** — dry run, no edits.
9. **`apply --pr`** — branch → commits per `CommitStyle` → **push by the human's CLI** → PR body lists comments + screenshots. *(held)*
10. **Close the loop to the author** — on `applied`: in-app notification with commit/PR link + 👍/👎; 👎 reopens. Email only when the channel is un-held.
11. **Batch by file** — group queue items by resolved source file. *(after Phase 4)*
12. **Dev 👍/👎 on AI work** → dataset for comment phrasing quality. *(unscheduled)*

### Phase 3 — Stakeholder side (CX)

13. **Passwordless quick-access** — `Role.QuickAccess` + `Invite`: magic link → widget authed; link-copy delivery first.
14. **Threaded replies + @mention** from CLI/dashboard. *(held)*
15. **Comment templates as chips** (existing `PredefinedAction`s). *(held)*

### Phase 4 — Source-path resolution in production

Today: tier 1 `data-component-source` attr → tier 2 dev-mode fiber/Vue internals → tier 3 null → AI greps. Production strips tier 2, so every prod comment pays the grep.

- **Vite build plugin** (opt-in via `init --source-map` / plugin option): stamps `data-component-source="<hash>"` on **each top-level host element** a component returns (Fragments → all roots; component returning only components → no stamp → ancestor walk finds nearest). HOCs stamp the wrapped root. Compile-time, inert.
- `hash = hash(repo-relative path + component export name)` — deterministic; changes only on rename.
- **`.pointer/manifest.json`** `{ hash → path, componentName }` — **gitignored**, regenerated by every dev/build run and by `apply`/`doctor`.
- Stale hash → fall back to grep seeded by the component name; CLI reports "source renamed since capture".
- `data-build-sha` on `<html>` (build-time) → §31 deploy awareness.
- Default-on only after hash stability proven on 2–3 real apps. Angular builder / Next RSC: separate, effort-flagged.
- No-plugin fallbacks: `npx -y pointer-feedback map` (selector/text → file index); source maps second.

### Phase 5 — Trust, safety, business

16. Apply audit log. *(held)* · 17. **Cut** → S6 secrets/payload flag. · 18. Per-project scoping rules. *(held)* · 19. Workspace fields (billing owner, member roles, notification budget, integrations) on existing Tenant. *(held)* · 20. **Dissolved** into feature-attached entitlement wiring. · 21. **Usage events** `installed`, `first_comment`, `first_apply`, `apply_failed` — in R1.2. · 22. → NEW-3. · 23. **Cut** → §47.

### Phase 6 — AI-tool interface & editor

24. **MCP server** — same npm package; typed tools; key stays in the CLI process. · 25. Scoped API keys (full UI). *(held; NEW-5 covers storage)* · 26. Editor extension. *(held)*

### Phase 7 — Comment quality & after-apply

27. Dedupe · 28. Before/after (manual attach) · 29. Multi-element · 30. Auto-changelog · 31. **Deploy awareness** (`CommitSha`, `data-build-sha`, first-seen table; `applied → deployed`; staging first) — R3.1 · 32. Outbound webhooks (before email). *(all held except 31)*

### Phase 8 — Privacy & security

33. **Capture privacy** — screenshot: opt-in per comment, one-time notice on first check (account-synced), no masking by default, optional "blur inputs", deletable by author/admin/owner (blob deleted, "removed by X"), retention via `RetentionDays`. DOM snapshot: drop input values, `data-snapshot-mask`, per-project "no text" toggle. Server: cascade deletes, retention job. **R3.4 = snapshot half; rest held.**
34. → NEW-3 (SRI, CSP, pinning). · 40. Kill switch = per-project flag → `disableSilently()`. *(held)*

### Phase 9 — Review workflow & reach

35. Preview environments as `ProjectAppUrl` rows on an ephemeral `AppEnvironment`, comments tagged Staging; auto-create on PR open, deactivate on merge. **Days.** *(held: §42 + §9)* · 36. Board · 37. AI triage · 38. QR mobile · 39. Reviewer link. *(held)*

### Phase 10 — Abuse, multi-repo, cloud apply, agencies

41. **Allowed origins** (`ProjectAppUrl` enforced on widget comment endpoints, `Origin`/`Referer`) + comment-POST rate limit — R1.5. · 42. Project ↔ repo mapping (`repoUrl`, `rootPath`), monorepos. *(held)* · 43. Cloud apply via GitHub App, SaaS-side, on the §7 core. *(held; a quarter)* · 44. Agency: tenant → clients → projects; filter discipline. *(held)*

### Phase 11 — AI quality, growth, docs

45. **Design tokens → `stack.json`** — R3.2 · 46. Comment-quality nudge · 47. Seeded demo project (absorbs §23) · **48. Public docs site — R2.7, [`execution/R2-07-docs-site.md`](execution/R2-07-docs-site.md); no longer held, because per-item pages are written with their feature (cross-cutting rule 6)** · 49. "Powered by" badge. *(46, 47, 49 held)*

### Phase 12 — Tenant onboarding

50. **Tenant invitation by email (CRITICAL, R1.8).** Today the super admin's *primary* path is
    `POST /api/admin/tenants` with `{ Email, Password, DisplayName }` — `TenantService.CreateAsync`
    hashes a password the super admin chose, so someone else's credential is picked and transmitted
    out of band. **The invitation flow already exists and must become the primary path:**
    `CreateInviteRequest.CreateNewWorkspace` (super-admin only) mints a null-owner `Invite`, and
    `InviteService.AcceptCreateNewWorkspaceAsync:445-500` creates a self-owned tenant from the
    invitee's **own** password and emails a link with no plaintext credential. So R1-08 is
    *surface + harden + prefill*, not a new subsystem (2–3 d), and the work is: super-admin
    Tenants UI (invite form, pending list, resend, revoke, copy-link), force `MaxUses = 1` (today
    `null` = unlimited — one leaked link could mint N workspaces), require and lock `Email`, carry
    `PlanId`/`DisplayName` through to acceptance, and a pending-invites read model on
    `api/admin/tenants/invites*` under the super-admin policy (`GET /api/admin/invites` is
    `Policies.Admin`, and `InviteService.ListAsync:183-200` hides revoked/expired rows).
    **Direct creation is kept and stays byte-compatible** — the E2E seed depends on it — demoted by
    labelling and a usage event, never by a guard.
    **Delivery:** invite mail already ships; what gates it is the DB setting `email_enabled`
    (`EmailService.cs:22` — the `Email__Enabled` env var is read by nothing), so an instance with
    mail off falls back to "copy the invitation link", exactly like R2-05.
    **Already shipped out of this item** (`main`, `42e534e`): invitation join links now resolve
    `app_base_url` → `brand_url_app` → compiled default, with `app_base_url` writable at
    `PUT /api/admin/settings` and `effectiveAppBaseUrl` reported back. It was a live bug — the setting
    had no writer anywhere, so every self-hosted instance mailed staff and client invitations pointing
    at the SaaS host, where the code does not exist.
    Spec: [`execution/R1-08-tenant-invitation.md`](execution/R1-08-tenant-invitation.md) ·
    Tests: [`testing/R1-08-tests.md`](testing/R1-08-tests.md).

51. **Project URLs belong to enabled workspace environments (R1.9).** `Project.AppUrl` is a single
    nameless URL that silently lands on a seeded global environment called `default`
    (`ProjectService.SyncDefaultAppUrlAsync:557-590`) — an environment that exists only because the
    old one-field model needed somewhere to put its one URL. **Change:** every project URL attaches to
    a named environment from the tenant's own `AppEnvironment` catalog, and only to one the workspace
    has **enabled** (new `AppEnvironment.IsEnabled`, default true — deliberately not named `IsActive`,
    which already means something different one table away on `ProjectAppUrl`). `default` is retired
    (disabled + hidden, never deleted — it is a global row other tenants may hold URLs on) in favour of
    a seeded **`local`**, and every existing global-`default` URL row migrates to `local`. Creating a
    project still takes one URL in one step: the dialog renders the environment-URL table **from the
    start**, pre-populated with a single `local` row, exactly as if "add environment URL" had been
    clicked once. Disabling an environment **blocks** writes and origin resolution but never hides or
    deletes existing URLs.
    **Not** the fixed `EnvironmentTag` enum: comment tagging and the three
    `Project.IsActiveLocal/Staging/Production` bools are a separate concept and are untouched
    (`Project.cs:9-15` already warns about the confusion). This item is about *where the app is
    deployed*; that one is about *which environment a comment was left in*.
    **Sequencing:** `CreateProjectRequest` gains an optional `appEnvironmentId`, and
    `npx pointer-feedback init` creates projects (§1 / R1.2), so **R1.9 must land before or together
    with R1.2** — if R1.2 ships first, its create call, its `--json` project summary and its acceptance
    criteria must be revisited.
    **Deferred, deliberately:** `Project.AppUrl` survives this release (mirroring the `local` row)
    because `InviteService:525-526` refuses a quick-access invite without it and
    `ExtensionService:109-120` uses it as a legacy origin fallback — the column drop is an R2
    follow-up. `MaxEnvironments` stays display-only; enabling an environment does not consume it.
    Spec: [`execution/R1-09-project-environment-urls.md`](execution/R1-09-project-environment-urls.md) ·
    Tests: [`testing/R1-09-tests.md`](testing/R1-09-tests.md).

### Phase 13 — Rebrand readiness

52. **Mock-domain rebrand rehearsal (R2.0b).** The plan rests on a white-label rule ("no brand string
    in output except what `/api/branding` returned") and a rebrand is planned
    (`docs/rebranding-plan` → `docs/rebranding/REBRANDING-PLAN.md`), but today that rule is proven only
    at the **string** level: R2-00's white-label job swaps `productName` and greps for leaks. Nothing
    proves the product *works* under a different **domain** — which is exactly what a rebrand changes.
    **This item adds the domain dimension:** rehearse "we are now **PickIt** at **pick-it.test**" against
    the local stack — browse to the mock domain and get the local server, with the widget, the served
    skills and `/embed.js`, the CLI output, the landing page and the **e-mails** (local Mailpit, already
    in the harness) all showing the new name and linking to the new domain — then tear it down.
    **Why it costs almost nothing:** no product code changes. `Pointer__PublicUrl` is already read by
    `PointerUrlResolver.ResolvePublicUrl` (`API/Extensions/PointerUrlResolver.cs:15-20`) and is already
    the single source for `/embed.js`, the `<POINTER_SERVER>` rewrite and branding asset URLs; branding
    is already DB-writable; the widget, landing page and CLI already read `/api/branding`. The work is
    test wiring: a Chromium `--host-resolver-rules` flag (no `sudo`, no `/etc/hosts`), a `Host` header
    for node-side specs, and a mandatory teardown.
    **Decision: `pick-it.test`, not `.dev`** — `.dev` is HSTS-preloaded in Chrome, so plain HTTP is
    force-upgraded and cannot be served locally; `.test` is RFC 6761-reserved and never resolves
    publicly, so a stray request fails closed. The real brand domain may still be `.dev`; the rehearsal
    only needs the same shape. A `--mock-domain-tls` variant (Caddy `tls internal`) is **manual only** and
    exists to prove the `X-Forwarded-Proto` path emits `https://`.
    **The e-mail assertion is the sharpest one:** with only `urls.app` set and `app_base_url` never
    written, an invitation join link must still come out as `http://pick-it.test:8090/join?code=` —
    exercising the `app_base_url → brand_url_app → default` chain shipped in `42e534e`.
    **Release 2, folded into R2.0** (+1 d) rather than its own slot: it shares the branding helpers, the
    teardown pattern and the CI job with the white-label work that already lives there, and it depends on
    the mail harness (R1-07) and `init`/`doctor` (R1-02/R1-04) — all of which land before R2.0. Pulling it
    into Release 1 would mean building the white-label scaffolding twice.
    **Gate:** run this rehearsal **and** `docs/rebranding/verify-no-pointer.sh` green *before* executing
    the rebranding plan. Neither alone suffices — the grep cannot catch a runtime string served from the
    DB, and the rehearsal cannot catch a hard-coded name on a path no scenario visits. Feed the plan's
    `NAME_LOWER`/`DOMAIN` answers in as `E2E_MOCK_BRAND`/`E2E_MOCK_DOMAIN` so the rehearsal tests the
    *actual* chosen identity.
    Spec: [`execution/R2-00-e2e-fresh-app-whitelabel.md`](execution/R2-00-e2e-fresh-app-whitelabel.md) ·
    Tests: [`testing/R2-00-tests.md`](testing/R2-00-tests.md) (R2-00-09…13) · Harness:
    [`testing/00-HARNESS.md`](testing/00-HARNESS.md) §13.

### Phase 14 — Market what is actually true

53. **Landing-page refresh (R3.6).** The current page (`landing/index.html`) works — live data from
    three endpoints, en/ar + RTL, dark mode, `/api/branding` white-label, and it dogfoods the widget —
    but it *lists features* rather than making an argument. The argument is the product's thesis and the
    thing a comment-widget competitor cannot copy: **an AI coding tool is only as good as the feedback
    it receives.** A screenshot tool hands the agent a picture and a sentence; a Pointer comment hands it
    the selector, the CSS rules that actually win, the route, the viewport and the stakeholder's words —
    so the search is narrow and grounded instead of a repo-wide grep.
    **Deliberately last in Release 3**, because most of what would make the page strongest is not built
    yet: "starts at the exact file" needs §35/R3.1's manifest (today `sourcePath` is dev-mode only —
    `capture.ts:242-257`), "one command" needs the CLI (R1.2), MCP framing needs R2.2, and the
    docs-nav entry needs R2.7. Shipping the page earlier means shipping claims that are not true yet.
    **Honesty is the hard constraint.** The PRD splits every claim into *claimable now* vs *blocked on
    item X*, and forbids invented metrics: the `docs/AI_AGENT_TOKEN_OPTIMIZATION.md` baseline
    (551,817 tokens / 24 turns / ~90 s) is **measured**, but the optimised figures are **projections
    written before the fix shipped** — so no percentage goes on the page until the telemetry is re-run
    against the shipped path. Each item's `## Docs` section names the landing sentence it unblocks, so
    the page changes with the feature rather than drifting into fiction.
    Spec: [`execution/R3-06-landing-refresh.md`](execution/R3-06-landing-refresh.md) ·
    Tests: [`testing/R3-06-tests.md`](testing/R3-06-tests.md) · PRD: [`prd/LANDING-PRD.md`](prd/LANDING-PRD.md).

### NEW items from review

NEW-1 on-disk contract freeze · NEW-2 served-file version stamp + `pointer update` · NEW-3 widget release engineering · NEW-4a/b/c continuous verification · NEW-4d fully-local client loop (c = local npm registry, harness §6 level 3) · NEW-5 API-key hardening · NEW-6 public privacy/self-host page · S6 secrets/payload advisory flag.

---

## Verification (per release, high level)

- **R1**: fresh Vite + static app → `npx -y pointer-feedback init` with **no AI tool** → first comment ≤ 5 min; Next app → CLI routes to skill with the message; `doctor` green; `--yes` in CI; comment POST from a non-listed origin → 403, burst → 429; login with a pre-hardening key still works after migration; CLI output against a second server with different branding contains no "Pointer".
- **R2**: 2+ comments with `CommitStyle=Separate` and `Single` via `apply`; `--plan` makes no edits; Claude Code + one non-Anthropic tool list/apply/reply through MCP with `skill.md` reduced; `doctor` flags a stale skill copy; author sees in-app notification with commit link; invitee opens magic link and comments without a password; secret-shaped comment shows the advisory flag and the flag is absent from the apply payload.
- **R3**: same repo on two machines → identical `manifest.json`; rename a component → stale-hash warning + fallback; `?v=` immutable, `stable` short-TTL, SRI matches, bundle ≤ 60 KB gz; filled form → snapshot has no input values; `data-snapshot-mask` → `•••`; new build sha → `applied` → `deployed` once.

## Related

- [`meetings/`](meetings/) — review transcripts; [`meetings/11-final-decisions.md`](meetings/11-final-decisions.md) is the authoritative outcome.
- [`execution/`](execution/) — per-item implementer specs.
- `docs/rebranding-plan` branch — `docs/rebranding/REBRANDING-PLAN.md` (§3.1 branding is data; CLI gets one `NAME_LOWER` row).
- `API/wwwroot/install.sh`, `pointer-init.md`, `skill.md`, `pointer.sh` — the current flow this plan replaces.
- `docs/AI_AGENT_TOKEN_OPTIMIZATION.md` — token-cost rationale behind Phase 4.
