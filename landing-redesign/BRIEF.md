# Pointer Landing Page — Rebuild Brief

## Read this first

Forget any existing landing page design entirely — this is a from-scratch visual rebuild. You are
one of **three independent AI builders** (Claude, agy/Gemini, GLM) each producing your own complete
version of this page from this exact brief, in your own separate folder, with zero communication
between you. Do not look at, copy, or coordinate with sibling folders (`../claude/`, `../agy/`,
`../glm/`) — build directly into the folder path given to you in your own task prompt. Everything
you need is below; there is no repo access assumed beyond what this brief states.

## Product summary

**Pointer** is element-level feedback for any web app that an AI coding tool can act on. A
stakeholder (client, PM, tester) clicks any element on the *running* app and types one sentence, in
whatever language they think in — the apply flow detects and translates it automatically. The widget
captures a **structured brief**: the DOM selector, a shallow snapshot, the CSS rules that actually
win, the route, page URL, viewport/device info, optional console/network context for bug reports,
and the source file path when available. The developer's AI tool (Claude Code, Cursor, Windsurf,
OpenCode, Antigravity — any tool, no vendor lock-in) fetches the queue over plain HTTP, edits the
real source, **commits and never pushes** — a human reviews the diff. When the fix ships, the
comment flips to **Live**, the author is notified and confirms with 👍/👎 (👎 reopens it).

**Thesis (use it or a sharper equivalent):** *"An AI coding tool is only as good as the feedback it
receives."* **Category:** AI-ready feedback — the input layer for agentic coding, not a
screenshot/ticketing tool (never name Marker.io/BugHerd, but that's the contrast).

**Two AI-economics differentiators to visualize honestly (see the graph spec below):**
1. **Cost-aware apply delegation** — when a run has 3+ comments in disjoint files and an edit is
   mechanical (a copy tweak, a color swap, a straightforward prop change), the orchestrating
   (premium) model plans and reviews while a cheaper worker model types the edit out.
2. **Built-in comment translation** — a non-English comment is detected and translated in, and the
   reply translated back out, by a cheap model — never the expensive orchestrator — by default, on
   every install, with no separate opt-in.

Both are real, shipped mechanisms. Neither has a measured savings number yet — see Hard Constraints.

## Audiences, in priority order

1. **Dev lead / senior engineer** (primary buyer, hero's target) — pain: translating vague
   screenshots/Slack threads into code; babysitting an AI agent that explores the codebase for 20
   turns before touching the right file.
2. **Solo founder / indie dev** — fastest to convert — pain: client change requests eat evenings.
3. **Agency / consultancy** — highest value — needs multi-tenant isolation, per-stakeholder tagging,
   the browser extension for client sites they don't host. Often has non-English-speaking clients —
   translation removes a real friction point specifically for this audience.
4. **Non-technical stakeholder** — arrives by invite, not via this page; one section should still
   reassure them ("click anything, type a sentence, nothing to install, write in your own language").

## Required sections (do not drop any)

1. **Nav** — brand name + logo (from `/api/branding`), links, language toggle (en/ar), theme toggle
   (light/dark), two CTAs: "Sign in" and primary "Try the demo".
2. **Hero** — the one-sentence promise, a visual proving *a comment becoming a change* (not a static
   widget screenshot), two CTAs: **"Try the demo — no install"** (primary,
   `https://demo.pointer.moamen.work`) and **"Create an account"** (`https://app.pointer.moamen.work`).
   Primary target: the dev lead.
3. **The brief** (second screen — the differentiator) — a table/list of what a single comment
   actually carries. Use this exact field list and rationale:
   | Field | Why an AI tool cares |
   |---|---|
   | `selector` | Identifies the exact DOM element without a screenshot or a description |
   | `snapshot` | The element's own opening tag, attributes (≤120 chars), text (≤160 chars) — deliberately shallow so it doesn't drown the model in child markup |
   | `appliedCssRules` | The rules that *win*, not the full computed dump — answers "why is it blue?" before the agent asks |
   | `computedStyles`, `classes`, `parentInfo` | Disambiguates structure when the selector is generic |
   | `route`, `pageUrl`, `pageTitle` | Identifies which screen/view to open |
   | `viewport`, `deviceType`, `devicePixelRatio` | Turns "broken on mobile" into a reproducible condition |
   | `PageContextSnapshot` (opt-in, bug reports) | Console errors and failed/slow network requests at the moment of the issue |
   | `sourcePath` | Configured attribute or framework dev-mode path when present (tiered — see honesty note below) |
   | `language` | The BCP-47 tag the comment was detected in |
4. **How it works / the loop** — four steps ending at the commit URL, returning to the asker: (1)
   Point & comment — anyone signed in clicks an element, leaves a short comment; (2) Triage in the
   dashboard — tagged by project, environment, stakeholder, status; (3) Applied by AI — a developer
   hands the queue to any AI tool over plain HTTP; (4) Committed, never pushed — the AI commits per
   project style, links each applied comment back to the author, **never pushes**, human reviews.
   Show translation somewhere in this flow: if the comment was non-English, the agent's reply arrives
   back in the *same* language the stakeholder wrote in.
5. **Before/after contrast** — "Without Pointer" (a vague Slack/verbal ask, a dev guessing, an AI
   exploring blindly) vs. "With Pointer" (click → structured brief → targeted apply → commit).
6. **Why it saves time/cost/tokens** — four angles: save time (context attached, no back-and-forth),
   cut costs (fewer clarification meetings), spend fewer AI tokens (qualitative only — see honesty
   rule), one organized queue (tagged, triaged). **Embed the cost-aware-apply animated graph here**
   (full spec below) as its own subsection/graphic, not just prose.
7. **Cost-aware apply graph** (new, animated) — see full spec in its own section below.
8. **Features grid** (trim to ~4 distinct, non-overlapping cards) — two-line install (script tag +
   custom element, no SDK); multi-project & multi-tenant, enforced server-side; Shadow-DOM style
   isolation; browser extension for sites you don't control.
9. **"Works with your stack"** — anonymized stack/AI-tool counts, live from
   `GET /api/public/stacks-summary`; entire section hidden until `totalProjects > 0`.
10. **"Pointer in numbers"** — public thresholded stats band, live from `GET /api/public/stats`;
    must look intentional whether it's showing 0, 1, or all metrics (each metric independently
    present/absent).
11. **"Built for the whole team"** — two-card split: stakeholders (no install, just click and
    comment, in their own language) / developers (pull the queue, apply with any AI tool).
12. **Trust & privacy** — four objections, sharpened answers:
    - "An AI edits my code?" → commits, never pushes; you review the diff; every applied comment
      carries its commit URL.
    - "What do you capture?" → element-level DOM metadata only; never form values, cookies,
      localStorage, or request bodies; link to `/data.html` and `/privacy.html` (carry those two
      pages forward as-is — they're legal/content pages, not part of the visual redesign scope).
    - "Which AI tool do I use?" → any; plain HTTP + MCP; no lock-in.
    - "Is my data stuck with you?" → self-host: API + Postgres, your own database.
13. **Pricing** — rendered live from `GET /api/plans`; `displayState === 1` ("Coming soon") shows a
    dimmed/soon card with no CTA.
14. **Browser extension section + install stepper** — a real, walkable stepper (download zip → unzip
    → `chrome://extensions` → developer mode → load unpacked → sign in), not a static list.
15. **Final CTA band** + **Footer** — docs links (Install, Apply feedback, MCP server, API keys, All
    docs), Dashboard, Privacy, Data & self-hosting, GitHub. Docs link is white-label
    (`/api/branding`'s `urls.docs`, fallback to the bundled `/docs/` path).

## Feature inventory to mention somewhere (scannable, not a wall of text)

Widget in Shadow DOM (style-isolated, any framework) · Structured brief · Bug reports with console +
failed/slow network context · Private comments · Environments (Local/Staging/Production) · AI rules
(3-tier: Workspace › Project › Personal) · Picked actions (admin-authored trusted prompts) · CLI
`pointer-feedback` (list/apply/plan/mark/init/deployed) · MCP server (Claude Code, Cursor, Windsurf,
OpenCode) · Source mapping (Vite plugin, hash-stable, no path leak in prod) · Deploy awareness
(Applied vs Live) · Notifications + 👍/👎 verification · Workspaces, invites, magic-link access ·
Multi-project, multi-tenant · Browser extension · Widget pinning (`?v=` + SRI) · Secret detection
(`sk-`, `AKIA`… flagged, excluded from AI payload) · Input-value masking · API keys (hashed,
rotatable) · **Write feedback in your own language — detected and translated automatically, replies
come back in the same language** · **Self-hostable: API + Postgres, your data stays yours.**

## Cost-aware apply — the graph to design (build this, it's a required new section)

A small, self-contained **animated SVG/CSS graphic** — no chart library, no external data — inside
or right beside the "why it saves tokens" section, contrasting two bars:

- **Bar A — "One model does everything"**: a single, tall, uniform bar in one color (the
  premium-model hue), labeled "investigation, review, edits, translation — all on one expensive
  model."
- **Bar B — "Pointer: cost-aware delegation"**: a shorter *segmented* bar — a top segment in the
  premium-model hue labeled "investigation + review," and a visibly *smaller* bottom segment in a
  second, distinct cheaper-tier hue labeled "mechanical edits + translation → cheaper model." A
  small two-line legend under the bar names both delegated tasks concretely (mechanical edits,
  translation).

**Non-negotiable honesty rule:** label everything *qualitatively* — e.g. "fewer premium-model tokens
spent on typing and translating." **Never print a percentage, a "tokens saved" number, an "X×
cheaper" figure, or a turn count.** No real measurement of this specific mechanism exists. If bar
heights differ, base the *proportion* on the general shape of the claim (the delegated segment is
the minority — investigation/review is still the harder part), never on any specific ratio presented
as data.

Animate the bars filling in on scroll (`IntersectionObserver`), but the graphic **must render
correctly and completely as a static end-state** with `prefers-reduced-motion: reduce` set, or with
JS disabled — motion is a bonus, never the only way the comparison reads. Must stay legible at
360–390px, flip correctly under RTL (bars/labels mirror; relative proportions stay meaningful), and
hold contrast in both light and dark themes. Color choice: the "premium model" segment should read
as the brand accent color; the "cheaper model" segment should be a visibly distinct, cooler/quieter
neutral — not a state/status hue (don't accidentally imply "cheap = error" via red, or "cheap =
done" via green).

## Time & token savings — honesty rules (non-negotiable, applies page-wide)

- You MAY state the measured baseline problem qualitatively: a first-use AI interaction on a real
  host app historically involved dozens of exploratory turns and large raw-JSON payloads before
  touching the right file — present this as *the problem Pointer removes*, not as a savings claim,
  and **do not print the specific historical token/turn numbers** (they describe an older mechanism
  and are on the forbidden list below).
- You MAY make qualitative claims: "a lightweight summary view and a one-command helper replace many
  exploratory loops with one direct, targeted brief"; "the agent starts at the selector and the
  winning CSS rule, not at `src/`"; "one queue instead of screenshots in five channels"; "mechanical
  edits and translation delegate to a cheaper model, so the expensive model spends its tokens on the
  parts that actually need judgment."
- You MUST NOT print any savings percentage, "tokens saved" number, "X× faster" figure, customer
  logos, testimonials, star ratings, user counts, or "trusted by" strips. None exist. It's fine to
  leave a visually clean gap where a real measurement could drop in later, but do not fake one.
- Do not claim "starts at the exact file" unconditionally — say "when source mapping is enabled."

## Design brief — visual direction

**IMPORTANT — read this paragraph before the rest of this section.** This brief is being built
three times, independently, by three different AI builders (Claude, agy/Gemini, GLM), and the
project owner explicitly wants to see **three genuinely different creative imaginations**, not one
design skin repeated three times.

- **If you are the `claude` builder:** the design system below, **"The Review Margin,"** is
  **pinned — honor it exactly.** This is the one committed visual world for that build.
- **If you are the `agy` builder or the `glm` builder:** **do NOT adopt "The Review Margin"
  palette/type/shape system below.** Treat this section only as *tone and reference context* (it
  tells you the product is calm/professional/developer-trustworthy, roughly at a Linear/Vercel/
  Resend quality bar, and definitely not a generic gradient-SaaS template) — then **invent your own
  distinct color palette, typography pairing, shape language, and layout personality** for this
  build. Your direction should be recognizably different from a "code review" metaphor if you have a
  stronger idea; surprise us. The only things that are NOT optional for you are listed under **"Hard
  visual/UX requirements"** below (RTL/i18n, 360px responsiveness, accessibility, performance, the
  `overflow-x` rule, and the cost-graph's color-semantics rule: whatever two colors you choose for
  the "premium" vs "cheaper" model bars, don't make the cheap one read as red/error or green/done) —
  those apply to all three builds regardless of visual direction. Everything else below this
  paragraph (fonts, hex values, "the four jobs rule," the mascot placement) is the `claude` builder's
  pinned system, not a shared mandate.

### "The Review Margin" (claude builder's pinned system — agy/glm: reference tone only, see above)

Adopt this product's existing design world, **"The Review Margin"** — a feedback console that reads
like a pull-request review: white ground, cool gutters, hairlines, and a *diff vocabulary* for
state. Quality bar: sit next to **Linear, Vercel, and Resend** in finish and feel. Anti-references:
generic SaaS template, stock-illustration hero, gray-on-gray console screenshots, fake dashboards
full of lorem numbers.

- **Type:** IBM Plex Sans Arabic (OFL, covers Latin + Arabic) for all text; IBM Plex Mono for code,
  keys, counts. Tabular numerals. Self-host the fonts, `font-display: swap`.
- **Color:** brand blue `#0969da` (dark mode `#4493f8`) for links, focus, active state, and the
  primary button only (the "four jobs rule" — don't scatter the accent). State hues, each with a
  soft tint: Open `#0969da`, Ready `#9a6700`, Applied/Live `#1a7f37`, Archived `#59636e`, Danger
  `#d1242f`. Neutrals: canvas `#ffffff` / dark `#0d1117`, gutter `#f6f8fa` / `#161b22`, ink `#1f2328`
  / `#e6edf3`, hairline `#d0d7de`. **Note:** `GET /api/branding` returns a live `primaryColor` field
  — treat `#0969da` as the *default/fallback* brand seed, and read the real accent from the API
  response at runtime so a rebrand doesn't require a code change; design the palette so swapping the
  accent doesn't break the state-hue system (state hues are fixed, independent of brand color).
- **Shape:** 6px radii, flat at rest, shadows only on floating/elevated layers. Motion: purposeful,
  ≤300ms, respects `prefers-reduced-motion`.
- **Mascot:** the existing dog mascot (`assets/dog-mascot.png`, copy it into your folder if you use
  it) is the logo — use it small in nav/footer, not as hero art. You may also render brand text
  without the mascot if your direction calls for it (`GET /api/branding` may return no logo asset).
- **Voice:** plain, second person, short declarative sentences, explain the *why*. No hype
  adjectives.

**Hard visual/UX requirements:**
- Light + dark (system preference + manual toggle), English + Arabic with **full RTL** (use logical
  CSS properties — `margin-inline-*`, `inset-inline-*`, never physical `left`/`right`). Persist
  language choice in `localStorage`. Arabic copy must be real Arabic, not transliteration.
- **Responsive to 360px** with zero horizontal scroll; touch targets ≥44px; the "how it works" flow
  works as a tappable stepper on mobile, not a cramped 4-column grid. **Most visitors are on
  mobile — design mobile-first, then scale up to desktop, not the reverse.**
- Accessibility: semantic landmarks, visible focus rings, AA contrast, alt text, keyboard-operable
  stepper/toggle, `aria-live` on anything that animates text, non-animated fallback for the
  cost-graph.
- Performance: no framework required (plain HTML/CSS/vanilla JS is fine and matches the deploy
  target), fonts self-hosted with `font-display: swap`, no layout shift from late-arriving API data
  (reserve space or use skeletons that don't linger — see the "no persistent spinner" rule below).
- `html { overflow-x: hidden }` only — **never** also on `body` (that combination silently breaks
  `position: sticky`).

## Live API contracts — integrate for real, no hardcoded numbers/plans/branding

`API_BASE` resolution (use this exact pattern so the page works both hosted and self-hosted):
```js
var API_BASE = (typeof window !== 'undefined' && window.__POINTER_API__)
  || (document.querySelector('meta[name="pointer-api"]') || {}).content
  || "https://api.pointer.moamen.work";
```

All four endpoints below are `GET`, anonymous, no auth header needed. Every fetch must be
**defensive**: wrap in `.then/.catch`, and on failure or empty data the section renders its fallback
or hides — **never** an empty shell or a spinner that persists past ~2s.

### `GET /api/plans` — pricing
Response is wrapped: `{ data: PlanPublicResponse[] }` (or the array may arrive unwrapped — handle
both: `var list = res && (res.data || res);`).
```ts
type PlanPublicResponse = {
  slug: string;
  name: string;
  priceMonthly: number;      // 0 or falsy = free tier
  currency: string;          // e.g. "USD"
  interval: number;          // enum: 0 = monthly, 1 = yearly (BillingInterval)
  featureBullets: string[];
  displayState: number;      // enum: 0 = visible/normal, 1 = "coming soon" (dim card, no CTA)
  sortOrder: number;         // sort ascending before rendering
};
```
Example:
```json
{ "data": [
  { "slug": "free", "name": "Free", "priceMonthly": 0, "currency": "USD", "interval": 0,
    "featureBullets": ["1 project", "Community support"], "displayState": 0, "sortOrder": 0 },
  { "slug": "team", "name": "Team", "priceMonthly": 29, "currency": "USD", "interval": 0,
    "featureBullets": ["Unlimited projects", "Multi-tenant workspaces", "Priority support"],
    "displayState": 0, "sortOrder": 1 },
  { "slug": "enterprise", "name": "Enterprise", "priceMonthly": 0, "currency": "USD", "interval": 0,
    "featureBullets": ["SSO", "SLA"], "displayState": 1, "sortOrder": 2 }
] }
```
On empty/failed response: render a graceful fallback card with a "Create an account" CTA, not a
blank grid.

### `GET /api/public/stacks-summary` — "works with your stack"
```ts
type StacksSummaryResponse = {
  totalProjects: number;
  frontend: Record<string, number>;   // e.g. { react: 12, vue: 3 }
  backend: Record<string, number>;    // e.g. { dotnet: 8, node: 5 }
  aiTools: Record<string, number>;    // e.g. { "claude-code": 10, cursor: 4 }
};
```
Wrapped as `{ data: StacksSummaryResponse }`. **Hide the entire section when `totalProjects === 0`**
or the request fails. Render the top N tags per group (frontend+backend merged into one "stack" tag
row, AI tools as a separate row), sorted by count descending. Humanize known tokens (react → React,
dotnet → .NET, "claude-code" → Claude Code, "opencode-glm" → "opencode + GLM", etc.) with a
title-cased fallback for unknown tokens.

### `GET /api/public/stats` — "Pointer in numbers"
```ts
type PublicStatsResponse = {
  appliedComments: number | null;   // null until it clears the server's anonymity threshold
  projects: number | null;
  workspaces: number | null;
  languages: string[];              // distinct comment languages seen in ≥3 projects; [] below that
  aiTools: string[];                // distinct AI tools seen in ≥3 projects; [] below that
  medianHoursToApply: number | null;
};
```
Wrapped as `{ data: PublicStatsResponse }`. Every numeric field is independently nullable —
**render only the metrics that are present** (as "N+" style stat cards, numbers already rounded down
server-side), and hide the whole section only if *zero* metrics are present. `languages` array (if
non-empty) can ride as its own tag group (e.g. "Feedback written in: English, Arabic, …" using
`Intl.DisplayNames` to localize the language names). Format `medianHoursToApply`: <1h → "under N
min", <48h → "N.T hours", else → "N days".

### `GET /api/branding` — white-label
```ts
type BrandingResponse = {
  productName: string;
  tagline: string;
  primaryColor: string;             // hex, e.g. "#0969da" — see Design brief color note above
  urls: { app: string; demo: string; docs: string; landing: string };
  assets: { logo?: string; iconSquare?: string; favicon?: string; appleTouch?: string; pwa192?: string; pwa512?: string };
  extension: { storeUrl: string; zipUrl: string };
  version: number;
};
```
Wrapped as `{ data: BrandingResponse }`. On load: replace product name everywhere (`data-brand-name`
style hooks), swap the logo if `assets.logo` is present, swap the favicon if `assets.favicon` is
present, update `<title>`, rewrite any hardcoded `https://app.pointer.moamen.work` /
`https://demo.pointer.moamen.work` links to `urls.app` / `urls.demo`, and point the docs link at
`urls.docs` (fallback to the bundled `/docs/` path if absent). Wire the browser-extension CTA to
`extension.storeUrl` (Chrome Web Store button) when present, else fall back to the manual
zip-install stepper using `extension.zipUrl`. On failure: keep the bundled defaults (`Pointer`,
default blue, default demo/app URLs) — never blank the nav/footer.

### Note on `/api/leads`
There is **no real `/api/leads` endpoint in this API.** A prior version of the page used
`/api/plans` and `POST /api/leads` only as **illustrative copy inside a hero mock-up** (a fictional
signup-form animation showing "this is the kind of thing a comment could point at"), not as a real
integration. Do not build a real lead-capture form against a nonexistent endpoint — either omit that
illustrative detail or keep it purely as flavor text inside a clearly-fictional demo mock (not
wired to a real network call).

## Hard constraints

- **No fabricated commercial claims.** These exact patterns must never appear in rendered text:
  a specific percentage (e.g. "96%"), a specific turn count (e.g. "24 turns"), a specific token
  figure (e.g. "551,817 tokens"), `npx ` (no CLI install command exists yet), "MCP" (no MCP server
  yet), "links to the PR" / "pull request", "cloud apply", "one link, no password" / "magic link(s)",
  "starts at the exact file/line" (unconditionally — only conditional phrasing like "when source
  mapping is enabled" is OK), "trusted by", "testimonial" / "case study" markers, uptime/SLA figures
  like "99.9%" or "SLA".
- Every number on the page is either live from the API or absent — never a static placeholder that
  looks real.
- No AI-generated raster imagery (none available this round) — build entirely from CSS/SVG/
  typography/live data/authored vector shapes. `landing/assets/dog-mascot.png` (the existing mascot)
  may be reused — copy it into your own folder — or dropped entirely, your choice.
- Fully responsive, **mobile-first**: must look and work cleanly at 360–390px width first, then scale
  to desktop (1280–1600px+). Most visitors arrive on a phone.
- No required build step to preview: plain HTML/CSS/vanilla JS is the expected stack (matches the
  deploy target — a static folder served as-is). If your tooling wants to add a trivial build step,
  it must still produce a static output directory that can be served with no server-side logic.
- Genuinely complete and production-ready: real meta/OG tags (title, description, canonical,
  `og:*`/`twitter:*`), a favicon, a working nav, forms/CTAs wired to the real endpoints above, no
  lorem ipsum, no "TODO" placeholders, no broken internal links.
- Legal pages (`privacy.html` / `data.html` equivalents) may be carried over content-as-is,
  restyled to match your new visual system or left simply linked out — your call, they are not
  central to this round's visual redesign.
- Every live-data section must render completely with **zero network** (all three fetches failing)
  — no empty shells, no spinner that never resolves.

## Cost-aware delegation instruction — apply this to your OWN build process

You are the **orchestrator** for this build. Do the direction-setting, the hero/signature
composition, the cost-aware-apply graph, and final integration/QA yourself — these need real
judgment. **Delegate mechanical, repetitive, or boilerplate implementation work** — e.g. repeated
pricing-card markup, the features grid, the footer, i18n string tables (en/ar), JSON-to-DOM
rendering glue for the four API integrations, carrying over the legal pages — **to a faster/cheaper
model tier if your tool supports invoking one.** This mirrors the exact "orchestrator plans and
reviews, cheap worker types" pattern this product's own cost-aware-apply skill uses, and it's a hard
requirement from the project owner (this build should demonstrate the same economics it's marketing)
— not optional, and not something to skip because it's "just you" doing the work. If your tool has no
mechanism to invoke a cheaper model tier, do the work in clearly separated passes anyway (a fast,
low-scrutiny mechanical pass vs. a careful judgment pass) and say so in your report. **State in your
final report exactly which parts you delegated and to which model/mechanism** — this is a
deliverable, not a nice-to-have.

## Definition of done

- All 15 required sections present, populated with real/live-fetched data where applicable, no
  fabricated numbers or claims from the forbidden list.
- Verified visually at a mobile width (~390px, mobile-first) and a desktop width (~1440px), light
  and dark, both languages if you implement full i18n (English is mandatory; Arabic/RTL strongly
  encouraged per the design brief but your call on scope/time).
- The cost-aware-apply graph animates on scroll but has a correct, legible static end-state under
  `prefers-reduced-motion: reduce` or with JS disabled.
- No console errors when opened locally. Note: you may have no live API to test against in your
  sandbox — if fetches fail (no network/CORS/DNS), every section must still render sensibly with its
  empty/fallback state, never break or show `undefined`/`null`/`[object Object]`.
- Self-contained folder: everything the page needs (CSS/JS/fonts/assets) lives inside your own
  folder, referenced with relative paths, so the folder can be copied anywhere and served as a
  static root with no build step at serve time.
- Your final report states: section-by-section what you built, your chosen visual direction in one
  paragraph, which parts you delegated to a cheaper model and how, and any known gaps/TODOs.
