# R2-07 — Public documentation site (§48 · Release 2, first item · 1–2 days)

## Goal
A developer who lands on the product site can find how to install it, how to apply comments, what the
CLI does and what data is captured — without reading a GitHub README or asking. The site is a plain
static section of the existing landing page, so every feature's page can be written **by the model that
just built the feature**, while the context is live, instead of by someone re-reading code months later.

This doc builds the **shell**: layout, navigation, styling, the page manifest and the tests. It does not
write the content — each execution doc's `## Docs` section owns its own page.

## Out of scope
- The page *content* for individual features (owned per item — see `01-OVERVIEW.md` Definition of done).
- Search (see Decision 5). Localisation of docs content (Decision 6). A docs build step (Decision 3).
- `landing/data.html` — the buyer-facing privacy page R3-05 owns. The docs site **links** to it and must
  not duplicate it.

## Prerequisites
- None hard. Ships at the **start of Release 2** so that pages written during Release 1 have a shell to
  be adopted into (Task 8). R1 items are not blocked: they write their page as a plain HTML file and
  this doc wraps it.
- Verified facts:
  - The bare domain serves `/srv/landing` and imports the `(dashboard)` snippet, whose
    `try_files {path} {path}/index.html /index.html` already resolves subdirectory index pages
    (`Caddyfile`; proven in production by `landing/v2/`).
  - `docker-compose.prod.yml:60` bind-mounts `./landing:/srv/landing:ro`. No build step exists for
    `landing/` and none is added here.
  - `/api/branding` returns `urls.docs` (`BrandingService.cs:15` `DefaultUrlDocs`, persisted as
    `brand_url_docs`, exposed on `BrandingResponse.Urls.Docs`).
  - The landing page's `applyBrand()` (`landing/index.html:792-806`) rewrites `[data-brand-name]`,
    `[data-brand-logo]` and prefix-matched `app`/`demo` URLs — but **not** the docs link, which is
    hard-coded to the GitHub README at `landing/index.html:511`.

## Design references

- **Impeccable docs — <https://impeccable.style/docs>.** The docs site is a frontend surface, so it is
  built through the `impeccable` skill like every other UI in this project, and its upstream
  documentation is the reference for the *process*: creating a design, working within a design system,
  auditing and critiquing a screen, focused refinement (layout, typography, colour, motion), and
  simplification/hardening (responsive, accessible, resilient). Consult it when you need the reasoning
  behind a step. Its own advice applies here: describe the outcome you want rather than hunting for a
  command.
- **The landing page is the design system of record for this site.** `landing/index.html` already
  carries the tokens (`--pf-*`), the light/dark handling and the RTL shell; the docs site inherits them
  rather than inventing a second look. A docs page that does not look like it belongs to the landing
  page is a rework.

## Design

### A. Location and URL
**Decision: `landing/docs/`, served at `<landing-domain>/docs/`.** A `docs.<domain>` subdomain would need
a new Caddy site block, a DNS record, its own certificate and a second bind mount; the subdirectory needs
**none of those** — it is already served and already routes. `/api/branding`'s `urls.docs` remains the
source of truth for *where the docs are*, so a self-hoster can still point somewhere else entirely.

```
landing/docs/
  index.html                 overview + "start here"
  assets/docs.css            one stylesheet, tokens copied from landing/index.html
  pages.json                 the manifest (§D)
  <page>.html                one file per page, owned by an execution doc
```

**Known gotcha, accepted:** the `(dashboard)` snippet ends in `/index.html`, so a dead `/docs/nope.html`
link serves the **landing page with HTTP 200**, not a 404. A broken link is therefore invisible to a
status check. This is why the link-check (§E, `R2-07-04`) asserts the target **exists on disk** rather
than that it returns 200.

### B. Page shell
Every page is a complete standalone HTML document: `<head>` with the same font/token `<style>` block as
`landing/index.html`, a sidebar nav, the content, and the shared footer.

**Decision: hand-written HTML, one file per page.** Rejected markdown-rendered-at-deploy (there is no
build step, and adding one changes how `landing/` deploys) and rejected a client-side markdown renderer
(it breaks the link-check, breaks no-JS readers, and hurts search indexing — and being findable in search
is the entire reason this item exists).

**Decision: the nav is duplicated verbatim into every page.** Static HTML has no includes. To stop the
copies drifting, `R2-07-02` asserts the block between `<!-- nav:start -->` and `<!-- nav:end -->` is
byte-identical across every page.

### C. Branding, theme and direction
- **Footer docs link (the fix):** add `data-brand-docs` to the landing's footer anchor
  (`landing/index.html:511`) and extend `applyBrand()` to `if (b.urls.docs) document.querySelectorAll('[data-brand-docs]').forEach(a => a.href = b.urls.docs)`.
  An attribute marker, not a URL prefix match — the existing `app`/`demo` rewrites match on a hard-coded
  prefix, which cannot work for a default that is a GitHub README URL. The bundled fallback stays the
  README, so the link is never empty.
- **Docs pages carry `data-brand-name`/`data-brand-logo`** and run the same `/api/branding` fetch, so a
  white-labelled instance renames the docs too. Must not break the §13 mock-domain rehearsal: the pages
  hold no hard-coded host beyond the same bundled fallbacks the landing already uses.
- **Dark mode:** `prefers-color-scheme` on the same tokens as the landing. No toggle.
- **Direction: the shell is RTL-safe** (logical properties: `margin-inline`, `padding-inline`, `text-align: start`), verified by flipping `dir="rtl"`.
  **Decision: content ships EN-only in v1.** Bilingual docs would double the writing burden on *every*
  implementer, which is what would make the write-docs-with-the-feature rule collapse. The shell is
  ready; an AR pass is a hold-list follow-up.

### D. `landing/docs/pages.json` — how the page set stays honest
```json
{ "pages": [ { "file": "install.html", "title": "Install", "nav": "Getting started",
               "ownedBy": "R1-02", "summary": "one line" } ] }
```
`R2-07-03` asserts three-way agreement: every `*.html` on disk appears in the manifest, every manifest
entry exists on disk, and every `ownedBy` names a real `docs/roadmap/execution/*.md`. A feature page
cannot be orphaned, and a deleted feature's page cannot silently survive.

### E. Nav groups (v1)
`Getting started` (index, install, cli-reference) · `Using it` (apply, mcp, notifications,
inviting-stakeholders) · `Configuration` (project-settings, workspaces, api-keys, source-mapping,
widget-versions) · `Privacy & self-hosting` (links out to `/data.html` and `/privacy.html`).
Groups are fixed here; pages are added to them by the items that own them.

**Decision: no search in v1.** ~15 pages with a visible nav does not need an index, and a client-side
index is a build step. Revisit past 30 pages.

## Tasks
1. `landing/docs/assets/docs.css` — tokens and layout lifted from `landing/index.html`'s `<style>`; logical properties throughout; `prefers-color-scheme` dark block.
2. `landing/docs/index.html` — the shell reference implementation: head, `<!-- nav:start -->`/`<!-- nav:end -->` block, content, footer, the `/api/branding` fetch and `applyBrand` subset.
3. `landing/docs/pages.json` — seeded with every page that exists at merge time.
4. `landing/index.html` — add `data-brand-docs` to the footer anchor (`:511`), point it at `/docs/`, and extend `applyBrand()` (`:799-803`) per §C. Keep the GitHub README as the bundled fallback.
5. `landing/index.html` nav — add a `Docs` link alongside the existing sections.
6. `e2e/scripts/lib/docs.mjs` — helpers the tests need: `readManifest()`, `navBlock(file)`, `internalLinks(file)`.
7. `e2e/docs/docs-site.spec.mjs` + `e2e/docs/docs-site.spec.ts` (Playwright, for theme/RTL) per `R2-07-tests.md`.
8. **Adopt pages written during Release 1** — any `landing/docs/*.html` created by an earlier item gets the shell (head, nav block, footer) and a `pages.json` entry. List what was adopted in the report.
9. `AGENTS.md` — one paragraph: where docs live, that a feature's page is written with the feature, and that `pages.json` must be updated.

## Dashboard tasks
None. No API or DTO change (`urls.docs` already exists and is already returned).

## Docs
Owns `landing/docs/index.html` — the entry page: what the product does in three sentences, the nav
groups, and a "start here → install" call to action. Answers *"where do I begin?"*.

## Tests
- E2E (`R2-07-tests.md`): `R2-07-01` serve + every nav target resolves · `R2-07-02` nav block identical across pages · `R2-07-03` manifest ↔ disk ↔ owner three-way check · `R2-07-04` internal-link check against disk · `R2-07-05` footer link follows `/api/branding` · `R2-07-06` dark mode + `dir="rtl"`.
- No unit tests (static HTML).

## Acceptance criteria
- [ ] `node e2e/scripts/serve-dir.mjs landing <PORTS.landing>` → `/docs/` renders with nav, and every nav link resolves to a file that exists on disk.
- [ ] The landing footer's Docs link points at `/docs/` by default and at `urls.docs` when `/api/branding` returns one; with the API unreachable it still points at the bundled README fallback.
- [ ] `pages.json` and `landing/docs/*.html` agree in both directions, and every `ownedBy` resolves to a real execution doc.
- [ ] The nav block is byte-identical in every page.
- [ ] Introducing a dead internal link makes `R2-07-04` fail naming the file and the href (verify, then revert).
- [ ] Dark mode renders (background token differs from light) and `dir="rtl"` produces no horizontal overflow.
- [ ] The §13 mock-domain rehearsal still passes: no hard-coded host in `landing/docs/` beyond the bundled fallbacks.

## Rollout / compatibility
Purely additive — new files under `landing/`, one attribute and one branding rewrite in
`landing/index.html`. No API, no schema, no Caddy change; the existing bind mount and `try_files` already
serve it. The old GitHub README link keeps working as the fallback, so nothing breaks if the site is not
deployed yet.

## Report template
Files created/changed · which Release-1 pages were adopted (Task 8) · the `pages.json` entry count ·
pasted output of the serve + link-check run · confirmation of the deliberate dead-link check.
