# PRD — Landing page

**Item:** §53 / R3.6 · **Execution:** [`../execution/R3-06-landing-refresh.md`](../execution/R3-06-landing-refresh.md) ·
**Tests:** [`../testing/R3-06-tests.md`](../testing/R3-06-tests.md)

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
