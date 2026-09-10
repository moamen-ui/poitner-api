# R3-05 — Public "Data & self-hosting" page + privacy policy refresh

(NEW-6 · Release 3 · ½ day)

## Goal

The first question an agency or enterprise asks is "what exactly does this capture from our app, and
where does it live?". A policy page exists (`landing/privacy.html`, 148 lines, effective 2026-09-01)
but it is legal-shaped and predates R3-04/NEW-5. Ship a **buyer/engineer-facing** page —
`landing/data.html` — that answers, concretely and honestly: what is captured (with the actual
field list and limits), what is never captured, how to mask, retention & deletion, security of
keys/tokens, and the self-hosting boundary. Then refresh `privacy.html` so the two never contradict.

## Out of scope

- Legal review / jurisdiction-specific language (GDPR/CCPA clauses) — the founder owns that; this page is factual.
- A docs site (§48, held). This is one static page in `landing/`.
- Dashboard or API changes.

## Prerequisites

- **R3-04** merged (so the statements about form values, `data-snapshot-mask` and `CaptureTextContent` are true) and **NEW-5 (R1-06)** merged (API keys hashed). If either is not yet live, the page must say "from version X" or omit the claim — never state something the shipped code does not do.
- Facts: landing is plain static HTML bind-mounted into Caddy (`docker-compose.prod.yml:60`, `./landing:/srv/landing:ro`), so deploy = `git pull` on the VM; footer links live in `landing/index.html:512` (`/privacy.html`, `data-i18n="foot.privacy"`) and `landing/v2/index.html:2709`; i18n strings at `landing/index.html:572` (en) / `:628` (ar); `privacy.html` sections: What we collect · How we use it · Where it's sent · Browser extension permissions · Data retention · Security · Children's privacy · Changes · Contact (`landing/privacy.html:87-140`). `BrandingResponse.Urls.Docs` exists (`Application/DTOs/Branding/BrandingResponse.cs:7`). Element capture fields: `Domain/ValueObjects/ElementCapture.cs`; snapshot limits `capture.ts:139,145` (120/160 chars); page-context capture is opt-in per project + per comment ("Report as a bug") — `Project.PageContextCaptureEnabled`, `Comment.IsBugReport`.

## Design

### A. `landing/data.html` — structure (same look as `privacy.html`: reuse its `<style>` block and header/footer)

1. **In one paragraph** — Pointer stores *feedback about UI elements*, not your users' data. Nothing is captured until a signed-in stakeholder clicks an element and submits a comment.
2. **What a comment contains** — a table generated from `ElementCapture`: `selector`, `snapshot` (element's own tag, attrs ≤ 120 chars, text ≤ 160 chars), `classes`, `computedStyles`, `appliedCssRules`, `sourcePath` (a path or an 8-char hash), `parentInfo`, `pageUrl`/`route`/`pageTitle`, `viewportWidth/Height`, `deviceType`, `devicePixelRatio`, `userAgent`, optional `screenshotUrl`; plus the comment body, environment, author, replies.
3. **What is never captured** — form values (`input/textarea/select`), cookies, localStorage, request/response bodies or headers, keystrokes, other pages, anything before the click.
4. **Opt-in extras** — screenshot (checkbox per comment; the image shows exactly what the stakeholder sees), bug-report context (console errors/warnings + failed/slow request method/URL/status/duration; project-enabled + per-comment checkbox).
5. **Masking & controls** — `data-snapshot-mask` (one attribute on any subtree), project setting "Capture element text" off, `screenshot="false"` on the element, `enabled` env guard for production builds.
6. **Where it lives** — hosted: our API + Postgres in <region/provider — founder fills in>; self-hosted: **your** API + Postgres; the widget, CLI and extension are thin clients that only talk to the server URL you configure. Diagram (inline SVG or a simple table) of the boundary.
7. **Retention & deletion** — comments kept until deleted; deleting a project deletes its comments and screenshots; deleting a screenshot removes the file; `RetentionDays` plan entitlement noted as "available on request" until the retention job ships (§33 full).
8. **Security** — passwords hashed; **API keys stored hashed (SHA-256, prefix-indexed)**; JWT 12 h; per-workspace data isolation enforced in the database layer; widget assets pinnable with SRI (R3-03); `git push` never performed by AI tooling.
9. **Questions** — contact + link to `privacy.html`.

All product names via the same i18n mechanism as `index.html`? **Decision:** no i18n on this page for v1 (English only, like `privacy.html`); add `lang="en"`.

### B. `privacy.html` refresh

- "What we collect → Feedback content": add "Form field values are never captured; hosts can mask any region with `data-snapshot-mask`; projects can disable text capture."
- "Security": add API-key hashing; keep the extension sentence.
- "Data retention": add screenshot deletion and the per-project text toggle; bump *Effective date*.
- Add a link: "For the engineering view of what is captured, see **Data & self-hosting**."

### C. Links

- Footer of `landing/index.html` (and `landing/v2/index.html`) → add `Data & self-hosting` next to Privacy (`data-i18n="foot.data"`, en "Data & self-hosting", ar "البيانات والاستضافة الذاتية").
- `pointer-init.md` privacy subsection (R3-04) → link to `/data.html`.
- Dashboard project settings help text (R3-04) → link to `/data.html`.

## Tasks

1. Create `landing/data.html` (§A), copying `privacy.html`'s head/style/header/footer; `<title>Data & self-hosting — Pointer</title>`; `<meta name="description">`.
2. Edit `landing/privacy.html` (§B); update effective date.
3. Edit `landing/index.html:512` and i18n maps at `:572`/`:628`; mirror in `landing/v2/index.html:2709,2843,2963`.
4. Cross-links in `API/wwwroot/pointer-init.md` (R3-04 subsection) and dashboard help text (if R3-04's dashboard task is done; else note as follow-up).
5. Verify locally: `python3 -m http.server 8099 -d landing` → open `/data.html`, `/privacy.html`; check dark mode (`prefers-color-scheme`) and mobile width; no horizontal scroll; all links resolve.

## Dashboard tasks

none (optional: link text in project settings, covered by R3-04).

## Tests

- No unit tests. **Checks:** `npx html-validate landing/data.html landing/privacy.html` (or `tidy -q -e`) → no errors; link check with `lychee --offline landing/*.html` (or a `grep -o 'href="[^"]*"'` list manually resolved); Lighthouse accessibility ≥ 95 on `data.html` (chrome-devtools `lighthouse_audit`).
- **E2E scenario:** `landing-data-page-links` (Playwright: footer link "Data & self-hosting" on `/` navigates to `/data.html`; page contains the strings "never captured", "data-snapshot-mask", "self-hosted").

## Acceptance criteria

- [ ] `/data.html` renders with the shared landing styles in light and dark mode, ≤ 1 screen of scrolling on desktop per section, no horizontal scroll at 390 px.
- [ ] Every factual claim on the page maps to shipped code (reviewer spot-checks: form values, mask attribute, API-key hashing, 12 h JWT, project deletion cascade).
- [ ] `privacy.html` and `data.html` agree; effective date bumped.
- [ ] Footer links present in `index.html` and `v2/index.html`, in both languages.
- [ ] Deployed via `git pull` on the VM (landing is bind-mounted); `curl -I https://<landing>/data.html` → 200.

## Rollout / compatibility

- Static page; no risk. Keep the old `/privacy.html` URL (linked from the extension store listing).
- If R3-04 / NEW-5 slip, publish with the corresponding sentences removed rather than delaying the page.

## Report template

```
Branch: feat/r3-05-privacy-page
Files: landing/data.html, landing/privacy.html, landing/index.html, landing/v2/index.html, API/wwwroot/pointer-init.md
Validation: html-validate ✓ links ✓ lighthouse a11y <score>
Screenshots: light / dark / 390px
Claims verified against: R3-04 <commit>, R1-06 <commit>
Skipped / open: region/provider sentence left as <TODO founder>
```
