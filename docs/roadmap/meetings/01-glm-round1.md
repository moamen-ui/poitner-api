
> build · glm-5.2

→ Read API/Program.cs
→ Read web-component/src/capture.ts
→ Read API/wwwroot/skill.md
→ Read docs/roadmap/DX-UX-CX-PLAN.md
→ Read Domain/Entity/Comment.cs
→ Read API/wwwroot/install.sh
→ Read Domain/Entity/Project.cs
→ Read API/wwwroot/pointer-init.md
✱ Glob "API/**/RateLimiting*.cs" 1 match
✱ Grep "QuickAccess" in Domain · 4 matches
✱ Grep "branding|Branding" in API · 55 matches
→ Read API/Extensions/RateLimitingExtensions.cs
✱ Grep "EnsureAsync" in . · 39 matches
→ Read Application/Services/Interfaces/IProjectService.cs [limit=70, offset=25]
✱ Grep "class (Tenant|Workspace)" in . · 8 matches
✱ Grep "\[Route\(|\[HttpGet|HttpPost\(" in API/Controllers · 93 matches
# Review: DX-UX-CX-PLAN.md

## 1. FACTUAL ISSUES

1. **§19 / order-slot 1 assumes workspaces don't exist. They do.** `TenantService` (Application/Services/Implementation/TenantService.cs:12), tenant stamping/query filters (Application/Common/TenantStamp.cs, Tests/TenantQueryFilterTests.cs), workspace admin ownership (Tests/WorkspaceAdminOwnershipTests.cs:22), and skill.md's own Workspace-tier AI rules (skill.md:117). "Introduce while the schema is cheap to change" describes shipped code. The real remaining work is client-grouping (§44) + billing hooks — a much smaller slot-1.
2. **§1.4 "list the user's projects from the API" — endpoint doesn't exist.** The only project list is admin-gated (`api/admin/projects`, API/Controllers/Admin/ProjectsController.cs:15-20). Phase 1 needs a new user-scoped `GET /api/projects` (and non-dashboard project *creation* — `EnsureAsync` is strictly resolve-only, IProjectService.cs:25-26, CreateProjectValidator.cs:13). Unaccounted server work.
3. **pointer-init.md — the file §1.7 builds on — contradicts the code.** It claims projects self-register on first comment (API/wwwroot/pointer-init.md:12), then says the opposite (pointer-init.md:25), and the code is strict (above). §6 "one doc path" must fix this or the CLI inherits a lie.
4. **Phase 4's "zero widget changes" glosses an attr-name split.** The deployed ecosystem hardcodes `source-attr="data-component-source"` — embed.js (API/Program.cs:309) and every snippet in pointer-init.md:48,87,348-355. The plugin stamps `data-pf`. Configurable attr makes it *mechanically* zero-change (capture.ts:247-254), but either the plugin stamps the existing name or every embed path changes. Also: pointer-init.md:348-355 already tells users to hand-write this build plugin — Phase 4 is "productize what users are currently told to build", which the plan never says.
5. **§41's threat model is overstated.** Comment POST requires a JWT and signup is admin-approved (pointer-init.md:346-347) — "anyone can post" is really "any approved account can flood at unlimited rate". Rate limiting indeed covers only signup/demo/plans (RateLimitingExtensions.cs:30-59) — the gap is real, the severity wording isn't. Note also: origin config half-exists as `ProjectAppUrl` rows + the widget-status gate (IProjectService.cs:84-94) — extend it, don't build a parallel setting.
6. **§13 misnames and undersells what exists**: `IsQuickAccess` isn't a thing — it's `Role.QuickAccess` (Domain/Entity/Role.cs:28), with an `Invite` entity already targeting projects (Domain/Entity/Invite.cs:31). Same pattern elsewhere: demo infra (DemoService, `demo` limiter), export/import (ExportImportController), public `/api/plans`, extension endpoints — several plan items are extensions of unacknowledged code, so their cost estimates are wrong in both directions.

## 2. WRONG OR RISKY DECISIONS

1. **Workspaces at slot 1.** With zero customers the schema is *always* cheap to change — the argument only bites once data exists. The deadline is "before the first paying customer," not "now." Alternative: slot 1 goes to the CLI funnel; workspace extension waits for the first agency signal.
2. **Keep `pointer*` for the npm CLI.** The package name is the one thing a rebrand can't cleanly fix later — every quick-start command ever printed says `npx pointer`. The CLI is the moment the placeholder name becomes load-bearing. Alternative: land the final name (the rebrand plan already exists on a branch) *before* the CLI ships, or ship `npx @scope/cli` where scope is rename-tolerant.
3. **§17 server-side injection pattern flagging is security theater.** Regex-matching "ignore previous"/"curl"/"rm" is trivially bypassed and mostly yields false positives; the real defense is already structural (skill.md:62-104: data-vs-instruction, scope limits, no-push, human review). Alternative: audit log (§16) + a red-team corpus of injection comments run against the skill in CI. Drop the scanner.
4. **§9 (`apply --pr`) before §10 (author notification).** PRs drag in §42 repo mapping, push rights, host auth. The stakeholder win — "fixed, here's the link" — needs only `CommitUrl` (Comment.cs:21) + a notification, both nearly free today. Reverse them.
5. **§24 (MCP) at order-slot 5 while being called "the biggest DX multiplier."** Self-contradiction. It's the natural next step after the `apply` core (§7) and it *shrinks* Phase 1 (§1.6 skills-directory chore becomes moot for the big four tools). Build `apply` as a lib, expose CLI + MCP off it in the same month.
6. **§22 email with caps + budget machinery now.** Email is the highest-maintenance channel (deliverability, SPF/DKIM, bounces) and there's nobody to notify yet. In-app (free) then webhooks (§32, cost stays with customer) — email when a customer asks twice.
7. **§11 batch-by-file sits in Phase 2 but depends on Phase 4.** Today `sourcePath` is null in prod (tier 2 is dev-only, capture.ts:255-257), so batching by file can't work where most comments happen. Move it after the manifest.

## 3. REPRIORITIZED ORDER (first 10)

1. **§41** — one day of work, closes the only open abuse surface, builds on existing `ProjectAppUrl` rows.
2. **§1** — `npx pointer init` + the missing user-scoped project list/create endpoints + `/check`: the funnel *is* the metric; also forces the white-label seams (branding-fetched identity, server as sole input) to become enforced reality.
3. **§2 (pre-filled `--key` command only)** — 90% of the login UX for 5% of the work; park the device-code flow.
4. **§3 + §4** — `doctor` + `/api/meta` inside the CLI release: turns future support into self-serve, detects stale installs.
5. **§7** — `apply` as a shared core lib with a CLI entry: prerequisite for §24 and §43; makes the loop scriptable and token-measurable.
6. **§24** — MCP server on that core: the plan's own biggest multiplier; ends skill-copy drift across four tools at once.
7. **Phase 4 (Vite plugin + manifest, stamping `data-component-source`)** — apply quality where comments actually happen (staging/prod), which today pay the grep on every comment.
8. **§45** — design tokens into `stack.json`: rides the same detection pass as init; near-free apply-quality gain.
9. **§10 (in-app half)** — commenter retention with zero new infra; the email half waits.
10. **§13** — quick-access invites: `Role.QuickAccess` + `Invite` exist (Role.cs:28, Invite.cs:31) — cheapest path to non-dev commenters, i.e. "the whole team lives in the loop."

(Bundle into #2: `installed`/`first_comment` events from §21 — you're optimizing time-to-first-comment; measure it from day one.)

## 4. ADD

1. **Skill-copy freshness.** Every `install.sh` run leaves a frozen skill.md in a repo (install.sh:18-21) with no version check. Version-stamp served skills, `doctor` flags stale, `pointer update` refreshes. §24 only fixes drift for MCP users; the plan keeps supporting the rest.
2. **Widget release engineering as one item.** Merge §22+§34 into a policy: immutable `pointer.js?v=<sha>` + short-TTL `stable` + SRI snippet + deploy smoke + a KB/ms budget — today every deploy reaches every customer site instantly (Program.cs:249-254) and nothing measures widget weight inside host apps.
3. **Continuous fresh-app E2E.** Scheduled init→comment→apply on a generated Vite *and* Angular app. The 5-minute promise is currently a one-off verification (line 146); it must be a test.
4. **White-label CI job.** Boot the stack against a second server URL with custom branding; assert zero hard-coded "Pointer" in CLI/widget output. The plan lists this as one-off verification (line 149) — as a hedge on OSS-vs-SaaS it's only real if continuous.
5. **Public privacy/self-host answer.** Screenshots + DOM snapshots of customer apps is the first procurement question for agencies. §33 builds the mechanics; nobody writes the artifact buyers read.

## 5. REMOVE OR HOLD (6+ months)

- **§20 plan limits** — no billing, no volume; enforcement code rots and throttles your first users.
- **§37 AI triage** — cost and accuracy risk with no volume to triage.
- **§38 QR + §39 bookmarklet** — QR is gated on §13 anyway; bookmarklets die under CSP; the extension already covers §39's use case.
- **§26 editor extensions** — MCP already delivers comments into the editor via the AI tool; gutter markers are polish.
- **§28 auto re-screenshot after deploy** — the most failure-prone item in the plan (auth on prod, selector drift, deploy detection). Redesign as manual attach, later.
- **§30/§31/§35 deploy cluster** — gated on deploy volume you don't have; §10-at-commit-time covers 90% of the need.
- **§23 fake-output demo** — fake terminal output erodes trust exactly where you're selling the loop; merge into §47's real seeded demo (DemoService already exists).
- Hold as-is: §2 device-code half, §14 @mentions, §29, §36, §43 (strategic, but only after CLI apply is proven), §44.

## 6. THE ONE THING

**§1 — `npx pointer init`, shipping with the §2 pre-filled `--key` command.** The stated goal (first comment ≤ 5 min, no AI required) is purely an install-funnel problem, and every other item's value multiplies only after install completes. It's also where white-label stops being a decision in a table and becomes enforced behavior — your cheapest hedge while OSS-vs-SaaS stays undecided. Do §41 in the same month as a one-day hygiene fix; the month itself belongs to init.
