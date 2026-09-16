# Design brief — Pointer landing page (paste-ready prompt for an AI design/coding tool)

> Paste everything below the line into the design tool. It is self-contained; the tool does not need
> repo access. Facts in it were extracted from the live landing page, the docs, the roadmap PRDs, the
> served AI skills and the dashboard's DESIGN.md on 2026-09-15.

---

## Role

You are a senior product designer + front-end engineer designing the **marketing landing page for
Pointer**, a developer tool. Deliver a complete, production-grade, interactive page — not a mockup and
not a template. Quality bar: it should sit next to **Linear, Vercel and Resend** in finish and feel.
Anti-references: generic SaaS template, stock-illustration hero, gray-on-gray console screenshots,
fake dashboards full of lorem numbers.

## What Pointer is (one paragraph — the page must make this obvious in 5 seconds)

Pointer is **element-level feedback for any web app that your AI coding tool can act on**. A
stakeholder (client, PM, tester) clicks any element on the *running* app and types one sentence. The
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
   the browser extension to comment on client sites they don't host.
4. Non-technical stakeholders arrive by invite, not via this page — but one section must reassure them
   ("click anything, type a sentence, nothing to install").

## The job of the page

Show **how the product works** (the flow, end to end, with the real artifacts), make **why it saves time
and tokens** concrete and honest, and show how it **organizes the edit-with-AI workflow** (one queue,
statuses, environments, review-then-commit, deploy awareness, verification). Convert to two CTAs:
**"Try the demo — no install"** (primary, `https://demo.pointer.moamen.work`) and **"Create an account"**
(`https://app.pointer.moamen.work`). Secondary: **Docs** (`/docs/`).

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
   Personal precedence). Contrast with "a screenshot and a guess."
4. **One queue** — the dashboard's Comments review screen: rows with status pills **Open → Ready to
   apply → Applied → Live → Archived**, environment badges **Local / Staging / Production**, author,
   route, flags (Bug, Private lock, Flagged secret). Filters: status, environment, flagged, live/not
   live, search.
5. **Apply with your AI tool** — a terminal: `npx pointer-feedback apply --plan` (shows the plan) then
   `apply --mark`. Show the agent going **straight to the file** (no exploration turns), the diff, and
   one commit per comment or one per run (project setting `commitStyle`). Big, explicit: **"Commits.
   Never pushes. You review the diff."**
6. **Live + verified** — CI reports the build (`pointer deployed` / `data-build-sha`), the comment flips
   to **Live** with a commit link; the author's bell shows "Your comment was applied" and 👍 *Looks right*
   / 👎 *Not fixed* (👎 reopens). Close the loop visibly.

Make steps advance on scroll (desktop) with a sticky visual, and as a tappable stepper on mobile.
Include a **"before / after" toggle** somewhere: *Without Pointer* (Slack message + screenshot → dev
guesses → AI explores → 24 turns) vs *With Pointer* (click → brief → one targeted apply → commit).

## Time & token savings — the honesty rules (non-negotiable)

- You MAY state the **measured baseline problem**: a first-use AI interaction on a real host app cost
  **551,817 tokens across 24 turns (~90 s)**, mostly from exploratory loops and ~45 KB raw JSON dumps per
  query. Present it as *the problem Pointer removes*, not as a savings claim.
- You MAY make **qualitative** savings claims: "a lightweight summary view and a one-command helper
  replace twenty exploratory loops with one direct, targeted brief"; "the agent starts at the selector and
  the winning CSS rule, not at `src/`"; "one queue instead of screenshots in five channels."
- You MUST NOT print a savings percentage, a "tokens saved" number, an "X× faster" figure, customer
  logos, testimonials, star ratings, user counts or "trusted by" strips. None exist yet. Design a
  **slot** for a measured number/testimonial (clearly a placeholder in the source) so it can be dropped
  in later without redesign.
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
rotatable) · Dashboards in Angular, React and Vue at parity · **Self-hostable: API + Postgres, your
data stays yours.**

## Trust section (keep the 4 questions; sharpen the answers)

"An AI edits my code?" (it commits, never pushes; you review) · "What do you capture?" (list it; link
`/data.html`, `/privacy.html`) · "Which AI tool do I use?" (any; HTTP + MCP) · "Is my data stuck with
you?" (self-host).

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
  The current page uses a green→blue gradient; **retire it** in favour of this system so landing and
  product match.
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
  stepper, `aria-live` on anything that animates text.
- **Performance:** static single HTML file plus assets, no framework, no build step, fonts self-hosted
  and `font-display: swap`, LCP under 2 s on a mid-range phone, no layout shift from late data.
- **Live data hooks to preserve:** pricing rendered from `GET https://api.pointer.moamen.work/api/plans`
  (`displayState===1` → "Coming soon"), stack tags from `GET /api/public/stacks-summary` (hidden until
  `totalProjects > 0`), branding overrides from `GET /api/branding` and `window.__POINTER_API__` /
  `<meta name="pointer-api">` for white-label self-hosters. The page dogfoods the widget:
  `<script src="https://api.pointer.moamen.work/pointer.js">` + `<pointer-feedback
  project="pointer-landing" server="https://api.pointer.moamen.work" environment="production">`.
- Footer links: Docs (Install, Apply feedback, MCP server, API keys, all docs), Dashboard, Privacy,
  Data & self-hosting, GitHub. Keep the Chrome-extension install stepper (download zip → unzip →
  `chrome://extensions` → developer mode → load unpacked → sign in).

## Deliverables

1. `index.html` (self-contained, with `<style>` and `<script>`), plus any SVG/asset files it needs.
2. A short `DESIGN-NOTES.md`: the section map, the copy in both languages, where the placeholder
   "evidence slots" are, and anything you deliberately left out.
3. Screenshots at 1440, 768 and 390 px, light and dark, EN and AR.

## Process

Propose the section order and the hero concept first (three headline options, one recommended), then
build. Verify in one batched pass (desktop + mobile, both themes, both languages), fix in one batch,
stop. Do not invent features, numbers, quotes or customers that are not in this brief.
