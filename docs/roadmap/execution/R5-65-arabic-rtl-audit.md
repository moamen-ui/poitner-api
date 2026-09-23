# R5-65 — Arabic / RTL completeness audit (§65 · Release 5 · 1 d)

**Status (2026-09-23):** in progress — static pass under way (GLM), writing
`docs/runbooks/RTL-AUDIT-2026-09-23.md`; the browser/screenshot pass (§3.2 Option A/B) is pending.

**Fix-now 1/3/4 shipped `db0dcb7`**: the shadow root's `dir` now follows the widget's resolved
language (`applyDir()`, `element.ts`) instead of being forced `ltr`; `timeAgo()` (`dom.ts`) now
formats through `Intl.RelativeTimeFormat(getLang())` instead of hardcoded English; four hardcoded
strings (unread/99+, dismiss notification, open full screenshot, element screenshot) now route
through `i18n.ts` with new `en`/`ar` keys. Live in `pointer.js` (gzip 65,590 B, hash `4ab0a0db165d`).
Fix-now 2 (e-mail RTL) and fix-now 5 (Arabic privacy/terms) remain, as does the browser pass.

## 1. Goal

Produce an **audit checklist** (not fixes) that covers every dashboard route, widget state, and
e-mail template in the `ar` (Arabic) locale, identifying mirroring, numeral, date, plural-rule,
truncation, icon, and export issues. Arabic is already the first-class second language (both `en`
and `ar` i18n files exist); this audit finds the gaps before launch.

Effort: 1 d (audit only; fixes are ticketed separately).

## 2. Prerequisites (verified facts)

- **Widget i18n**: `web-component/src/i18n.ts` — flat `STRINGS.en` / `STRINGS.ar` objects with
  dotted keys. `t(key, vars)` at line 115. Arabic strings are present.
- **Dashboard i18n**: `pointer-dashboard/react/public/assets/i18n/{en,ar}.json` — namespaced
  top-level keys. `useTranslation()` hook (react-i18next). **`pointer-dashboard` is a separate repo**
  (per `CLAUDE.md`); this doc's dashboard-side citations are verified against the sibling checkout
  at `/Users/momen/Desktop/REPOS/pointer-dashboard` as of 2026-09-22 and may not exist at that
  relative path in every implementer's environment — re-verify against whatever `pointer-dashboard`
  checkout is available, or hand the dashboard-side checklist rows to the `dashboard-agent`.
- **Language switching mechanism**: dashboard — `pointer-dashboard/react/src/lib/preferences.tsx`
  (`PreferencesProvider`, `:64-132` in the checkout spot-checked 2026-09-22 — the whole component;
  the effect that applies `lang`/`dir` is at `:70-83`) reads `user.language` / a `localStorage` `LANG_KEY` / browser
  language, and on change sets `document.documentElement` `lang`/`dir` (`:75`), calls
  `i18n.changeLanguage`, and persists via `PATCH /api/me/preferences`. **There is no `?lng=` URL
  query-param switch** — the audit must toggle language from the UI (`Shell.tsx:216`,
  `toggleLanguage` in the user menu), not a URL parameter.
- **E-mail templates**: transactional email is sent via Brevo/SMTP
  (`docker-compose.prod.yml:56-59`, `Infrastructure/Email/BrevoEmailSender.cs`,
  `Infrastructure/Email/SmtpEmailSender.cs`) through `EmailService.SendAsync`
  (`Application/Services/Implementation/EmailService.cs:19`). **There is no HTML template file and
  no per-locale content**: every subject/body is a hardcoded English string built inline in C#
  (e.g. `InviteService.cs:222-225` invite subject, `UserService.cs:203`/`:238` approve/reject
  emails, `AuthService.cs:85`/`:183`, `SuggestionService.cs:375`). The audit should expect E1–E4 to
  surface "no Arabic email content exists at all" as the finding, not a mirroring/RTL defect — actual
  email localization is a fix, out of scope for this audit (see §10).
- **Existing RTL support — dashboard**: `preferences.tsx:75` sets `dir="rtl"`/`dir="ltr"` on
  `<html>` when the language toggles; components use Tailwind `rtl:` variants throughout (e.g.
  `DataTable.tsx`, `dialog.tsx`, `dropdown-menu.tsx`).
- **Existing RTL support — widget**: the shadow DOM is **forced LTR always**
  (`web-component/src/styles/_base.scss:5-12`: `:host { direction: ltr; text-align: start; }`) —
  this is deliberate, so a host page's own `dir="rtl"` never flips the toolbar/sidebar/cards. The
  widget does **not** set `dir` on its shadow root and does **not** derive layout direction from its
  own `pointer_widget_language` setting. The one thing that does mirror is the **launcher button's
  corner**: `pageIsRtl()` (`web-component/src/dom.ts:189-198`) reads the **host page's** `dir`
  attribute/computed direction (independent of the widget's language), and `templates.ts:157,162`
  adds an `fbk-rtl` class consumed by `_launcher.scss:31` (`direction: rtl`). So an `ar`-language
  widget embedded in an LTR host page renders Arabic text inside a forced-LTR layout (buttons,
  kebab menu, textarea direction all stay LTR) — this is a likely real audit finding, not a citation
  error to fix; see the added cross-cutting check X8 below.
- **No automated RTL test** exists in the e2e suite.

## 3. Design

This document IS the deliverable — it defines the audit checklist, how to run the audit, where
results go, and the triage rule.

### 3.1 Audit checklist

For **every** item below, the auditor switches the browser/widget to `ar` and visually inspects:

#### Dashboard routes (pointer-dashboard/react)

| # | Route | Check |
|---|---|---|
| D1 | `/login` | Form layout mirrored; password toggle icon position; error messages in Arabic |
| D2 | `/register`, `/register-admin` | Same as D1 |
| D3 | `/forgot-password`, `/reset-password` | Same as D1 |
| D4 | `/` (projects list) | Table columns RTL; status badges; action buttons; empty state |
| D5 | `/projects/:key` (comments list) | Filters, search, pagination; comment cards; status chips |
| D6 | `/projects/:key/settings` | All settings sections; toggles; selects; accordion direction |
| D7 | `/comments/:id` (comment detail) | Element facts `<dl>`; replies; timestamps; custom fields |
| D8 | `/settings` | All accordion sections; AI rules; comment fields; workspace name |
| D9 | `/settings` → MFA card (if R5-61 merged) | QR code centered; code input LTR (digits); labels |
| D10 | `/notifications` | Notification cards; timestamps; read/unread styling |
| D11 | `/cli-login` | Device-code approval page |
| D12 | `/admin/tenants` (super-admin) | Tenant table; action dropdowns |
| D13 | `/admin/plans` (super-admin) | Plans table; edit dialog |
| D14 | `/profile` / `/me` | API key display; change password form; preferences |

#### Widget states

| # | State | Check |
|---|---|---|
| W1 | Floating launcher button | Position (bottom-right becomes bottom-left); tooltip direction |
| W2 | Sidebar open | Comment list; tabs; environment selector |
| W3 | Comment card | Body text; metadata row; kebab menu; custom fields |
| W4 | Reply | Reply body; author name; timestamp |
| W5 | Composer (new comment) | Textarea direction; predefined actions; "Add more fields"; submit button |
| W6 | Edit mode (body, fields) | Inline inputs; Save/Cancel buttons |
| W7 | Login modal | Email/password inputs; error messages |
| W8 | Toast notifications | Position; text direction; dismiss button |
| W9 | Pin markers on page | Tooltip direction (`data-fbk-tip-side`, `data-fbk-tip-align`); cluster menu |
| W10 | Empty state | No-comments illustration/text |

#### E-mail templates

| # | Template | Check |
|---|---|---|
| E1 | Invite e-mail | Subject; body; CTA button; link |
| E2 | Password reset | Subject; body; link |
| E3 | Approval notification | Subject; body |
| E4 | Quick-access link | Subject; body; link |

#### Cross-cutting

| # | Area | Check |
|---|---|---|
| X1 | Numerals | Are Arabic-Indic numerals (٠١٢…) used, or Western Arabic (012…)? Be consistent. |
| X2 | Dates | `Intl.DateTimeFormat` with `locale: 'ar'` — verify Gregorian calendar (not Hijri) unless intended |
| X3 | Plurals | Arabic has 6 plural forms (zero, one, two, few, many, other). Check i18n keys that interpolate counts |
| X4 | Truncation | Long Arabic text in fixed-width containers (badges, table cells, buttons) |
| X5 | Icons | Directional icons (arrows, chevrons, reply) must mirror in RTL |
| X6 | Exports | The only export today is JSON (`GET /api/projects/{key}/export`, `GET
  /api/export` (workspace-wide, despite the "export" name it is not path-prefixed with
  `workspace/`) — `API/Controllers/ExportImportController.cs:38,53` (`[HttpGet]` routes); no PDF/CSV
  export exists as of 2026-09-22). Confirm this: raw JSON has no rendering/RTL concern, so this item should
  record "N/A — no rendered export format exists yet" rather than a mirroring finding; revisit if a
  PDF/CSV export ships later. |
| X7 | Toasts / snackbars | Position and text alignment |
| X8 | Widget forced-LTR shadow DOM | The widget's shadow UI is hardcoded `direction: ltr`
  (`web-component/src/styles/_base.scss:5-12`) regardless of `pointer_widget_language`; only the
  launcher button's corner mirrors, via the **host page's** direction (`dom.ts:189-198`,
  `templates.ts:157,162`, `_launcher.scss:31`), not the widget's own language. Confirm whether
  Arabic text inside a forced-LTR composer/sidebar/kebab-menu (W3, W5, W6) reads as an acceptable
  known limitation or a "fix now" — this is expected to be the audit's most significant finding. |

### 3.2 How to run the audit

**Option A — Chrome DevTools (manual)**:
1. Open the dashboard at `https://app.pointer.moamen.work`.
2. Set language to Arabic from the user menu (`toggleLanguage`, `Shell.tsx:216`) — there is no
   `?lng=` URL parameter.
3. Walk through every route in the checklist.
4. For the widget: embed it on a test page, set `pointer_widget_language` in localStorage to `ar`.

**Option B — Playwright (automated screenshots)**:
```bash
npx playwright test --project=chromium --headed \
  --config=e2e/playwright.config.ts \
  -- e2e/audit/rtl-audit.spec.ts
```
The spec navigates every dashboard route and widget state with `lang=ar`, `dir=rtl`, takes a
screenshot, and writes them to `e2e/audit/rtl-screenshots/`. A human reviews the screenshots.

Create `e2e/audit/rtl-audit.spec.ts` (skeleton):
```typescript
import { test } from '@playwright/test';
const routes = [
  '/login', '/', '/settings', '/notifications', '/profile',
  // … all from the checklist
];
for (const route of routes) {
  test(`RTL screenshot: ${route}`, async ({ page }) => {
    await page.goto(`https://app.pointer.moamen.work${route}`);
    await page.evaluate(() => {
      document.documentElement.lang = 'ar';
      document.documentElement.dir = 'rtl';
    });
    await page.waitForTimeout(1000);
    await page.screenshot({ path: `e2e/audit/rtl-screenshots/${route.replace(/\//g, '_')}.png`, fullPage: true });
  });
}
```

### 3.3 Where results go

Create `docs/roadmap/audits/R5-65-rtl-results.md` with a table:

| # | Item | Status | Issue | Fix priority |
|---|---|---|---|---|
| D1 | /login | ✅ OK / ❌ Issue | description | now / ticket |

### 3.4 Triage rule

- **Fix now** (before launch): anything that makes the UI **unusable** in Arabic — overlapping text,
  completely wrong layout, missing translations (English showing through), broken form submission.
- **Ticket** (post-launch): cosmetic issues — slightly off spacing, non-mirrored decorative icon,
  numeral format preference, Hijri calendar preference.

## 4. Safety / impact

**No code change.** This document and the optional Playwright skeleton are the deliverables.
Any fixes found are separate PRs.

## 5. File-level tasks

1. **`docs/roadmap/execution/R5-65-arabic-rtl-audit.md`** — this document (already being written).
2. **`e2e/audit/rtl-audit.spec.ts`** (new, optional) — Playwright screenshot skeleton.
3. **`docs/roadmap/audits/R5-65-rtl-results.md`** (new, filled by auditor) — results table.

## 6. Tests

No automated test (this is an audit, not a fix). The Playwright skeleton is a tool, not a
pass/fail test.

## 7. Acceptance criteria

1. `docs/roadmap/audits/R5-65-rtl-results.md` exists and contains a row for every item in the
   checklist (≥14 dashboard + 10 widget + 4 email + 8 cross-cutting = 36 rows minimum).
2. Every row has a Status (✅ or ❌) and, if ❌, a Fix priority (now or ticket).
3. All "fix now" items have a corresponding GitHub issue or are listed in a follow-up execution doc.
4. The audit was performed with the browser set to `lang=ar` and `dir=rtl`.

## 8. Rollback

N/A — no code change.

## 9. Release steps

1. Merge the doc.
2. An auditor (the implementer or a teammate) walks through the checklist within 1 d.
3. Results are committed to `docs/roadmap/audits/R5-65-rtl-results.md`.
4. "Fix now" items are either fixed in-line or tracked.

## 10. Out of scope

Actual RTL fixes (separate PRs), Hijri calendar support, Arabic-Indic numeral policy decision,
full accessibility (a11y) audit, additional languages beyond en/ar, widget font-family for
Arabic (uses system fonts already).
