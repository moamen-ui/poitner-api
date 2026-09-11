# R2-07-tests — Public documentation site

## Covers
Acceptance criteria of [`../execution/R2-07-docs-site.md`](../execution/R2-07-docs-site.md):
AC-1 (serve + nav resolves) → **R2-07-01** · AC-4 (nav identical) → **R2-07-02** · AC-3 (manifest ↔ disk
↔ owner) → **R2-07-03** · AC-5 (dead link fails the check) → **R2-07-04** · AC-2 (footer follows
`/api/branding`) → **R2-07-05** · AC-6 (dark mode + RTL) → **R2-07-06** · AC-7 (no hard-coded host) →
**R2-07-03** step 4.

## Preconditions
- Prerequisite docs merged: none. R2-07 is self-contained static HTML; the API is needed only by
  `R2-07-05`, which uses the running stack from `reset.sh` + `seed.mjs` like any other scenario.
- Fixture server: `node e2e/scripts/serve-dir.mjs landing $PORTS.landing` — **port 8099**, already in the
  harness registry (§2) as `landing`, shared with R3-05. The two must not run concurrently; see
  *State coupling*.
- Personas: `superAdmin` for `R2-07-05` only (branding is a super-admin setting,
  `SettingsController`/`BrandingController` are `Policies.SuperAdmin`). Everything else is anonymous —
  these are static files.
- Helpers: `e2e/scripts/lib/docs.mjs` (`readManifest`, `navBlock`, `internalLinks`) from R2-07 task 6.
- **Branding teardown is mandatory** for `R2-07-05`: capture `GET /api/branding` first and restore it in
  a `finally`, exactly as `R2-00`'s white-label window does (`scripts/reset-branding.mjs`). A leaked
  `brand_url_docs` poisons every later branding assertion.

## Scenarios

| id | intent | tier | layer | role | steps | expected | evidence |
|---|---|---|---|---|---|---|---|
| R2-07-01 | docs site serves and every nav target resolves | PR | api | — | 1. `node e2e/scripts/serve-dir.mjs landing 8099`. 2. `GET http://localhost:8099/docs/` . 3. Parse the block between `<!-- nav:start -->` and `<!-- nav:end -->`; collect every `href`. 4. For each internal href (not `http`, not `mailto`), resolve it against `landing/` and `fs.existsSync` the file — **do not** assert on HTTP status (the Caddy `(dashboard)` fallback returns 200 + the landing page for a missing path, so a 200 proves nothing). 5. `GET /docs/assets/docs.css`. | 2 → 200, body contains `nav:start` and a `[data-brand-name]` element; 4 → every internal target exists on disk, list printed; 5 → 200, `content-type` contains `text/css` | report row with the resolved nav-target count |
| R2-07-02 | the nav block is identical in every page | PR | api | — | 1. `readManifest()` → the page list. 2. For each page, `navBlock(file)` = the bytes between the two markers. 3. Compare every block to the first. | all blocks byte-identical; on failure the report names the first differing file and a unified diff of the two blocks | diff pasted on failure |
| R2-07-03 | manifest ↔ disk ↔ owner, and no hard-coded host | PR | api | — | 1. `readdir landing/docs/*.html` vs `pages.json` `file` values — compare both directions. 2. For each entry, `fs.existsSync(landing/docs/<file>)`. 3. For each `ownedBy`, assert `docs/roadmap/execution/<id>-*.md` matches exactly one file. 4. Grep every `landing/docs/**/*.html` for `https?://[a-z0-9.-]+` and assert every hit is either a bundled fallback already present in `landing/index.html` (the README, `app.`/`demo.`/`pointer.moamen.work`) or a relative path. | 1 → sets equal, no orphan file, no phantom entry; 2 → all exist; 3 → every owner resolves to exactly one doc; 4 → no host outside the fallback allowlist | the three set-difference lists (expected empty) |
| R2-07-04 ⛓ | a dead internal link fails the check | PR | api | — | 1. Run `R2-07-01` step 4 → baseline green. 2. Append `<a href="/docs/definitely-not-here.html">x</a>` to `landing/docs/index.html`. 3. Re-run the internal-link check. 4. **`finally`:** `git checkout -- landing/docs/index.html`. | 3 → fails, naming `index.html` and `/docs/definitely-not-here.html`; 4 → `git status --porcelain landing/docs/` is empty | both runs' output; the clean `git status` |
| R2-07-05 ⛓ | the footer Docs link follows `/api/branding` | nightly | api + widget | superAdmin | 1. Capture `GET /api/branding` → `original`. 2. Browser: `page.goto('http://localhost:8099/')`; read `document.querySelector('[data-brand-docs]').href` → **default**. 3. `PUT /api/admin/branding { urls: { docs: 'https://docs.selfhost.test' } }`. 4. New browser context (branding is fetched once per load — `landing/index.html:836`); `goto` again; read the href. 5. Block `**/api/branding` via `page.route(r => r.abort())`; `goto`; read the href. 6. **`finally`:** `PUT` `original` back, then re-read `GET /api/branding` to confirm. | 2 → `/docs/` (or the bundled README fallback, whichever the shipped default is — assert it is one of the two, never empty); 4 → exactly `https://docs.selfhost.test`; 5 → the bundled fallback, **not** empty and **not** the previous override; 6 → branding equals `original` field-for-field | the three href values; the restored branding payload |
| R2-07-06 | dark mode and RTL | nightly | widget | — | 1. `page.emulateMedia({ colorScheme: 'light' })`; `goto /docs/`; read `getComputedStyle(document.body).backgroundColor` → `light`. 2. `emulateMedia({ colorScheme: 'dark' })`; reload; read → `dark`. 3. `document.documentElement.setAttribute('dir','rtl')`; assert `document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1`. 4. Assert the nav's computed `padding-inline-start` is non-zero in both directions (proves logical properties, not `padding-left`). | 1 ≠ 2 (both non-transparent); 3 → no horizontal overflow; 4 → non-zero both ways | the two colour values; the scrollWidth/clientWidth pair |

## Spec files
- `e2e/docs/docs-site.spec.mjs` — R2-07-01 … R2-07-04 (node, no browser).
- `e2e/docs/docs-site.spec.ts` — R2-07-05, R2-07-06 (Playwright).
- `e2e/scripts/lib/docs.mjs` — **new**: `readManifest()`, `navBlock(file)`, `internalLinks(file)`.
- Reuses `e2e/scripts/serve-dir.mjs` (R2-00) and `e2e/scripts/reset-branding.mjs` (R2-00).

## Not covered here
- **Page content correctness.** Whether `install.html` actually describes the install is the owning
  item's acceptance criterion, not this doc's. R2-07 proves the *shell and the wiring*.
- **Prose quality / reading level** — human review.
- **Search** — not built (R2-07 Decision 5).
- **AR translation of docs content** — EN-only in v1 (Decision 6); `R2-07-06` proves the shell is
  RTL-*capable*, which is all that is claimed.
- **Production Caddy behaviour** — `/docs/` relies on the existing `try_files` and the existing bind
  mount, both already exercised by `landing/v2/`; re-testing Caddy belongs to R3-03's header matrix.

## Flake notes
- Port 8099 is shared with R3-05 (harness §2). Both are static-file scenarios in the same phase — run
  them sequentially, never concurrently, or the second `--strictPort` bind fails.
- `R2-07-05` must use a **fresh browser context** after the branding `PUT`: `loadBranding()` runs once per
  page load (`landing/index.html:836`), so reusing the context reads the stale value and the scenario
  fails for the wrong reason.
- `R2-07-04` writes to a tracked file. Its `finally` `git checkout` is part of the PASS criteria — a run
  that leaves `landing/docs/index.html` dirty has failed even if the assertion passed.
- No fixed sleeps anywhere: `serve-dir.mjs` readiness is polled, branding changes are observed by
  reloading rather than waiting.

## State coupling
```
R2-07-04 <- R2-07-01    # needs the baseline green run first; mutates a tracked file and reverts it
R2-07-05 <- reset       # consumes and restores global branding state (one-shot global row)
```
