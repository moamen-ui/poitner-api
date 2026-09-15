# Release audit — plan vs. code (2026-09-15)

Three read-only auditors (one per release) checked every deliverable in `execution/R*-*.md` against
the pointer-api **working tree** (incl. the 31 uncommitted files) and the `pointer-dashboard` repo.
Their dashboard/docs claims were then re-verified by grep; corrections are marked **[verified]**.

## Verdict in one line

**Backend, CLI, widget, served skills, e2e and landing/docs are essentially complete for R1–R3
(≈ 270 deliverables, ~5 missing).** The one systematic gap is the **dashboard**: nearly every
"Dashboard tasks" section from R1.5 onward is unbuilt in all three apps — the once-per-phase
`dashboard-agent` run never happened for R2/R3 (and only partially for R1).

| Release | Code (API/CLI/widget/e2e) | Docs pages | Dashboard (3 apps) |
|---|---|---|---|
| R1 — install without an AI | done | 2 pages missing | partial (Angular ahead of React/Vue) |
| R2 — apply from anywhere | done | 1 section missing | **not started** |
| R3 — apply quality in prod | done (1 page refresh) | 1 page refresh | **not started** |

---

## Release 1 — install without an AI

| Item | Status | Evidence / note |
|---|---|---|
| R1.1 NEW-1 contract freeze | ✅ | `docs/ON-DISK-CONTRACT.md`, `Tests/OnDiskContractTests.cs` |
| R1.2 §1 `npx pointer-feedback init` | ✅ | `cli/src/commands/init.ts`, `detect.ts`, `inject/*`, `UsageEvent`, `EventsController`, `/check`; published to npm 0.1.4 |
| R1.3 §2 quick-start command | ✅ | `ProducesResponseType` fixes; dashboard `initCommand` present in all 3 apps **[verified]** |
| R1.4 §3 doctor + §4 `/api/meta` | ✅ code · ⚠ docs | `MetaController`, `cli/src/checks.ts` (11 checks), exit codes 0/1/3/5. **`landing/docs/cli-reference.html` and `self-hosting.html` do NOT exist [verified]** (auditor wrongly said they did) |
| R1.5 §41 allowed origins + rate limit | ✅ code · ❌ dashboard | `Project.EnforceAllowedOrigins`, `OriginNormalizer`, `comments`/`login` rate-limit policies, widget 403 toast. **No `enforceAllowedOrigins` toggle in any dashboard app [verified]** |
| R1.6 NEW-5 API-key hardening | ✅ | `ApiKey` entity, `ApiKeyProtector` (SHA-256 + AES-GCM), backfill service, `Prefix`/`LastUsedAt` in DTO and dashboards |
| R1.7 NEW-4a harness + CI | ✅ | `.github/workflows/e2e.yml` (PR + nightly cron), `run-e2e.sh` tiers/`--only`/`--list`, `lib/report.mjs`, mailpit + verdaccio |
| R1.8 §50 tenant invitation | ✅ code · ⚠ dashboard | 4 endpoints in `TenantsController`, `TenantInviteService`. **Invite UI exists in Angular only; React/Vue tenants pages have none [verified]** |
| R1.9 §51 URLs per enabled environment | ✅ code · ⚠ dashboard | `IsEnabled`/`IsRetired`, two migrations, seeder retires `default`. Projects pages filter by `isEnabled` in all 3 apps; **the enable/disable toggle on the Environments page is missing in all 3 [verified]** |
| R1.10 NEW-4d local client loop | ✅ | `scripts/publish-clients-local.mjs`, `CLIENTS_REGISTRY`, `npm run clients:local`; `dashboard-agent.md` references it |

## Release 2 — apply from anywhere

| Item | Status | Evidence / note |
|---|---|---|
| R2.0 NEW-4b fresh-app e2e + white-label | ✅ | `e2e/fresh-app/fresh.spec.ts` R2-00-01…08, `run.mjs`; R2-00-06 still red (known, sequencing) |
| R2.0b §52 mock-domain rehearsal | ✅ | `e2e/rebrand/mock-domain.spec.ts`, Playwright host resolver; TLS variant manual-only |
| R2.0c NEW-4c Verdaccio | ✅ | `docker-compose.yaml`, `e2e/verdaccio/config.yaml`, nightly-only start |
| R2.1 §7/§8 apply core + `pointer apply` | ✅ | `cli/src/apply/{queue,context,prompt,git,mark,run,projection}.ts`; `--plan/--mark/--fail/--tool`; `no-push.test.ts`; `skill.md` rewritten; `landing/docs/apply.html` |
| R2.2 §24 MCP server | ✅ | `cli/src/mcp/*` 9 tools + resource + prompt; `landing/docs/mcp.html`; `e2e/mcp/` |
| R2.3 NEW-2 served version stamp | ✅ code · ⚠ docs | stamps in 4 served files, `MetaResponse.SkillVersion`, `pointer update [--check]`, doctor staleness. **`pointer update` docs section has no page (cli-reference.html missing)** |
| R2.4 §10 in-app notifications | ✅ code · ❌ dashboard | `Notification` entity + 4 `/api/me/notifications*` endpoints, `POST /comments/{id}/verify`, widget badge/list/👍👎. **Dashboard bell/dropdown: 0 files in all 3 apps [verified]** |
| R2.5 §13 quick-access magic link | ✅ code · ❌ dashboard | `QuickAccessLink`, `login-with-invite`, rotate/revoke, widget token exchange. **`magicLink` copy/rotate UI: 0 files in all 3 apps [verified]** |
| R2.6 S6 secrets flag | ✅ code · ❌ dashboard · ⚠ docs | `PayloadFlagDetector` (10 patterns), `X-Pointer-Client` gate, widget pill, excluded from AI views. **Dashboard: no `X-Pointer-Client` header, no badge/filter in any app [verified]**; "Flagged content" section not on `landing/data.html` |
| R2.7 §48 docs site | ✅ | `landing/docs/` 11 pages + `pages.json` + sidebar shell; e2e docs-site specs |

## Release 3 — apply quality in production

| Item | Status | Evidence / note |
|---|---|---|
| R3.1 Phase 4 Vite plugin + §31 | ✅ (Vite only) | `cli/src/vite/{hash,index,transform,resolve}.ts`, `pointer map`, `init --source-map` (uncommitted `inject/source-map.ts`), doctor `source-map` check, `apply` auto-builds manifest; `Comment.CommitSha/DeployedAt/DeployedSha`, `ProjectBuild` table, `POST /api/projects/{key}/builds`, widget reports `<html data-build-sha>`, `pointer deployed`. Angular builder attempted and reverted → SUPERSEDED. Dashboard: no `commitSha`/deploy surfacing **[verified]** |
| R3.2 §45 design tokens | ✅ | `cli/src/stack/design.ts`, `stack.json` design block, `--no-design`, `doctor --refresh-stack`, "## Design system" in apply prompt |
| R3.3 NEW-3 widget release eng | ✅ | `pointer.version.json` (gzip 31 KB ≤ 60 KB gate in `build.mjs`), `widget/<hash>/` retention, SRI + `adoptedStyleSheets` fallback, Caddy immutable headers, `?v=` middleware, `smoke-widget.sh` |
| R3.4 §33-lite snapshot privacy | ✅ code · ❌ dashboard | `SnapshotSanitizer`, form-value masking, `data-snapshot-mask`, `Project.CaptureTextContent` in entity/DTOs. **Toggle absent from all 3 dashboard apps [verified]** |
| R3.5 NEW-6 data/self-host page | ⚠ | `landing/data.html` + footer links done; **`landing/privacy.html` refresh not done** (form values, mask, soft-delete wording, key hashing) |
| R3.6 §53 landing refresh | ✅ | `landing/index.html` rewritten, `landing/v2/` gone **[verified]**, i18n en/ar, dark mode |

---

## What still remains (ordered)

### A. Dashboard — the real backlog (all three apps, parity)
1. **R2.4 notifications** — bell + unread count, list, mark read/all, 👍/👎 verify on applied comments.
2. **R2.6 secrets flag** — send `X-Pointer-Client: dashboard` from the interceptor/mutator; flag badge on comment rows; "flagged" filter.
3. **R2.5 quick-access** — invite dialog shows/copies magic link, `LinkExpiresAt`/`LinkUses`, rotate + revoke.
4. **R3.4 `CaptureTextContent`** toggle in project settings.
5. **R1.5 `EnforceAllowedOrigins`** toggle in project settings.
6. **R1.9** enable/disable (and retired badge) on the Environments page.
7. **R1.8** tenant-invite UI in React and Vue (Angular has it).
8. **R3.1** surface `commitSha` / deployed state on comments (optional, spec'd as dashboard task).
9. Regenerate clients first (`npm run clients:local`) — several of these DTO fields are new since 1.0.33.

### B. Docs (landing/docs)
10. Create `cli-reference.html` (all commands, doctor checks + exit codes, `pointer update`, `map`, `deployed`, `--source-map`) and `self-hosting.html` (`/api/meta`, `Cli:MinVersion`, `Auth__ApiKeyEncryptionKey`). Add both to `pages.json`.
11. Add "Flagged content" section to `landing/data.html` (R2.6).
12. Refresh `landing/privacy.html` (R3.5 task 2).

### C. Known small items
13. R2-00-06 e2e still red (test sequencing, not product).
14. Widget "unknown environment" surfacing in dashboard (follow-up from the env-resolution change; not in plan).

## Plan drift — doc is now wrong, code is intentional

| Doc says | Code does |
|---|---|
| Status: "CLI is not published to npm" | Published: `pointer-feedback` 0.1.4 (public npm) |
| `environment` attribute injected by init/skill; client `pointerEnv()` map | Attribute only when `--environment` pinned; server resolves from Origin → `ProjectAppUrl` (`ResolveEnvironmentAsync`, `EnvironmentTag.Unknown`) |
| Project create takes a "default app URL" | Create takes URL + `appEnvironmentId` (local by default); no default URL |
| `source-attr` widget attribute | Removed everywhere; only `data-component-source` |
| Phase 4 Angular builder (effort flag) | Attempted via `codePlugins`, could not intercept `.ts`; reverted. Vite-only |
| init injects into Vite/Next/Angular/static | HTML-only injection (index.html / master layout); Next & monorepo routed to the skill |
| Status table "R1–R3 shipped" | True for API/CLI/widget/e2e; **dashboard tasks for R1.5, R1.8 (React/Vue), R1.9, R2.4–R2.6, R3.1, R3.4 are not shipped** |
