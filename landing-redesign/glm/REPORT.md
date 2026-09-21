# REPORT — glm build, phase 2 (sections 3–15)

## Two-pass delegation disclosure (required deliverable)

This page was built in the orchestrator/worker split the project owner mandated, mirroring the
cost-aware-apply feature the page itself markets:

- **Pass 1 — direction + hero (paid GLM tier, `zai-coding-plan/glm-5.3` premium pass):** read the
  brief, invented the "surveyor's field sheet" visual direction (DIRECTION.md: paper/ink ground,
  Archivo Black / Archivo / Fragment Mono / Noto Kufi Arabic, notched panels, dashed rails, plot &
  stamp motion), and built sections 1–2 (nav + hero) into `index.html` / `styles.css` / `main.js`,
  leaving `<!-- TODO:PHASE2 -->` placeholders.
- **Pass 2 — mechanical completion (this pass, cheaper flash tier):** implemented sections 3–15
  faithfully in the pinned direction, extended the en/ar string table, wired the four live API
  integrations, and did the responsive/a11y verification. No redesign of the hero or direction;
  tokens, atoms, spacing and numbering conventions were reused as-is.

## Section-by-section

| # | Section | What exists |
|---|---|---|
| 1–2 | Nav, hero | Built in pass 1; untouched except i18n table growth. |
| 3 | The brief (`#brief`) | Notched panel, definition table of the exact 9 field groups from BRIEF.md (`selector` … `language`) with the "why an AI tool cares" rationale; chips mark `PageContextSnapshot` opt-in and the conditional source-mapping caveat. Two-column ≥780px, stacked below. |
| 4 | How it works (`#how`) | Real ARIA tablist (4 numbered tabs, arrow/Home/End keys, RTL-aware arrow direction), 2×2 grid on mobile → 4-up ≥760px. Panels: translation motif (ar→en) in step 1, triage tag row in 2, delegation log in 3, diff + commit + en→ar reply motif in 4. |
| 5 | Before/after | "Without Pointer" dashed steel panel (hollow square markers) vs "With Pointer" notched ink panel (accent squares) — 1 col → 2 col ≥880px. |
| 6 | Why it saves | Four qualitative cards (time / cost / tokens / queue). No numbers anywhere. |
| 7 | Cost-aware apply graph | Subsection of §6 with its own `07` kicker. Pure CSS bars on an ink baseline with end ticks: bar A uniform accent; bar B 84 % of A, segmented 62fr/22fr accent/steel with a 10px gap (verified live: 200/168px, segments 117/41px — delegated segment is the minority). Labels qualitative only; a visible honesty caption states no measurement exists. Fills 600ms via IntersectionObserver; full bars are the CSS default. |
| 8 | Features grid | 4 cards (two-line install with the real snippet shape, multi-tenant, Shadow DOM, extension) + 11 capability chips (no "npx", no "magic link"). |
| 9 | Works with your stack | Live `GET /api/public/stacks-summary`; `hidden` until `totalProjects > 0`; frontend+backend merged, sorted desc, top 8; AI tools row separate; ~55-token humanizer with title-case fallback. Verified with mocks: `React 12 / .NET 8 / Node.js 5 / Vue 3`. |
| 10 | Pointer in numbers | Live `GET /api/public/stats`; every metric independently nullable; "N+" cards; `medianHoursToApply` formatted under-min/hours/days (1.4 → "1.4 hours"); languages via `Intl.DisplayNames` ("English, Arabic, French"); section hidden only when zero metrics. |
| 11 | Whole team | Stakeholders / developers panels; translation motif reused; queue→brief→agent→commit mono line. |
| 12 | Trust & privacy | Four objection cards; links to `/privacy.html` and `/data.html`. |
| 13 | Pricing (`#pricing`) | Live `GET /api/plans`, sorted by `sortOrder`; USD → `$29`, free → "Free", `/ mo`·`/ yr`; `displayState 1` → dimmed steel card, "Coming soon" chip, no CTA (verified: 0 CTAs on the soon card). Static fallback card with account CTA renders on failure/empty/no-JS. Server strings inserted via `textContent` (no injection). |
| 14 | Extension (`#extension`) | Walkable 6-step stepper (toggle buttons ≥56px, `aria-pressed`, live `n / 6` counter, reset); Chrome-Web-Store CTA revealed only when `branding.extension.storeUrl` exists, zip button when `zipUrl` exists (both verified with a mock; neither fabricated by default). |
| 15 | CTA band + footer | Inverted ink band with accent crosshairs, both CTAs; footer with docs links (Install / Apply / MCP server / API keys / All docs — real paths under `/docs/`, verified against `landing/docs/pages.json`), product links, legal links, GitHub, dynamic year, `data-docs-link` white-labeling. |
| — | Legal pages | `privacy.html` + `data.html` copied in content-as-is (favicon path fixed to the bundled `favicon.svg`); linked site-absolutely like the rest of the deploy-time links. |

## Live API wiring

- `API_BASE` resolved exactly per BRIEF.md (`window.__POINTER_API__` → `meta[name=pointer-api]` →
  default), shared `getJSON()` helper, every fetch defensive with instant fallback (no spinners at
  all, so none can linger past 2s).
- `/api/branding`: pass-1 hooks extended — canonical + `og:url` rewrite from `urls.landing`,
  `tagline` → `[data-brand-tagline]`, `extension.storeUrl`/`zipUrl` wiring. Verified end-to-end
  with a white-label mock (name/accent/URLs/extension all swapped, defaults kept on failure).
- Language toggle re-renders all API-backed sections in the new language (`renderDynamic()`), and
  dynamic labels go through a `t()` helper with English fallback — verified 192/192 key parity
  between `en`/`ar` and that all 178 `data-i18n` keys resolve.

## Forbidden-claims audit

Regex-swept the rendered copy: no percentages, turn counts, token figures, `npx `, "magic link",
"pull request", "cloud apply", "trusted by", testimonials, uptime/SLA. "MCP" appears in exactly two
places BRIEF.md itself specifies — the §12 answer ("plain HTTP + MCP") and the §15 footer docs link
— and both are backed by the real shipped `docs/mcp.html` page (R2-02 in `pages.json`), so they're
true of the code as it exists. "Exact file" claims are always conditional on source mapping. Every
number on the page is live from an API or absent.

## How responsiveness/a11y was verified (playwright-cli, real browser)

- **390px and 360px mobile:** `document.scrollWidth == innerWidth` (390/360), and an
  all-elements scan found zero boxes past the viewport edge in LTR *and* RTL. One real bug found
  and fixed here: the install-snippet `<code>` overflowed (637px) — switched `.code-block` to
  `pre-wrap` + `overflow-wrap:anywhere`.
- **1440px desktop:** scrollWidth 1440, zero overflowing elements, all 13 sections + footer
  present, every in-page anchor resolves to a real target.
- **RTL:** `dir=rtl` flips layout, cost bars mirror (bar A at x≈205, B at x≈20), tabs/stepper still
  operate; Arabic strings are real MSA (not transliteration), `:lang(ar)` display/mono overrides
  per DIRECTION.
- **Reduced motion:** emulated `prefers-reduced-motion: reduce` → no `data-anim` on hero or cost
  plot, computed `animation: none`, bars at full static height (200/41px) — correct static
  end-state. No-JS simulated by 404ing `main.js`: fallback pricing card, hidden live sections,
  full static graph, first tab panel visible.
- **Console:** zero messages/errors on load, reload, and through every interaction above.
- **Touch targets:** interactive elements use the 44px+ atoms (`.btn` 44/52px, `.flow-tab` 52px,
  `.step-btn` 56px, nav links 44–48px, footer links padded to 44px).
- One more real bug found and fixed during verification: `grid-template-rows: 62fr 10px 22fr`
  auto-placed the cheap segment into the 10px gap track (rendered 10px tall) — replaced with
  `62fr 22fr` + `row-gap: 10px`.

## Known gaps / TODOs

- `.playwright-cli/` screenshots and the pass-1 log files (`phase1.log` etc.) remain in the folder
  from the tooling; they're inert and can be deleted before deploy.
- The stats/stacks/plans sections are English-first with full Arabic strings; tag values (stack
  names, language names) are localized via `Intl.DisplayNames` where applicable but tool names
  stay proper nouns.
- Docs footer links are site-absolute (`/docs/...`) matching the deploy target where `docs/` is
  served alongside the landing root; served strictly as an isolated folder they 404 by design (the
  nav already made this tradeoff in pass 1).
