# Claude — position for the E2E test-suite meeting

## Harness facts I verified
- `e2e/scripts/seed.mjs` already creates: super admin, tenant owner (`e2e-owner@example.com`), Deputy, Developer, PM, Tester, a QuickAccess Client (invited to `e2e-alpha`), projects `e2e-alpha`/`e2e-beta`, comments C1–C8. So **all seven roles exist**; what is missing is a *second tenant* for cross-tenant isolation, and per-scenario throwaway projects.
- Email: `Infrastructure/Email/SmtpEmailSender.cs` is selected by `Email:Provider=smtp` (`DependencyInjection.cs:43-48`), host/port from `Email:Smtp:Host|Port`, defaults `localhost:1025`. **Mailpit** is my pick (MailHog is archived; smtp4dev is .NET-heavy): SMTP `1025`, UI/API `8025`, `GET /api/v1/search?query=to:<addr>`, `GET /api/v1/message/<id>` (HTML/Text/Headers), `DELETE /api/v1/messages`. In compose the API's host is the service name `mailpit`.
- `e2e/scripts/lib/api.mjs` has `get/post/patch/login/ApiError`; `widget.spec.ts` pre-authenticates the widget through `localStorage` (`pointer_token`/`pointer_user`) and reveals it via `sessionStorage pointer_visible` — the cheap pattern to reuse everywhere.
- `playwright-cli` is installed (`/usr/local/bin/playwright-cli`); `chrome-devtools` MCP is available in the agent session; neither has any place in CI.

## Proposals
1. **Layers**: `api` (node scripts, no browser) · `cli` (spawn the packed CLI in a temp git repo) · `widget` (Playwright, fixture apps) · `dashboard` (Playwright against a local build of `../pointer-dashboard` when `DASHBOARD_DIR` is set — otherwise the dashboard layer is **skipped and reported**, never silently green) · `mail` (Mailpit API).
2. **CLI under test** = the exact publish artifact: `cd cli && npm pack` → `e2e/state/pointer-feedback-<v>.tgz` → scenarios run `npx -y <tgz> …` in a temp dir with `git init`. No `npm link` (hides packaging bugs).
3. **Second tenant** (`e2e-other-owner@example.com`, project `e2e-gamma`) for every isolation assertion (notifications, events, api keys, builds, origins).
4. **Token rule**: specs are the only thing CI runs — 0 tokens. During authoring, `playwright-cli` (`codegen`/snapshot) to discover selectors ≈ 2–5 k tokens per scenario; `chrome-devtools` MCP only to debug a failing spec's console/network ≈ 5–15 k. Never drive a scenario interactively twice — the second time it must be a spec.
5. **Evidence**: `e2e/state/report.md` written by the zero-AI phases too (today only `audit.mjs` writes it) + JUnit XML + traces on failure; each scenario id from the docs appears in the report with PASS/FAIL/SKIP.
6. **Time budget**: PR suite ≤ 12 min (api + widget + cli smoke); nightly adds fresh-app inits, mail, dashboard; manual-only = anything needing a real AI tool or a real deploy.
