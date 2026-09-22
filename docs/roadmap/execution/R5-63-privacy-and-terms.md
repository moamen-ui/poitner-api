# R5-63 — Privacy policy + Terms of Service (§63 · Release 5 · 3–5 d writing)

## 1. Goal

Publish `landing/privacy.html` and `landing/terms.html` before the demo goes public. The privacy
policy is PDPL-first (Saudi Personal Data Protection Law) with a GDPR section, states Riyadh data
residency (Oracle Cloud, Riyadh region — founder decision F1), describes what the widget stores in
the visitor’s browser, retention numbers from DB-08, and lists subprocessors. The terms cover SaaS
use under Saudi law.

Effort: 3–5 d (mostly writing/review, minimal code).

## 2. Prerequisites (verified facts)

- **Existing draft**: `landing-redesign/glm/privacy.html:1-158` — a GLM-authored draft covering
  what is collected (account info, feedback content, diagnostics, preferences, extension data),
  how it’s used, where it’s sent, extension permissions, data retention (incomplete — says
  "physical purge … is on our roadmap"), security, children’s privacy, changes, contact
  (`moamen.ui@gmail.com`). **Not PDPL-aware. No Riyadh residency statement. No subprocessor list.
  No widget storage keys. Retention is vague.**
- **Landing structure**: `landing/` directory has `index.html`, `data.html`, `privacy.html` (already
  exists — likely the old/placeholder version), `docs/`, `assets/`.
- **Docs build**: `landing/docs/build-shell.mjs:1-159` — `readdirSync(here)` (`:95`) only rewrites
  `*.html` files that live **inside `landing/docs/`**; `landing/privacy.html` and `landing/terms.html`
  (at the landing root) are never touched by this script regardless of any flag. `external: true` in
  `pages.json` has no effect inside `build-shell.mjs` itself (the `local` filter that reads it,
  `:21`, is computed but unused); it is consumed instead by the e2e helper
  `e2e/scripts/lib/docs.mjs:25-29` (`localPages()`), which skips root-level entries (`file` starting
  with `/`, or `external: true`) when asserting that every manifest entry has a matching on-disk
  file under `/docs/`.
- **`pages.json`**: `landing/docs/pages.json:1-96` — 13 pages. No `privacy.html` or `terms.html`
  entry yet. Only the `/` (index) entry currently sets `"external": true`; `/data.html` is also a
  root-level page but has no `external` flag — it is unaffected either way since `build-shell.mjs`
  never touches files outside `landing/docs/`. The new entries below follow the `/` pattern
  (`"file": "/privacy.html"`, `"external": true`) so the e2e on-disk check (`docs.mjs`) skips them.
- **Widget storage keys** (from `docs/ON-DISK-CONTRACT.md:23` and verified in
  `web-component/src/element.ts`):
  - `localStorage`: `pointer_token` (JWT), `pointer_user` (user JSON), `pointer_env_<project>`
    (selected environment), `pointer_toolbar_pos` (toolbar position JSON), `pointer_widget_theme`
    (light/dark override), `pointer_widget_language` (en/ar override).
  - `sessionStorage`: `pointer_visible` (sidebar state), `pointer_page_session_id` (diagnostic
    context session ID), `pointer_lang_reported` (once-per-session language beacon guard).
- **Retention numbers** (DB-08, `docker-compose.prod.yml:40-43`):
  - Usage events: 180 d (`:40`).
  - Read notifications: 90 d (`:41`).
  - Page context snapshots: 30 d (`:42`).
  - Dead invites: 90 d (`:43`).
- **Subprocessors**:
  - Oracle Cloud (Riyadh region) — infrastructure.
  - Brevo — transactional email (`docker-compose.prod.yml:56-59`, `Email__ApiKey`, `Email__FromEmail`).
  - GitHub — CI/CD, package registry (`.github/workflows/`).
- **Founder decisions**: F1 (KSA/GCC first, Riyadh residency, PDPL + GDPR section),
  F5 (screenshots: kept with the comment, deleted with comment/workspace; DSAR allows targeted),
  F6 (self-written now; counsel at first paying customer with DPA).
- **Contact**: `moamen.ui@gmail.com` (`landing-redesign/glm/privacy.html:151`).
- **Dependencies (status 2026-09-22, written but not implemented)**: the erasure/deletion promises
  in §3.1 items 8/12 (account deletion, right to erasure) describe the design in
  `docs/db/execution/DB-11c-deletion-semantics-remove-disable-erase.md` (tombstone erase; comments
  and replies are kept, attributed to the tombstone — only a full workspace hard-delete, DB-03,
  removes comment rows). The "operator access to your data" posture is **not yet audited or
  time-boxed**: `docs/db/execution/DB-13-operator-impersonation.md` §1 states that *today* the
  super-admin token passes every query filter (`Infrastructure/AppDbContext.cs`, per DB-13's
  Prerequisites) and can read any workspace's content silently; DB-13's audited, time-boxed
  impersonation (surfaced to the workspace admin via `docs/db/execution/DB-12-audit-log.md`'s
  `audit_events`) is written but not implemented. Do not publish a claim that operator access is
  audited or time-boxed until DB-12 and DB-13 ship — write this policy to describe **current**
  behavior (staff/operator access as needed, not yet logged to a customer-visible security log) and
  revise the "your rights" / "internal access" wording once DB-11c, DB-12 and DB-13 land.

## 3. Design

### 3.1 `landing/privacy.html`

Rewrite from the GLM draft. Structure:

1. **Header**: effective date, applicability (widget, dashboard, extension, CLI).
2. **Data controller**: Moamen UI (operating as Pointer), Riyadh, Kingdom of Saudi Arabia.
3. **Legal basis (PDPL)**: legitimate interest for operating the feedback service; consent for
   optional diagnostic context.
4. **Legal basis (GDPR section)**: legitimate interest (Art. 6(1)(f)) for non-EU users; consent
   (Art. 6(1)(a)) for optional diagnostics. Note: no EU-residency promise (F1).
5. **What we collect**: expand the GLM draft with the widget storage keys table and the diagnostic
   context detail.
6. **Widget browser storage** table:

   | Key | Storage | Purpose | Cleared when |
   |---|---|---|---|
   | `pointer_token` | localStorage | JWT session token | logout / manual clear |
   | `pointer_user` | localStorage | Display name, role, preferences | logout |
   | `pointer_env_<project>` | localStorage | Last-selected environment per project | manual |
   | `pointer_toolbar_pos` | localStorage | Draggable toolbar position | reset position |
   | `pointer_widget_theme` | localStorage | Light/dark override | manual |
   | `pointer_widget_language` | localStorage | Language override (en/ar) | manual |
   | `pointer_visible` | sessionStorage | Sidebar open/closed state | tab close |
   | `pointer_page_session_id` | sessionStorage | Diagnostic session ID | tab close |
   | `pointer_lang_reported` | sessionStorage | Language beacon guard | tab close |

   **No cookie is set.** The widget uses `localStorage` and `sessionStorage` only.

7. **Screenshots** (F5): a screenshot may be captured with each comment. It is stored server-side
   and deleted when the comment or workspace is deleted. The screenshot depicts the customer’s
   application UI, not the commenter’s personal data. On a DSAR erasure request, the operator may
   delete individual screenshots.
8. **Data retention**: the retention table from DB-08. Comments and user accounts persist until
   deleted by the user or workspace admin; hard-delete via DB-03.
9. **Data residency**: all data is stored in the Oracle Cloud Riyadh region (Middle East). No data
   is transferred to the EU or US except for transactional email delivery via Brevo (EU).
10. **Subprocessors** table:

    | Subprocessor | Purpose | Location |
    |---|---|---|
    | Oracle Cloud | Infrastructure, compute, storage | Riyadh, KSA |
    | Brevo | Transactional email | EU |
    | GitHub | CI/CD, package registry | US |

11. **Widget consent posture paragraph** — a pre-written paragraph that customers embedding the
    widget can paste into their own privacy policy:
    > "We use Pointer (…) to collect feedback from our team. The Pointer widget stores a session
    > token and display preferences in your browser’s localStorage (no cookies). Data is processed
    > in Saudi Arabia. See [Pointer’s privacy policy](…) for details."

12. **Your rights (PDPL)**: access, correction, deletion, data portability, objection. Contact.
13. **Your rights (GDPR)**: the standard GDPR rights, with the note that GDPR applies only if
    the user is in the EEA.
14. **Children**: not directed at children.
15. **Changes**: updated at this URL.
16. **Contact**: `moamen.ui@gmail.com`.
17. **`[LEGAL-REVIEW]`** placeholders at: data controller registration, PDPL Article references,
    DPA availability, age threshold.

### 3.2 `landing/terms.html`

Structure:
1. Acceptance, eligibility.
2. Service description (SaaS feedback tool).
3. Account responsibilities.
4. Acceptable use.
5. IP (Pointer’s IP; user retains ownership of their content).
6. Data processing (cross-ref privacy policy).
7. Availability, SLA (best-effort, no guarantee).
8. Limitation of liability.
9. Indemnification.
10. Termination.
11. Governing law: Kingdom of Saudi Arabia; disputes: Riyadh courts.
12. Changes.
13. Contact.
14. **`[LEGAL-REVIEW]`** placeholders throughout.

### 3.3 File registration

Add to `landing/docs/pages.json`:
```json
{ "file": "/privacy.html", "title": "Privacy Policy", "nav": "Legal", "external": true, "ownedBy": "R5-63" },
{ "file": "/terms.html", "title": "Terms of Service", "nav": "Legal", "external": true, "ownedBy": "R5-63" }
```

Then run `node landing/docs/build-shell.mjs` so every docs page’s sidebar includes the links.

### 3.4 Caddy

No change — `landing/` is already bind-mounted and served by `file_server` at
`pointer.moamen.work` (`Caddyfile:68-71`). New HTML files are served automatically on next
`git pull` on the VM.

## 4. Safety / impact

**Content only** — two new HTML files in `landing/`, two entries in `pages.json`, `build-shell.mjs`
regeneration of nav blocks. No API, migration, or config change. No new served URL in the
contract (these are landing pages, not API endpoints).

## 5. File-level tasks

1. **`landing/privacy.html`** — full rewrite per §3.1.
2. **`landing/terms.html`** (new) — per §3.2.
3. **`landing/docs/pages.json`** — add the two entries per §3.3.
4. **`node landing/docs/build-shell.mjs`** — regenerate all docs page navs.

## 6. Tests

No automated test (content pages). The e2e `docs` phase (`e2e/run-e2e.sh:181`) runs
`bash scripts/pw.sh docs` which checks that every page in `pages.json` loads. The
`build-shell.mjs --check` mode (`:16`) exits 1 if any page would change — add it to CI if not
already.

## 7. Acceptance criteria

1. `curl -s -o /dev/null -w '%{http_code}' https://pointer.moamen.work/privacy.html` → 200.
2. `curl -s -o /dev/null -w '%{http_code}' https://pointer.moamen.work/terms.html` → 200.
3. `node landing/docs/build-shell.mjs --check` → exit 0.
4. `grep -c 'PDPL' landing/privacy.html` → ≥1.
5. `grep -c 'LEGAL-REVIEW' landing/privacy.html landing/terms.html` → ≥2 each.
6. `grep -c 'pointer_token' landing/privacy.html` → 1 (the storage table).
7. `grep -c 'Riyadh' landing/privacy.html` → ≥2.
8. `grep -c 'Oracle Cloud' landing/privacy.html` → ≥1.
9. `grep -c 'Brevo' landing/privacy.html` → ≥1.
10. The docs sidebar on any docs page (e.g. `/docs/install.html`) shows "Privacy Policy" and
    "Terms of Service" links.

## 8. Rollback

Delete the two HTML files and revert `pages.json`. `git pull` on the VM removes them from the
live landing site immediately.

## 9. Release steps

1. Merge PR.
2. On the VM: `cd ~/pointer-api && git pull --ff-only`. Pages are live immediately (Caddy
   `file_server` reads from disk).
3. Verify criteria 1–2.
4. Schedule legal review (F6: counsel at first paying customer).
5. Revisit the erasure/operator-access wording once DB-11c, DB-12 and DB-13 ship (see Dependencies
   in §2) — this doc's published text must describe current behavior, not the post-DB-13 posture.

## 10. Out of scope

DPA (Data Processing Agreement) — deferred to first paying customer (F6). Cookie banner (no
cookies are set). CCPA-specific section. Age verification. Translated legal pages (Arabic
translation is a follow-up). Automated DSAR handling (see R5-66).
