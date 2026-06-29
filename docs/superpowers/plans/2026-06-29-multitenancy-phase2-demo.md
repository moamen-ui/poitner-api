# Multi-Tenancy Phase 2 (Demo Tenants) Implementation Plan

> **✅ STATUS: SHIPPED TO PRODUCTION (2026-06-29).** All tasks (D1–D7) complete and deployed live; one-click demo runs at `demo.pointer.moamen.work`. The `- [ ]` checkboxes below were NOT ticked during execution — progress was tracked in `.superpowers/sdd/progress.md` (the authoritative ledger). Treat this plan as DONE; do not re-run it. Prod demo round-trip verified 6/6 + a live browser pass. Next work = Phase 3 (billing/plans/quotas, email verification) — roadmap, not started.

> REQUIRED SUB-SKILL: superpowers:subagent-driven-development. [GLM] = opencode+GLM-5.2 (`--pure`, tracked); [Claude] = own subagent + close review. Builds on the completed Phase 1.

**Goal:** One-click "Try the demo" → ephemeral, self-seeded, auto-expiring scoped-admin tenant (10-comment cap, 24h hard-delete), with abuse caps and the `demo.pointer.moamen.work` dashboard.

**Architecture:** Demo tenants ARE Phase-1 scoped admins (`IsDemo`+`ExpiresAt`). Provisioning seeds the tenant's own sample data; Phase-1 isolation needs no shared-seed case. A background job hard-deletes expired demos via the proven `TenantService.HardDeleteAsync`.

## Global Constraints
- Branch `feat/multitenancy-phase1` (continue — same feature line; one deploy at the end) in both repos.
- Verify each backend task: `dotnet build` + container rebuild + curl. Local end-to-end demo test before deploy. NO deploy until the user's final go.
- Demo tenants must remain fully subject to Phase-1 isolation (no special bypass that leaks across tenants). Anonymous/system paths use `.IgnoreQueryFilters()` + explicit scope only.
- Defaults: `DemoMaxActive=100`, `DemoCommentCap=10`, `DemoTtlHours=24`, demo-create rate limit 3/hour/IP.

---

## Task D1 [Claude]: Demo columns + migration
**Files:** `Domain/Entity/User.cs` (+`bool IsDemo`, `DateTime? ExpiresAt`); `Infrastructure/Mappings/UserMapping.cs` (map `is_demo`, `expires_at`); migration `AddDemoColumns`.
- [ ] Add the two properties; map snake_case columns; `dotnet ef migrations add AddDemoColumns -p Infrastructure -s API`; `dotnet build`; rebuild container; `curl /api/statuses` (4 statuses) confirms migration applied. Commit `feat(api): demo columns (is_demo, expires_at)`.

---

## Task D2 [Claude]: Demo provisioning service + endpoint
**Files:** `Application/Services/{Interfaces/IDemoService.cs, Implementation/DemoService.cs}`; `Application/DTOs/Demo/DemoSessionResponse.cs`; `API/Controllers/DemoController.cs`; `Program.cs` (rate-limit policy "demo").
**Produces:** `IDemoService.ProvisionAsync() : Task<Result<DemoSessionResponse>>`; `DemoSessionResponse { token, email, password, projectKey, expiresAt, serverUrl }`; `POST /api/demo` `[AllowAnonymous][EnableRateLimiting("demo")] [Tags("Demo")]`.
- [ ] **DemoService.ProvisionAsync** (inject `IUnitOfWork`, `IPasswordHasher`, `ITokenService`, `IConfiguration`):
  1. Active-cap: `count(users where IsDemo && ExpiresAt > now && DeletedAt==null)` via `.IgnoreQueryFilters()`; if `>= DemoMaxActive` → `Result.Failure("Demo is at capacity, please try again shortly.")` (controller maps to 429/409).
  2. Create demo user: role "Workspace Admin" (look up by name, `.IgnoreQueryFilters()`), random `email = demo-{8hex}@demo.pointer`, random strong password (return the plaintext in the response only), `DisplayName="Demo User"`, `PublicId=Guid.NewGuid()`, `OwnerId = <that PublicId>`, `ApprovalStatus=Approved`, `IsActive=true`, `IsDemo=true`, `ExpiresAt=now+DemoTtlHours`. Hash password.
  3. Seed the tenant's own data (all stamped `OwnerId = the demo user's PublicId`): one `Project { Key=$"demo-{8hex}", Name="Demo Project", IsActive=true }`; ~3 sample `Comment`s (varied `Status`/`Environment`, `AuthorId = demo user's PublicId`, simple `Element` snapshot, non-private) — keep total seeded < DemoCommentCap so the guest can still add some.
  4. SaveChanges; issue token via `_tokenService.Issue(demoUser)` (ensure the user's `Role` is loaded so claims include is_admin + tenant); return `DemoSessionResponse` with `serverUrl` from config (`Pointer:PublicUrl` or the request host).
- [ ] **DemoController.Create**: `POST /api/demo` → ProvisionAsync; map capacity failure → `StatusCode(429, result)`, success → `Ok`.
- [ ] **Program.cs**: add a fixed-window rate-limit policy `"demo"` (3 / 1 hour, partition by client IP); `[EnableRateLimiting("demo")]` on the action.
- [ ] `dotnet build`; rebuild; live: `POST /api/demo` → 200 with token+creds+projectKey; log in with the returned creds → dashboard token works; the demo project + sample comments are visible to that demo user only. Commit `feat(api): demo provisioning endpoint (seeded ephemeral tenant)`.

---

## Task D3 [Claude]: 10-comment cap for demo tenants
**Files:** `Application/Services/Implementation/CommentService.cs`.
- [ ] In `CreateAsync`, after resolving `projectOwnerId`, if that owner is a demo tenant (`users where PublicId==projectOwnerId && IsDemo`, `.IgnoreQueryFilters()`) AND `count(comments where OwnerId==projectOwnerId && DeletedAt==null) >= DemoCommentCap (10)` → return `Result<CommentResponse>.Failure("Demo limit reached: a demo workspace allows at most 10 comments.")`. Non-demo tenants unaffected.
- [ ] `dotnet build`; rebuild; live: provision a demo, add comments until the 11th is rejected (seeded count counts toward the 10). Commit `feat(api): enforce 10-comment cap on demo tenants`.

---

## Task D4 [Claude]: 24h cleanup background service
**Files:** `API/Hosted/DemoCleanupService.cs` (or `Infrastructure/Hosted/`); `Program.cs` (`AddHostedService`).
- [ ] `DemoCleanupService : BackgroundService` — `PeriodicTimer(TimeSpan.FromHours(1))` loop (run once shortly after startup too). Each tick: create a DI scope; query `.IgnoreQueryFilters()` demo users with `ExpiresAt < now && DeletedAt==null`; for each, `await tenantService.HardDeleteAsync(user.PublicId)` inside try/catch (log + continue on error); log total swept. Respect the `stoppingToken`.
- [ ] Register `builder.Services.AddHostedService<DemoCleanupService>();`.
- [ ] `dotnet build`; rebuild; live: provision a demo, manually set its `ExpiresAt` to the past (SQL via `docker compose exec db psql`), wait for/trigger a sweep (or temporarily shorten the interval to verify), confirm the tenant + its project/comments + upload files are gone and a second (non-expired) tenant is untouched. Commit `feat(api): hourly demo cleanup (hard-delete expired demo tenants)`.

---

## Task D5 [GLM]: Regenerate clients (Demo tag)
- [ ] Add `Demo` to `orval.config.ts` tags; `npm run generate-clients`; verify the demo hook (`usePostApiDemo` / `postApiDemo`) + `DemoSessionResponse` model in all 3 clients; commit `chore(clients): add Demo tag`. (Publish at deploy.)

---

## Tasks D6-NG / D6-REACT / D6-VUE [subagents, parallel; GLM reviews]: "Try the demo" UI (×3)
**Shared contract:** local-link the client source for build-verify (revert before commit, per Phase-1 Task 14 method). On the **login page**, a "Try the demo" button → `POST /api/demo` (the generated demo hook) → store the returned token + user the same way normal login does → navigate into the dashboard → show a **Demo panel** with: the project key, a copy-paste `<pointer-feedback project="{projectKey}" server="{serverUrl}">…</pointer-feedback>` snippet (copy button), the widget login (email + password), and a countdown to `expiresAt`. en + ar i18n. No commit by the subagent — controller reverts link + commits.
- [ ] **D6-NG / D6-REACT / D6-VUE**: implement per framework; `npm run build` (with local-link); report link-config files to revert. Controller reverts links + commits `feat(<fw>): try-the-demo one-click + demo panel`.

---

## Task D7 [Claude]: Local full-feature test + final GLM review
- [ ] Re-run the Phase-1 isolation matrix (13/13) + by-id (5/5) + global-role (6/6) to confirm Phase 2 didn't regress isolation. Run the demo end-to-end: provision → widget login → 10-comment cap → expiry+cleanup. Final GLM `--pure` adversarial review of the whole branch (Phase 1 + Phase 2). Fix any Critical/Important. Document results.

---

## Deploy (single, user-approved): merge both repos to `main`; deploy API (auto-migrate adds is_demo/expires_at + Phase-1 columns); publish clients; build+deploy 3 dashboards (incl. signup `.enabled` read fix); add `demo.pointer.moamen.work` to the VM `Caddyfile` pointing at the dashboard; live verify (isolation recheck + a real "Try the demo" round-trip on prod).

## Out of scope (Phase 3): billing/plans, email verification, demo→paid conversion.
