1. HARNESS
- **Local stack**: `docker-compose` with `api` (:8090), `db` (Postgres 15 :5433), `mailpit` (:8025 HTTP, :1025 SMTP for local emails), `fixture-app` (Vite :4173), and the CLI installed locally.
- **Directory layout**: `e2e/scripts/` (reset, seed, mail-asserts), `e2e/widget/` (Playwright specs), `e2e/cli/` (CLI command specs), `e2e/api/` (API integration tests), `e2e/fixture-app/` (test targets).
- **Seed extensions**: Add an `e2e-tenant-isolated` (separate Tenant Owner and project) to prove cross-tenant boundaries. Map: `SUPER_ADMIN` (super-admin), `TENANT_OWNER` (workspace admin), `USERS.deputy` (admin-tier), `USERS.developer` (developer), `USERS.pm` (PM), `USERS.tester` (tester), `CLIENT` (quick-access).
- **Reset strategy**: Wipe DB volume (`docker compose down -v`), `up -d`, poll `/swagger/v1/swagger.json` until 200 OK, run `seed.mjs`.
- **CLI installation**: Built via `npm pack` in `cli/` and installed globally via `npm install -g pointer-feedback-*.tgz` to emulate a clean user environment without dev-dependency bleed. 
- **Dashboard mocking**: Exercise via raw API boundary tests. The separate Angular dashboard repo relies on `/swagger.json` (Orval); asserting the endpoints and DTO shapes here is sufficient.
- **Email assertions**: HTTP GET to Mailpit's `/api/v1/messages` (polling with timeout), asserting on `Subject`, `To`, and extracting tokens/links from the `HTML` body.
- **Evidence**: Appended `report.md` capturing pass/fail tables, scenario timings, and links to Playwright traces/video artifacts.

2. TOKEN RULE
- **Rule**: AI-driven browser tools (`playwright-cli`, `chrome-devtools` MCP) are strictly for **authoring, locator discovery, and debugging failures**. The final deliverable MUST be a committed, zero-AI Playwright spec or Node script.
- **Cost comparison**: 
  - Authoring/Debugging via MCP: ~15,000–30,000 tokens per scenario (vision frames, DOM parsing, iterative loops).
  - Running pure Playwright spec in CI/Nightly: 0 AI tokens.
  - *Conclusion*: MCP is cheaper for one-off developer time during authoring; pure specs are infinitely cheaper for repetitive CI execution.

3. SCENARIO INVENTORY
- **R1-01 (Contract freeze)**: `contract-schema-valid` (Developer, api, PR CI).
- **R1-02 (CLI Init)**: `init-vite-no-ai` (Developer, cli, PR CI), `init-static-no-ai` (Developer, cli, PR CI), `init-next-handoff` (Developer, cli, PR CI), `init-yes-ci` (Developer, cli, PR CI).
- **R1-03 (Dashboard Quickstart Key)**: `quickstart-copies-prefilled-command` (Admin, dashboard/api, PR CI).
- **R1-04 (Doctor and Meta)**: `doctor-green-after-init` (Developer, cli, PR CI), `doctor-detects-tracked-credentials` (Developer, cli, PR CI).
- **R1-05 (Allowed Origins & Ratelimit)**: `origin-enforced-blocks-foreign-origin` (Tester, api, PR CI), `origin-enforced-allows-localhost-local` (Tester, api, PR CI), `comment-burst-429` (Tester, api, nightly).
- **R1-06 (API Key Hardening)**: `legacy-key-still-logs-in-after-upgrade` (Developer, api, PR CI), `regenerated-key-old-one-rejected` (Developer, api, PR CI).
- **R2-00 (Fresh App Whitelabel)**: `tenant-isolation-cross-tenant-read-blocked` (Deputy, api, PR CI - isolation), `whitelabel-delivery-no-branding` (Tester, mail, PR CI).
- **R2-01 (Apply Core CLI)**: `apply-plan-makes-no-edits` (Admin, cli, PR CI), `apply-separate-commits` (Admin, cli, PR CI), `apply-single-commit` (Admin, cli, PR CI), `apply-never-pushes` (Admin, cli, PR CI), `apply-fallback-summary-for-non-admin` (Developer, cli, PR CI).
- **R2-02 (MCP Server)**: `mcp-tools-list-matches-catalogue` (Admin, cli, PR CI), `mcp-get-queue-partitions-untrusted` (Admin, cli, PR CI), `mcp-get-comment-whitelisted-view` (Admin, cli, PR CI), `mcp-commit-and-mark-stages-files-never-pushes` (Admin, cli, PR CI).
- **R2-03 (Served Version Stamp)**: `doctor-flags-stale-skill-copy` (Developer, cli, PR CI).
- **R2-04 (In-app Notifications)**: `notify-applied-shows-badge-to-author` (Client, widget, PR CI), `notify-thumbs-down-reopens-with-note` (Client, widget, PR CI), `notify-read-all-clears-badge` (Client, widget, PR CI).
- **R2-05 (Quick Access Invites)**: `quick-access-magic-link-signs-in` (Client, widget/mail, PR CI), `quick-access-url-param-stripped-on-success` (Client, widget, PR CI), `quick-access-url-param-stripped-on-failure` (Client, widget, PR CI), `quick-access-rotated-link-rejected` (Client, widget, PR CI), `quick-access-password-login-blocked` (Client, api, PR CI), `quick-access-61st-login-with-invite-in-minute-is-429` (Client, api, nightly - isolation).
- **R2-06 (Secrets Flag)**: `flag-secret-in-comment-shows-badge-in-widget` (Tester, widget, PR CI), `flag-absent-from-pointer-get-json` (Developer, cli, PR CI).
- **R3-01 (Vite Plugin Manifest)**: `source-stamp-prod-build` (Developer, cli/widget, PR CI), `stale-hash-warning` (Developer, cli, PR CI), `deploy-awareness-widget` (Client, widget, PR CI), `deploy-awareness-cli` (Developer, cli, PR CI).
- **R3-02 (Design Tokens)**: `init-writes-design-tokens` (Developer, cli, PR CI).
- **R3-03 (Widget Release Eng)**: `widget-pinned-sri-loads` (Tester, widget, PR CI), `widget-pinned-older-build` (Tester, widget, PR CI), `widget-pinned-unknown-404` (Tester, widget, PR CI), `widget-nonce-csp-styles` (Tester, widget, PR CI), `deploy-smoke-local` (Tester, api, PR CI).
- **R3-04 (Snapshot Privacy)**: `snapshot-no-form-values` (Client, widget, PR CI), `snapshot-mask-attribute` (Client, widget, PR CI), `project-no-text-capture` (Admin, widget, PR CI), `legacy-widget-sanitized` (Tester, api, PR CI).
- **R3-05 (Privacy Page)**: `landing-data-page-links` (Anonymous, dashboard/public, PR CI).

4. GAPS
- **Missing Cross-Tenant Seed**: `e2e/scripts/seed.mjs:62` creates `e2e-beta` under the same `TENANT_OWNER` as `e2e-alpha`. To prove strict cross-tenant isolation, the harness must provision a completely separate Tenant/Owner.
- **Developer Apply Queue Fallback**: `R2-01-apply-core-cli.md` (Tasks §1 & Tests) defines the `apply-queue` as admin-only, meaning non-admins get `view=summary` which strips `AiRules` and `PickedActions`. Since `docs/E2E_TEST_PLAN.md` uses `Developer` as the automation account, the AI will never receive `AiRules` or `PickedActions`, undermining the core AI-apply workflow.
- **Notification Emails Missing**: `R2-04-inapp-notifications.md` (Out of scope) explicitly holds email delivery for later, contradicting the founder's hard requirement to assert on "notifications when enabled" through the local mail server.
- **Rate Limit Test Flakiness**: `R2-05-quick-access-invites.md` (Tests) defines the scenario `61st login-with-invite in a minute is 429`. Running this sequentially in CI will block the IP for the next minute, causing subsequent test failures unless the harness injects `X-Forwarded-For`.

5. RISKS
- **Rate Limits (429s)**: Hitting rate limits (signups, logins) mid-suite breaks subsequent API calls from the same IP. *Mitigation*: Dynamically inject unique `X-Forwarded-For` headers per test or move 429 assertion tests to the very end of the suite.
- **Mail Delivery Latency**: SMTP delivery is asynchronous. *Mitigation*: Do not use fixed `sleep()`; use Playwright's auto-retrying `expect.toPass` against Mailpit's `/api/v1/messages` endpoint with a sane timeout (e.g., 10s).
- **Widget Timers / Event Races**: The widget intercepts clicks via `stopPropagation()`, which can race with Playwright runner automation. *Mitigation*: Explicitly decouple assertions; interact with elements normally, then perform a distinct click to pick, relying on element visibility checks rather than blind timeouts.
- **Port Collisions**: Hardcoded `docker-compose` ports (:8090, :5433) will collide in parallel CI runners. *Mitigation*: Map host ports ephemerally (`0:8080`) in CI and dynamically inject the resolved `BASE_URL` into the test context.
