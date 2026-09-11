# Opus review — Release-3 test scenario documents (2026-09-11)

Reviewer: Claude Opus 5, read-only adversarial pass over `docs/roadmap/testing/R3-01-tests.md` …
`R3-05-tests.md` against `00-HARNESS.md`, the matching `docs/roadmap/execution/R3-0*.md`, and the
real code. Verdicts applied to the documents in the same session (see the commit that follows).

Read: `00-HARNESS.md`, the five `R3-0*-tests.md`, their five execution docs, and the cited code
(`web-component/src/capture.ts`, `element.ts`, `constants.ts`, `templates.ts`, `build.mjs`,
`package.json`, `API/Program.cs`, `Caddyfile`, `docker-compose.yaml`, `Dockerfile`, `e2e/**`,
`landing/**`, `Domain/Entity/Comment.cs`, `Domain/Enums/CommentStatus.cs`,
`Application/DTOs/Comment/*`, `Application/Services/Implementation/ProjectService.cs`).

## 1. COVERAGE GAPS

1.1 **R3-01 AC-3 second half unproven.** `execution/R3-01:197` requires "bundle behaviour unchanged
(snapshot test of one route's HTML apart from the attributes)". `buildFixture({ enabled })` exists
(`R3-01-tests:14`) but no scenario ever passes `enabled:false`. *Edit:* R3-01-01 step 0 —
`buildFixture({enabled:false})`, diff `dist/index.html` + one route's markup against the stamped
build modulo `data-component-source|data-build-sha`.

1.2 **R3-01 AC-4 passes vacuously.** `R3-01-tests:21` step 3 asserts `git status --porcelain` has "no
line mentioning `.pointer/manifest.json`". Git collapses untracked dirs to `?? .pointer/`, so this
holds even when nothing is ignored. *Edit:* `git -C <repo> check-ignore -q .pointer/manifest.json` →
exit 0, plus `status --porcelain -uall`.

1.3 **R3-01 AC-2 is contradicted, not proven.** `execution/R3-01:196` says uppercase sha → **400**;
`:137` says trim+lowercase *then* validate. `R3-01-tests:26` row 1 picks 200. Passing the scenario
therefore falsifies the AC as written. *Edit:* amend AC-2 in the execution doc to "uppercase
normalised, malformed → 400" in the same PR.

1.4 **R3-01 AC-6 fallback unproven.** `R3-01-tests:22` step 6 asserts only exit 0 + the warning line;
"apply proceeds via fallback search" (`execution/R3-01:200`) is not asserted. *Edit:* assert the
`--plan` output contains a search-by-component-name instruction for `Card`.

1.5 **R3-02 AC-4 measured by proxy.** `R3-02-tests:22` step 5 times a whole `doctor --refresh-stack`
process at ≤ 3 s — node boot + I/O dominate, so a 2.9 s detector is indistinguishable from 0.2 s.
*Edit:* have `doctor` print `detectMs=<n>` and assert `< 2000`.

1.6 **R3-02 `libraries` never asserted.** `execution/R3-02:50` / task 1 `buildDesignBlock()` emits
`libraries`; no scenario checks it and it is absent from "Not covered here". *Edit:* add
`design.libraries` deep-equal `[]` to R3-02-01 step 3 (the vite fixture ships no component library)
or add `@radix-ui/*` to the fixture.

1.7 **R3-03: bare `?v=` untested (and unspecified).** Neither `execution/R3-03:32-38` nor
`R3-03-tests:30` rows 1-9 covers `GET /pointer.js?v=` (empty value). It matters: Caddy's
`not query v=*` matches an empty value, so `?v=` bypasses the Caddy `no-cache` and lands on the API's
unknown-hash path. *Edit:* add row `GET /pointer.js?v=` → 404 + `X-Pointer-Widget-Version-Mismatch`,
both hops.

1.8 **R3-03 AC-6 cannot fail.** `R3-03-tests:31` step 4 returns PASS for every finite metric
(`>60 && !hardFail` → PASS). It proves `perf-init.mjs` runs; nothing else. *Edit:* say so in
`## Covers` — "AC-6 metric half is a script smoke test, not a gate".

1.9 **R3-04 AC-2 partly unproven.** `R3-04-tests:26` step 5 checks `selector` only; "classes,
computed styles, source path unaffected" (`execution/R3-04:84`) is untested. *Edit:* same step —
assert `element.classes`, `element.computedStyles`, `element.sourcePath` equal an unmasked control
pick's.

1.10 **R3-04 sanitizer idempotency untested end-to-end.** `execution/R3-04:52` promises idempotency;
a widget-produced `value="•••"` must not be re-rewritten. *Edit:* R3-04-01 step 5 — assert `•••`
occurs exactly once in the stored snapshot.

1.11 **R3-05 AC-5 "both languages" half-proven.** `R3-05-tests:20` step 9 asserts the Arabic string
only on `/v2/`; `landing/index.html:628` (ar map) is never checked. *Edit:* assert `page.content()`
on `/` also contains `البيانات والاستضافة الذاتية`.

1.12 **R3-05 AC-1 section budget can pass vacuously.** `landing/privacy.html` has **zero**
`<section>` elements (`:87-140` are bare `<h2>` inside `<main>`), and `execution/R3-05:56` says
data.html copies that shell. `page.locator('section')` may resolve to 0. *Edit:* assert
`await page.locator('section').count() >= 7` before the loop.

## 2. FACTUAL ERRORS

2.1 **`retained[0]` contradicts its own precondition.** `R3-03-tests:12` **prepends** the fabricated
`H0` entry; `R3-03-tests:25` step 1 asserts `retained[0].hash === H`. After the nightly fabrication
runs (before the widget phase, by the doc's own ordering) `retained[0]` is `H0`. `execution/R3-03:41`
also defines the order as build order (newest first). *Edit:* insert the fabricated entry at index 1;
assert `retained[0].hash === H && retained.some(r => r.hash === H0)`.

2.2 **The SRI-tamper fixture is not constructible as described.** `R3-03-tests:15` hard-codes
`pinnedPage()`'s script src to `http://localhost:8090/pointer.js?v=…`, while `:16`'s proxy only
rewrites `GET /pointer.js*` **on its own origin (4177)**. The browser fetches untampered bytes
straight from the API, SRI passes, and steps 6-7 fail. *Edit:*
`pinnedPage({ v, integrity, project, origin = 'http://localhost:8090' })`; the tamper spec passes
`origin: 'http://localhost:4177'`.

2.3 **Wrong first rule in the constructed stylesheet.** `R3-03-tests:28` step 3 expects
`adoptedStyleSheets[0].cssRules[0].selectorText` to match `/\.pf-/`. `API/wwwroot/pointer.css` is
banner → `@charset "UTF-8";` → `:host { all: initial; … }`; `cssRules[0].selectorText` is `:host`.
*Edit:* assert `cssRules.length > 0` and that the joined `cssText` contains `.pf-launcher`.

2.4 **`waitForResponse('**/capture-config')` on an unauthenticated boot never resolves.**
`fetchCaptureConfig()` is reachable only from `init()` (`web-component/src/element.ts:460`), and
`init()` runs only `if (this.token)` (`element.ts:266`). `R3-03-tests:28` step 2 waits for it on a
page the doc explicitly describes as a `preAuthWidget`-free boot (`:35`). *Edit:* `preAuthWidget` the
csp-nonce page, or drop the wait and gate on `#pf-add` + the `/pointer.css` response.

2.5 **Non-serializable `page.evaluate` return.** `R3-03-tests:25` step 7 and `:27` step 5 return
`…shadowRoot` from `page.evaluate`; Playwright cannot serialize a ShadowRoot and throws (it only
appears to work because the pass case is `null`). *Edit:*
`=> !!document.querySelector('pointer-feedback')?.shadowRoot` → `false`.

2.6 **R3-03-02 step 4 asserts a pin no step writes.** `R3-03-tests:25` step 2 pins
`staticTemplateRepo/index.html` to **H**; `:26` step 4 asserts doctor prints
`pinned widget <H0> is behind server <H>`. Nothing rewrites the file to `H0`. *Edit:* insert
"rewrite the snippet's `?v=` to `H0` and its `integrity` to the H0 descriptor" before step 4.

2.7 **`static-template` fixture has no owner.** `e2e/fixture-app/` contains only `alpha`, `beta`,
`smoke`, `serve.mjs`. `00-HARNESS:79` lists `static-template` but attributes it to no doc;
`R3-03-tests:11-19` declares only `csp-nonce` and `pinned-tamper` as its Decisions, yet `R3-03-01`
step 2 and `R3-03-02` steps 4-5 depend on it. *Edit:* add a Decision creating
`e2e/fixture-app/static-template/index.html` (plain HTML, no pointer snippet) to R3-03-tests
Preconditions.

2.8 **Manifest entry count hard-codes an unspecified fixture file.** `R3-01-tests:21` step 2 asserts
`Object.keys(entries).length === 4` counting `App`, which `execution/R3-01:172` (task 13b) never
lists. Also `TrackedCard = memo(Card)` is not §B's HOC case — `execution/R3-01:50` stamps "the inner
function", which exists only for `memo(function X(){…})`; `memo(Card)` with an imported `Card` has no
inner function in that file. *Edit:* assert named keys (`Card`, `PlanList`, `Shell` present; the memo
alias adds none) instead of a count.

2.9 **Determinism-matrix rationale and tooling are wrong.** `R3-01-tests:25` step 1 justifies
`macos-latest` with "path separators differ" — both runners are POSIX `/`; the Windows case is the
unit test's (`execution/R3-01:187`, two mocked git roots). Step 2 uses `sha256sum`, which does not
exist on macOS. *Edit:* drop the separator rationale (keep the two runners for FS/locale differences)
and use `shasum -a 256` or `node -e`.

2.10 **`SetStackAsync` semantics misstated.** `R3-02-tests:16` and `:46` call it
"idempotent/append-if-new". `Application/Services/Implementation/ProjectService.cs:715-718`:
`frontend`/`backend` are **write-once-if-empty** — the first POST wins and later ones are silently
ignored, never merged. *Edit:* restate; see 4.2.

2.11 **`check-ignore` assertion depends on an ignore form nothing writes yet.** `R3-02-tests:22` step
4 expects `git check-ignore .pointer/stack.json` → exit 1. Git cannot re-include a file whose parent
directory is excluded, and `API/wwwroot/install.sh:77` writes `.pointer/` with an inert
`!.pointer/stack.json` at `:79`. The repo's own `.gitignore:5` uses the working `.pointer/*` form.
*Edit:* add to Preconditions "R1-01's on-disk contract emits `.pointer/*`, not `.pointer/`" and
assert that literal line.

2.12 **TODO marker strings do not match.** `execution/R3-05:34` specifies
`<!-- TODO(founder): region/provider -->`. `R3-05-tests:20` step 7 counts `TODO(founder:` and matches
`/TODO\(founder: region\/provider\)/` — neither is a substring of the specified marker, so the count
is 0 and the "exactly 1" assertion always fails. *Edit:* count `TODO(founder)` and match
`/TODO\(founder\): region\/provider/`.

2.13 **`footer .brand` does not exist.** `landing/privacy.html:70` puts `.brand` in
`<header class="top">`; the footer is
`<footer class="bottom"><div class="wrap">Pointer</div></footer>` (`:144-146`). `R3-05-tests:20` step
6 selects `footer .brand`. *Edit:* `header.top .brand` (and `footer.bottom .wrap` if the footer needs
proving).

2.14 **The dark-mode assertion cannot hold on a page built as specified.** `landing/privacy.html`
contains **zero** `@media` rules and no `prefers-color-scheme` block, and `execution/R3-05:56` says
data.html copies its `<style>`. `R3-05-tests:22` step 2 asserts `bgDark !== bgLight`. *Edit:* add a
Precondition that R3-05 tasks 1-2 introduce `@media (prefers-color-scheme: dark)` overriding
`--pf-bg`/`--pf-text` in the shared block — otherwise AC-1's dark half is unshippable.

## 3. UNIMPLEMENTABLE STEPS

3.1 See 2.2 (tamper proxy never in the request path), 2.4 (capture-config wait), 2.7 (missing
fixture). Each blocks a whole scenario on first run.

3.2 **`preAuthWidget` is not exported.** It is a module-local `async function` in
`e2e/widget/widget.spec.ts:40`. `R3-01-tests:32`, `R3-03-tests:35`, `R3-04-tests:32` all say "reuses
`preAuthWidget`". *Edit:* add a harness task extracting it to `e2e/widget/lib/auth.ts` and cite that
path in all three docs.

3.3 **R3-05-02 cannot run in a nightly job.** `lighthouse_audit` is an MCP tool; a headless runner has
no agent to call it, so `R3-05-tests:21` step 4's SKIP branch is the only reachable one. *Edit:*
retier to **manual**, or replace with committed
`npx lighthouse --only-categories=accessibility --output=json` (a zero-token oracle, so
§7-compliant) and keep it nightly.

3.4 **R3-03-07 asserts a contract its execution doc does not define.** `R3-03-tests:31` invents
`--fixture/--runs/--out` and the `{metricMs, runs, gate}` JSON; `execution/R3-03:110` (task 1)
specifies neither. *Edit:* move the flag + JSON contract into R3-03 task 1, or state in
`## Spec files` that this suite owns it.

3.5 **Unspecified exit codes.** `R3-01-tests:24` step 8 asserts `exit 2`; `R3-03-tests:26` step 5
asserts `exit 1`. `execution/R3-01:165` (task 9) and `execution/R3-03:119` (task 10) say only
"errors". *Edit:* pin the codes in the execution docs, or assert `code !== 0` + the message regex.

3.6 **`POINTER_SERVER` override asserted inline.** `R3-02-tests:24` step 2 passes `POINTER_SERVER` in
`env`, but `:14` defers all `init` argv/env spellings to `init-args.mjs` (owned by R1-02). If R1-02
ships a `--server` flag only, the step breaks. *Edit:* route the override through
`INIT_ENV`/`INIT_ARGS` as the doc's own rule requires.

3.7 *Verified, not a defect:* `web-component/package.json:7-11` has build/watch/typecheck only — no
test runner. `R3-03-tests:45` correctly routes `constants.test.ts`/`stylesheet-fallback.test.ts` to
the §G harness, and `R3-04-tests:12` correctly declares R3-03 §G a prerequisite.

## 4. FLAKE RISKS

4.1 **R3-01 pollutes the AI ground truth.** `R3-01-tests:21` step 9 creates a widget comment on
`e2e-alpha`; `:23` steps 1-2 PATCH alpha comments to Applied; `:24` step 9 creates a ReadyToApply
comment on alpha; `:26` posts builds against alpha. `e2e/scripts/probe-visibility.mjs:51-70` asserts
**exact set equality** of alpha's comment ids at `pageSize=100`, `e2e/state/expected.json` pins c1-c8
statuses, `e2e/widget/widget.spec.ts:1-3` states alpha is deliberately never touched by the browser
suite, and `R3-04-tests:18,47` repeats the rule. The `--with-ai` phase and `scripts/audit.mjs`
(`e2e/run-e2e.sh` step 5) run *after* these. Also, R3-01-03 step 1 never says *which* comment id it
PATCHes. *Edit:* give R3-01 its own `e2e-r301` project via the 409-tolerant creator
(`widget.spec.ts:22-36`) and never PATCH seeded comments.

4.2 **R3-02 write-once ordering hazard.** `R3-02-tests:25` step 4 runs `spawnCli(INIT_ARGS)` from a
copy of `e2e/fixture-app/smoke` — `INIT_ARGS` targets `e2e-alpha`. If `design-tokens.spec.mjs` runs
before `stack-post.spec.mjs`, `ProjectService.cs:715-718` locks alpha's stack to the plain-HTML
fixture's detection and `R3-02-03` step 4 / `R3-02-05` step 3 (`frontend ∋ 'react'`) fail until the
next `down -v`. *Edit:* the empty-tokens half of R3-02-04 must target a throwaway project key, never
`INIT_ARGS`.

4.3 **R3-03 fabrication mutates the working tree with only a `finally` restore.** `R3-03-tests:12`
rewrites `API/wwwroot/pointer.version.json` and adds `widget/H0/`. A crashed run leaves them behind
and poisons every subsequent run's rows 1-2 plus the CI freshness check (`execution/R3-03:89`).
*Edit:* call `retained-fixtures.mjs --restore` at the **start** of the widget phase as well as in
`finally`.

4.4 **`dotnet watch` races `restart-api.mjs`.** `Dockerfile:3-4` runs `dotnet watch` and
`docker-compose.yaml` bind-mounts `./:/src`, so writing `API/wwwroot/widget/H0/*` can trigger its own
restart concurrently with the explicit one. *Edit:* after `restart-api.mjs`, poll
`GET /pointer.version.json` until `retained` contains `H0` — not just `/swagger/v1/swagger.json`.

4.5 **`Caddyfile.e2e` is a hand-copy that can silently desync.** `R3-03-tests:13` copies the `@widget`
matcher "verbatim"; a later edit to the real `Caddyfile` leaves the matrix green against stale rules.
*Edit:* generate `Caddyfile.e2e` at run time by extracting the `@widget` + `header @widget` lines from
`Caddyfile`, failing if the extraction yields nothing.

4.6 **Port registry is fragmented.** `00-HARNESS:33` registers 4173/4174/4175/4176/4177 only;
`pinned=4180` (`R3-03-tests:15`), `privacy=4178` (`R3-04-tests:13`), `recorder=4179`
(`R3-02-tests:24`), `landing=8099` (`R3-05-tests:13`) are each declared "registered in
`constants.mjs`" by a different doc, and `e2e/scripts/lib/constants.mjs` has no `PORTS` export today.
*Edit:* fold all four into the harness §2 table in one PR.

4.7 **R3-01-03 step 5's "3 s more" is the fixed sleep §9 forbids** and still cannot prove absence (the
beacon is fire-and-forget; a call at 3.1 s passes). *Edit:* keep the poll but state the bound in Flake
notes as an accepted weakness, or expose the module flag for a `page.evaluate` check.

4.8 **`npx --yes html-validate@8` is an unpinned network install on the PR tier**
(`R3-05-tests:23` step 1), against §1.2 "local, deterministic". *Edit:* add `html-validate` to root
devDependencies now, not "when it proves flaky".

4.9 **The nonce-strict CSP blocks more than the `<link>`.** `style-src 'nonce-pfe2e'` with no
`'unsafe-inline'` also blocks every `style="…"` attribute the widget renders via `innerHTML` (e.g.
pins, `web-component/src/templates.ts:238`) and `ensureHighlightStyle()`'s injected `<style>`. Nothing
is asserted on those today, but a later "renders correctly" assertion on 4176 would be misleading.
*Edit:* one line in R3-03 Flake notes.

## 5. TIERING / BUDGET

5.1 **R3-03-06 is over-tiered.** Rows 1-8 are pure node `fetch` against `:8090` (< 1 s total) and
cover AC-2/AC-3 entirely; only row 9 needs the Caddy container. *Edit:* split — rows 1-8 **PR**, row 9
**nightly**. This is the single biggest tiering win: the header contract then regresses visibly on the
PR that breaks it.

5.2 **R3-05-02 is mis-tiered** as "nightly (or manual)" but is manual-only in practice (3.3). *Edit:*
tier = manual, or convert to a CLI Lighthouse run to earn the nightly tier.

5.3 **PR-tier growth.** R3-04 adds three browser scenarios + a fourth fixture server, R3-05 adds two
browser scenarios + one node spec, R3-01-06 and R3-03-05 add two API specs. Against today's PR widget
phase (two specs, `e2e/run-e2e.sh` phase 4) that is roughly a 3× widget-phase increase — still inside
the 15 min budget, but note it in the harness §8 budget line so the next doc knows the remaining
headroom.

5.4 **Correctly tiered** (no change): R3-01-01/02/03/04/05 nightly (npm ci + vite build); R3-02 all
nightly; R3-03-01/02/03/04/07 nightly; R3-03-05 PR; R3-04-01/02/04 PR; R3-05-01/03/04 PR. R3-04-03 at
PR is justified despite being the only scenario that mutates a project setting mid-suite — its
`finally` restore (`R3-04-tests:27` step 6, `:51`) is correctly made part of the PASS criteria.

## 6. VERDICT

**R3-01-tests — READY-WITH-EDITS.** (a) move every write off `e2e-alpha` to a dedicated `e2e-r301`
project (4.1); (b) replace the `git status` gitignore check with `git check-ignore -q` (1.2);
(c) assert named manifest keys instead of `length === 4` and drop the `memo(Card)` HOC claim (2.8);
(d) fix the matrix rationale + `sha256sum` (2.9); (e) reconcile the uppercase-sha 200-vs-400
contradiction with AC-2 (1.3); (f) pin the `exit 2` contract in `execution/R3-01` task 9 (3.5).

**R3-02-tests — READY-WITH-EDITS.** (a) restate `SetStackAsync` as write-once-if-empty and stop
running the empty-tokens fixture's `init` against `e2e-alpha` (2.10, 4.2); (b) make the
`check-ignore` assertion conditional on the `.pointer/*` form (2.11); (c) replace the 3 s wall-clock
proxy for AC-4 with a printed `detectMs` (1.5); (d) move the server override into `INIT_ENV` (3.6);
(e) add a `design.libraries` assertion (1.6).

**R3-03-tests — NOT-READY.** Three load-bearing steps cannot execute as written — the tamper proxy is
never in the request path (2.2), the nonce fixture waits on a response an unauthenticated widget never
sends (2.4), and `static-template` has no owner (2.7) — and three more assert the wrong thing:
`retained[0]` contradicts the doc's own fabrication step (2.1), the constructed-sheet assertion
targets `:host` not `.pf-` (2.3), and R3-03-02 step 4 checks a pin no step writes (2.6). Every fix is
one-to-few lines, but until they land this document specifies a suite that goes red on first run for
reasons unrelated to R3-03. Also add the bare-`?v=` row (1.7) and re-tier rows 1-8 to PR (5.1).

**R3-04-tests — READY-WITH-EDITS.** The strongest of the five: the expected snapshot strings match
`web-component/src/capture.ts:134-147` exactly — void-element `<input …/>` form with no space before
`/>`, attribute document order, bare attribute name when the value is empty, `class`/`style` excluded
— and the fixture markup, project isolation (`e2e-privacy`), `•••` literal rule and
toggle-restore-in-`finally` are all correct. Add (a) the classes/computedStyles/sourcePath assertions
for AC-2 (1.9); (b) a `•••`-occurs-once idempotency assertion (1.10); (c) `PORTS.privacy` into the
harness table (4.6).

**R3-05-tests — READY-WITH-EDITS.** (a) fix the `TODO(founder)` marker string, which currently always
fails (2.12); (b) `footer .brand` → `header.top .brand` (2.13); (c) add the precondition that the
shared style block gains a `prefers-color-scheme: dark` section, since `privacy.html` has none
(2.14); (d) guard the `<section>` loop with a count assertion (1.12); (e) retier R3-05-02 to manual
(5.2); (f) commit `html-validate` instead of `npx --yes` (4.8); (g) assert the Arabic footer string on
`/` as well as `/v2/` (1.11).
