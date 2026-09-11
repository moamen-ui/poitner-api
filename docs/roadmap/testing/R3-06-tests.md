# R3-06-tests — landing page refresh

## Covers
Acceptance criteria of [`../execution/R3-06-landing-refresh.md`](../execution/R3-06-landing-refresh.md):
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
