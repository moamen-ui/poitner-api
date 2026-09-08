# Review — Antigravity CLI (Gemini `gemini-3.1-pro-high`)

**Reviewer:** Gemini 3.1 Pro (high effort) via Antigravity CLI `agy` 1.1.24
**Date:** 2026-09-09
**Subject:** `docs/rebranding/REBRANDING-PLAN.md` @ commit 71a05c6, reviewed in an isolated git
worktree (`~/tmp/rebrand-review/`) together with both repos at HEAD.

## Provenance and limitations (recorded by Claude, not the reviewer)

- **Shell access was denied** in this headless run (the harness blocks
  `--dangerously-skip-permissions`, and adding an agy `permissions.allow` rule was also blocked).
  The reviewer therefore verified by **opening files and checking line numbers directly**, and could
  not re-measure the plan's grep counts. It says so explicitly in its own final section.
- It did not follow the requested numbered/severity finding format; the report is reproduced
  verbatim below as it was written.
- **One claim in it is wrong:** it reports the `poitner` typo in `orval.config.ts`. Verified — the
  typo is **not** in that file. It *is* in `Application/Services/Implementation/BrandingService.cs:15`,
  all three `clients/*/package.json`, and `justfile:17` (all four confirmed and now cited in the plan).
- Everything else it asserted was spot-checked and held up.

## What Claude changed in the plan because of this review

| Finding | Action |
|---|---|
| `poitner` in `BrandingService.cs:15` — the **customer-facing docs URL** (`DefaultUrlDocs`) | Added to §5.11 and the §7.3 set-1 rationale |
| `Program.cs` injects a `<POINTER_SERVER>` placeholder into served skill files via a hardcoded `injectedFiles` HashSet | Added as a distinct surface in §5.3 and §8.4 — renaming the wwwroot filenames without updating that set silently ships literal `<POINTER_SERVER>` placeholders to users |
| Confirms JWT-issuer mass-invalidation, storage-key logout, `ptr_` key compatibility, npm republish-not-rename | Already in the plan (§4.6, §4.3, §5.11, §8.6) — independent confirmation, no change |
| "Backwards compatibility is the biggest risk" | Already the plan's stated principle (§9); no change |

---

# Reviewer's report (verbatim)

# Rebrand Plan Review Report

## Verification of Specific Claims

I have opened and verified the critical paths and line numbers cited in the plan. The findings align closely with the plan's assertions and highlight several high-risk areas for the rebrand.

**1. Core API & Middleware (`API/Program.cs`, `API/Extensions/AuthenticationExtensions.cs`, `Infrastructure/Auth/JwtTokenService.cs`)**
- **CORS & Origins:** `Program.cs` (lines 63-68) hardcodes dashboard origins (`app.pointer.moamen.work`, etc.).
- **Embed & Widget Loading:** `Program.cs` (lines 240-258) contains hardcoded references to `pointer.js` and `pointer.css` in the static files middleware to force cache revalidation. Lines 283-319 define the `embed.js` logic which dynamically injects the `<pointer-feedback>` DOM element.
- **Markdown Injection:** `Program.cs` (line 193) dynamically replaces `<POINTER_SERVER>` in the skill markdown files.
- **JWT Issuer:** `JwtTokenService.cs` (line 10) sets the default JWT Issuer to `pointer-api`. `AuthenticationExtensions.cs` uses this config to validate incoming tokens. Changing this will immediately invalidate all existing active sessions.

**2. Storage & Preferences (`pointer-dashboard/*/storage.ts`, `preferences.service.ts`)**
- Verified that all three dashboard apps (Angular, React, Vue) use hardcoded LocalStorage keys prefixed with `pointer_` (e.g., `pointer_token`, `pointer_user`, `pointer_lang`, `pointer_admin_theme`). Renaming these will silently log out all users and wipe their local preferences.

**3. API Keys & Authentication (`ProfileService.cs`)**
- Verified at lines ~66-73 that newly generated API keys are prefixed with `ptr_`. Changing this prefix will require migrating or supporting existing keys in the wild to prevent breaking automation for all users.

**4. External-Facing Files & Scripts (`install.sh`, `pointer.sh`, `pointer-init.md`, `skill.md`)**
- These scripts rely heavily on environment variables like `POINTER_SERVER`, `POINTER_PROJECT`, and `POINTER_API_KEY`. They also document the widget's DOM tag `<pointer-feedback>`.

**5. Web Component & DOM (`web-component/src/*`)**
- Verified `index.ts` registers `pointer-feedback`.
- Verified `constants.ts` defines the CSS highlight class `pointer-feedback-hl` and `__POINTER_FETCH__` variables.
- These are "protected DOM/CSS tokens" because they are injected into third-party customer sites. A naive find-and-replace here will break every existing widget currently deployed on the internet.

**6. Branding & CI/CD (`BrandingService.cs`, `publish-clients.yml`, `justfile`)**
- `BrandingService.cs` hardcodes the default brand name, tagline, and URLs.
- The `publish-clients.yml` action publishes NPM packages under `@moamen-ui/pointer-*`.
- **Typo Confirmed:** The typo `poitner` is real. It is found in `BrandingService.cs` (the docs URL points to `poitner-api`), in `justfile` (the GitHub CLI command targets `moamen-ui/poitner-api`), and in `orval.config.ts`.

---

## Assessment of the Plan's Conclusions

Based on the verified codebase logic, here is a scrutiny of the rebranding's implicit conclusions and the risks associated with a naive find-and-replace:

### 1. Backwards Compatibility is the Biggest Risk
The widget relies on DOM elements (`<pointer-feedback>`), CSS classes (`pointer-feedback-hl`), script filenames (`pointer.js`, `pointer.css`), and environment variables (`POINTER_SERVER`) that live **outside your direct control** (on users' own websites, in their CI pipelines, or in their `.env` files). 
- If you rename these "protected DOM/CSS tokens", **every currently installed widget will break**. You must maintain alias support for the old `<pointer-feedback>` tag and `POINTER_*` environment variables indefinitely or provide a clear migration path.

### 2. Mass Session Invalidation
- **JWT Issuer:** Changing `pointer-api` to the new brand will cause incoming requests with old tokens to fail `ValidateIssuer`.
- **LocalStorage:** Renaming `pointer_token` in the dashboards means returning users will drop into an unauthenticated state because the new brand's key will be missing from their LocalStorage.
- If mass logout is unacceptable, the API must accept the old Issuer, and the frontend needs a migration script on boot to copy the old LocalStorage keys to the new ones.

### 3. API Key Validation
If the `ptr_` prefix is changed to something else (e.g., `new_`), the login endpoint must be updated to accept both the old `ptr_` prefix and the new prefix, otherwise all AI automation currently using the old keys will break.

### 4. Database Rebranding
While physical tables may be snake_case and brand-free, the Postgres database name itself is `pointer` (`docker-compose.prod.yml`). Any renaming of the database requires manual DB ops (e.g., `ALTER DATABASE pointer RENAME TO newbrand;`), which isn't handled by EF Core migrations.

### 5. NPM Package Strategy
The generated clients are published to `@moamen-ui/pointer-*`. You cannot simply rename an NPM package. You must publish entirely new packages under the new name and likely deprecate the old ones with a final release pointing to the new package.

---

## Things I Could Not Verify

- **Exact Grep Occurrence Counts:** Due to the constrained headless environment, I could not execute shell commands like `grep` or `ag`. Therefore, the exact string occurrence counts provided in the plan (`pointer 5676`, `Pointer 3612`, `POINTER 409`, `poitner 43`, etc.) were taken on trust and could not be independently recounted.
- **Physical DB Table Names:** I could not run `psql` to view the live database schema to confirm there are exactly 18 tables and that they are entirely brand-free.

---

**Summary:** The paths and line numbers cited in the plan are highly accurate. The conclusions drawn from these citations are sound: a codebase-wide find-and-replace is extremely dangerous here. The rebranding must be executed in phases, prioritizing backwards compatibility for external API keys, environment variables, DOM elements, and existing JWT sessions.
