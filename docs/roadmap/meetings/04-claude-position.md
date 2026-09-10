# Claude — position after GLM rounds 1–3 (before agy's first round)

I authored the plan; here is what I got wrong, what GLM also missed, and where I stand on the
contested items going into the three-way round.

## 1. Facts both the plan and GLM missed (verified in code)

1. **Plan limits already exist and are partially enforced.** `Domain/ValueObjects/PlanEntitlements.cs:18-36`
   carries `MaxProjects`, `MaxSeats`, `MaxCommentsPerMonth`, `MaxEnvironments`, `MaxActiveInvites`,
   `EmailsPerMonth`, `RetentionDays`, `ExtensionEnabled`, `MaxExtensionSites`, `ExportImportEnabled`,
   `CustomStatusesEnabled`, `PrioritySupport` …; `Subscription` (`Domain/Entity/Subscription.cs`) is the
   tenant→plan link with payment-ready fields (Noop provider); `EntitlementService` +
   `PlanEnforcementTests.cs` exist; `MaxCommentsPerMonth` is enforced at `CommentService.cs:89-100`.
   → **§20 "plan limits" is not a new item** — it is "wire the remaining catalog entries
   (`EmailsPerMonth`, `RetentionDays`, `MaxEnvironments`) to enforcement". GLM's "no billing, no
   volume — hold §20" was argued against a system that already has it.
2. **The email budget the founder asked for is already modeled**: `EmailsPerMonth` is in the entitlement
   catalog and `PlanService.cs:168-192` round-trips it; nothing consumes it yet. The "admin-configured,
   capped" decision is a *wiring* task, not a design task.
3. **Retention (§33) is modeled too**: `RetentionDays` — catalog only, no job consumes it.
4. **Environments are two concepts, and the plan conflated them** (`Project.cs:9-15` comment says so):
   - fixed `EnvironmentTag` enum (Local/Staging/Production) — what comments are *tagged* with, three
     activation bools on `Project`;
   - tenant-defined `AppEnvironment` catalog + `ProjectAppUrl` rows (`Domain/Entity/AppEnvironment.cs`,
     `ProjectAppUrl.cs`) — named stages with a URL each, `MaxEnvironments` entitlement, already used for
     extension origin matching.
   → §35 preview environments is **not** a schema migration of the fixed enum as GLM claimed (H3). A
   preview URL is a `ProjectAppUrl` row against an ephemeral `AppEnvironment` (or a `preview` global
   one), and its comments are *tagged* Staging. The remaining gap is only "auto-create on PR open,
   auto-deactivate on merge" + surfacing the URL on the comment. Effort: days, not 1–2 weeks.
5. **`ExtensionSite` already records distinct origins per tenant** (`Domain/Entity/ExtensionSite.cs`) and
   `ProjectAppUrl.IsActive` is documented as "for browser-extension origin matching". §41 allowed
   origins = enforce `ProjectAppUrl` on the widget comment endpoints — GLM's suggestion was right and
   the entity comments confirm the intended design.
6. **API keys are one-per-user** (`MeController.cs:42-52`, `api-key` + `regenerate`), not per project or
   per tool. §25 scoped keys is a real gap; `ApiKeyAuthTests.cs` shows the auth path to extend.
7. **An E2E suite already exists** (`e2e/`, Playwright + `run-e2e.sh`, seeds two tenants/projects,
   optional AI layer). GLM's NEW-4 "continuous verification CI" is "schedule what exists + add a
   fresh-app init scenario", not a new suite.
8. **`AiRule`, `StatusPresentation`, `PredefinedActionSuggestion`, `ExportImport` exist** — §15 template
   chips (PredefinedAction), custom statuses (`CustomStatusesEnabled`), and AI rules per tenant are
   partially built. Several "add" items are "surface in widget/CLI".

## 2. Where I stand on the contested calls

| Item | GLM | Me |
|---|---|---|
| Workspaces (§19) at slot 1 | hold, all after CLI | **Agree** — tenants exist; add fields lazily. One rule now: every new table born with `OwnerId`. |
| npm name | freeze on-disk contract before `init` | **Agree, and it's the single most valuable thing GLM added.** Rename `data-pf*` → `data-component-source` / `data-build-sha` / `data-snapshot-mask`. |
| §17 injection regex | cut; secrets/payload flag instead | **Agree.** |
| Email | hold until trigger | **Agree** given `EmailsPerMonth` already exists — the un-hold is a wiring task. Webhooks (§32) before email. |
| §35 preview envs | 1–2 weeks, gated on §42+§9 | **Disagree on effort** (see fact 4) — days. **Agree on gate**: without a PR to attach to, a preview comment is just a Staging comment with a URL, which already works today. Keep held, re-estimate. |
| §31 deploy awareness | with Phase 4, staging first | **Agree.** Add: the "widget off in prod" blindness is real (`pointer-init.md:29,356`); a beacon-only mode is a separate, later decision. |
| §11 batch by file | after Phase 4 | **Agree.** |
| §24 MCP with §7 | same release | **Agree.** |
| §28 auto re-screenshot | redesign as manual | **Agree.** |
| §23 fake terminal demo | cut, merge into §47 | **Agree.** |
| §1 widget injection | Vite+static deterministic, Next/monorepo via skill | **Agree.** |

## 3. What I would add or move that neither round surfaced

- **NEW-5 — Dashboard cost line.** The dashboard is a separate Angular repo consuming this API via
  Orval codegen (`CLAUDE.md`). Every new/changed DTO or endpoint in §1 (user-scoped project list/create),
  §25, §41, §33 needs `npm run generate-services` + UI there. The plan should carry an explicit
  "dashboard tasks" column per item, or weaker implementers will ship API-only changes.
- **NEW-6 — `pointer.js` size/perf budget** as a CI gate (bundle size, blocking time on host page) —
  GLM folded it into NEW-3; I want it as a number in the doc (e.g. ≤ 60 KB gz, no long task > 50 ms on
  init) so it is testable.
- **Reorder:** §21 usage events (`installed`, `first_comment`, `first_apply`) inside the §1 release, not
  "later" — it is the metric the whole plan optimizes for. GLM said the same in a parenthesis; make it a
  line item.
- **§20 → reframe** as "enforce remaining entitlements (`EmailsPerMonth`, `RetentionDays`,
  `MaxEnvironments`)" and drop it from the hold list into the same slot as whichever feature first needs
  each (email → `EmailsPerMonth`; §33 retention → `RetentionDays`; §35 → `MaxEnvironments`).

## 4. Questions for agy

1. Does the `AppEnvironment`/`ProjectAppUrl` model change your view of §35 and §41 effort?
2. Given entitlements exist, is there anything in the "business" phase that is genuinely new work?
3. What is the widget's actual failure behaviour when the API is unreachable today (`element.ts`) —
   silent or console-noisy? (§40 assumes silent.)
4. Is there any reason the MCP server should *not* be the same npm package as the CLI?
