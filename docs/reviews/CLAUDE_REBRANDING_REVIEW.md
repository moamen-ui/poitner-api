# Independent Technical Review: `docs/REBRANDING_MASTER_PLAN.md`

**Reviewer:** Claude (Sonnet), acting as an independent systems-architecture reviewer
**Method:** The plan was cross-checked line-by-line against the actual repository content (not
taken at face value) — `Program.cs`, `docker-compose*.yml`, `Caddyfile`, `install.sh`,
`AdminSeeder.cs`, the Branding subsystem, `Tests/*.cs`, `web-component/src/*`, `extension/src/*`,
`.github/workflows/*`, `justfile`, `.env*.example`, and the client packages. Every finding below is
backed by a concrete file reference from this repo, not speculation.

**Verdict: The plan is well-organized and mostly safe for the parts it covers, but it is not
complete or accurate enough to hand to an autonomous agent unsupervised.** It contains one
architecture-level blind spot serious enough to cause the agent to fight the application's own
design (see §1.1), several fabricated/incorrect specifics that don't match the real codebase, a
broken verification script (see §2.1 — the safety net does not actually catch the thing it claims
to catch), and a real cross-repo sequencing bug matching the exact risk the prompt asked me to
check (see §3.2).

---

## 1. Completeness

### 1.1 CRITICAL: The plan is entirely unaware of the runtime Branding system

This is the single biggest gap. The codebase already has a **live, database-backed, runtime
white-labeling system** that the plan never mentions:

- `Application/Services/Interfaces/IBrandingService.cs`, `Application/Services/Implementation/BrandingService.cs`
- A `BrandingController` (`GET /api/branding`, super-admin-editable) with `BrandingResponse` /
  `BrandingUrlsResponse` / `BrandingAssetsResponse` / `BrandingExtensionResponse` DTOs
- A super-admin **"Branding" page** in the dashboard (confirmed via captured UI snapshots) that
  edits the product name/URLs/logo/colors, persisted as `AppSetting` rows
- `extension/REBRAND.md` (already in the repo!) explicitly documents that "Most of the product
  brand is **runtime-driven** from the super-admin Branding page... and needs **no extension
  change**" — the popup fetches `/api/branding` at runtime, and the widget already reflects the
  branded name
- `landing/index.html:795-810` fetches branding and **rewrites hardcoded `app.pointer.moamen.work`
  links at runtime** (`document.querySelectorAll('a[href^="https://app.pointer.moamen.work"]')...`)
- `orval.config.ts` even lists `Branding` as one of the generated-client API tags

**Why this matters:** The master plan treats "rebranding" purely as a static, source-level
find-and-replace exercise (namespaces, file names, hardcoded strings). It never tells the executing
agent that a live white-labeling feature already exists and that most *display-facing* copy
(product name shown to end users, widget UI text, popup heading) is **already** parametrized and
should **not** be hardcoded again. An agent following this plan literally would either:
1. Waste enormous effort hunting for and hardcoding "PickIt" strings across dashboard UI
   components that are already dynamically sourced from `/api/branding`, or
2. Not realize the two concerns are different: **(a)** the *technical/source* rebrand (repo name,
   C# namespaces, npm package names, file names — what this plan actually covers) vs. **(b)** the
   *product* rebrand (the display name a tenant sees, which is a run-time admin-configurable value
   that already has its own settings/seed path completely separate from source code).

The plan needs an explicit new section distinguishing these two rebrand types and telling the
agent which BrandingService/AppSetting default values (the *seed* defaults an operator sees before
ever visiting the Branding admin page) need updating vs. which UI strings are genuinely
hardcoded and need literal replacement.

### 1.2 Fabricated / inaccurate specifics (the plan cites things that don't exist)

- **Phase 2 header says "Backend .NET 9 Solution".** The actual `TargetFramework` in every
  `.csproj` is `net8.0` (`API/Pointer.API.csproj:4`), and `Dockerfile` pulls
  `mcr.microsoft.com/dotnet/sdk:8.0` / `aspnet:8.0`. This directly contradicts `AGENTS.md` in the
  same repo, which correctly says ".NET 8". An agent trusting the plan's header over the repo's own
  `AGENTS.md` could be misled into thinking a framework upgrade is in scope.
- **Phase 2.4.6 claims:** *"Default project names / demo accounts: update labels from 'Pointer
  Demo' to '${BRAND_DISPLAY} Demo'"* in `API/Seed/AdminSeeder.cs`. I read the entire file — **no
  such string exists**. There is no `"Pointer Demo"` literal anywhere in the seeder. This is a
  hallucinated instruction that would send an agent searching for text that isn't there, while the
  *actual* demo-tenant logic lives in `Application/Services/Implementation/DemoService.cs`
  (unreferenced by the plan at all).
- **Phase 9.2 instructs renaming a `${BRAND_SLUG}-redis` volume/service.** There is **no Redis
  anywhere** in `docker-compose.yaml` or `docker-compose.prod.yml` — only `db` (Postgres) and `api`
  (+ `caddy` in prod). This is a fabricated service that doesn't exist in this stack.

These three items alone show the plan was **not** written by directly inspecting this codebase — it
reads like a generic template for "a project literally named Pointer" rather than a plan derived
from *this* repo's real files. An autonomous agent has no way to know which parts are
grounded and which are invented without doing exactly the kind of independent verification this
review just did.

### 1.3 Concrete missing files / subsystems

| Area | What's missing | Evidence |
|---|---|---|
| **`/embed.js` endpoint** | Never mentioned anywhere in the plan, yet it hardcodes `/pointer.js`, the `<pointer-feedback>` tag name, and the `window.__pointerEmbedded` guard flag in a JS template string. | `API/Program.cs:286-319` |
| **`GetSection("Pointer")` call site** | Phase 2.4.2 renames the `appsettings.json` key, but the C# code that *reads* it (`app.Configuration.GetSection("Pointer")`) is never explicitly flagged as needing the matching rename — miss this and the Swagger widget-embed silently stops working (`GetValue("Enabled", false)` always returns `false`). | `API/Program.cs:180` |
| **`PointerUrlResolver` call sites** | Phase 2.4.1 renames the *class*, but the two call sites (`PointerUrlResolver.ResolvePublicUrl(...)`) are static-method invocations, not `using`/namespace strings — Phase 2.3 ("namespaces & usings") does not cover them. | `API/Program.cs:213, 288` |
| **`docker-compose.prod.yml` env vars** | Entire `environment:` block for the `api` service — `Pointer__Enabled`, `Pointer__Server`, `Pointer__Project: "pointer-api"`, `Pointer__Environment`, `JWT__Issuer: "pointer-api"`, `Email__FromName: "Pointer"`, `ConnectionStrings__Default` (`Database=pointer`) — is never referenced by the plan. This is exactly where the self-configuring widget-embed and JWT issuer claim live. | `docker-compose.prod.yml:20-46` |
| **`.env.example` / `.env.prod.example`** | Not mentioned once. Contains `Database=pointer`, `JWT__Issuer=pointer-api`, `admin@pointer.local`, `POINTER_SERVER=https://api.pointer.moamen.work`, `EMAIL_FROM_NAME=Pointer`. | `.env.example`, `.env.prod.example` |
| **GitHub Actions CI** | `.github/workflows/publish-clients.yml` is never mentioned. It hardcodes the `@moamen-ui` npm scope, `npm view @moamen-ui/pointer-angular version`, env var name `POINTER_SWAGGER_URL`, and a `$GITHUB_STEP_SUMMARY` string listing `@moamen-ui/pointer-{angular,react,vue}`. Regenerating clients (Phase 4) without touching this workflow leaves CI publishing under the *old* brand. | `.github/workflows/publish-clients.yml:33,46,54,76` |
| **`justfile`** | Never mentioned. Contains `psql -U pointer -d pointer`, `gh workflow run publish-clients.yml -R moamen-ui/poitner-api` (note: already a **typo**, "poitner"), `.pointer/pointer.sh` reference, and multiple echoed strings ("Dashboard: cd ../pointer-dashboard..."). | `justfile` (whole file) |
| **`install.sh` — under-specified by ~10x** | Phase 6.1.4 lists exactly two `curl` lines and one `mkdir/ln -sf` block as "the" installer update. The real file has ~15 distinct `pointer`-branded tokens across 96 lines: the `.pointer/` directory itself, `.pointer/pointer.sh`, `.pointer/bridge.mjs`, `.pointer/credentials.env(.example)` heredoc bodies, five `.gitignore` append lines (`.pointer/`, `!.pointer/credentials.env.example`, `!.pointer/stack.json`, `!.pointer/pointer.sh`, `!.pointer/bridge.mjs`), and ~8 echoed status lines. A literal, non-creative execution of the plan ships a **half-renamed** installer. | `API/wwwroot/install.sh` (whole file) |
| **API-key prefix `ptr_`** | Hardcoded in `Application/Services/Implementation/ProfileService.cs:73` (`"ptr_" + Convert.ToHexString(...)`) and asserted in `Tests/ApiKeyAuthTests.cs:121,180`. Also baked into `install.sh`, `pointer-init.md`, and `skill.md` sample values. Never mentioned by the plan — is the key-prefix convention in scope or not? No guardrail either way. | `Application/Services/Implementation/ProfileService.cs:66-73` |
| **`web-component/src` — only 3 of 13 files addressed** | Phase 3 names `index.ts`, `element.ts`, `constants.ts` only. The directory also has `auth-ui.ts`, `capture.ts`, `dom.ts`, `framework-source.ts`, `icons.ts`, `pagecontext.ts`, `shortcut.ts`, `templates.ts`, `types.ts`, plus 10 `.scss` partials — any of which may contain brand strings, localStorage keys, or class names not enumerated. | `web-component/src/*` |
| **`extension/src` — only 2 of 6 files addressed** | Phase 5.2 names `popup.ts`/`options.ts` only. `background.ts`, `content-bridge.ts`, `inject-main.ts`, `shared.ts` are unaddressed — `inject-main.ts` in particular is likely where the injected `<script src=".../pointer.js">` literal actually lives. | `extension/src/*` |
| **Already-committed brand-named binary artifacts** | `extension/pointer-ext-v0.1.0.zip` and `landing/pointer-extension.zip` are tracked files whose *filenames* need renaming — a content-grep audit (Phase 10) will never flag a binary filename. Not mentioned anywhere. | repo root listing |
| **`clients/react/package.json` repository URL typo** | Already reads `"url": "git+https://github.com/moamen-ui/poitner-api.git"` — misspelled **"poitner-api"**, not "pointer-api". Any exact-string-match replace of `pointer-api` → `${brand}-api` (as the plan implies) will **silently skip this occurrence** because it doesn't contain the substring "pointer-api". This is a concrete, real example of why a "known token list" replace strategy is fragile — it demonstrates the exact class of hidden dependency the prompt asked me to check for. | `clients/react/package.json:20` |
| **`Tests/*.cs` hardcoded brand domain** | Seven test files construct a `NoopBrandingService`/mock with `Urls = ... { App = "https://app.pointer.moamen.work" }` (`ApiKeyAuthTests.cs`, `ChangePasswordTests.cs`, `DemoUpgradeTests.cs`, `MonetizationSignupTests.cs`, `PlanEnforcementTests.cs`, `UserGovernanceTests.cs`, `WorkspaceAdminOwnershipTests.cs`). Phase 10 says `dotnet test` must pass (it will, since it's just a string literal) — but Phase 10's own "no residual pointer" grep audit will flag all seven as failures the plan gives no instruction for. | `Tests/*.cs` (7 files) |
| **Docs corpus** | `README.md`, `DEPLOY.md`, `SELF_HOSTING.md`, and the rest of `docs/*.md` are never mentioned, yet they almost certainly contain setup instructions, curl examples, and screenshots referencing the old brand. | `docs/`, root `*.md` |
| **Caddyfile — plan's replacement drops real production logic** | Phase 9.1's proposed Caddyfile is a simplified sketch. The actual file has: a reusable `(dashboard)` snippet, an `@widget` path matcher with `Cache-Control: no-cache, must-revalidate` covering `/pointer.js /pointer.css /embed.js /skill.md /api/branding` (the exact mechanism that makes "the widget auto-updates from the server" true), a `demo.pointer.moamen.work` host, and a bare-domain → `landing/` host. Applying the plan's block **verbatim** silently regresses widget-update propagation and drops the demo/landing hosts entirely. | `Caddyfile` (whole file) vs. plan §9.1 |
| **Domain-model ambiguity** | The plan's Phase 0 assumes buying a brand-new apex domain (`pickit.dev`). The *actual* production setup runs every service as a subdomain of a personal/shared domain (`*.pointer.moamen.work`, e.g. `api.pointer.moamen.work`). The plan never asks or resolves whether the new brand keeps this "subdomain-of-a-shared-domain" model or moves to its own apex — this materially changes Caddyfile, DNS, CORS origin literals, and SPF/DKIM/DMARC scope (Phase 9.4), and is silently assumed away. | `Caddyfile`, `API/Program.cs:61-68` vs. plan Phase 0 |

---

## 2. Negative Guardrails

The *intent* of Phase 1 is correct and necessary — I confirmed `cursor: pointer` (4×),
`pointer-events` (7×), `PointerEvent`/`pointerdown`/`pointerup` all genuinely exist in
`web-component/src/styles/*.scss` and `element.ts:851-881`. This is a real risk the plan is right
to guard against. However:

### 2.1 CRITICAL: The Phase 10 verification script cannot catch what it claims to catch

```bash
grep -rnw . -e "pointer" ... | grep -vE "(cursor-pointer|pointer-events|PointerEvent|onpointer|setPointerCapture|releasePointerCapture)"
```

`grep` is **case-sensitive by default**, and no `-i` flag is present. The primary search pattern
`"pointer"` (lowercase) will **never match** any PascalCase symbol: `PointerEvent`,
`PointerFeedback`, `PointerUrlResolver`, `Pointer.API`, `Pointer.sln`, the `"Pointer"` config-section
key, doc titles like "Pointer API". Two consequences:

1. **The negative-filter term `PointerEvent` in the second `grep -v` is dead code** — it can never
   be triggered, because the first grep already never surfaces anything matching that case.
2. **The audit provides zero coverage for exactly the tokens Phase 2/3 spent the most effort
   renaming** — C# classes, namespaces, and the widget's own `PointerFeedback` class name. If any of
   those renames were incomplete, this "must pass cleanly" verification step would report success
   anyway. That is a false sense of safety, arguably worse than no check at all.

**Fix:** add `-i` to the primary grep, and rewrite the negative list to be regex-safe and
case-aware (e.g. `pointerdown|pointerup|pointermove|pointerenter|pointerleave|pointerover|pointerout|pointercancel|pointerevent|pointer-events|cursor-pointer|setpointercapture|releasepointercapture|haspointercapture` with `-i`).

### 2.2 Guardrail list and verification regex are out of sync

Phase 1 prose additionally calls out `pointerenter`, `pointerleave`, `pointerover`, `pointerout`,
`pointercancel`, `hasPointerCapture`, and `@media (pointer: fine|coarse)` as protected — **none of
these appear in the Phase 10 verification regex**. Two independent lists that are supposed to
express the same allow-list have drifted apart within the same document.

### 2.3 Noise: the verification command will be unusable in practice

`--exclude-dir` only lists `{node_modules,.git,dist,bin,obj}`. This repo currently has **hundreds**
of committed files under `.playwright-mcp/` and `.playwright-cli/` (Playwright accessibility-tree
snapshots), many containing literal text like `[cursor=pointer]` for every clickable element on
every captured page. Running the audit as written would flood the output with hundreds of
irrelevant matches, making it impractical for an agent (or human) to find the handful of real
findings. `--exclude-dir` should also include `.playwright-mcp`, `.playwright-cli`, and
`e2e/test-results`.

### 2.4 A guardrail item that guards against nothing

Phase 1 §3 lists `IntPtr`, `UIntPtr`, `fixed` blocks, and `*` memory-pointer syntax as things to
avoid renaming. None of these tokens contain the substring "pointer" (case-insensitively or
otherwise) — a keyword-based rename strategy was never at risk of touching them. This is
dead/misleading guidance that could cause an agent to spend time double-checking a non-issue while
missing the real risks in §2.1–§2.3.

### 2.5 Missing guardrail: the API-key prefix decision

As noted in §1.3, `ptr_` is a hardcoded, product-meaningful prefix (`ProfileService.cs:73`). The
plan gives no rule for whether this is in-scope. If an agent treats it as "just another `pointer`
string" and renames it, every **already-issued, live API key** for existing users breaks
authentication (`Tests/ApiKeyAuthTests.cs:180` shows the format is validated), with no migration
path discussed anywhere in Phase 9 or 11.

---

## 3. Execution Order & Dependency Graph

### 3.1 Publishing authentication is never set up

Phase 4.3 runs `npm publish` against GitHub Packages (`npm.pkg.github.com`, scope `@moamen-ui` per
`.github/workflows/publish-clients.yml:33-34`) for the **renamed** package coordinates. Phase 0
never collects an `NPM_TOKEN`/`GH_TOKEN` variable, and Phase 4.3/Phase 11 never instruct
configuring `~/.npmrc` or `NODE_AUTH_TOKEN` for local publishing. An agent executing this literally
will hit an authentication error with no remediation step provided.

### 3.2 CONFIRMED: client-package publishing is sequenced *after* the dashboard build it's a prerequisite for

This is the exact risk the prompt asked me to verify, and it is real:

- Phase 11 **Step 3** ("Execute Phases 1–9 locally on a migration branch") includes Phase 7, which
  rewrites the dashboard's `package.json` dependency to the **new, not-yet-published** package name
  (e.g. `@moamen-ui/pickit-angular`).
- Phase 11 **Step 4** then runs "Phase 10 Verification", whose own step 5 is:
  ```bash
  cd ../../pointer-dashboard
  cd angular && npm run build
  ```
  This requires `npm install` to resolve `@moamen-ui/pickit-angular` from the registry — but that
  package **has not been published yet**. Publishing doesn't happen until Phase 11 **Step 5**,
  which comes *after* Step 4.
- Making it worse: Phase 11 Step 5 ("Publish new NPM client packages") happens **before** Step 6
  ("Push code to the new GitHub remotes"). But the *only* automated publish path
  (`.github/workflows/publish-clients.yml`) lives in the repo and can't run in CI until the code is
  pushed. So Step 5 must mean "run `npm publish` locally" (Phase 4.3) — which circles back to the
  missing-auth gap in §3.1 — while the CI workflow that's supposed to be the source of truth for
  publishing sits unused and un-migrated (still hardcoding `@moamen-ui/pointer-*`, per §1.3).

**Net effect:** following Phase 11's literal step order will break the dashboard build during
verification. The plan needs either (a) a local `npm link`/workspace/`file:` dependency step so the
dashboard can build against the renamed client *before* it's published, with real publishing
deferred to after Step 6, or (b) explicit re-ordering: publish (even as a `-rc`/prerelease version)
before the dashboard build-verification step.

### 3.3 Cross-repository coordination is unaddressed

Phase 7 (dashboard) and Phase 5 (extension, actually present as `extension/` in *this* monorepo —
see §3.4) edit content that, per `AGENTS.md`, lives in a **separate repository**
(`pointer-dashboard`). Phase 11 Step 3 says "Execute Phases 1–9 locally on a migration branch" as
if it's one atomic operation, but it actually spans at least two independent git repositories/
checkouts, each needing its own clone, branch, commit, and (per §3.2) a coordinated publish handoff
between them. The plan never surfaces this as a distinct coordination concern (e.g., "clone
`pointer-dashboard` alongside this repo before starting Phase 7").

### 3.4 A structural surprise the plan doesn't acknowledge

The plan's Phase 5 ("Browser Extension") and Phase 8 ("Landing Page") are written as if
`extension/` and `landing/` might be separate repos (mirroring how Phase 7 treats
`pointer-dashboard`). In reality, **both already live inside this same `pointer-api` monorepo**
(`extension/`, `landing/` at the repo root). This isn't necessarily wrong, but the plan's own
"Directory structure"-style framing never states this, so an agent has to discover it independently
(as this review did) rather than being told outright — a completeness/clarity gap given the plan's
stated goal of leaving "nothing to guess."

### 3.5 Extension activation depends on a deploy that hasn't happened yet

The rebuilt extension (Phase 5) injects `https://${SUBDOMAINS.API}/${BRAND_SLUG}.js` — but per
Phase 11, the new domain/containers aren't live until **Step 7** ("Deploy new containers"), which
comes after the extension is built/pushed (Step 6). The plan should note that the extension can be
*built* early but must not be *published/activated* for real users until Step 7 completes, or every
installed copy 404s on its injected script.

---

## 4. Actionability

**Strengths:** Phases 2–6 mostly use precise `git mv` commands, exact JSON snippets, and named
symbols — genuinely good practice, and for the parts that are accurate (see caveats above) an agent
could follow them mechanically.

**Weaknesses that force guessing/hallucination:**

- Phase 5.1: *"Icons and web-accessible resources: point to new branding icons"* — no filenames,
  sizes, or source. `extension/REBRAND.md` (already in-repo!) is far more precise about this
  (16/32/48/128px requirements, reuse of `pwa192`/`iconSquare` Branding-page assets) — the master
  plan should have deferred to or merged with this existing document instead of re-deriving a
  vaguer version of the same guidance.
- Phase 2.4.3: *"Update EF Core migration history if necessary or generate initial migration for
  fresh DB"* — this is the single highest-risk step in the entire plan (renaming
  `Database=pointer` in a **live production** connection string points at a different, empty
  database) and it's dispatched in one hedgy sentence with no decision procedure, no mention of
  `pg_dump`/rename-in-place vs. fresh migration, and no rollback plan.
  For a plan aimed at an *autonomous* agent, "if necessary" is exactly the kind of ambiguity that
  produces silent data loss.
- Phase 7.5: *"Update product titles, fallback headings, and email invite placeholders"* — no file
  paths at all, despite the plan's stated goal of always giving "explicit file paths."
- As covered in §1.1, the plan gives the agent no way to distinguish "this string is genuinely
  hardcoded and needs replacing" from "this value is already runtime-configurable via
  `/api/branding` and should be left alone (or have its *seed default* updated instead)." This is
  the largest source of potential hallucinated/wasted/harmful work for an autonomous executor.

---

## 5. Prioritized Recommendations

**Must fix before this plan is safe to execute autonomously:**
1. Add a section explaining the runtime Branding system (`IBrandingService`/`BrandingController`/
   `/api/branding`) and clearly separate "source/technical rebrand" from "runtime product-branding
   defaults." Point the agent at `extension/REBRAND.md` as the existing source of truth for
   extension-specific branding.
2. Fix the Phase 10 verification grep: add `-i`, complete the negative allow-list to match Phase 1's
   prose exactly, and add `--exclude-dir` entries for `.playwright-mcp`, `.playwright-cli`,
   `e2e/test-results`.
3. Resolve the client-publish-before-dashboard-build ordering bug (§3.2) — either via local linking
   or explicit re-sequencing — and add an NPM/GitHub Packages auth step to Phase 0/4.3.
4. Remove or correct the fabricated specifics: ".NET 9" → ".NET 8", drop the nonexistent "Pointer
   Demo" seed-label instruction (or replace with a real audit of `DemoService.cs`), and drop the
   nonexistent Redis service from Phase 9.2.
5. Add explicit coverage for: `API/Program.cs`'s `/embed.js` endpoint, `GetSection("Pointer")`,
   `PointerUrlResolver` call sites, `docker-compose.prod.yml` env vars, `.env*.example`,
   `.github/workflows/publish-clients.yml`, `justfile`, and a decision on the `ptr_` API-key prefix.
6. Replace the piecemeal `install.sh` bullet list with either a full line-by-line diff or an
   instruction to treat the file as fully in-scope for the standard token-replacement pass.
7. Add a decision on the domain model (new apex vs. continued subdomain-of-shared-domain) before
   Phase 9, since it changes Caddyfile/DNS/CORS/email scope materially.
8. Preserve (don't naively overwrite) the actual `Caddyfile`'s caching/host logic — diff against the
   real file rather than supplying a simplified rewrite.

**Should fix:**
9. Expand Phase 3/5 file lists to the *actual* contents of `web-component/src/` and
   `extension/src/` (13 and 6 files respectively, only 3 and 2 of which are named).
10. Add the seven `Tests/*.cs` files with hardcoded `app.pointer.moamen.work` mocks to Phase 2.
11. Note the two already-committed brand-named zip artifacts (`extension/pointer-ext-v0.1.0.zip`,
    `landing/pointer-extension.zip`) as files needing `git mv`.
12. Flag the pre-existing `poitner-api` typo in `clients/react/package.json` as a concrete example of
    why exact-string-match replacement needs a human/agent review pass, not just automation.
13. Add `README.md`, `DEPLOY.md`, `SELF_HOSTING.md`, and the `docs/` corpus to scope, even if only as
    a "grep and review, don't blindly replace" checklist item.

## Summary Table

| Criterion | Verdict |
|---|---|
| Completeness | **Incomplete** — one architectural blind spot (runtime Branding), ~15 concrete missing files/subsystems, 3 fabricated specifics |
| Negative guardrails | **Right intent, broken enforcement** — Phase 10's verification script cannot detect PascalCase symbols due to a case-sensitivity bug |
| Execution order | **Contains a real sequencing bug** — dashboard build (Phase 10) precedes client publish (Phase 11) for a dependency that doesn't exist yet |
| Actionability | **Mixed** — strong for file/namespace mechanics, weak/hand-wavy for DB migration, icons, and anything touching the Branding system |
