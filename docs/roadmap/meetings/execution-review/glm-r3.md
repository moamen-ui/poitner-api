1. FACTUAL ERRORS

1.1 **R3-05:35** — "deleting a project deletes its comments and screenshots" is false. `ProjectService.DeleteAsync` (`ProjectService.cs:365-443`) soft-deletes comments/replies/actions (`DeletedAt` stamped at 396/409/421) and **never deletes screenshot files** (no `_fileStorage.DeleteAsync` in the method). Files are only removed on comment edit with `RemoveScreenshot` (`CommentService.cs:549-552`). Contradicts the doc's own rule (R3-05:22) and 11-final-decisions:14 ("soft-delete").

1.2 **R3-01:177** — "build the `e2e/fixture-app` Vite variant": no Vite fixture exists. `e2e/fixture-app/{smoke,alpha,beta}` are plain static HTML/CSS/JS (no bundler, no package.json). Same for R3-02:102 ("detection … on `e2e/fixture-app`") — it has no Tailwind/CSS-vars, so that acceptance check is vacuous.

1.3 **R3-04:32** — §A.2 keeps "`class`" as a structural attribute inside masked subtrees, but `class`/`style` are already excluded from every snapshot today (`capture.ts:137`); listing it invites an implementer to re-add it.

1.4 **R3-04:63** — "(already loads the project … — reuse)" is true for `CreateAsync` (`EnsureAsync`, `CommentService.cs:48`) but **false for `EditAsync`** (`CommentService.cs:529-561` never loads the project).

1.5 **R3-03:92** — "`document.adoptedStyleSheets`" is the wrong property: the `<link>` lives in the shadow root (`element.ts:186-191`); the check must be `shadowRoot.adoptedStyleSheets`.

1.6 **R3-01:111** — "would silently fall through switch statements" understates the widget failure: `STATUS_STR[c.status] || 'open'` (`element.ts:630`) renders an unknown value 5 as **open**, actively misclassifying (worse than a blank).

*Verified-correct (spot checks): Program.cs:239-256/286-319/309; Caddyfile @widget:29-30; constants.ts:153-162; build.mjs:27-29; capture.ts:128-146/200-202/242-257; framework-source.ts:50-90; Comment.cs:21; CommentStatus.cs:3; CommentSummaryDto.cs:19; ApplyElementDto.cs:18; ProjectService.cs:674-690/717-720; CaptureConfigResponse.cs; install.sh:79; skill.md:181-196; pointer-init.md:29/170-189/293-337/348-355; landing/privacy.html (148 lines, :77 effective date, :87-140 sections); index.html:512/572/628; v2:2709/2843/2963; docker-compose.prod.yml:60; Dockerfile:8-13; inject-main.ts:~90; JwtTokenService.cs:10 (12 h); only publish-clients.yml in .github/workflows; fixture "✓ completed" pill `templates.ts:162-163`.*

2. AMBIGUITIES (proposed text)

2.1 **R3-01 ProjectBuild.OwnerId** — 01-OVERVIEW:57 says `OwnerFor(currentUser)`; the Comment precedent stamps from the **project's** owner (`CommentService.cs:54-61`). Add: *"Stamp `ProjectBuild.OwnerId` from the resolved project's `OwnerId` (as `CommentService.CreateAsync` does), not from the caller."*

2.2 **R3-01:129 ReportBuildRequest.Sha** has no validator. Add: *"`Sha` validator: `^[0-9a-f]{7,40}$` after `Trim().ToLowerInvariant()`."*

2.3 **R3-01 §F role gate** unspecified — QuickAccess Clients can currently be assumed allowed. Add: *"Reject `IsQuickAccess` callers on `POST /builds` (mirrors `UpdateStatusAsync`, CommentService.cs:482-483); marking deployed is a lifecycle action."*

2.4 **R3-01 DeployedCommentIds** semantics. Add: *"Ids newly marked by this call only; empty on repeat reports of the same sha."*

2.5 **R3-01 manifest.prev.json** introduced only in passing (§E.2/task 4). Add: *"On every manifest write, rename the existing `manifest.json` to `manifest.prev.json` first; never merge contents."*

2.6 **R3-04 §A.1 value source** — typed values live in the DOM property, not the attribute; the E2E (`R3-04:76`, fill form → `•••`) only passes if the property is read. Add: *"‘Non-empty value' reads the DOM value property (`(el as HTMLInputElement).value`), not the attribute; the `value` attribute itself is always dropped for form tags."*

2.7 **R3-04 §A.3 `data-user*`** glob vs regex. Add: *"Prefix match `^data-user` (attr names lowercased), i.e. data-user, data-username, data-user-id."*

2.8 **R3-04 §A.2** keeps `data-*` **values** inside masked subtrees — `data-customer-name="…"` would leak through a mask added for exactly that. Add: *"Inside a masked subtree, `data-*` names are kept but their values become `•••`."*

2.9 **R3-04 §C regex robustness** — snapshot attribute values are emitted unescaped today (`capture.ts:139-141`), so a value containing `"` corrupts the string the sanitizer regex parses. Add a task: *"Escape `"` as `&quot;` in `shallowSnapshot` attribute values"* and *"sanitizer treats a tag whose quote balance is odd as malformed → pass through unchanged."*

2.10 **R3-03 §F trigger race** — the 1500 ms timer fires the fetch path even for a slow-but-working link. Add: *"Only the link's `error` event starts the constructed-stylesheet fallback; the 1500 ms timer (existing `_stylesReady`, element.ts:302-312) only stops waiting."*

2.11 **R3-03 startup hash** behavior on missing/corrupt `pointer.version.json`. Add: *"Missing/corrupt → treat every `?v=` as mismatch (no-cache, no mismatch header), log once, never 500."*

2.12 **R3-02 §A "first 20 files by size"** — Add *"20 smallest"*.

2.13 **R3-02 byte-identical output** needs a canonical form. Add: *"Write with fixed key order (version, detectedAt|libraries, tokens, guidance), 2-space indent, trailing newline."*

2.14 **R3-05:34** region/provider TODO — add an explicit `<!-- TODO(founder): region/provider -->` marker so the "every claim maps to shipped code" review (:74) flags it.

3. CONTRADICTIONS

3.1 **R3-02:50 vs :101** — `detectedAt` wall-clock timestamp makes "byte-identical `stack.json`" impossible. Drop the field (or exclude it from the determinism claim).

3.2 **R3-03 §D:2 vs §B:56** — `builtAt` wall-clock timestamp means the CI `git diff --exit-code` freshness check can **never pass** (rebuild always differs). Derive `builtAt` from the git commit timestamp, or diff only `hash`/`files`.

3.3 **01-OVERVIEW:40** ("zero runtime deps except `mcp`") vs **R3-01:79** (optional Babel/Vue peers for `pointer-feedback/vite`). Amend the overview: *"…except `mcp` and the `vite` subpath's optional peers (R3-01)."*

3.4 **01-OVERVIEW:45** ("R3 `map` (held)") vs **R3-01 task 7** shipping `pointer map --from-source`. Amend: *"`map --from-source` (manifest regen only); selector/text index still held."*

3.5 **R3-05:35** vs code and 11-final-decisions:14 — see 1.1.

4. SECURITY / DATA

4.1 R3-05 deletion claim (1.1) — the single highest-stakes item: a privacy page stating a deletion behavior the code does not perform.

4.2 R3-01 `POST /builds` integrity — see 2.3; low severity, gate it.

4.3 R3-04 — `data-snapshot-mask` is client-side only and **not** server-enforceable (server never sees the host DOM); §C covers only the sensitive-attr list + `CaptureTextContent`. Document explicitly: *"Mask-attribute behavior requires a current widget; the server guarantees only §C."*

4.4 R3-04 §C regex on unescaped snapshots — see 2.9.

4.5 Migrations: R3-01 (3 nullable cols + table) and R3-04 (1 defaulted bool) are additive — compliant with 01-OVERVIEW:58; R3-02/03/05 touch no schema. "AI never pushes" — no doc instructs a push; R3-01's apply E2E commits only. Compliant.

4.6 R3-02 tokens local-only with a body-strip test (R3-02:100) — correct boundary, enforceable.

5. TESTABILITY

5.1 **R3-03:114 and R3-04:74 prescribe vitest+jsdom widget unit tests, but `web-component/package.json` has no test framework at all** (scripts: build/watch/typecheck only). Both docs need a task: *"Add vitest + jsdom + `npm test` to web-component/"* (and 01-OVERVIEW's DoD should mention it).

5.2 R3-01:177 — the Vite fixture must be created (task + scope), else the integration test and two E2E scenarios (`source-stamp-prod-build`, `stale-hash-warning`) cannot exist.

5.3 R3-03:121 "through Caddy in prod" is unverifiable pre-merge — replace with a local Caddy container using the prod Caddyfile (or `caddy validate` + post-merge prod curl evidence in the report).

5.4 R3-03 §E smoke depends on `/api/meta` (R1-04) but task 7 wires it into `run-e2e.sh` unconditionally — add *"skip the /api/meta check with a warning while R1-04 is unmerged."*

5.5 R3-02:100 "network-recorded test" — no recorder exists; replace with *"unit-assert `stackfile.buildRequestBody()` output contains no `design` key."*

5.6 R3-03 §D:4 "long task > 50 ms **attributable to** pointer.js" — longtask attribution (`attribution` property) is partial/non-portable. Rewrite: *"assert init-phase long-task total ≤ (baseline run without the widget) + 50 ms"* or measure via `performance.mark/measure` around boot.

5.7 R3-01:182 two-machine byte-identical entries — not checkable by one implementer; rephrase as *"two clean-clone CI builds (matrix) produce identical `entries`; evidence pasted."*

6. OPEN QUESTIONS

(a) **Acceptable exception, keep it.** All three of `@babel/parser`/`traverse`/`generator` are transitive deps of `@babel/core` (itself a dep of `@vitejs/plugin-react`), and `@vue/compiler-sfc` ships with `@vitejs/plugin-vue` — so the optional peers are normally already installed; the subpath fails loudly when absent (R3-01:79). The zero-dep alternative — an `enforce: 'post'` transform over Vite's **already-compiled** output (plain JS → Rollup's `this.parse()`, no deps) stamping `jsx(...)`/`createElement`/`_createVNode` calls by enclosing function — exists but must special-case every JSX runtime's call shape, loses TS-level structure (generics, HOC wrappers), and breaks when later host minifiers mangle function names. esbuild exposes no AST. Amend 01-OVERVIEW (3.3) and note the rejected alternative in R3-01 §C.

(b) **Yes, `immutable` is a lie, and the mismatch path is actively harmful to SRI hosts**: the browser refuses to execute bytes failing `integrity`, so every widget deploy silently kills the widget on every pinned host (no fallback to latest, just a CSP-style console error). Correct contract: make `?v=` content-addressed — `build.mjs` also emits `pointer.<hash>.js`/`.css` (or `dist/<hash>/`), keep the last **N≈10** builds in `wwwroot` (~1.2 MB total), known hash → serve exact bytes with `immutable`, unknown hash → **404** + `X-Pointer-Widget-Version-Mismatch: <current>`; `pointer.version.json` lists retained hashes; retiring a hash older than N is documented and `doctor --pin-snippet` warns. Then SRI holds for the pinned lifetime. Minimum fallback if history is refused: drop "immutable", document "SRI pin survives only until the next widget release — update the snippet when `doctor` warns" (which the doc half-says at R3-03:132, contradicting §A's `immutable`).

(c) Hard-fail when: the guard has been green on scheduled `main` runs for ≥ 2 consecutive weeks (CI longtask noise characterized), threshold split (>60 ms fail, 50–60 ms warn), and only on `main`/scheduled runs — PR runs stay warn-only. "Once R3 ships" (R3-03:81) is undefined; replace with that trigger.

(d) **Confirmed — nullable `DeployedAt` is right.** Widget: `STATUS_STR` map (`constants.ts:46-51`), unknown value coerced to `'open'` (`element.ts:630`), chips built from the map (`constants.ts:116-125`), card/actions switch on the four strings (`templates.ts:161-206`). `pointer.sh`/skill.md patch literal `status=3`; apply-queue filters `status=2` (00-API-INVENTORY:34). Dashboard (separate repo) would at least fail loudly on a regenerated union type; the widget fails silently — the worst client. Decision verified sound.

(e) **Right call.** R2-02's MCP server runs on the dev machine and reads `.pointer/stack.json` locally, so MCP benefits with zero upload; §43 cloud apply is held and is the only future consumer needing server-side tokens — revisit at its trigger. Tokens can embed internal naming (`--customer-*`, component names), and server-side storage would add a tenant-scoped table + auth surface for no current benefit. R3-02's strip-before-POST test (R3-02:100, fix per 5.5) makes the boundary enforceable.

7. VERDICT

- **R3-01 — READY-WITH-EDITS:** 1.2 (create Vite fixture task), 2.1–2.5, 3.3, 3.4, 5.1, 5.7.
- **R3-02 — READY-WITH-EDITS:** 3.1 (drop `detectedAt`), 2.12, 2.13, 5.5, replace the `e2e/fixture-app` AC with the Tailwind fixture (1.2).
- **R3-03 — READY-WITH-EDITS:** **blocking** 3.2 (CI can never pass) and the §A pinned/SRI contract rewrite (6b); then 1.5, 2.10, 2.11, 5.1, 5.3, 5.4, 5.6, (c).
- **R3-04 — READY-WITH-EDITS:** 1.3, 1.4, 2.6–2.9, 4.3, 5.1.
- **R3-05 — NOT-READY:** the deletion claim (1.1/4.1) is false against shipped code and violates the doc's own honesty prerequisite (R3-05:22). Either restate ("deleting a project hides its comments from all views; screenshot files are deleted when a comment's image is removed; full purge incl. files is roadmap") or land the purge first. With that fix (plus 2.14, and aligning §A.8's SRI sentence with R3-03's revised contract): READY-WITH-EDITS — all other line references verified accurate.
