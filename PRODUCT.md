# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Stack

Static HTML/CSS/vanilla JS, no build step — inferred from the incumbent `landing/` implementation
(plain `index.html` + `data.html` + `privacy.html`, inline `<style>`/`<script>`, client-side `fetch`
against the live API, no framework, no bundler) and from the deploy path (Caddy bind-mounts
`./landing:/srv/landing:ro` as a static root — see `docker-compose.prod.yml` / `Caddyfile`). Not
asked explicitly this round (autonomous/background session, three parallel AI builders); kept
identical to the incumbent so any of the three outputs can drop into the same mount unchanged.

## Users

- **Primary:** a developer or small dev team already shipping a web app, who wants faster,
  lower-friction feedback loops with non-technical stakeholders (PMs, designers, clients, QA)
  without building a bug-tracker integration themselves.
- **Secondary:** the non-technical stakeholder who leaves feedback directly on the running app —
  they never see code, just click an element and type a comment, often in their own language.
- **Tertiary:** whichever coding agent (Claude Code, Cursor, Windsurf, Antigravity, OpenCode, plain
  CLI) turns the queued comments into real commits — the product explicitly targets "any AI tool,"
  not one vendor.

## Product Purpose

Pointer lets anyone click an element on a running web app and leave a short comment; the element's
selector, computed styles, and DOM context are captured automatically. Developers (or their AI
coding agent) fetch the queue of ready comments and apply the changes to the real source, then mark
them applied — closing the loop from "someone said something's wrong" to "it's fixed in code"
without a manual bug-tracker hand-off. Success = comments that used to live in Slack screenshots or
verbal asides instead become structured, actionable, source-accurate change requests an AI can act
on unattended.

## Positioning

Pointer's mechanism a competitor can't casually copy: the comment ships with a full **element
capture** (selector, snapshot, computed styles, applied CSS rules, source path hints) so an AI agent
can apply the fix without a human re-describing where the bug lives — most "feedback widget"
products just collect text. On top of that: **cost-aware apply** (the apply skill lets an
orchestrator-tier model delegate mechanical, disjoint edits to a cheaper worker model instead of
burning premium tokens on typing) and **built-in comment translation** (a non-English comment is
detected and translated in/out automatically, default-on, no consumer config) — both are AI-economics
features, not UI features, and both are inferred from `docs/roadmap/LANDING-PLAN.md` /
`docs/roadmap/prd/LANDING-DESIGN-PROMPT.md` as the two things this redesign must visualize honestly
(no invented percentages/×-savings — see those docs' explicit honesty constraints).

## Operating Context

- Install flow: a consumer runs the **Integrate Pointer** skill (`/pointer-init.md`) to drop the
  `<pointer-feedback>` widget into their app (Vite/Angular/Next/static detected automatically).
- Feedback flow: a teammate clicks an element in the running app, types a comment (any language),
  it's captured with full element context.
- Apply flow: a developer or their AI agent runs `npx pointer-feedback apply` (the **Apply Feedback**
  skill, `/skill.md`) against the queue; the CLI supports `--plan`, `--mark`, cost-aware delegation
  to a cheaper model for mechanical edits, and automatic translation in/out for non-English comments.
- The admin/React dashboard (separate repo, `pointer-dashboard`) is where teams manage projects,
  users, and see funnel/verification/insight analytics — the landing page is the pre-signup surface
  that has to earn the first click into that dashboard or the CLI docs.

## Capabilities and Constraints

- No paid-plan/pricing facts beyond what `landing/index.html` already renders from the live
  `GET /api/plans` endpoint — this redesign must keep pricing **live-rendered**, never hardcoded.
- Real, anonymized usage numbers are available from `GET /api/public/stats` (applied comments,
  projects, workspaces, active languages, active AI tools, median hours-to-apply) — every field is
  `null`/omitted below a minimum-count threshold server-side, so the page must render gracefully
  with some or all stats absent (no fabricated placeholder numbers).
  `GET /api/public/stacks-summary` similarly reports which frameworks/stacks are actually in use.
  `GET /api/branding` supplies product name/tagline/primary color/asset URLs/extension links — the
  page should consume it rather than hardcode brand strings, so a future rebrand doesn't require a
  landing-page edit.
  `POST /api/leads` accepts signup/lead capture from the pricing or CTA flow.
- Constraint carried from the existing page: **no fabricated commercial claims** — customer counts,
  named logos, testimonials, or benchmark numbers must be either real (from the API) or omitted, per
  `e2e/landing/forbidden-claims.mjs`'s enforced list.
- Existing brand asset on hand: `landing/assets/dog-mascot.png` (mascot artwork) and
  `landing/assets/favicon.ico`. Treat `dog-mascot.png` as an existing recognizable asset unless a
  world direction gives a strong reason to retire it.
- This round produces **three independent implementations** of the same brief (by three different
  AI backends), each a complete, self-contained static site in its own sibling folder, each free to
  choose its own visual direction from the shared design brief — this is a deliberate exception to
  "one visual world," scoped to this comparison round only.

## Brand Commitments

- Product name, tagline, and primary accent color are **not hardcoded** — they come from
  `GET /api/branding` at runtime (see Capabilities above). Do not invent a fixed brand palette that
  fights that live value; treat the branding response's `primaryColor` as a seed/accent the visual
  system should read gracefully, with the rest of the palette designed around it.
- `docs/roadmap/prd/LANDING-DESIGN-PROMPT.md` is a **user-authored, pinned design brief** (written by
  the product owner for feeding into an AI design tool) — its color/type/section/tone guidance is a
  binding brief constraint for whichever direction each builder chooses, not a suggestion to
  re-litigate.

## Evidence on Hand

- `docs/roadmap/LANDING-PLAN.md` — consolidated PRD + execution plan + test scenarios for the
  landing page (source of truth for required sections/copy facts).
- `docs/roadmap/prd/LANDING-DESIGN-PROMPT.md` — the pinned visual/design brief, including the new
  "cost-aware apply" animated graph spec and the translation-feature messaging.
- `landing/index.html`, `landing/data.html`, `landing/privacy.html` — the incumbent implementation;
  read as evidence of required live-data wiring and legal content, not as visual authority (this is
  an explicit full redesign — the old look is discarded).
- No customer logos, testimonials, or named case studies exist. Do not invent them.

## Product Principles

1. Prove the mechanism, don't just claim it — the element-capture, cost-aware-apply, and
   translation features are all demonstrable; show them working rather than asserting benefits.
2. Every number on the page is either live from the API or absent — never a placeholder that looks
   real.
3. Works for both audiences in one scroll: the non-technical commenter needs to see "click and
   type," the developer needs to see "the AI actually applies it."
4. Any AI coding tool, not one vendor — copy and demos should not read as Claude-Code-exclusive.
5. Mobile is not an afterthought: most visitors arrive on a phone; the primary narrative and CTA
   must work at 360–390px first, then scale up.

## Accessibility & Inclusion

Comments and the CLI/skill flow support non-English input (translation is a built-in feature) —
the landing page itself should at minimum keep the incumbent's RTL-safe structure in mind
(the widget/dashboard already support Arabic) even where full page localization is out of scope for
this round. Standard WCAG AA contrast/keyboard/focus expectations apply (see the impeccable
craft-floor checklist).
