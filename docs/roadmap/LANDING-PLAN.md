# Pointer — Landing page plan

> Consolidates everything that plans the marketing landing page (`landing/index.html`) into one
> file, per request 2026-09-19. Previously split across
> `docs/roadmap/prd/LANDING-PRD.md`, `docs/roadmap/execution/R3-06-landing-refresh.md`,
> `docs/roadmap/testing/R3-06-tests.md`, and two archival files under `docs/superpowers/` — all
> merged in below with their content unchanged (only the cross-links between them were repointed
> to in-document anchors). Those five source files no longer exist; this is the one place to look.
>
> The **paste-ready AI design-tool prompt** stays a separate file —
> [`prd/LANDING-DESIGN-PROMPT.md`](prd/LANDING-DESIGN-PROMPT.md) — since it must remain
> self-contained and copy-pasteable with no repo context, which a planning doc is not.
>
> Roadmap item: **§53 / R3.6**, tracked in [`DX-UX-CX-PLAN.md`](DX-UX-CX-PLAN.md).

## Contents
1. [PRD (content contract)](#part-1--prd-content-contract) — what the page must say and prove.
2. [Execution plan](#part-2--execution-plan) — how it's built, what must not break, current status.
3. [Test scenarios](#part-3--test-scenarios) — the E2E coverage for the acceptance criteria above.
4. [Appendix — original 2026-06-30 plan and design spec](#part-4--appendix-history) (superseded,
   kept for history only — do not re-run).

---

## Part 1 — PRD (content contract)

**Item:** §53 / R3.6 · **Execution:** [Part 2 below](#part-2--execution-plan) ·
**Tests:** [Part 3 below](#part-3--test-scenarios)

This PRD defines **what the page must say and prove**. It does not contain final copy — the implementer
writes that, through the `impeccable` skill, against this document.

---

## 1. Why this exists

The current page (`landing/index.html`, 943 lines) is competent and already does the hard parts: live
data from three endpoints, en/ar with RTL, dark mode, white-label via `/api/branding`, and it dogfoods
the widget on itself. It is not a rewrite candidate.

What it lacks is an **argument**. It describes features ("captures the selector, the CSS that wins")
and asserts benefits ("spend fewer AI tokens") without connecting them. The connection is the product's
actual thesis, and it is the thing no competitor can copy by adding a comment widget:

> **An AI coding tool is only as good as the feedback it receives.**

That is the page's job. Everything else is support.

---

## 2. Positioning

**One-sentence promise:** *Your team points at the UI; your AI tool gets a brief precise enough to
change the right line.*

**Category:** not "visual feedback tool" (Marker.io, BugHerd own that framing and it is a ticketing
promise). Pointer's category is **AI-ready feedback** — the input layer for agentic coding.

**The competitive line:** a screenshot tool hands your AI a picture and a sentence. Pointer hands it a
selector, the CSS that actually wins, the route, the viewport, and the stakeholder's exact words.

---

## 3. The AIX argument — in full, with evidence

This section is the spine. Each claim below must be traceable to a captured field or a flow step that
exists, or it does not go on the page.

### 3.1 What a comment actually carries

From `web-component/src/capture.ts` — every comment is created with:

| Captured | Why an AI tool cares |
|---|---|
| `selector` (`generateSelector`) | Identifies the element without a screenshot or a description |
| `snapshot` — the element's own opening tag, attributes ≤120 chars, text ≤160 chars (`shallowSnapshot`, `:128-146`) | Enough to recognise the element in source; deliberately shallow so it does not drown the model in child markup |
| `appliedCssRules` — **the rules that win**, not the full computed dump | The single most useful field: "why is it blue" is answered before the agent asks |
| `computedStyles`, `classes`, `parentInfo` | Disambiguates when the selector is generic |
| `route`, `pageUrl`, `pageTitle` | Which screen — so the agent opens the right route component |
| `viewportWidth/Height`, `deviceType`, `devicePixelRatio`, `userAgent` | "Broken on mobile" becomes a reproducible condition |
| `sourcePath` (tiered — see §3.4) | When present, the file itself |
| `PageContextSnapshot` (opt-in, bug reports) | Console errors and failed/slow requests at the moment of the complaint |

**Proof point for the page:** this is a *structured brief*, not a screenshot. Show the field list.

### 3.2 The loop that exists today

1. Anyone signed in clicks an element on the running app and types a sentence.
2. It lands in a dashboard queue, tagged by project, environment, stakeholder and status.
3. A developer runs their AI tool; the served skill (`API/wwwroot/skill.md`) + `.pointer/pointer.sh`
   fetch the queue and apply the changes to real source files.
4. The tool commits per the project's `CommitStyle`, and each applied comment carries its commit URL
   back to the person who asked. **The AI never pushes** — that stays human.

Steps 1–4 all ship today. Say so plainly; it is a stronger claim than most of the roadmap.

### 3.3 The token argument — and its honesty rule

`docs/AI_AGENT_TOKEN_OPTIMIZATION.md` contains **measured** session telemetry for a first-use
interaction against a real host app: **551,817 tokens across 24 turns, ~90 seconds**, driven by auth
drift, subshell amnesia, and 45 KB of raw JSON dumped into the model's context.

The same document's optimised figures (~18,000 tokens, 2 turns, ~5 s) are **projections written before
the fix shipped**, not measurements taken after it.

> **Rule: the 96% reduction figure does not go on the landing page.** It is an engineering projection.
> Either re-run the telemetry against the shipped `pointer.sh` + `?view=summary` path and publish the
> *measured* number with its method, or make the qualitative claim only ("a summary view and a one-command
> helper instead of twenty-odd exploratory turns"). A number the founder cannot defend in a demo is worse
> than no number.

What *is* safe today, because the mechanisms shipped: `?view=summary` returns id/status/route/body
instead of full element payloads, and `pointer.sh list|queue|get|apply` collapses the discovery
choreography into one command.

### 3.4 The honest limit on source paths — and the payoff when it lifts

`capture.ts:242-257` resolves `sourcePath` in three tiers: a configured attribute → React/Vue
**dev-mode** internals → `null`.

**In a production build, tier 2 is stripped.** So a comment from staging or production usually arrives
with `sourcePath: null`, and the agent falls back to searching by text and class.

- **Today's honest claim:** the comment carries the selector, the winning CSS and the route, so the
  search is narrow and grounded — not "the AI starts at the right file".
- **After R3.1 (Vite plugin + manifest):** every element carries a stable hash resolving to an exact
  file, in production. *That* is when "starts at the right line" becomes true, and it is the single
  biggest upgrade to this page's argument. Plan the copy so that sentence can be switched on.

---

## 4. Audiences and the job each hires it for

| Segment | Their job-to-be-done | What must be on the page for them |
|---|---|---|
| **Dev lead / senior engineer** (primary buyer) | "Stop translating vague feedback into code." | The structured-brief field list; that the AI edits real source and commits but never pushes; two-line install; self-host and privacy links |
| **Solo founder / indie dev** (fastest to convert) | "Ship client changes without a meeting." | Demo with no install; two-line install; free tier; the whole loop on one screen |
| **Agency / consultancy** (highest value) | "Let clients point instead of writing ambiguous emails." | Multi-project and multi-tenant isolation; the browser extension for sites they don't control; per-stakeholder tagging; roles |
| **Non-technical stakeholder** (the actual commenter, rarely the visitor) | "Be understood without learning tools." | One line: click anything, type a sentence, nothing to install. They arrive via invite, not this page — do not spend the hero on them |

**Primary target of the hero: the dev lead.** They are the one who installs it.

---

## 5. Message hierarchy

**Hero — five seconds.** Name, one-line promise, the loop in one visual, and two CTAs (demo without
install; create account). The visual must show a *comment becoming a change*, not a widget screenshot.

**Second screen — the proof.** The structured brief: what a single comment actually carries, as fields.
This is the page's differentiator and currently appears only as a one-line feature card. Promote it.

**Third — the loop.** Click → triage → AI applies → commit link back to the asker. Four steps, showing
that the human stays in control of the push.

**Then — objections, in the order they occur:**

| Objection | Answer the page must give |
|---|---|
| "An AI edits my code?" | It edits a branch and commits; **it never pushes**. Scope is the element described. You review the diff like any other. Commit URL on every applied comment. |
| "What do you capture from my app?" | The field list plus a link to `landing/data.html` (R3.5). Do not restate it — link it; one canonical page. |
| "Which AI tool do I have to use?" | Any. It is served markdown and plain HTTP, not an integration. Name the ones known to work, avoid implying partnership. |
| "Is my data stuck with you?" | Self-host: API + Postgres, your own database. Link, don't duplicate. |
| "Will this break my app's styles?" | Shadow DOM isolation. |

**Last — pricing, extension, final CTA.**

---

## 6. Section plan and evidence

| # | Section | Status | Evidence / data source |
|---|---|---|---|
| 1 | Nav | keep | `/api/branding` → name + logo (`[data-brand-name]`, `[data-brand-logo]`) |
| 2 | Hero | **rewrite** | Promise per §2; CTAs to `urls.demo` / `urls.app` from branding |
| 3 | **The brief** (new) | **new** | §3.1 field table, from `capture.ts` |
| 4 | Loop / how it works | rewrite | §3.2, four steps, ending at the commit URL |
| 5 | Without/with contrast | keep | Existing copy is good |
| 6 | Why it pays | **revise** | Keep time/cost/queue. **Rewrite the token card** per §3.3 — no invented number |
| 7 | Features | trim | 8 cards is too many for the scan; fold into §3/§4 and keep the distinct ones (multi-tenant, extension, Shadow DOM, dashboard) |
| 8 | Stack tags | keep | **Live:** `/api/public/stacks-summary`; hidden when `totalProjects === 0` |
| 9 | Stakeholder / developer split | keep | — |
| 10 | Trust & privacy (new) | **new** | §5 objections; links to `/docs/` (R2-07) and `/data.html` (R3.5) |
| 11 | Pricing | keep | **Live:** `/api/plans`; `displayState === 1` → "Coming soon" |
| 12 | Extension | keep | `/api/branding` → `extension.storeUrl` / `zipUrl` |
| 13 | Final CTA + footer | revise | Footer **Docs** link → `urls.docs` from branding (R2-07 task) |

---

## 7. Claimable now vs blocked on an item

**The rule:** a sentence goes on the page only if a visitor could verify it today.

### Claimable now

- Click any element on a running app and leave a comment; the comment carries selector, shallow
  snapshot, winning CSS rules, classes, parent info, route, page URL/title, viewport, device type, DPR
  and user agent.
- Opt-in page context (console errors, failed/slow requests) on bug reports.
- Comments are tagged by project, environment (Local/Staging/Production), stakeholder and status, and
  triaged in a dashboard with roles.
- Any AI coding tool can fetch the queue and apply changes to real source over plain HTTP, via served
  markdown skills — no SDK, no integration.
- The AI commits per the project's commit style and **never pushes**; each applied comment carries a
  commit URL.
- Two-line install (script tag + element). Shadow DOM isolation. Multi-project, multi-tenant, enforced
  server-side. Browser extension for sites you don't control. Demo without installing anything.
- Self-hosting: API + Postgres. White-label: product name, logo and URLs are server-driven.
- A lightweight summary view and a one-command helper exist so an agent does not ingest full element
  payloads to triage.

### Blocked — do not claim until the item ships

| Claim | Blocked on |
|---|---|
| "Your AI tool starts at the exact file/line" (in production) | **R3.1** Vite plugin + manifest |
| Any specific token-reduction percentage or turn count | A **re-measurement** against the shipped path (§3.3) |
| "One command to install" / `npx …` anywhere | **R1.2** CLI |
| "Works natively inside Claude Code / Cursor" (MCP framing) | **R2.2** MCP server |
| "Get notified when your comment ships" | **R2.4** notifications |
| "Comments link to the PR" | **R2.9** `apply --pr` (held) |
| "We apply it for you in the cloud" | **R2.43** cloud apply (held) |
| "Invite clients with one link, no password" | **R2.5** quick-access magic links |
| "Full docs" as a nav item | **R2.7** docs site |
| Any customer name, logo, testimonial or case study | Having a customer who agreed in writing |
| Uptime, SLA, or "trusted by N teams" | Measurement |

**Maintenance obligation:** each blocked claim is unblocked by the item that ships it. Every execution
doc in this programme carries a `## Docs` section; for the items above, that section also names the
landing-page sentence to switch on. The page is **not** a launch artifact to be revisited quarterly —
it changes with the feature, in the same change.

---

## 8. Non-goals

- Not a rewrite. The existing structure, i18n, theming and live-data wiring stay.
- No build step, no framework, no runtime dependency (`landing/` is bind-mounted read-only into Caddy).
- No blog, changelog, newsletter, chat widget, or third-party analytics/marketing scripts. The widget
  the page dogfoods is the only script it loads beyond its own.
- No pricing invention — pricing renders from `/api/plans`.
- No duplication of `/docs/` or `/data.html`; link to them.
- Not a Release-1 blocker. It sells features; it waits until enough of them are true.

---

## 9. Success measures

Tie to the usage events R1.2 introduces (§21: `installed`, `first_comment`), not to page vanity metrics.

| Measure | Why it is the right one |
|---|---|
| **Visit → `installed` rate** | The page's actual job: convince a dev lead to run the install |
| **`installed` → `first_comment` within 24 h** | Whether the page set accurate expectations. A high install rate with a low first-comment rate means the page over-promised |
| **Demo CTA → account created** | Whether "try without installing" converts, or is a dead end |
| Self-host / privacy link clicks | Whether the objection sections are load-bearing (informs how prominent they should be) |

Explicitly **not** measures: pageviews, time on page, bounce rate.

**Baseline honesty:** none of these can be measured until R1.2 ships the events. Until then this page
ships on judgement, and that is stated rather than dressed up in numbers.

---

## 10. Open decisions for the implementer

1. **`landing/v2/`** — a full interactive scroll-journey variant, indexable (no `noindex`), committed
   in `54fa92d`. It is a second public page competing with `/` for the same visitor and the same search
   terms. Resolve it (fold the good parts in, or `noindex` it, or delete it) — see the execution doc.
2. Hero visual: keep the existing mock-and-terminal animation, or replace it with the structured-brief
   field list as the hero proof. Decide through `impeccable`.
3. Whether the trust section is its own block or is distributed inline next to each claim it defends.

---

## Part 2 — Execution plan

## Goal
Rebuild the landing page's **argument** around the product's real thesis — an AI coding tool is only as
good as the feedback it receives — without rewriting the page's working machinery. The visitor (a dev
lead) must understand in five seconds what the product does, see on the second screen *what a single
comment actually carries*, and reach pricing having had the "an AI edits my code?" objection answered.

Content contract: [Part 1 above](#part-1--prd-content-contract). That PRD says what must be proved;
this doc says how it is built and what must not break.

## Shipped since this plan was written
The core rewrite (§C) landed as written — brief, loop, why, trimmed features, trust & privacy,
`landing/v2/` retired — plus four features added in the same pass that weren't in the original task
list. Recorded here so this doc stays the accurate map of the page, not just the original intent:
- **"Works with your stack"** (`landing/index.html:550-566`, `id="stack"`) — anonymized stack counts
  from `GET /api/public/stacks-summary`; `hidden` by default, only shown once real data arrives
  (defensive per §B.3).
- **"Pointer in numbers"** (`:568-576`, `id="stats"`) — the public, thresholded stats band from
  `GET /api/public/stats`; same hide-until-real-data pattern.
- **"Built for the whole team"** (`:577-583`) — a two-card split (stakeholders / developers), i18n
  keys `team.*`.
- **Browser extension section + install stepper** (`:629-650`, stepper JS `:1172`) — a new
  acquisition path (point-on-any-site) not in the original PRD scope.

Also shipped, but **not yet reflected anywhere on the page**: built-in comment translation
(commit `9ea5cac`, `API/wwwroot/skills/translate.md`, `web-component/src/i18n.ts`'s
`detectTextLanguage`). A stakeholder can write feedback in their own language (Arabic today, any
BCP-47-detectable language in general) and the apply flow translates it in, replies in that same
language, with **no separate opt-in** — this is default-on for every install. This is a real,
claimable capability with no PRD coverage yet; worth a line in the "why"/trust copy (a claim like
"write feedback in your own language" is honest today) and it feeds directly into §G below, since
translation is a second real instance of the same cheap-model-delegate pattern as mechanical-edit
delegation.

`landing/index.html` is now 1,272 lines (was 943 at plan time). All four additions above must keep
meeting §B's constraints (defensive fetch, white-label, en/ar + RTL, dark mode, no horizontal
scroll) exactly like the original sections — re-run the acceptance checklist after touching any of
them.

## Out of scope
- A rewrite. Structure, i18n, theming, live-data wiring and the dogfooded widget stay.
- Writing `/docs/` (R2-07) or `/data.html` (R3.5) content — this page **links** to them.
- Any build step, framework, bundler or runtime dependency.
- Analytics, chat widgets, newsletter capture, third-party marketing scripts.
- New API endpoints. Every piece of live data already has one.

## Prerequisites
- **PRD read in full**, especially §7 (claimable now vs blocked) — it is the honesty gate for every
  sentence written here.
- Facts verified in code before starting:
  - `landing/index.html` — 943 lines, no build step. Three live fetches, **all defensive**:
    `/api/plans` (`:739`, `.catch` `:746`), `/api/public/stacks-summary` (`:777`, `.catch` `:787`),
    `/api/branding` (`:836`, `.catch` `:839`). Brand hooks `[data-brand-name]` / `[data-brand-logo]`
    (`:274`, `:508`, applied `:796-797`). Footer links at `:510-512`.
  - `landing/` is **bind-mounted read-only** into Caddy (`docker-compose.prod.yml`); deploying it is
    `git pull` on the VM, nothing more.
  - `web-component/src/capture.ts:128-146` (`shallowSnapshot`) and `:242-257` (source-path tiers) — the
    evidence behind the PRD's §3.1 field table and its §3.4 honesty limit.
  - `landing/v2/index.html` — 3,752 lines, indexable, committed `54fa92d`.

## Design

### A. Design process — required
This is a frontend surface, so it goes through the **`impeccable` skill**: invoke it before designing
and follow it. Reference for the process behind each step: <https://impeccable.style/docs> — design
creation, working within a design system, audit and critique, focused refinement (layout, typography,
colour, motion), and simplification/hardening (responsive, accessible, resilient). Its own advice
applies: describe the outcome, don't hunt for a command.

The page **is** this project's design system of record (tokens `--pf-*`, the light/dark handling, the
RTL shell). Refine it; do not introduce a second visual language.

### B. Constraints that must survive — verify each after every change
1. **No build step.** Hand-written HTML/CSS/JS in `landing/`. No npm dependency at runtime.
2. **White-label.** `/api/branding` drives name, logo and URLs. Any new outbound link to a Pointer
   property uses a branding value with the current hard-coded URL as fallback — never a new hard-coded
   host. This includes the footer **Docs** link, which currently points at the GitHub README (`:511`)
   and must become `urls.docs` (see §D).
3. **Every fetch stays defensive.** The page renders completely with zero network. Sections backed by
   live data hide or fall back; they never render empty shells or spinners that persist.
4. **en/ar with RTL** and **dark mode** — parity for every new section. Arabic is a shipped locale; an
   untranslated section is an unfinished section.
5. **No horizontal scroll at 390 px.** Wide content scrolls inside its own container.
6. **The page dogfoods the widget** (`<pointer-feedback project="pointer-landing" …>`). It keeps working.
7. **`html { overflow-x: hidden }` only** — never also on `body`; that combination silently breaks
   `position: sticky` (learned the hard way on the v3 experiment).

### C. Content changes
Per PRD §6. The substantive ones:
- **Hero** — rewrite to the PRD's promise; two CTAs (demo, account) from branding URLs.
- **"The brief"** — new section, the PRD §3.1 field table. This is the page's differentiator and
  currently exists only as a one-line feature card.
- **Loop** — four steps ending at the commit URL returning to the asker, and stating the AI never pushes.
- **Token card** — rewrite per PRD §3.3. **No percentage, no turn count, no latency figure** unless a
  re-measurement lands first (see §F).
- **Features** — trim from eight cards; fold the rest into the brief and loop sections.
- **Trust & privacy** — new; answers the four objections, links to `/docs/` and `/data.html`.

### D. Footer docs link (white-label)
`:511` is hard-coded to the GitHub README. The branding payload already carries `urls.docs`
(`BrandingService.cs:15` `DefaultUrlDocs`, written via `brand_url_docs`). R2-07 introduces a
`data-brand-docs` attribute marker for exactly this — a URL-prefix rewrite cannot work here because the
default is a GitHub URL, not a Pointer host. Apply the marker and read the value in the existing
branding handler (`:796-797` block).

**Decision:** if R2-07 has not landed when this item is implemented, add the marker and the handler
here anyway; R2-07 then only has to point it at `/docs/`.

### E. `landing/v2/` — resolve it
A 3,752-line interactive variant, **indexable** (no `noindex`), publicly reachable, competing with `/`
for the same visitor and the same search terms. Leaving it is a real cost: split search signal, two
pages to keep honest, and a second place for claims to go stale.

**Decision: fold and retire.** Harvest whatever the `impeccable` pass judges better than the current
page (the scroll journey is the likely candidate), land it in `/`, then delete `landing/v2/`. If the
pass concludes v2's approach should *replace* `/` wholesale, that is an acceptable outcome — but the
result is still **one** page. Do not leave two.

Record in the report which parts were harvested and which were dropped.

### F. The token number
The PRD forbids publishing the 96% figure because it is a projection, not a measurement
(`docs/AI_AGENT_TOKEN_OPTIMIZATION.md` §5 vs §2). If the founder wants a number on the page, the
prerequisite is a re-run of the telemetry against the shipped `pointer.sh` + `?view=summary` path, with
the method published alongside it. **That measurement is not part of this item.** Ship the qualitative
claim; leave the sentence structured so a measured number can be dropped in later.

### G. Cost-aware apply — animated token-cost illustration
New ask (2026-09-19), tied to **two** shipped mechanisms, not a projection — both are the same
underlying pattern (premium model reasons, cheap model does the mechanical part), so the graphic
should illustrate the pattern, not just one instance of it:
1. **Mechanical-edit delegation** — Step 3b in `skill.md` / `skills/apply.md` (`cli/src/apply/*`,
   commit `e69a50d`, published as `pointer-feedback@0.5.0`): the orchestrating model plans and
   reviews while a cheaper worker model types out mechanical edits, whenever a run has 3+ items in
   disjoint files and an item qualifies as mechanical.
2. **Comment translation** — `skills/translate.md` (commit `9ea5cac`,
   `web-component/src/i18n.ts`'s `detectTextLanguage`): a non-English comment is detected and
   translated in/out by a cheap model (Haiku or the tool's equivalent small tier), never the
   orchestrator, and this is default-on for every install (see the new bullet in "Shipped since
   this plan was written" above).

Because both are real and shipped they can be illustrated — but §F's honesty rule still applies:
**no invented percentage**. `docs/AI_AGENT_TOKEN_OPTIMIZATION.md`'s 96%/97% figures describe a
*different, older* mechanism (discovery/auth/payload overhead in an earlier `skill.md`) and were
never re-measured for production; they must not be borrowed to describe either delegation case
above.

**What to build:** a small, self-contained (no chart library — §B.1) animated SVG/CSS graphic inside
the existing "why" section (`landing/index.html:526-536`, `why.*` i18n keys) contrasting two bars —
"One model does everything" vs. "Pointer: investigation and review stay on the premium model;
mechanical edits and translation delegate to a cheaper worker" — sized and labeled *qualitatively*
("fewer premium-model tokens spent on typing and translating", not "N% cheaper"). If room allows, a
small two-line legend under the second bar naming both delegated tasks (mechanical edits,
translation) makes the claim concrete without a fabricated number. Animate the bars in on scroll
(`IntersectionObserver`), but respect `prefers-reduced-motion: reduce`: render both bars already in
their end state, no animation, when set or when JS is unavailable — motion is a bonus, never the
only way the comparison reads. Must stay legible at 390px (§B.5) and correct through an RTL flip and
dark mode (§B.4), same as every other section.

Copy note: describe the *mechanisms* ("mechanical edits and translation delegate to a cheaper
model"), never a claimed savings number, until a real measurement exists — mirrors §F's existing
rule for the token card, extended to this graphic.

## Tasks
1. Run the `impeccable` pass over the current page (audit/critique) before writing anything; record what
   it flags.
2. Rewrite the hero (PRD §5) — promise, visual, two CTAs from branding URLs.
3. Add the **brief** section — the `capture.ts` field table as the second-screen proof.
4. Rewrite the loop section to four steps ending at the commit URL; state that the AI never pushes.
5. Rewrite the token card per §F — qualitative, no invented figures.
6. Trim the features grid; fold survivors into the brief/loop sections.
7. Add the trust & privacy section with links to `/docs/` and `/data.html` (no duplicated content).
8. Footer docs link → `data-brand-docs` + branding handler (§D).
9. en/ar strings for every new or changed string, both files, RTL verified.
10. Fold/retire `landing/v2/` (§E).
11. Re-verify every constraint in §B; run the R3-06 test scenarios.
12. Build the cost-aware-delegation graphic (§G) inside the "why" section — qualitative bars, no
    invented numbers, static reduced-motion fallback.
13. en/ar strings for the graphic's labels; verify the bar layout flips correctly under RTL.
14. Verify dark-mode contrast and 390px layout for the graphic; confirm it renders with zero
    network (pure SVG/CSS, no fetch) and degrades to its static end-state with JS disabled.

## Dashboard tasks
None — this is the public marketing page, not the dashboard.

## Docs
**None — the page *is* the artifact.** It links to `/docs/` (R2-07) and `/data.html` (R3.5) rather than
duplicating them, and adds no docs page of its own. Add one entry to `landing/docs/pages.json` marking
`/` as an external "Product overview" entry so the docs nav can link back.

## Tests
- E2E: [Part 3 below](#part-3--test-scenarios) — scenarios `R3-06-01` … `R3-06-08`.
- No unit tests (static HTML, no build step).

## Acceptance criteria
- [ ] The page renders completely with **zero network** — no empty shells, no persistent spinners.
- [ ] Each live section degrades independently: `/api/plans` down → pricing fallback; stacks-summary
      down or `totalProjects === 0` → section hidden; `/api/branding` down → bundled brand retained.
- [ ] A white-label swap (branding set to another product name/logo/urls) changes the nav, footer,
      CTAs **and the docs link**, with no "Pointer" left in visible copy except frozen names.
- [ ] en ↔ ar flips to RTL with every new section translated.
- [ ] Dark mode correct on every new section.
- [ ] No horizontal scroll at 390 px.
- [ ] Every internal link resolves **on disk** (not merely HTTP 200 — see the test doc's Caddy note).
- [ ] The dogfooded widget still loads and can post a comment from the page.
- [ ] `landing/v2/` no longer exists, and nothing links to it.
- [ ] No sentence on the page appears in the PRD §7 "blocked" list.
- [ ] No percentage, turn count or latency figure appears anywhere (§F).
- [ ] The cost-aware-delegation graphic (§G) renders a legible static end-state with
      `prefers-reduced-motion: reduce` or no JS, and animates in otherwise.
- [ ] The graphic's labels are qualitative only — no percentage, turn count, or latency figure
      (§F extended to this section).

## Rollout / compatibility
Static files, bind-mounted read-only; deploy is `git pull` on the VM — no container rebuild. Removing
`landing/v2/` breaks any external link to it; it was never announced, so accept the 404 rather than
maintaining a redirect. Keep `/privacy.html` and `/data.html` paths stable — they are linked from
emails and the dashboard.

## Report template
Files changed · the `impeccable` audit findings and which you acted on · what was harvested from `v2`
and what was dropped · the claim-by-claim check against PRD §7 · screenshots (light/dark, en/ar, 390 px)
· the zero-network render evidence.

---

## Part 3 — Test scenarios

## Covers
Acceptance criteria of [Part 2 above](#part-2--execution-plan):
zero-network render → **R3-06-01** · independent degradation of the three live sections → **R3-06-02** ·
white-label swap incl. the docs link → **R3-06-03** · en↔ar + RTL → **R3-06-04** · dark mode →
**R3-06-04** · no horizontal scroll at 390 px → **R3-06-05** · internal links resolve on disk →
**R3-06-06** · the dogfooded widget still works → **R3-06-07** · `landing/v2/` retired and the
forbidden-claim gate → **R3-06-08**.

## Preconditions
- R3-06 tasks merged: the rewritten `landing/index.html`, its en/ar maps, the `data-brand-docs` marker,
  and `landing/v2/` removed.
- Serving: `node e2e/scripts/serve-dir.mjs landing <PORTS.landing>` — `PORTS.landing = 8099`, already in
  the harness §2 registry (shared with R3-05; **the two must not run concurrently** — both serve
  `landing/` on 8099, see Flake notes). Started/killed by `run-e2e.sh` with the existing trap.
- No personas, no seed dependency — every flow is anonymous. The page only *fetches* from the API; it
  never authenticates.
- The API stack is up for R3-06-02/03/07; R3-06-01 requires it to be **unreachable from the browser**
  (achieved by route-aborting, not by stopping the container — see that scenario).
- **Decision:** every scenario blocks the widget script (`**/pointer.js`) except R3-06-07, so a widget
  failure can never be misread as a page failure. R3-06-07 is the one place it is allowed to load.
- **Decision:** the forbidden-claim list for R3-06-08 lives in `e2e/landing/forbidden-claims.mjs`,
  derived from PRD §7's blocked table. When an item ships and unblocks a claim, its execution doc's
  `## Docs` section removes the entry — the constant is the machine-readable half of that rule.

## Scenarios

| id | intent | tier | layer | role | steps | expected | evidence |
|---|---|---|---|---|---|---|---|
| R3-06-01 | page renders fully with zero network | PR | widget (browser) | — | 1. `page.route('**/api/**', r => r.abort())` and `page.route('**/pointer.js', r => r.abort())` **before** `goto`. 2. `goto('http://localhost:8099/')`, `waitForLoadState('domcontentloaded')`. 3. Assert the hero heading, the brief section, the loop section and the footer are all visible. 4. Assert **no** element matching `.pf-skeleton, [aria-busy="true"], .loading` remains after 2 s. 5. `page.content()` contains no literal `undefined`/`null`/`[object Object]`. 6. Collect console errors. | 3 → all four visible; 4 → none; 5 → no literals; 6 → **zero uncaught errors** (aborted fetches are caught by the three `.catch` handlers at `index.html:746/787/839`) | screenshot + console dump |
| R3-06-02 | each live section degrades independently | PR | widget | — | For each of the three endpoints in turn, abort **only** that one and let the others succeed: (a) `**/api/plans` → pricing; (b) `**/api/public/stacks-summary` → stack tags; (c) `**/api/branding` → nav/footer brand. Plus (d) `stacks-summary` fulfilled with `{data:{totalProjects:0,frontend:{},backend:{},aiTools:{}}}`. | (a) pricing shows its static fallback, other sections unaffected; (b) the stack section is **hidden**, not empty; (c) bundled name/logo retained (`Pointer`, `assets/dog-mascot.png`), the rest of the page unaffected; (d) section hidden — `totalProjects === 0` is the documented hide condition | one row per sub-case |
| R3-06-03 | white-label swap, including the docs link | nightly | widget + api | superAdmin | 1. Capture current branding (`GET /api/admin/branding`). 2. `PUT /api/admin/branding` → `productName: "Acme Review"`, `urls.app/demo/docs` → `https://app.acme.test` / `https://demo.acme.test` / `https://docs.acme.test`. 3. Fresh context (branding is fetched once per load), `goto('http://localhost:8099/')`, await the `/api/branding` response. 4. Assert nav + footer brand text, hero CTA hrefs, and the footer docs link. 5. Leak check over `page.locator('body').innerText()` **and** every `title`/`aria-label` attribute. 6. `finally`: restore the captured branding. | 4 → brand text `Acme Review` in both nav and footer, CTAs point at the acme hosts, **footer docs link `https://docs.acme.test`** (proves `data-brand-docs`, execution §D); 5 → zero matches for `/(?<!-)\bPointer\b(?!-)/g` outside frozen names (`pointer.js`, `<pointer-feedback>`, `.pointer/`); 6 → restored | before/after screenshots; the leak-regex match count |
| R3-06-04 | en↔ar + RTL, and dark mode | PR | widget | — | 1. `goto` with `?lang=ar` (or click the language toggle — use whichever the page implements; read it first). 2. Assert `document.documentElement.dir === 'rtl'`. 3. For each **new** section (hero, brief, loop, trust), assert its text is not identical to the English string — i.e. it is actually translated, not a fallthrough. 4. Toggle back to en; `dir === 'ltr'`. 5. `emulateMedia({ colorScheme: 'dark' })`; for each new section assert the computed `background-color` differs from the light value and text contrast is not `transparent`/identical to background. | 2,4 → dir flips both ways; 3 → **no section falls through to English** (the common failure: a new section added to `en.json` only); 5 → dark values differ per section | ar + dark screenshots; the list of any untranslated keys |
| R3-06-05 | no horizontal scroll at 390 px | PR | widget | — | 1. `setViewportSize({ width: 390, height: 844 })`, `goto`. 2. `evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)`. 3. Assert `getComputedStyle(document.body).overflowX !== 'hidden'` — the page must fit, not hide the overflow (and `body` overflow-x must stay unset so `position: sticky` keeps working, execution §B.7). 4. Repeat at 320 px. | 2 → true at both widths; 3 → `body` does **not** carry `overflow-x: hidden`; `html` may | screenshots at both widths |
| R3-06-06 | every internal link resolves **on disk** | PR | cli (node) | — | 1. Parse `landing/index.html` for `href` values that are relative or begin `/`. 2. For each, resolve against `landing/` and assert the target file exists (`index.html` appended for directory paths). 3. Separately `fetch` each over `http://localhost:8099` and record the status. 4. Assert no href points at `/v2/` or `/v3/`. | 2 → **every target exists on disk**; 4 → none. Step 3 is recorded but **not** an assertion: the Caddy `(dashboard)` snippet's `/index.html` fallback means a dead `/docs/…` path returns **200 with the landing page**, so status is not evidence of existence (the trap R2-07-04 documents) | the link table with disk-exists + status columns |
| R3-06-07 | the dogfooded widget still works on the page | nightly | widget + api | tester | 1. Do **not** block `pointer.js`. 2. `preAuthWidget(page, testerToken, testerUser)` (`e2e/widget/lib/auth.ts`). 3. Register `const cfg = page.waitForResponse('**/capture-config')` **before** `goto`. 4. `goto('http://localhost:8099/')`; `await cfg`. 5. Reveal the toolbar (`#pf-launcher`), then `#pf-add`; pick the hero heading; fill `#pf-comment-text`; `#pf-submit`. 6. `GET /api/projects/pointer-landing/comments?pageSize=5` as tester. | 4 → capture-config resolves (the widget booted on the real page); 5 → success toast; 6 → the newest comment's body matches, and its `element.route` is `/` | comment id + the widget DOM dump |
| R3-06-08 | v2 retired, and no forbidden claim shipped | PR | cli (node) | — | 1. Assert `landing/v2/` does not exist. 2. `grep -ri "v2/" landing/` → no references. 3. Load `e2e/landing/forbidden-claims.mjs` (PRD §7 blocked list) and assert the rendered text of `/` matches **none** of its patterns — including any `\d+%`, "turns", "tokens" with a figure, `npx `, "MCP", "PR", "cloud apply", "trusted by", and any testimonial marker. 4. Assert the token section exists but carries no numeral-plus-`%` or numeral-plus-`tokens` construction. | 1,2 → gone and unreferenced; 3 → zero matches, each pattern reported individually so a failure names the claim; 4 → qualitative only (execution §F) | the per-pattern match table |

## Spec files
- `e2e/landing/landing.spec.ts` — R3-06-01…05, 07 (Playwright; reuses `e2e/widget/lib/auth.ts` for 07).
- `e2e/landing/links.spec.mjs` — R3-06-06 (node; filesystem + fetch, no browser).
- `e2e/landing/claims.spec.mjs` — R3-06-08 (node; text extraction + pattern table).
- `e2e/landing/forbidden-claims.mjs` — **new** shared constant, the machine-readable PRD §7 blocked list.
- `run-e2e.sh` — the `landing` phase serves `landing/` on `PORTS.landing` and kills it via the trap.

## Not covered here
- The **quality** of the copy — that is the `impeccable` pass and human review, not an assertion.
- Whether a claim is *true* — only whether a **forbidden** claim is absent. A false statement that is
  not on the blocked list passes; that gap is closed by PRD §7 discipline and review, not by a test.
- `/docs/` and `/data.html` content — owned by R2-07 and R3-05 tests respectively; this suite only
  asserts the links resolve.
- Visual regression / screenshot diffing — deliberately out (the page is meant to change; a pixel diff
  would fail on every intended edit).
- Lighthouse/a11y scoring — R3-05-02 already owns that for the landing shell, at manual tier per the
  harness §7 token rule.

## Flake notes
- ⛓ **State coupling: `PORTS.landing` (8099) is shared with R3-05.** Both scenarios serve `landing/`
  from the same port. They must run in the same serial phase, never concurrently; the `landing` phase in
  `run-e2e.sh` owns the server for both suites.
- ⛓ **R3-06-03 mutates global branding.** It is the same `brand_url_*` state that R2-00-05/06/08 and
  §52's mock-domain rehearsal write. It must not overlap either window, and its `finally` restore is
  part of the PASS criteria — a leaked brand poisons every later scenario in the run.
- R3-06-03 must create its browser context **after** the branding PUT: branding is fetched once per page
  load (`index.html:836`), so a context opened earlier keeps the old values.
- Register `waitForResponse` **before** the triggering `goto` in R3-06-03 and R3-06-07 — the response can
  land first and leave the waiter hanging to timeout.
- R3-06-01 aborts routes rather than stopping the API container: stopping it would affect every other
  suite sharing the stack, and the abort path is what actually exercises the page's `.catch` handlers.
- The success toast in R3-06-07 lives ~2.2 s (`element.ts:1523-1529`); poll for it at ≤ 250 ms intervals
  rather than asserting once after a delay.

---

## Part 4 — Appendix — history

**Superseded — kept for reference only, do not re-run.** These two files planned and shipped the
*original* landing page on 2026-06-30, before the PRD/refresh above rebuilt its argument. Facts in
them are stale (e.g. the green→blue gradient they specify was retired per
[Part 1](#part-1--prd-content-contract)'s design-system decision) — treat everything below as
historical record, not current direction.

### Appendix A — original 2026-06-30 implementation plan

# Pointer Landing Page Implementation Plan

> **✅ STATUS: SHIPPED TO PRODUCTION (2026-06-30).** All 8 tasks complete, individually reviewed, final whole-branch review clean. Live at `pointer.moamen.work` (bare domain now serves the bilingual landing page; redirect-to-app removed). Netlify backend decommissioned in the `Pointer` repo. The `- [ ]` checkboxes were not ticked during execution — progress tracked in `.superpowers/sdd/progress.md`. Treat this plan as DONE; do not re-run it.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship a single-page, bilingual (en/ar) marketing landing page for Pointer at the bare domain `pointer.moamen.work`, and decommission the legacy Netlify backend.

**Architecture:** One self-contained static `index.html` (inline `<style>` + inline `<script>`, no framework, no build step) lives in `pointer-api/landing/`, is copied to `/srv/landing` on the VM, and is served by Caddy at `pointer.moamen.work` (changed from a redirect-to-app). A separate workstream in the legacy `Pointer` repo removes the Netlify files and corrects the docs.

**Tech Stack:** Plain HTML5 + CSS (flex/grid, logical properties for RTL) + vanilla JS (i18n dictionary, language toggle, scripted hero animation). Caddy static file server. No npm, no dependencies.

## Global Constraints

- **Two repos.** Landing page + Caddy/deploy live in `pointer-api` (the VM pulls this). Netlify removal + doc cleanup live in `Pointer` (`/Users/momen/Desktop/REPOS/Pointer`).
- **Zero build step / zero dependencies** for the landing page — one self-contained `index.html`; CSS and JS inline; brand assets sit alongside in `pointer-api/landing/assets/` or are inlined as data URIs.
- **CTAs (exact URLs):** "Try the demo" → `https://demo.pointer.moamen.work`; "Sign in" and "Create account" → `https://app.pointer.moamen.work`.
- **Bilingual:** English + Arabic. Default English; if `navigator.language` starts with `ar`, start in Arabic. A persisted choice (`localStorage` key `pointer_lang`) always wins. Toggle in the nav. Arabic sets `<html lang="ar" dir="rtl">`.
- **Brand:** green→blue gradient (`#16a34a` → `#2563eb`), dog mascot (`assets/dog-mascot.png` from the `Pointer` repo root), light theme (slate `#0f172a` text on white / soft-gradient sections), `system-ui` font stack.
- **Accessibility/responsive:** honors `prefers-reduced-motion` (static animation frame); fully responsive mobile→desktop; the page body must never scroll horizontally.
- **GitHub footer link:** `https://github.com/moamen-ui` (confirm the exact repo with the user at build time; if repos are private, the link target is still the org page).
- **Bare domain switch:** `pointer.moamen.work` stops 301-redirecting to the app and serves the landing page. `app.pointer` and `demo.pointer` are unchanged.
- **No deploy until the user's explicit go.** Build + verify locally first.
- **Verification, not unit tests:** this is a static page with no JS test framework. Each task's "test" is a concrete render/behavior check (grep the HTML, serve it on `python3 -m http.server 8799`, and/or a browser check). Use the browser tooling available to the worker; fall back to `curl`/`grep` where a headless check suffices.

---

## File Structure

**`pointer-api` repo:**
- Create: `landing/index.html` — the entire landing page (HTML + inline CSS + inline JS).
- Create: `landing/assets/dog-mascot.png` — copied from `Pointer/dog-mascot.png` (or a resized copy).
- Create: `landing/assets/favicon.ico` — copied from `Pointer/favicon.ico`.
- Modify: `Caddyfile` — replace the `pointer.moamen.work` redirect block with a static file server.
- Modify: `DEPLOY.md` — document the landing build/deploy step.

**`Pointer` repo (Netlify decommission):**
- Delete: `netlify.toml`, `netlify/functions/api.mjs` (+ the `netlify/` dir), root `package.json`, `public/index.html` (+ `public/` if otherwise empty of source).
- Modify: `.gitignore` (drop the `.netlify/` line), `README.md`, `AGENTS.md`, `CLAUDE.md`.

---

## Task 1: Page scaffold — `<head>`, brand tokens, sticky nav

**Files:**
- Create: `pointer-api/landing/index.html`

**Interfaces:**
- Produces: the document skeleton with CSS custom properties `--pf-green`, `--pf-blue`, `--pf-text`, `--pf-bg`, gradient helpers, and a sticky `<header class="nav">` containing the logo, anchor links, a language toggle button `#lang-toggle`, and the two CTAs. Later tasks append `<section>`s into `<main>` and i18n attributes.

- [ ] **Step 1: Create `landing/index.html` with head + brand tokens + nav**

```html
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>Pointer — click-to-comment feedback your team turns into code with AI</title>
  <meta name="description" content="Pointer lets anyone on your team click an element on a running app and leave a comment. Developers hand the queue to any AI tool, which applies the change to the real source." />
  <link rel="icon" href="assets/favicon.ico" />
  <style>
    :root {
      --pf-green: #16a34a;
      --pf-blue: #2563eb;
      --pf-text: #0f172a;
      --pf-muted: #475569;
      --pf-bg: #ffffff;
      --pf-soft: #f1f5f9;
      --pf-border: #e2e8f0;
      --pf-grad: linear-gradient(135deg, var(--pf-green), var(--pf-blue));
      --pf-radius: 14px;
      --pf-max: 1080px;
    }
    * { box-sizing: border-box; }
    html, body { margin: 0; padding: 0; }
    body {
      font-family: system-ui, -apple-system, "Segoe UI", Roboto, sans-serif;
      color: var(--pf-text);
      background: var(--pf-bg);
      line-height: 1.6;
      overflow-x: hidden;            /* never scroll the body sideways */
    }
    img { max-width: 100%; display: block; }
    a { color: var(--pf-blue); text-decoration: none; }
    .wrap { max-width: var(--pf-max); margin: 0 auto; padding: 0 20px; }
    .btn {
      display: inline-flex; align-items: center; gap: 8px;
      padding: 11px 18px; border-radius: var(--pf-radius);
      font-weight: 700; font-size: 15px; cursor: pointer; border: 1px solid var(--pf-border);
      background: #fff; color: var(--pf-text); white-space: nowrap;
    }
    .btn.primary { background: var(--pf-grad); color: #fff; border: none; }
    .btn:focus-visible { outline: 3px solid color-mix(in srgb, var(--pf-blue) 50%, transparent); }
    /* Sticky nav */
    .nav {
      position: sticky; top: 0; z-index: 10;
      background: color-mix(in srgb, #fff 88%, transparent);
      backdrop-filter: saturate(1.4) blur(8px);
      border-bottom: 1px solid var(--pf-border);
    }
    .nav .wrap { display: flex; align-items: center; gap: 16px; height: 64px; }
    .brand { display: flex; align-items: center; gap: 10px; font-weight: 800; font-size: 18px; }
    .brand img { width: 30px; height: 30px; }
    .nav-links { display: flex; gap: 18px; margin-inline-start: auto; }
    .nav-links a { color: var(--pf-muted); font-weight: 600; font-size: 14px; }
    .nav-cta { display: flex; gap: 10px; align-items: center; }
    #lang-toggle { background: var(--pf-soft); border: 1px solid var(--pf-border); border-radius: 999px; padding: 6px 12px; font-weight: 700; cursor: pointer; }
    @media (max-width: 760px) {
      .nav-links { display: none; }
    }
  </style>
</head>
<body>
  <header class="nav">
    <div class="wrap">
      <div class="brand"><img src="assets/dog-mascot.png" alt="" /> <span>Pointer</span></div>
      <nav class="nav-links">
        <a href="#how" data-i18n="nav.how">How it works</a>
        <a href="#features" data-i18n="nav.features">Features</a>
        <a href="#demo" data-i18n="nav.demo">Demo</a>
      </nav>
      <div class="nav-cta">
        <button id="lang-toggle" type="button" aria-label="Switch language">العربية</button>
        <a class="btn" href="https://app.pointer.moamen.work" data-i18n="nav.signin">Sign in</a>
        <a class="btn primary" href="https://demo.pointer.moamen.work" data-i18n="nav.demoCta">Try the demo</a>
      </div>
    </div>
  </header>

  <main></main>

  <script>
    // i18n + animation wiring added in later tasks.
  </script>
</body>
</html>
```

- [ ] **Step 2: Copy brand assets into place**

Run:
```bash
mkdir -p /Users/momen/Desktop/REPOS/pointer-api/landing/assets
cp /Users/momen/Desktop/REPOS/Pointer/dog-mascot.png /Users/momen/Desktop/REPOS/pointer-api/landing/assets/dog-mascot.png
cp /Users/momen/Desktop/REPOS/Pointer/favicon.ico /Users/momen/Desktop/REPOS/pointer-api/landing/assets/favicon.ico
```
Expected: both files exist under `landing/assets/`.

- [ ] **Step 3: Serve and verify it loads**

Run:
```bash
cd /Users/momen/Desktop/REPOS/pointer-api/landing && python3 -m http.server 8799 >/dev/null 2>&1 &
sleep 1
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:8799/index.html
curl -s http://localhost:8799/index.html | grep -c 'Try the demo'
```
Expected: `200`, and `1` (the demo CTA present). Then stop the server: `pkill -f "http.server 8799"`.

- [ ] **Step 4: Commit**

```bash
cd /Users/momen/Desktop/REPOS/pointer-api
git add landing/index.html landing/assets/
git commit -m "feat(landing): page scaffold — head, brand tokens, sticky nav"
```

---

## Task 2: Hero section + CTAs

**Files:**
- Modify: `pointer-api/landing/index.html` (insert into `<main>`, add CSS in the `<style>` block)

**Interfaces:**
- Consumes: brand tokens + `.btn` styles from Task 1.
- Produces: `<section class="hero">` with `#hero-anim` (an empty stage the animation in Task 4 fills), headline, subhead, and the two CTAs.

- [ ] **Step 1: Add hero markup as the first child of `<main>`**

```html
<section class="hero">
  <div class="wrap hero-grid">
    <div class="hero-copy">
      <h1 data-i18n="hero.title">Point at the UI. Ship it with AI.</h1>
      <p class="lead" data-i18n="hero.sub">Anyone on your team clicks an element on a running app and leaves a short comment. Developers hand the queue to any AI tool, which applies the change to the real source — no more translating vague feedback into code.</p>
      <div class="hero-cta">
        <a class="btn primary" href="https://demo.pointer.moamen.work" data-i18n="hero.demo">Try the demo — no install</a>
        <a class="btn" href="https://app.pointer.moamen.work" data-i18n="hero.signup">Create an account</a>
      </div>
    </div>
    <div class="hero-stage" id="hero-anim" aria-hidden="true"></div>
  </div>
</section>
```

- [ ] **Step 2: Add hero CSS to the `<style>` block (before the closing `</style>`)**

```css
.hero { padding: 64px 0 48px; background: radial-gradient(1200px 400px at 70% -10%, color-mix(in srgb, var(--pf-blue) 12%, transparent), transparent); }
.hero-grid { display: grid; grid-template-columns: 1.1fr 1fr; gap: 40px; align-items: center; }
.hero h1 { font-size: clamp(30px, 5vw, 52px); line-height: 1.1; margin: 0 0 16px; letter-spacing: -0.02em; }
.hero .lead { font-size: 18px; color: var(--pf-muted); margin: 0 0 28px; }
.hero-cta { display: flex; flex-wrap: wrap; gap: 12px; }
.hero-stage { min-height: 320px; }
@media (max-width: 860px) {
  .hero-grid { grid-template-columns: 1fr; }
  .hero-stage { order: -1; min-height: 240px; }
}
```

- [ ] **Step 3: Verify CTAs point to the right hosts**

Run:
```bash
cd /Users/momen/Desktop/REPOS/pointer-api/landing
grep -o 'https://demo.pointer.moamen.work' index.html | head -1
grep -o 'https://app.pointer.moamen.work' index.html | head -1
```
Expected: each prints its URL (both CTAs wired).

- [ ] **Step 4: Commit**

```bash
cd /Users/momen/Desktop/REPOS/pointer-api
git add landing/index.html
git commit -m "feat(landing): hero section + CTAs"
```

---

## Task 3: Content sections — problem→solution, how-it-works, features, team split, final CTA, footer

**Files:**
- Modify: `pointer-api/landing/index.html`

**Interfaces:**
- Consumes: `.wrap`, `.btn`, brand tokens.
- Produces: `<section>`s with `id="how"`, `id="features"`, `id="demo"`, and a `<footer>` containing the GitHub link. All user-facing strings carry `data-i18n` keys (defined in Task 5).

- [ ] **Step 1: Append the sections after the hero, inside `<main>`**

```html
<!-- Problem → solution -->
<section class="band">
  <div class="wrap">
    <div class="contrast">
      <div class="contrast-card bad">
        <h3 data-i18n="contrast.badTitle">Without Pointer</h3>
        <p data-i18n="contrast.bad">"Go to the checkout page, find the header, make the title 24px and a bit darker…"</p>
      </div>
      <div class="contrast-card good">
        <h3 data-i18n="contrast.goodTitle">With Pointer</h3>
        <p data-i18n="contrast.good">🐕 click the title → 💬 "make this 24px" → ✨ AI applies it.</p>
      </div>
    </div>
  </div>
</section>

<!-- How it works -->
<section id="how" class="wrap section">
  <h2 class="section-title" data-i18n="how.title">How it works</h2>
  <div class="steps">
    <div class="step"><span class="num">1</span><h3 data-i18n="how.s1t">Click & comment</h3><p data-i18n="how.s1">Anyone signed in clicks any element on the running app and leaves a short comment.</p></div>
    <div class="step"><span class="num">2</span><h3 data-i18n="how.s2t">Collected & tagged</h3><p data-i18n="how.s2">Comments are stored per project and tagged with environment, stakeholder, and author.</p></div>
    <div class="step"><span class="num">3</span><h3 data-i18n="how.s3t">Applied by AI</h3><p data-i18n="how.s3">A developer hands the queue to any AI tool, which edits the real source files.</p></div>
  </div>
</section>

<!-- Features -->
<section id="features" class="band">
  <div class="wrap section">
    <h2 class="section-title" data-i18n="feat.title">Built for real teams</h2>
    <div class="features">
      <div class="feature"><h3 data-i18n="feat.f1t">Two-line install</h3><p data-i18n="feat.f1">Drop a script tag and a tag. No package, no SDK.</p></div>
      <div class="feature"><h3 data-i18n="feat.f2t">Multi-project</h3><p data-i18n="feat.f2">One server serves many apps, partitioned by project.</p></div>
      <div class="feature"><h3 data-i18n="feat.f3t">Multi-stakeholder</h3><p data-i18n="feat.f3">Every comment is tagged by environment, stakeholder, and author.</p></div>
      <div class="feature"><h3 data-i18n="feat.f4t">Element + source aware</h3><p data-i18n="feat.f4">Captures the selector, snapshot, the CSS that wins, the page route, and an optional source path.</p></div>
      <div class="feature"><h3 data-i18n="feat.f5t">AI-agnostic</h3><p data-i18n="feat.f5">Any AI tool applies the changes with plain HTTP — Claude Code, Cursor, and more.</p></div>
      <div class="feature"><h3 data-i18n="feat.f6t">Style-isolated</h3><p data-i18n="feat.f6">The widget renders in a Shadow DOM, so it never clashes with your app's CSS.</p></div>
      <div class="feature"><h3 data-i18n="feat.f7t">A real dashboard</h3><p data-i18n="feat.f7">Triage, statuses, roles, and per-project views — for the whole team.</p></div>
      <div class="feature"><h3 data-i18n="feat.f8t">Multi-tenant</h3><p data-i18n="feat.f8">Each workspace sees only its own data, enforced server-side.</p></div>
    </div>
  </div>
</section>

<!-- Built for the whole team -->
<section class="wrap section">
  <div class="split">
    <div class="split-card"><h3 data-i18n="team.stkT">For stakeholders</h3><p data-i18n="team.stk">Point and comment on the live app — no install, no setup. Clients, PMs, and testers just click.</p></div>
    <div class="split-card"><h3 data-i18n="team.devT">For developers</h3><p data-i18n="team.dev">Pull the queue and let any AI apply the changes to the real source. Comments carry the page route and the CSS that actually wins.</p></div>
  </div>
</section>

<!-- Final CTA -->
<section id="demo" class="cta-band">
  <div class="wrap">
    <h2 data-i18n="final.title">Try it in one click — no install</h2>
    <div class="hero-cta" style="justify-content:center">
      <a class="btn primary" href="https://demo.pointer.moamen.work" data-i18n="final.demo">Try the demo</a>
      <a class="btn" href="https://app.pointer.moamen.work" data-i18n="final.signup">Create an account</a>
    </div>
  </div>
</section>

<footer class="footer">
  <div class="wrap footer-grid">
    <div class="brand"><img src="assets/dog-mascot.png" alt="" /> <span>Pointer</span></div>
    <div class="footer-links">
      <a href="https://app.pointer.moamen.work" data-i18n="foot.dashboard">Dashboard</a>
      <a href="https://api.pointer.moamen.work/pointer-init.md" data-i18n="foot.docs">Docs</a>
      <a href="https://github.com/moamen-ui" rel="noopener" target="_blank">GitHub</a>
    </div>
  </div>
</footer>
```

- [ ] **Step 2: Append section CSS to the `<style>` block**

```css
.section { padding: 56px 0; }
.section-title { font-size: clamp(24px, 3vw, 34px); margin: 0 0 28px; text-align: center; }
.band { background: var(--pf-soft); }
.contrast { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; padding: 40px 0; }
.contrast-card { background: #fff; border: 1px solid var(--pf-border); border-radius: var(--pf-radius); padding: 20px; }
.contrast-card.bad { color: var(--pf-muted); }
.contrast-card.good { border-color: color-mix(in srgb, var(--pf-green) 50%, var(--pf-border)); }
.steps, .features { display: grid; gap: 18px; }
.steps { grid-template-columns: repeat(3, 1fr); }
.features { grid-template-columns: repeat(4, 1fr); }
.step, .feature { background: #fff; border: 1px solid var(--pf-border); border-radius: var(--pf-radius); padding: 20px; }
.step .num { display: inline-grid; place-items: center; width: 30px; height: 30px; border-radius: 50%; background: var(--pf-grad); color: #fff; font-weight: 800; margin-bottom: 10px; }
.step h3, .feature h3 { margin: 0 0 6px; font-size: 17px; }
.step p, .feature p { margin: 0; color: var(--pf-muted); font-size: 14px; }
.split { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; }
.split-card { padding: 24px; border-radius: var(--pf-radius); border: 1px solid var(--pf-border); background: #fff; }
.cta-band { background: var(--pf-grad); color: #fff; text-align: center; padding: 56px 0; }
.cta-band h2 { font-size: clamp(24px, 3.4vw, 36px); margin: 0 0 22px; }
.footer { border-top: 1px solid var(--pf-border); padding: 28px 0; }
.footer-grid { display: flex; align-items: center; justify-content: space-between; gap: 16px; flex-wrap: wrap; }
.footer-links { display: flex; gap: 18px; }
.footer-links a { color: var(--pf-muted); font-weight: 600; }
@media (max-width: 860px) {
  .steps { grid-template-columns: 1fr; }
  .features { grid-template-columns: 1fr 1fr; }
  .contrast, .split { grid-template-columns: 1fr; }
}
@media (max-width: 520px) {
  .features { grid-template-columns: 1fr; }
}
```

- [ ] **Step 3: Verify structure + GitHub link present**

Run:
```bash
cd /Users/momen/Desktop/REPOS/pointer-api/landing
grep -c 'id="how"\|id="features"\|id="demo"' index.html
grep -c 'github.com/moamen-ui' index.html
```
Expected: `3` (the three anchor targets) and `1` (GitHub link).

- [ ] **Step 4: Commit**

```bash
cd /Users/momen/Desktop/REPOS/pointer-api
git add landing/index.html
git commit -m "feat(landing): content sections + footer with GitHub link"
```

---

## Task 4: Scripted hero animation + reduced-motion fallback

**Files:**
- Modify: `pointer-api/landing/index.html` (CSS in `<style>`, markup inside `#hero-anim`, JS in the `<script>` is NOT required — the loop is CSS-driven)

**Interfaces:**
- Consumes: `#hero-anim` from Task 2.
- Produces: a self-running CSS animation (mock app card + cursor + highlight + typed popover + dropped pin). No backend. A `@media (prefers-reduced-motion: reduce)` rule freezes it to a representative static frame.

- [ ] **Step 1: Replace the empty `#hero-anim` with the mock-UI markup**

```html
<div class="hero-stage" id="hero-anim" aria-hidden="true">
  <div class="mock">
    <div class="mock-bar"><span></span><span></span><span></span></div>
    <button class="mock-target">Buy now</button>
    <div class="mock-text"></div>
    <div class="mock-text short"></div>
    <span class="pf-cursor"></span>
    <span class="pf-ring"></span>
    <div class="pf-pop"><b data-i18n="anim.label">Make this bolder</b></div>
    <span class="pf-pin">1</span>
  </div>
</div>
```

- [ ] **Step 2: Add the animation CSS to the `<style>` block**

```css
.mock { position: relative; background: #fff; border: 1px solid var(--pf-border); border-radius: var(--pf-radius); box-shadow: 0 20px 50px rgba(2,6,23,.12); padding: 22px; height: 320px; overflow: hidden; }
.mock-bar { display: flex; gap: 6px; margin-bottom: 18px; }
.mock-bar span { width: 11px; height: 11px; border-radius: 50%; background: var(--pf-border); }
.mock-target { font: inherit; font-weight: 700; padding: 10px 16px; border-radius: 10px; border: none; color: #fff; background: var(--pf-grad); }
.mock-text { height: 12px; border-radius: 6px; background: var(--pf-soft); margin-top: 16px; }
.mock-text.short { width: 60%; }
.pf-cursor { position: absolute; width: 18px; height: 18px; border-radius: 50%; border: 2px solid var(--pf-blue); background: rgba(37,99,235,.2); top: 60%; left: 60%; animation: pf-cursor 6s ease-in-out infinite; }
.pf-ring { position: absolute; left: 22px; top: 56px; width: 96px; height: 42px; border: 2px solid var(--pf-blue); border-radius: 10px; opacity: 0; animation: pf-ring 6s ease-in-out infinite; }
.pf-pop { position: absolute; left: 130px; top: 52px; background: #0f172a; color: #fff; font-size: 13px; padding: 8px 12px; border-radius: 10px; opacity: 0; transform: translateY(4px); animation: pf-pop 6s ease-in-out infinite; white-space: nowrap; }
.pf-pin { position: absolute; left: 96px; top: 44px; width: 22px; height: 22px; border-radius: 50% 50% 50% 2px; background: var(--pf-grad); color: #fff; font-size: 12px; font-weight: 800; display: grid; place-items: center; opacity: 0; animation: pf-pin 6s ease-in-out infinite; }
@keyframes pf-cursor {
  0% { top: 75%; left: 70%; } 25% { top: 60px; left: 56px; } 100% { top: 60px; left: 56px; }
}
@keyframes pf-ring { 0%,18% { opacity: 0; } 28%,100% { opacity: 1; } }
@keyframes pf-pop { 0%,30% { opacity: 0; transform: translateY(4px); } 42%,92% { opacity: 1; transform: translateY(0); } 100% { opacity: 0; } }
@keyframes pf-pin { 0%,40% { opacity: 0; transform: scale(.4); } 52%,100% { opacity: 1; transform: scale(1); } }
@media (prefers-reduced-motion: reduce) {
  .pf-cursor, .pf-ring, .pf-pop, .pf-pin { animation: none; }
  .pf-ring, .pf-pop, .pf-pin { opacity: 1; }       /* show the representative end frame */
  .pf-cursor { top: 60px; left: 56px; }
}
```

- [ ] **Step 2b: Verify reduced-motion static frame in a browser**

Serve (`python3 -m http.server 8799` in `landing/`). In the browser worker, emulate reduced motion and confirm the pin/popover/ring are visible (static) and no animation runs. If only `curl` is available, verify the media query exists:
```bash
grep -c 'prefers-reduced-motion: reduce' /Users/momen/Desktop/REPOS/pointer-api/landing/index.html
```
Expected: `1`.

- [ ] **Step 3: Verify the animation renders without console errors**

Serve and open `http://localhost:8799/` in the browser worker; confirm the mock card, cursor, popover, and pin elements exist in the DOM and there are no console errors. Stop the server afterwards.

- [ ] **Step 4: Commit**

```bash
cd /Users/momen/Desktop/REPOS/pointer-api
git add landing/index.html
git commit -m "feat(landing): scripted hero animation + reduced-motion fallback"
```

---

## Task 5: Bilingual i18n (en/ar), toggle, RTL, persistence, auto-detect

**Files:**
- Modify: `pointer-api/landing/index.html` (fill the inline `<script>`, add RTL CSS)

**Interfaces:**
- Consumes: every element carrying a `data-i18n="<key>"` attribute from Tasks 1–4.
- Produces: a `STRINGS` dictionary with `en` and `ar` for every key used, an `applyLang(lang)` function (sets `textContent` for each `[data-i18n]`, sets `<html lang>`/`dir`, updates the toggle label, persists to `localStorage`), and the toggle wiring + first-load language resolution.

- [ ] **Step 1: Fill the inline `<script>` with the dictionary + apply logic**

```html
<script>
  var STRINGS = {
    en: {
      "nav.how": "How it works", "nav.features": "Features", "nav.demo": "Demo",
      "nav.signin": "Sign in", "nav.demoCta": "Try the demo",
      "hero.title": "Point at the UI. Ship it with AI.",
      "hero.sub": "Anyone on your team clicks an element on a running app and leaves a short comment. Developers hand the queue to any AI tool, which applies the change to the real source — no more translating vague feedback into code.",
      "hero.demo": "Try the demo — no install", "hero.signup": "Create an account",
      "contrast.badTitle": "Without Pointer", "contrast.bad": "\"Go to the checkout page, find the header, make the title 24px and a bit darker…\"",
      "contrast.goodTitle": "With Pointer", "contrast.good": "🐕 click the title → 💬 \"make this 24px\" → ✨ AI applies it.",
      "how.title": "How it works",
      "how.s1t": "Click & comment", "how.s1": "Anyone signed in clicks any element on the running app and leaves a short comment.",
      "how.s2t": "Collected & tagged", "how.s2": "Comments are stored per project and tagged with environment, stakeholder, and author.",
      "how.s3t": "Applied by AI", "how.s3": "A developer hands the queue to any AI tool, which edits the real source files.",
      "feat.title": "Built for real teams",
      "feat.f1t": "Two-line install", "feat.f1": "Drop a script tag and a tag. No package, no SDK.",
      "feat.f2t": "Multi-project", "feat.f2": "One server serves many apps, partitioned by project.",
      "feat.f3t": "Multi-stakeholder", "feat.f3": "Every comment is tagged by environment, stakeholder, and author.",
      "feat.f4t": "Element + source aware", "feat.f4": "Captures the selector, snapshot, the CSS that wins, the page route, and an optional source path.",
      "feat.f5t": "AI-agnostic", "feat.f5": "Any AI tool applies the changes with plain HTTP — Claude Code, Cursor, and more.",
      "feat.f6t": "Style-isolated", "feat.f6": "The widget renders in a Shadow DOM, so it never clashes with your app's CSS.",
      "feat.f7t": "A real dashboard", "feat.f7": "Triage, statuses, roles, and per-project views — for the whole team.",
      "feat.f8t": "Multi-tenant", "feat.f8": "Each workspace sees only its own data, enforced server-side.",
      "team.stkT": "For stakeholders", "team.stk": "Point and comment on the live app — no install, no setup. Clients, PMs, and testers just click.",
      "team.devT": "For developers", "team.dev": "Pull the queue and let any AI apply the changes to the real source. Comments carry the page route and the CSS that actually wins.",
      "final.title": "Try it in one click — no install", "final.demo": "Try the demo", "final.signup": "Create an account",
      "foot.dashboard": "Dashboard", "foot.docs": "Docs",
      "anim.label": "Make this bolder"
    },
    ar: {
      "nav.how": "كيف يعمل", "nav.features": "المميزات", "nav.demo": "تجربة",
      "nav.signin": "تسجيل الدخول", "nav.demoCta": "جرّب العرض",
      "hero.title": "أشِر إلى الواجهة. ونفّذ بالذكاء الاصطناعي.",
      "hero.sub": "أي عضو في فريقك ينقر على عنصر في التطبيق الفعلي ويترك تعليقًا قصيرًا. ثم يسلّم المطوّر قائمة الملاحظات لأي أداة ذكاء اصطناعي لتطبّق التغيير على الكود المصدري مباشرة — دون ترجمة ملاحظات غامضة إلى كود.",
      "hero.demo": "جرّب العرض — بدون تثبيت", "hero.signup": "أنشئ حسابًا",
      "contrast.badTitle": "بدون Pointer", "contrast.bad": "«اذهب إلى صفحة الدفع، وابحث عن العنوان، واجعل حجمه ٢٤ بكسل وأغمق قليلًا…»",
      "contrast.goodTitle": "مع Pointer", "contrast.good": "🐕 انقر العنوان ← 💬 «اجعله ٢٤ بكسل» ← ✨ يطبّقه الذكاء الاصطناعي.",
      "how.title": "كيف يعمل",
      "how.s1t": "انقر وعلّق", "how.s1": "أي مستخدم مسجّل ينقر على أي عنصر في التطبيق ويترك تعليقًا قصيرًا.",
      "how.s2t": "تُجمع وتُصنّف", "how.s2": "تُحفظ التعليقات لكل مشروع وتُوسم بالبيئة والجهة المعنية والكاتب.",
      "how.s3t": "يطبّقها الذكاء الاصطناعي", "how.s3": "يسلّم المطوّر القائمة لأي أداة ذكاء اصطناعي فتعدّل ملفات الكود الفعلية.",
      "feat.title": "مصمّم للفرق الحقيقية",
      "feat.f1t": "تركيب بسطرين", "feat.f1": "أضِف وسم سكربت ووسم العنصر. بدون حزمة وبدون SDK.",
      "feat.f2t": "متعدد المشاريع", "feat.f2": "خادم واحد يخدم عدة تطبيقات، مقسّمة حسب المشروع.",
      "feat.f3t": "متعدد الجهات", "feat.f3": "كل تعليق موسوم بالبيئة والجهة المعنية والكاتب.",
      "feat.f4t": "مدرك للعنصر والمصدر", "feat.f4": "يلتقط المحدِّد واللقطة وقواعد CSS الفاعلة ومسار الصفحة ومسار المصدر اختياريًا.",
      "feat.f5t": "محايد تجاه الأدوات", "feat.f5": "أي أداة ذكاء اصطناعي تطبّق التغييرات عبر HTTP — Claude Code وCursor وغيرها.",
      "feat.f6t": "معزول الأنماط", "feat.f6": "تُعرض الأداة داخل Shadow DOM فلا تتعارض مع تنسيقات تطبيقك.",
      "feat.f7t": "لوحة تحكم حقيقية", "feat.f7": "فرز وحالات وأدوار وعروض لكل مشروع — لكل الفريق.",
      "feat.f8t": "متعدد المستأجرين", "feat.f8": "كل مساحة عمل ترى بياناتها فقط، مع فرض ذلك من الخادم.",
      "team.stkT": "للجهات المعنية", "team.stk": "أشِر وعلّق على التطبيق الحي — بدون تثبيت أو إعداد. العملاء ومديرو المنتج والمختبِرون ينقرون فقط.",
      "team.devT": "للمطوّرين", "team.dev": "اسحب القائمة ودع أي ذكاء اصطناعي يطبّق التغييرات على الكود الفعلي. تحمل التعليقات مسار الصفحة وقواعد CSS الفاعلة.",
      "final.title": "جرّبه بنقرة واحدة — بدون تثبيت", "final.demo": "جرّب العرض", "final.signup": "أنشئ حسابًا",
      "foot.dashboard": "لوحة التحكم", "foot.docs": "الوثائق",
      "anim.label": "اجعله أعرض"
    }
  };

  function applyLang(lang) {
    var dict = STRINGS[lang] || STRINGS.en;
    document.querySelectorAll('[data-i18n]').forEach(function (el) {
      var k = el.getAttribute('data-i18n');
      if (dict[k] != null) el.textContent = dict[k];
    });
    document.documentElement.lang = lang;
    document.documentElement.dir = (lang === 'ar') ? 'rtl' : 'ltr';
    var toggle = document.getElementById('lang-toggle');
    if (toggle) toggle.textContent = (lang === 'ar') ? 'English' : 'العربية';
    try { localStorage.setItem('pointer_lang', lang); } catch (e) {}
  }

  (function initLang() {
    var saved = null;
    try { saved = localStorage.getItem('pointer_lang'); } catch (e) {}
    var lang = saved || ((navigator.language || '').toLowerCase().indexOf('ar') === 0 ? 'ar' : 'en');
    applyLang(lang);
    var toggle = document.getElementById('lang-toggle');
    if (toggle) toggle.addEventListener('click', function () {
      applyLang(document.documentElement.lang === 'ar' ? 'en' : 'ar');
    });
  })();
</script>
```

- [ ] **Step 2: Add RTL adjustments to the `<style>` block**

```css
[dir="rtl"] .nav-links { margin-inline-start: 0; margin-inline-end: auto; }
[dir="rtl"] .step .num { }
[dir="rtl"] .pf-pop, [dir="rtl"] .pf-pin, [dir="rtl"] .pf-ring, [dir="rtl"] .pf-cursor { /* animation stage stays LTR (it mimics an app UI) */ }
[dir="rtl"] .hero-stage { direction: ltr; }
```

- [ ] **Step 3: Verify every `data-i18n` key exists in both dictionaries**

Run:
```bash
cd /Users/momen/Desktop/REPOS/pointer-api/landing
python3 - <<'PY'
import re, json
html = open('index.html', encoding='utf-8').read()
keys = set(re.findall(r'data-i18n="([^"]+)"', html))
# crude extraction of the two dicts' keys
en = set(re.findall(r'"([a-zA-Z.]+)":', html.split('en: {')[1].split('ar: {')[0]))
ar = set(re.findall(r'"([a-zA-Z.]+)":', html.split('ar: {')[1]))
missing_en = keys - en
missing_ar = keys - ar
print("keys in markup:", len(keys))
print("missing in en:", sorted(missing_en))
print("missing in ar:", sorted(missing_ar))
PY
```
Expected: `missing in en: []` and `missing in ar: []`.

- [ ] **Step 4: Verify the toggle flips dir/lang in a browser**

Serve and open in the browser worker. Click `#lang-toggle`; confirm `<html dir>` becomes `rtl`, Arabic text appears, the toggle label reads `English`, and `localStorage.pointer_lang === 'ar'`. Toggle back and confirm `ltr`/English.

- [ ] **Step 5: Commit**

```bash
cd /Users/momen/Desktop/REPOS/pointer-api
git add landing/index.html
git commit -m "feat(landing): bilingual en/ar i18n, toggle, RTL, persistence"
```

---

## Task 6: Responsive + cross-browser QA pass

**Files:**
- Modify: `pointer-api/landing/index.html` (only if QA finds issues)

**Interfaces:** none new — this is a verification gate over Tasks 1–5.

- [ ] **Step 1: No horizontal scroll at mobile width**

Serve; in the browser worker set viewport to 375×800 and confirm `document.documentElement.scrollWidth <= window.innerWidth` (no sideways scroll), in both `ltr` and `rtl`.

- [ ] **Step 2: CTAs are reachable and correct at mobile width**

At 375px confirm both hero CTAs and both final-band CTAs are visible and tappable, and their `href`s are the demo/app hosts.

- [ ] **Step 3: Lighthouse-ish sanity (optional, if browser worker supports it)**

Confirm no console errors, images have non-empty `alt` or `alt=""` (decorative), and the page has a single `<h1>`.

- [ ] **Step 4: Fix any issues inline, then commit (skip commit if nothing changed)**

```bash
cd /Users/momen/Desktop/REPOS/pointer-api
git add landing/index.html && git commit -m "fix(landing): responsive/RTL QA adjustments" || echo "no changes"
```

---

## Task 7: Caddy block + deploy wiring (NO deploy yet)

**Files:**
- Modify: `pointer-api/Caddyfile`
- Modify: `pointer-api/docker-compose.prod.yml`
- Modify: `pointer-api/DEPLOY.md`

**Interfaces:**
- Consumes: `landing/` from Tasks 1–6.
- Produces: a `pointer.moamen.work` static-server block, a `./landing:/srv/landing:ro` bind mount on the Caddy service (mirroring `./dashboard:/srv/dashboard:ro`), and a documented deploy step. The VM reload happens only at the user-approved deploy.

Context (confirmed): the Caddy service in `docker-compose.prod.yml` bind-mounts the repo's `dashboard/` dir read-only at `/srv/dashboard` and serves it. We mirror that for `landing/` → `/srv/landing` — **no copy step**, Caddy serves the repo dir directly, so a `git pull` updates the live files.

- [ ] **Step 1: Replace the redirect block in `Caddyfile`**

Find:
```
pointer.moamen.work {
    redir https://app.pointer.moamen.work{uri} permanent
}
```
Replace with:
```
# Bare domain → the marketing landing page (served from the bind-mounted landing/ dir).
pointer.moamen.work {
    root * /srv/landing
    encode gzip
    try_files {path} /index.html
    file_server
}
```

- [ ] **Step 2: Add the bind mount to the Caddy service in `docker-compose.prod.yml`**

Find the Caddy `volumes:` list (it contains `- ./dashboard:/srv/dashboard:ro`) and add, right after that line:
```yaml
      - ./landing:/srv/landing:ro
```

- [ ] **Step 3: Add the landing deploy step to `DEPLOY.md` (under "Updating")**

```markdown
**Landing page change** — from your machine `git push origin main`, then on the VM:

\`\`\`bash
cd ~/pointer-api && git pull --ff-only        # updates ./landing (bind-mounted into Caddy)
docker compose -f docker-compose.prod.yml up -d --force-recreate caddy
# force-recreate so the new Caddyfile + the landing bind-mount are picked up (single-file
# bind-mount inode gotcha).
\`\`\`
```

- [ ] **Step 4: Validate the Caddyfile parses (local, optional)**

Run (skip if docker/caddy unavailable; the VM start will validate either way):
```bash
cd /Users/momen/Desktop/REPOS/pointer-api && docker run --rm -v "$PWD/Caddyfile":/etc/caddy/Caddyfile caddy:2 caddy validate --config /etc/caddy/Caddyfile 2>&1 | tail -3 || echo "skip"
```
Expected: "Valid configuration" (or skip).

- [ ] **Step 5: Verify the edits are in place**

Run:
```bash
cd /Users/momen/Desktop/REPOS/pointer-api
grep -c 'root \* /srv/landing' Caddyfile
grep -c './landing:/srv/landing:ro' docker-compose.prod.yml
grep -c 'redir https://app.pointer.moamen.work' Caddyfile   # expect 0 — redirect removed
```
Expected: `1`, `1`, `0`.

- [ ] **Step 6: Commit**

```bash
cd /Users/momen/Desktop/REPOS/pointer-api
git add Caddyfile DEPLOY.md docker-compose.prod.yml
git commit -m "feat(landing): serve pointer.moamen.work as the landing page (Caddy bind-mount + deploy docs)"
```

---

## Task 8: Netlify decommission — remove files + correct docs (`Pointer` repo)

**Files (in `/Users/momen/Desktop/REPOS/Pointer`):**
- Delete: `netlify.toml`, `netlify/functions/api.mjs` (+ `netlify/`), `package.json`, `public/index.html`
- Modify: `.gitignore`, `README.md`, `AGENTS.md`, `CLAUDE.md`

**Interfaces:** none — independent cleanup. Safe because no live app points `VITE_POINTER_SERVER` at `tuwaiq-pointer.netlify.app` (user confirmed). Keeps `comments-skill/` (local zero-dep server) intact.

- [ ] **Step 1: Confirm nothing else references the deleted pieces**

Run:
```bash
cd /Users/momen/Desktop/REPOS/Pointer
grep -rIn "tuwaiq-pointer.netlify.app\|@netlify/blobs\|netlify/functions" . --exclude-dir=.git --exclude-dir=node_modules
```
Expected: only matches inside the files we're about to delete or the docs we're about to edit. If a `comments-skill/*` or `test.html` references the Netlify URL, note it for Step 3.

- [ ] **Step 2: Delete the Netlify files**

```bash
cd /Users/momen/Desktop/REPOS/Pointer
git rm -r netlify netlify.toml package.json public/index.html
# remove public/ entirely only if it has no other source files:
rmdir public 2>/dev/null || true
```
Expected: files staged for deletion.

- [ ] **Step 3: Remove the `.netlify/` line from `.gitignore`**

Edit `.gitignore` and delete the line `.netlify/` (leave the rest untouched).

- [ ] **Step 4: Correct the docs**

In `README.md`, `AGENTS.md`, and `CLAUDE.md`, remove/replace every mention of the Netlify backend and the `tuwaiq-pointer.netlify.app` URL so they describe only:
- the live SaaS on the Oracle VM — `api.pointer.moamen.work`, `app.pointer.moamen.work`, `demo.pointer.moamen.work` (and `pointer.moamen.work` = the landing page), and
- the local zero-dependency `node server.js` (in `comments-skill/`) for self-hosting.

Concretely:
- `README.md` Quick start: change the server URL examples from `https://tuwaiq-pointer.netlify.app` to `https://api.pointer.moamen.work`; in the hosting/"Local (solo)" section, drop the "point at the netlify URL" line and keep the local-server + tunnel guidance.
- `AGENTS.md`: delete the "Repo root — Netlify deployment wrapper" bullet, the `netlify/functions/api.mjs` bullets, the `npm install @netlify/blobs` build note, the "Netlify Blobs (deployed)" storage line, and the "Module format split" note's Netlify clause. Update the "Reconciling with CLAUDE.md" section to state the deployed backend is the .NET API on the VM, not Netlify.
- `CLAUDE.md`: this file describes the legacy `comments-skill` server design; remove any `GOOGLE_SCRIPT_URL`/Netlify-deploy claims that are now false and point readers to the SaaS as the deployed product. (Keep the local zero-dep server description.)

- [ ] **Step 5: Verify no stale references remain**

Run:
```bash
cd /Users/momen/Desktop/REPOS/Pointer
grep -rIn "tuwaiq-pointer.netlify.app\|@netlify/blobs\|netlify/functions\|Netlify Blobs" . --exclude-dir=.git --exclude-dir=node_modules || echo "clean"
```
Expected: `clean` (no matches).

- [ ] **Step 6: Commit**

```bash
cd /Users/momen/Desktop/REPOS/Pointer
git add -A
git commit -m "chore: decommission Netlify backend; docs describe the VM SaaS + local server"
```

---

## Deploy (single, user-approved — do NOT run until the user says go)

1. **Landing (pointer-api):** `git push origin main`; on the VM `cd ~/pointer-api && git pull --ff-only` (updates the bind-mounted `./landing`), then `docker compose -f docker-compose.prod.yml up -d --force-recreate caddy` (force-recreate so the new `Caddyfile` + the `landing` bind-mount are picked up — single-file bind-mount inode gotcha).
2. **Netlify decommission (Pointer):** `git push origin main` (and, if desired, delete the Netlify site from the Netlify dashboard — manual, outside this repo).
3. **Live verify:**
   - `curl -sI https://pointer.moamen.work/` → `200` and serves the landing page (not a 301 to the app).
   - `curl -s https://pointer.moamen.work/ | grep -c 'demo.pointer.moamen.work'` → ≥1.
   - `curl -sI https://app.pointer.moamen.work/` and `https://demo.pointer.moamen.work/` → still `200` (unchanged).
   - In a browser: language toggle flips en↔ar/RTL, animation runs, reduced-motion shows a static frame, CTAs navigate to demo/app.

## Out of scope

Pricing/billing (Phase 3), blog/CMS, analytics, A/B testing, a real embedded live widget (the scripted animation stands in), and any change to the app or demo dashboards.

---

### Appendix B — original 2026-06-30 design spec

# Pointer Landing Page — Design

Date: 2026-06-30
Status: Approved (brainstorming complete; ready for implementation plan)

## Summary

A single-page, bilingual (en/ar) marketing landing page for Pointer, served at the
bare domain **`pointer.moamen.work`** (which today merely redirects to the app). It
explains the product to a **whole product team** and drives two CTAs: **Try the demo**
(→ `demo.pointer.moamen.work`) and **Create account / Sign in** (→ `app.pointer.moamen.work`).
The page is a self-contained static `index.html` (inline CSS + vanilla JS, no build step),
matching the product's zero-dependency identity. The hero centerpiece is a self-running,
backend-free **scripted animation** of the widget in action. As part of the same effort,
the **legacy Netlify backend is decommissioned** and the docs that reference it are
corrected.

This effort spans two repos:
- **`pointer-api`** — the landing page source + Caddy/deploy wiring (the VM pulls this repo).
- **`Pointer`** (the legacy/widget repo) — Netlify file removal + doc cleanup.

## Goals & success criteria

- `pointer.moamen.work` serves the landing page (no longer redirects to the app);
  `app.pointer` and `demo.pointer` are unchanged.
- Both CTAs work: "Try the demo" → `https://demo.pointer.moamen.work`,
  "Create account"/"Sign in" → `https://app.pointer.moamen.work`.
- The scripted hero animation runs and loops; honors `prefers-reduced-motion` with a static fallback.
- Fully responsive (mobile → desktop); no horizontal body scroll.
- Bilingual: English + Arabic, toggle in the nav, correct `dir`/`lang`, RTL layout for Arabic, choice persisted.
- Netlify backend decommissioned; `README.md`, `AGENTS.md`, `CLAUDE.md` no longer make stale Netlify claims.

## Audience & positioning

Primary audience: the **whole product team**. Core message: *everyone points at the UI;
developers ship it with AI.* The page balances the stakeholder-feedback story (anyone
clicks an element and comments, no install) with the developer-apply story (pull the
queue, hand it to any AI, it edits the real source).

## Tech & hosting

- **One self-contained `index.html`** — inline `<style>` + a small inline `<script>`;
  brand assets (dog mascot, favicon) placed alongside or inlined as data URIs. **No
  framework, no build step.** Fast and Lighthouse-friendly.
- **Source location:** `pointer-api/landing/` (next to the `Caddyfile` and deploy script).
- **Deploy:** the existing VM deploy pipeline copies `landing/` → `/srv/landing` on the VM
  (same mechanism that ships the demo dashboard to `/srv/dashboard`).
- **Caddy:** change the `pointer.moamen.work` block from a redirect-to-app to a static
  file server:
  ```
  pointer.moamen.work {
      root * /srv/landing
      encode gzip
      file_server
  }
  ```
  Caddy single-file bind-mount gotcha applies if relevant: `docker compose up -d
  --force-recreate caddy` after the Caddyfile changes (new inode).

## Page structure (top → bottom)

1. **Sticky nav** — logo + mascot; anchor links (How it works · Features · Demo);
   **language toggle (EN / ع)**; `Sign in` (→ app) + primary `Try the demo` (→ demo).
2. **Hero** — team-wide headline + subhead, the two CTAs, and the scripted animation as the visual.
3. **Problem → solution** — the contrast framing: *"Go to the checkout page, find the
   header, make the title 24px"* → *click the title → 💬 "make this 24px" → ✨ AI applies it.*
4. **How it works (3 steps)** — ① anyone clicks an element & leaves a short comment
   ② comments are collected and tagged `{ project, environment, stakeholder }`
   ③ a developer hands the queue to any AI tool, which applies the change to the real source.
5. **Features grid** — two-line install · multi-project · multi-stakeholder/environment ·
   element + source aware · AI-agnostic · Shadow-DOM style isolation · dashboard · multi-tenant.
6. **Built for the whole team** — split panel: *stakeholders* (point & comment, no install)
   vs *developers* (pull the queue, AI applies).
7. **Final CTA band** — "Try it in one click, no install" → demo + signup.
8. **Footer** — links (dashboard, docs, **GitHub** — `github.com/moamen-ui`, exact repo confirmed at build), brand, mascot.

## Visual design

Existing brand: **green→blue gradient**, dog mascot, light theme (slate `#0f172a` text on
white / soft-gradient sections), `system-ui` font stack. Generous whitespace, gradient
accents on CTAs and section dividers. Responsive via flex/grid with `max-width: 100%` media.

## Scripted animation (hero centerpiece)

Pure CSS/JS, self-running loop, **no backend, no abuse surface**. A mock "app UI" card
holds a few elements; an animated cursor glides to an element → a highlight outline
appears → a comment popover types out a short message (e.g., *"Make this bolder"*, localized)
→ a numbered pin drops. Styled to mirror the real `<pointer-feedback>` widget (toolbar,
pin, popover) in brand colors. Respects `prefers-reduced-motion`: when set, render a single
static annotated frame instead of animating.

## Bilingual (en / ar)

- A small JS i18n dictionary (`{ en: {...}, ar: {...} }`) keyed per text node; the toggle
  swaps `textContent`, sets `document.documentElement.lang` + `dir` (`rtl` for Arabic), and
  persists the choice in `localStorage`.
- RTL-aware layout: directional spacing/alignment use logical properties or flip under
  `[dir="rtl"]`.
- Arabic copy reuses the dashboards' existing Arabic terminology for consistency.
- Default language: English (with auto-detect from `navigator.language` as a nice-to-have,
  overridden by the persisted choice).

## Netlify decommission (the `Pointer` repo)

- **Remove:** `netlify.toml`, `netlify/functions/api.mjs` (and the `netlify/` dir),
  the root `package.json` (its only dependency is `@netlify/blobs`), the `public/`
  placeholder, and the `.netlify/` entry in `.gitignore`.
- **Keep:** `comments-skill/` (the local zero-dependency `node server.js`) and
  `comments-skill/core.js` — it simply loses its second (Netlify) consumer.
- **Update docs** so they no longer describe the Netlify backend or the
  `tuwaiq-pointer.netlify.app` URL: `README.md`, `AGENTS.md`, `CLAUDE.md`. They should
  describe (a) the live SaaS on the Oracle VM (`api`/`app`/`demo.pointer.moamen.work`) and
  (b) the local zero-dep server for self-hosting.
- **Precondition:** confirmed no live app still points `VITE_POINTER_SERVER` at the
  Netlify URL (user confirmed).

## Out of scope

- Pricing (billing is Phase 3, not live), blog/CMS, analytics, A/B testing.
- A real embedded live widget (we deliberately chose the scripted animation instead).
- Any change to the app or demo dashboards themselves.
