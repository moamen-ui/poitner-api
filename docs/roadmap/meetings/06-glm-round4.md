
> build · glm-5.2

→ Read docs/roadmap/meetings/04-claude-position.md
→ Read docs/roadmap/meetings/05-agy-round1.md
→ Read Domain/ValueObjects/PlanEntitlements.cs
→ Read API/Controllers/Admin/ProjectsController.cs [limit=70]
→ Read Application/Services/Implementation/AuthService.cs [limit=60, offset=180]
→ Read Application/Services/Implementation/CommentService.cs [limit=45, offset=75]
→ Read Domain/Entity/ProjectAppUrl.cs
→ Read Domain/Entity/AppEnvironment.cs
→ Read web-component/src/element.ts

$ ls -la API/wwwroot/ API/wwwroot/vendor/ 2>/dev/null; echo "---"; ls e2e/ 2>/dev/null | head -20; echo "---"; ls Domain/Entity/ | head -40
API/wwwroot/:
total 416
drwxr-xr-x@ 12 momen  staff     384 Sep  9 18:02 .
drwxr-xr-x@ 16 momen  staff     512 Aug 30 19:24 ..
-rw-r--r--@  1 momen  staff       0 Jun 23 14:45 .gitkeep
drwxr-xr-x@  5 momen  staff     160 Jun 25 14:30 admin
-rw-r--r--@  1 momen  staff    4920 Sep  9 11:11 install.sh
-rw-r--r--@  1 momen  staff   19663 Sep  6 17:24 pointer-init.md
-rw-r--r--@  1 momen  staff   19491 Sep  9 12:42 pointer.css
-rw-r--r--@  1 momen  staff  118765 Sep  9 18:02 pointer.js
-rwxr-xr-x@  1 momen  staff    8464 Sep  9 18:02 pointer.sh
-rw-r--r--@  1 momen  staff   31020 Sep  9 18:02 skill.md
drwxr-xr-x@  4 momen  staff     128 Jun 25 15:09 uploads
drwxr-xr-x@  3 momen  staff      96 Jun 24 16:04 vendor

API/wwwroot/vendor/:
total 248
drwxr-xr-x@  3 momen  staff      96 Jun 24 16:04 .
drwxr-xr-x@ 12 momen  staff     384 Sep  9 18:02 ..
-rw-r--r--@  1 momen  staff  126551 Jun 24 16:04 snapdom.js
---
ai
fixture-app
node_modules
package-lock.json
package.json
playwright.config.ts
README.md
run-e2e.sh
scripts
state
test-results
widget
---
AiRule.cs
AppEnvironment.cs
AppSetting.cs
BaseEntity.cs
Comment.cs
ExtensionSite.cs
Invite.cs
PageContextSnapshot.cs
Plan.cs
PredefinedAction.cs
PredefinedActionSuggestion.cs
Project.cs
ProjectAppUrl.cs
Reply.cs
Role.cs
RoleTenantOverride.cs
StatusPresentation.cs
Subscription.cs
User.cs

# 1. CORRECTIONS TO YOURSELF

1. **agy is right; my factual #2 was WRONG. Concede.** API/Controllers/Admin/ProjectsController.cs:11-17 is plain `[Authorize]` with an explicit comment: "Broadened… stakeholders may now list/create their own projects." I read the `/api/admin/` route prefix as an auth gate. Order change: §1 drops "new user-scoped project endpoints" — init gets cheaper. Side effect: any personal key lists/creates projects tenant-wide, which *raises* leaked-key blast radius (feeds 2c below).
2. **Claude is right on entitlements. Concede.** PlanEntitlements.cs:18-36 carries MaxProjects/MaxSeats/MaxCommentsPerMonth/EmailsPerMonth/RetentionDays/MaxEnvironments; enforcement is live (CommentService.cs:89-100, `_entitlements.CheckCountAsync` on the month count). My "§20 hold — enforcement code rots" argued against a shipped system. Hold-list change: **§20 leaves the hold list**, reframed as "wire the display-only entries when their feature lands" (email→EmailsPerMonth, retention job→RetentionDays, §35→MaxEnvironments). Not standalone work, not top-15.
3. **Claude is right on the two-environment model. Concede on effort, hold on gate.** AppEnvironment.cs (tenant-defined named stages) + ProjectAppUrl.cs:16-19 (explicitly "a different, fixed-enum concept" from comment tagging) exist — a preview URL is a ProjectAppUrl row against an ephemeral AppEnvironment, comments tagged Staging. My H3 "1-2w schema migration" is withdrawn. But the gate stands (Claude agrees): without §42+§9 there's no PR to attach to — a preview comment is a Staging comment with a captured URL, which works today. Hold trigger unchanged; re-estimate: days.

# 2. AGY'S DISAGREEMENTS

1. **(a) §41 out of slot 1 — concede the ordering, hold the release.** The JWT point (which I made myself in round 1, factual #5) does deflate urgency: abuse requires an admin-approved account. §41 was slot 1 because it's ~a day, not because it's dire. Swap accepted — but it stays in release 1 alongside §1, because the funnel (pre-filled keys, invites, extension) widens the commenter population the same week. Moving a day-item later saves nothing.
2. **(b) Hold Phase 4 entirely — hold, with one hedge.** agy's failure model is wrong: a Vite transform injecting a `data-*` attribute is compile-time and inert — it can't "break the render tree." Fragments render no host element, so there's nothing to stamp — a *miss*, and the ancestor walk (capture.ts:246-254) plus tier-2/3 fallbacks degrade to today's behavior; HOCs still stamp the wrapped component's own root. The genuinely hard cases are Angular host bindings and Next hydration mismatch — already effort-flagged. The problem Phase 4 solves (every prod comment pays the grep) is documented, not hypothetical. Hedge conceded: ship the plugin **opt-in behind a dev flag first** (the pattern pointer-init.md:348-355 already describes), default-on only after hash-stability proof on real apps.
3. **(c) §25 before §24 — split. Concede the storage fix must precede MCP; hold full scoped keys.** Verified: keys are stored and compared in plaintext (AuthService.cs:213, `u.ApiKey == key` in SQL) — that's a days-sized fix (hash + keep the existing `ptr_` prefix as index material) and it enters the MCP release as NEW-5. But full §25 (multi-key entity, scopes, dashboard UI, migration off one-key-per-user at MeController.cs:42-52) is 1+ week and MCP is *strictly safer than the skill.md path it replaces* — today the same root key already rides in `.pointer/credentials.env` and shell snippets; MCP keeps it inside the CLI process. Blocking the biggest DX multiplier on a full key system trades a real delay for a marginal delta vs status quo. Mitigations now: document `.mcp.json` as user-level config, not repo-committed; full §25 fast-follow on the existing trigger.
4. **(d) §18 into top 15 — hold.** Enforcement code with zero current users; "Client can't target /admin" is speculative until a real client relationship defines what scoped means. The agency model it protects (§44) is itself held. First-customer trust comes from §33-lite, audit, and key hardening — not path rules. Trigger unchanged.

# 3. AGY'S "WHAT BOTH MISSED" — RANKED

1. **Dashboard repo blast radius — REAL, adopt.** Separate Angular repo + Orval regen per DTO change (AGENTS.md). Claude's NEW-5 "dashboard tasks column" is the right form. Changes cost accounting, not order.
2. **API key storage — REAL, adopt.** Verified plaintext (AuthService.cs:213). Becomes NEW-5 above; also covers agy's Q1 prefix/index concern.
3. **Tenant query-filter leaks — REAL as a §44 design constraint.** Filters are tenant-scoped, not client-scoped; client isolation inside a tenant needs its own discipline (the codebase's IgnoreQueryFilters comments show the culture exists — e.g. IProjectService.cs:78-82). A rule attached to §44, not current work.
4. **CSP nonces — REAL but small, fold into NEW-3.** Verified: runtime-created `<link>` into shadow root (element.ts:186-191) + lazy snapdom `<script>` (capture.ts:24-40); neither inherits host nonces. Allowlist CSPs are already documented (pointer-init.md:170-189); nonce-strict hosts need constructed CSSStyleSheet (not subject to style-src). A line item, not a workstream.
5. **EF migration backward-compat — REAL, one policy line.** Migrations already run on boot (Program.cs:123-129); the item is "additive-only while self-hosters exist." Near-zero effort.
6. **Widget bundle size — half-real.** Verified pointer.js = 118,765 B raw on every page (the real budget concern, Claude's ≤60 KB gz number is right); but snapdom (126,551 B vendored) is lazy-loaded only at capture (capture.ts:24-40) — agy's "loads continuously" attribution is wrong. Fold into NEW-3/NEW-6.
7. **VM limits for §32/§43 — generic.** §43 is SaaS-side by design (won't run on self-hosted VMs); §32 webhooks are cheap outbound. Doesn't change the order.

# 4. AGY'S 5 QUESTIONS

1. **Key storage:** Plaintext. `User.ApiKey` compared by SQL equality (AuthService.cs:213); no hash, no index needed while plaintext. Keys already carry a `ptr_` prefix (install.sh:56). Hashing requires id-bearing format for lookup.
2. **Fragments/HOCs:** Fragments → no host element, nothing stamped; ancestor walk (capture.ts:246-254) falls to the next stamped ancestor or existing fallbacks — miss, not break. HOCs → wrapped component's root still stamped; injection is compile-time, inert. Hard cases remain Angular host bindings and Next SSR hydration.
3. **Dashboard compat:** Unknown in detail (separate repo, unread here). Mechanism exists (Orval regen from live Swagger, AGENTS.md); the gap is a deploy-ordering/compat policy for additive-vs-breaking DTO changes.
4. **CSP nonces:** Widget injects its stylesheet `<link>` and snapdom `<script>` at runtime (element.ts:186-191, capture.ts:27-38) — no nonce inheritance. Allowlist hosts: solved by docs (pointer-init.md:170-189). Nonce-strict hosts: currently broken; constructed-stylesheet fixes CSS, scripts still require host allowlisting. No full nonce-only solution without host cooperation.
5. **Cloud apply on a single VM:** Unknown — no runner code exists. Known boundary: prod compose is Postgres+API+Caddy (AGENTS.md) and §43 is planned SaaS-side, so the self-hosted VM question may be moot by design.

# 5. REVISED TOP 15

1. **NEW-1** — on-disk contract freeze (decision doc; nothing writes to customer repos before this).
2. **§1** — `npx pointer init`; Vite+static deterministic, Next/monorepo via skill; existing `[Authorize]` project list/create (no new endpoints — corrected); absorbs §6 doc fix, §21-lite events, scheduling existing e2e/ suite + fresh-app init scenario.
3. **§2** — pre-filled `--key` command only.
4. **§3+§4** — `doctor` + `/api/meta` in the CLI release.
5. **§41** — origins + comment rate limit; same release as §1 (ordering conceded, release held).
6. **§7** — apply core lib + CLI entry (`--plan` rides along).
7. **NEW-5** — key hardening: hash `ApiKey` + prefix-indexed lookup; prerequisite-of-record for keys in agent configs.
8. **§24** — MCP, same release as §7; documented as user-level config, not repo-committed.
9. **NEW-2** — served-file version stamp + `pointer update`.
10. **Phase 4** — Vite plugin + manifest stamping `data-component-source`; **opt-in dev-flag first**, default-on after hash-stability proof.
11. **§45** — design tokens into `stack.json`.
12. **§10** — in-app author notification with commit link.
13. **NEW-3** — widget release engineering: immutable + `stable` + SRI, ≤60 KB gz budget, CSP constructed-stylesheet note.
14. **§33-lite** — DOM snapshot privacy (drop input values; `data-snapshot-mask`).
15. **§13** — quick-access invites (link-copy delivery; email wiring exists as `EmailsPerMonth`).

Hold-list deltas: §20 removed (reframed as feature-attached wiring); §35 re-estimated days, gate unchanged; §25 trigger now "first key outside a laptop, or first CI-committed config"; effort-flag on §35 withdrawn; §18 stays held.
