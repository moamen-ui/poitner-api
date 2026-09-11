# R3-06 — Landing page refresh (§53 · Release 3 · 3–4 days)

## Goal
Rebuild the landing page's **argument** around the product's real thesis — an AI coding tool is only as
good as the feedback it receives — without rewriting the page's working machinery. The visitor (a dev
lead) must understand in five seconds what the product does, see on the second screen *what a single
comment actually carries*, and reach pricing having had the "an AI edits my code?" objection answered.

Content contract: [`../prd/LANDING-PRD.md`](../prd/LANDING-PRD.md). That PRD says what must be proved;
this doc says how it is built and what must not break.

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

## Dashboard tasks
None — this is the public marketing page, not the dashboard.

## Docs
**None — the page *is* the artifact.** It links to `/docs/` (R2-07) and `/data.html` (R3.5) rather than
duplicating them, and adds no docs page of its own. Add one entry to `landing/docs/pages.json` marking
`/` as an external "Product overview" entry so the docs nav can link back.

## Tests
- E2E: [`../testing/R3-06-tests.md`](../testing/R3-06-tests.md) — scenarios `R3-06-01` … `R3-06-08`.
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

## Rollout / compatibility
Static files, bind-mounted read-only; deploy is `git pull` on the VM — no container rebuild. Removing
`landing/v2/` breaks any external link to it; it was never announced, so accept the 404 rather than
maintaining a redirect. Keep `/privacy.html` and `/data.html` paths stable — they are linked from
emails and the dashboard.

## Report template
Files changed · the `impeccable` audit findings and which you acted on · what was harvested from `v2`
and what was dropped · the claim-by-claim check against PRD §7 · screenshots (light/dark, en/ar, 390 px)
· the zero-network render evidence.
