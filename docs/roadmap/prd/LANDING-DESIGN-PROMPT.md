# Design brief — PinSay landing page (paste-ready prompt for an AI design/coding tool)

> Paste everything below the line into the design tool (Google Stitch, Claude for Design, v0, etc.).
> It is self-contained; the tool does not need repo access. Facts in it were extracted from the live
> landing page, the docs, the roadmap plan ([`LANDING-PLAN.md`](../LANDING-PLAN.md)), the served AI
> skills and the dashboard's DESIGN.md — last refreshed 2026-09-19 against the page as it exists
> today (1,272 lines; content rewrite from the plan's Part 2 has landed, the visual system below has
> not — that gap is this brief's job).
>
> **2026-09-26 — rebrand:** the product is now **PinSay** (domain **pinsay.dev**, owner decision). This
> brief uses the new name and the target `*.pinsay.dev` URLs. The code-level identifiers (`pointer.js`,
> `<pointer-feedback>`, the `pointer-feedback` CLI, `.pointer/`, `__POINTER_API__`) are unchanged until
> the rebranding plan (`docs/rebranding/`, branch `docs/rebranding-plan`) renames them, and the live API
> is still `api.pointer.moamen.work` until DNS moves — the brief tells the design tool to keep all of
> these in one config block so the switch is a one-line change.

---

## Brand name (read first)

- The product name is **PinSay** — always written with a capital P and capital S, one word. Domain:
  **pinsay.dev**. Tagline to build from (use it or a sharper equivalent): **"Pin it. Say it. Ship it."**
- The name says the whole loop: *pin* any element on the live app, *say* what you want, and an AI coding
  tool ships it. Use that as the naming idea in the hero; don't explain the name with a pun-heavy section.
- In the Arabic version, keep the name in Latin letters inline (**PinSay**), never transliterated —
  Arabic has no "p" sound, so an Arabic spelling would misread it.
- Never mention the old name ("Pointer") anywhere on the page.
- Technical identifiers shown in code samples (`pointer.js`, `<pointer-feedback>`, `npx pointer-feedback`,
  `.pointer/manifest.json`, `pointer deployed`) are the product's *current* real names — show them as they
  are, but keep every one of them, plus every URL, in a single `const CONFIG = {…}` block at the top of the
  page script (and CSS/HTML text generated from it) so a later rename is one edit.

---

## Role

You are a senior product designer + front-end engineer designing the **marketing landing page for
PinSay**, a developer tool. Deliver a complete, production-grade, interactive page — not a mockup and
not a template. Quality bar: it should sit next to **Linear, Vercel and Resend** in finish and feel.
Anti-references: generic SaaS template, stock-illustration hero, gray-on-gray console screenshots,
fake dashboards full of lorem numbers.

## What PinSay is (one paragraph — the page must make this obvious in 5 seconds)

PinSay is **element-level feedback for any web app that your AI coding tool can act on** — not only
bug fixes: a change request, an improvement, a new feature or an integration starts the same way. A
stakeholder (client, teammate, PM, tester) clicks any element on the *running* app and types one sentence, **in
whatever language they think in** — the apply flow detects and translates it automatically. The
widget captures a **structured brief** — the DOM selector, a shallow snapshot, the *winning* CSS rules,
route, page URL, viewport/device, optional console/network context for bug reports, and the source file
path when available. The developer's AI tool (Claude Code, Cursor, Windsurf, OpenCode — any tool, no
lock-in) fetches the queue over HTTP/MCP, edits the real source, **commits and never pushes**; a human
reviews the diff. When the fix is deployed the comment flips to **Live**, the author is notified and
confirms with 👍 / 👎 (👎 reopens it).

Thesis line (use it or a sharper equivalent): **"An AI coding tool is only as good as the feedback it
receives."** Category positioning: **AI-ready feedback — the input layer for agentic coding**, *not* a
screenshot/ticketing tool (Marker.io/BugHerd are the contrast, never named).

## Audiences (in priority order for this page)

1. **Dev lead / senior engineer** — buyer. Pain: translating vague screenshots and Slack threads into
   code; babysitting an AI agent that explores the codebase for 20 turns before touching the right file.
2. **Solo founder / indie dev** — fastest to convert. Pain: client change requests eat evenings.
3. **Agency / consultancy** — highest value. Needs multi-tenant isolation, per-stakeholder tagging, and
   the browser extension to comment on client sites they don't host. Often has non-English-speaking
   clients — translation removes a real friction point for this audience specifically.
4. Non-technical stakeholders arrive by invite, not via this page — but one section must reassure them
   ("click anything, type a sentence, nothing to install, write in your own language").

## The job of the page

Show **how the product works** (the flow, end to end, with the real artifacts), make **why it saves time
and tokens** concrete and honest, and show how it **organizes the edit-with-AI workflow** (one queue,
statuses, environments, review-then-commit, deploy awareness, verification). Convert to two CTAs:
**"Try the demo — no install"** (primary, `https://demo.pinsay.dev`) and **"Create an account"**
(`https://app.pinsay.dev`). Secondary: **Docs** (`/docs/`). (These are the target domains; until DNS moves
they resolve to today's `demo.pointer.moamen.work` / `app.pointer.moamen.work` — keep both in `CONFIG`.)

## The flow to visualize (this is the centerpiece — make it interactive)

Build a scroll- or click-driven **"one comment, start to finish"** walkthrough. Each step shows the real
artifact, styled as UI, not as an illustration:

1. **Install** — two lines: `<script src="…/pointer.js" defer>` + `<pointer-feedback project="…"
   server="…" environment="production">`. Alternative: `npx pointer-feedback init`.
2. **Point & comment** — a live-looking app (a pricing card or signup form) with the widget's inspect
   mode: hovered element outlined, a pinned bubble "Make this the primary action and match the header
   blue". Toggles visible: screenshot, *Report as a bug*, *Keep private*, environment selector.
3. **The brief** — show the JSON the AI actually receives, syntax-highlighted, with callouts:
   `selector`, `snapshot` (deliberately shallow), `appliedCssRules` ("the rules that win, not a computed
   dump"), `route` / `pageUrl`, `viewport` / `deviceType`, `sourcePath` (`file:line` or an 8-char hash
   resolved via `.pointer/manifest.json`), `pickedActions[].prompt`, `aiRules` (Workspace › Project ›
   Personal precedence), and `language` (the BCP-47 tag the comment was detected in).
4. **One queue** — the dashboard's Comments review screen: rows with status pills **Open → Ready to
   apply → Applied → Live → Archived**, environment badges **Local / Staging / Production**, author,
   route, flags (Bug, Private lock, Flagged secret). Filters: status, environment, flagged, live/not
   live, search.
5. **Apply with your AI tool** — a terminal: `npx pointer-feedback apply --plan` (shows the plan) then
   `apply --mark`. Show the agent going **straight to the file** (no exploration turns), the diff, and
   one commit per comment or one per run (project setting `commitStyle`). Big, explicit: **"Commits.
   Never pushes. You review the diff."** If the comment was non-English, show the agent's reply arriving
   back in the *same* language the stakeholder wrote in.
6. **Live + verified** — CI reports the build (`pointer deployed` / `data-build-sha`), the comment flips
   to **Live** with a commit link; the author's bell shows "Your comment was applied" and 👍 *Looks right*
   / 👎 *Not fixed* (👎 reopens). Close the loop visibly.

Make steps advance on scroll (desktop) with a sticky visual, and as a tappable stepper on mobile.
Include a **"before / after" toggle** somewhere: *Without PinSay* (Slack message + screenshot → dev
guesses → AI explores → 24 turns) vs *With PinSay* (click → brief → one targeted apply → commit).

## Cost-aware apply — the graph to design (new section)

PinSay's apply flow is **cost-aware**, not just token-aware: the AI tool's premium/expensive model
does the investigation and review, while two kinds of *mechanical* work delegate to a cheaper model —

1. **Mechanical edits** — when a run has 3+ comments in disjoint files and an item is mechanical (a
   copy tweak, a color swap, a straightforward prop change), the orchestrator plans and reviews while a
   cheaper worker model types the edit out.
2. **Comment translation** — a non-English comment is detected and translated in, and the reply
   translated back out, by a cheap model — never the expensive orchestrator — by default, on every
   install.

Design a small, self-contained **animated graphic** (SVG/CSS, no chart library, no external data) for
the "why it saves tokens" section, contrasting two bars:

- **Bar A — "One model does everything"**: a single, tall, uniform bar in one color (the premium-model
  hue), labeled "investigation, review, edits, translation — all on one expensive model."
- **Bar B — "PinSay: cost-aware delegation"**: a shorter *segmented* bar — a top segment in the
  premium-model hue labeled "investigation + review," and a visibly smaller bottom segment in a second,
  cheaper-tier hue labeled "mechanical edits + translation → cheaper model." A small two-line legend
  under the bar names both delegated tasks concretely.

**Non-negotiable honesty rule:** label everything *qualitatively* — "fewer premium-model tokens spent
on typing and translating" — and **never print a percentage, a "tokens saved" number, an "X× cheaper"
figure, or a turn count.** No real measurement of this specific mechanism exists yet; a fabricated
number here is worse than no number (this mirrors the rule for the rest of the token-savings copy
below — see "Time & token savings"). If you want the bars visually different heights, base the
*proportion* on the general shape of the claim (the delegated segment is the *minority* of the work,
since investigation/review is still the hard part), not on any specific ratio presented as data.

Animate the bars filling in on scroll into view (`IntersectionObserver`), but the graphic must render
correctly and completely as a **static end-state** with `prefers-reduced-motion: reduce` or with JS
disabled — motion is a bonus, never the only way the comparison reads. Must stay legible at 360–390 px,
flip correctly under an RTL layout (bars/labels mirror, but numeric/relative proportions stay
meaningful), and hold contrast in both light and dark themes.

## Time & token savings — the honesty rules (non-negotiable)

- You MAY state the **measured baseline problem**: a first-use AI interaction on a real host app cost
  **551,817 tokens across 24 turns (~90 s)**, mostly from exploratory loops and ~45 KB raw JSON dumps per
  query. Present it as *the problem PinSay removes*, not as a savings claim.
- You MAY make **qualitative** savings claims: "a lightweight summary view and a one-command helper
  replace twenty exploratory loops with one direct, targeted brief"; "the agent starts at the selector and
  the winning CSS rule, not at `src/`"; "one queue instead of screenshots in five channels"; "mechanical
  edits and translation delegate to a cheaper model, so the expensive model spends its tokens on the
  parts that actually need judgment."
- You MUST NOT print a savings percentage, a "tokens saved" number, an "X× faster" figure, customer
  logos, testimonials, star ratings, user counts or "trusted by" strips. None exist yet. Design a
  **slot** for a measured number/testimonial (clearly a placeholder in the source) so it can be dropped
  in later without redesign. This applies equally to the cost-aware-apply graphic above — it is a new
  section, not an exception to the rule.
- Do not claim "starts at the exact file" unconditionally — say "when source mapping is enabled" (Vite
  plugin today).

## Feature inventory to surface (secondary section, scannable, no walls of text)

Widget in Shadow DOM (style-isolated, any framework) · Structured brief · Bug reports with console +
failed/slow network context · Private comments · Environments (Local/Staging/Production) · AI rules
(3-tier) · Picked actions (admin-authored trusted prompts) · CLI `pointer-feedback` (list / apply /
plan / mark / init / deployed) · MCP server (Claude Code, Cursor, Windsurf, OpenCode) · Source mapping
(Vite plugin, hash-stable, no path leak in prod) · Deploy awareness (Applied vs Live) · Notifications +
👍/👎 verification · Workspaces, invites, magic-link access · Multi-project, multi-tenant · Browser
extension (comment on sites you don't control) · Widget pinning (`?v=` + SRI) · Secret detection
(`sk-`, `AKIA` … flagged, excluded from AI payload) · Input-value masking · API keys (hashed,
rotatable) · React dashboard · Plans with reference codes and manual (cash / bank transfer) payment · Several workspaces per account · **Write feedback in your own language —
detected and translated automatically, replies come back in the same language** ·
**Self-hostable: API + Postgres, your data stays yours.**

## Trust section (keep the 4 questions; sharpen the answers)

"An AI edits my code?" (it commits, never pushes; you review) · "What do you capture?" (list it; link
`/data.html`, `/privacy.html`) · "Which AI tool do I use?" (any; HTTP + MCP) · "Is my data stuck with
you?" (self-host).

## Sections already live — preserve, restyle, don't redesign away

The current page (post content-rewrite) has four sections beyond the original brief that must survive
into the new design, restyled to the visual system below rather than dropped:

- **"Works with your stack"** — anonymized stack counts, live from `GET /api/public/stacks-summary`,
  hidden entirely until `totalProjects > 0`.
- **"PinSay in numbers"** — a public, thresholded stats band, live from `GET /api/public/stats`
  (numbers appear only once real thresholds are met — design a version of this section that looks
  intentional both with and without numbers showing, since it is `hidden` by default in code).
- **"Built for the whole team"** — a two-card split: for stakeholders (no install, just click and
  comment) / for developers (pull the queue, apply with any AI tool).
- **Browser extension section + install stepper** — download zip → unzip → `chrome://extensions` →
  developer mode → load unpacked → sign in. Keep this as a real, walkable stepper, not a static list.

## Brand & visual system

Adopt the product's existing design world, **"The Review Margin"** — a feedback console that reads like a
pull-request review: white ground, cool gutters, hairlines, and a *diff vocabulary* for state.

- **Type:** IBM Plex Sans Arabic (free/OFL, covers Latin + Arabic) for all text; IBM Plex Mono for code,
  keys, counts. The landing page may use larger display sizes than the app, but keep the same family.
  Tabular numerals.
- **Color:** brand blue `#0969da` (dark `#4493f8`) used for links, focus, active state and the primary
  button only ("four jobs rule"). State hues are fixed: Open `#0969da`, Ready `#9a6700`, Applied/Live
  `#1a7f37`, Archived `#59636e`, Danger `#d1242f`, each with a soft tint. Neutrals: canvas `#ffffff` /
  dark `#0d1117`, gutter `#f6f8fa` / `#161b22`, ink `#1f2328` / `#e6edf3`, hairline `#d0d7de`.
  The current page still uses a green→blue gradient (`--pf-green: #16a34a` → `--pf-blue: #2563eb`);
  **retire it** in favour of this system so landing and product match — this has not happened yet,
  it is the core of this brief. For the cost-aware-apply graphic specifically: the "premium model" bar
  segment should read as the brand blue; the "cheaper model" segment should read as a visibly distinct,
  cooler/quieter neutral tone — not a state hue (avoid implying "cheap = error" by accidentally reusing
  the danger red, and avoid implying "cheap = done" by reusing the success green).
- **Shape:** 6px radii, flat at rest, shadows only on floating layers. Motion: purposeful, ≤300 ms,
  respects `prefers-reduced-motion`.
- **Mascot:** the existing dog mascot (`assets/dog-mascot.png`) is the logo; use it small in nav/footer,
  not as hero art.
- **Voice:** plain, second person, short declarative sentences, explain the *why*. No hype adjectives.

## Hard requirements

- **Light + dark** (system + manual toggle), **English + Arabic with full RTL** (logical CSS properties
  only; the page persists language in `localStorage`). Arabic copy must be real Arabic, not
  transliteration.
- **Responsive to 360 px** with zero horizontal scroll; touch targets ≥ 44 px; the walkthrough works on
  mobile as a stepper.
- **Accessibility:** semantic landmarks, visible focus, contrast AA, alt text, keyboard-operable
  stepper, `aria-live` on anything that animates text, and the cost-aware-apply graphic must have a
  legible non-animated fallback (see that section).
- **Performance:** static single HTML file plus assets, no framework, no build step, fonts self-hosted
  and `font-display: swap`, LCP under 2 s on a mid-range phone, no layout shift from late data.
- **Live data hooks to preserve** (API base = `CONFIG.api`, target `https://api.pinsay.dev`, today
  `https://api.pointer.moamen.work`): pricing rendered from `GET {CONFIG.api}/api/plans`
  (`displayState===1` → "Coming soon"), stack tags from `GET /api/public/stacks-summary` (hidden until
  `totalProjects > 0`), public stats from `GET /api/public/stats` (hidden until its own thresholds are
  met — see "Sections already live" above), branding overrides from `GET /api/branding` and
  `window.__POINTER_API__` / `<meta name="pointer-api">` for white-label self-hosters. The page dogfoods
  the widget: `<script src="{CONFIG.api}/pointer.js">` + `<pointer-feedback
  project="pointer-landing" server="{CONFIG.api}" environment="production">`. Title/meta/OG tags use
  "PinSay" and `https://pinsay.dev` as the canonical URL.
- Footer links: Docs (Install, Apply feedback, MCP server, API keys, all docs), Dashboard, Privacy,
  Data & self-hosting, GitHub. Keep the Chrome-extension install stepper (download zip → unzip →
  `chrome://extensions` → developer mode → load unpacked → sign in).

## Deliverables

1. `index.html` (self-contained, with `<style>` and `<script>`), plus any SVG/asset files it needs.
2. A short `DESIGN-NOTES.md`: the section map, the copy in both languages, where the placeholder
   "evidence slots" are, and anything you deliberately left out.
3. Screenshots at 1440, 768 and 390 px, light and dark, EN and AR, including the cost-aware-apply
   graphic in both its animated-in and reduced-motion static states.

## Process

Propose the section order and the hero concept first (three headline options, one recommended), then
build. Verify in one batched pass (desktop + mobile, both themes, both languages), fix in one batch,
stop. Do not invent features, numbers, quotes or customers that are not in this brief.
