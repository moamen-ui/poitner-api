# R5-00 — Release 5 execution docs index

Ten execution docs for the "ops pack" + "writing pack" items from
[`docs/roadmap/meetings/2026-09-22-foundations/07-final-report.md`](../../meetings/2026-09-22-foundations/07-final-report.md)
§2 (the ranked list, rows 5–15, minus row 11 "Activation funnel" which has no execution doc yet —
it would be `R5-64`, not part of this batch). Each doc was checked against this tree on 2026-09-22/23:
every `file:line` citation and quoted snippet/command verified against the actual file, factual
errors fixed, missing sections filled in, and cross-repo consequences (on-disk contract, orval
tags, compose/env keys) confirmed or added as tasks. See each doc's Prerequisites/Dependencies
section for the verified facts; this index only summarizes.

## The ten docs

| # | Title | Effort | Schema change? | Depends on | Dashboard change? | Status (2026-09-23) |
|---|---|---|---|---|---|---|
| [R5-58](R5-58-observability-baseline.md) | Observability baseline (JSON logs, request id, `/health`, Sentry, uptime ping) | 2–3 d | No | None — independent of DB-11/12/13 | No | **Shipped** `b3160c2`; live (`SENTRY_DSN`/`UPTIME_PING_URL` empty until owner supplies them) |
| [R5-59](R5-59-security-headers-and-login-limit.md) | Security headers + login rate limit + `security.txt` | 1 d | No | None — independent of DB-11/12/13 | No (Caddy config only) | **Shipped** `5a37117`+`c55dc46`; **§12 amendment shipped** `6ca148b` (per-e-mail lockout); **hardening shipped** `bc5e9b4` (GLM review M1/F5/F6/F7); verified live; F3/F4 open |
| [R5-60](R5-60-production-restore-drill.md) | Production-side restore drill | 0.5 d | No | None | No | **Shipped** `58f0fe7`; **drilled on production 2026-09-23** (4 s, counts matched, scratch dropped) |
| [R5-61](R5-61-operator-mfa.md) | Operator MFA (TOTP on the super-admin account) | 1–2 d | **Yes** — `users.totp_secret`/`totp_enabled_at` (nullable), new `user_recovery_codes` table | None hard — designed independent of DB-11c/DB-12 (soft follow-up: audit MFA enroll/disable once DB-12 ships) | Yes — MFA settings card + login-flow MFA step | Implemented on branch `feat/r5-61-operator-mfa` (rebased); reviews in progress — not merged |
| [R5-62](R5-62-jwt-key-rotation.md) | JWT `kid` + two-key rotation window | 1 d | No | Coordinate with DB-11a/b (`ITokenService.Issue` signature changes) — not blocking; independent of DB-12/13 | No | **Shipped** `ea436b0`+`4d48bbe`; deployed 2026-09-23 04:33 UTC; production logs `[JWT] active kid=k0; configured kids=[k0]`; `JWT_SIGNING_KEY` frozen |
| [R5-63](R5-63-privacy-and-terms.md) | Privacy policy + Terms of Service | 3–5 d (writing) | No | Text describes the DB-11c/DB-12/DB-13 posture (operator access, erasure) — must be revised once those ship; not blocked from publishing now | No (landing pages, not the dashboard app) | **Shipped** `3afe171`+`ab0cc98`; live; `[LEGAL-REVIEW]` placeholders remain |
| [R5-65](R5-65-arabic-rtl-audit.md) | Arabic/RTL completeness audit (checklist only, no fixes) | 1 d | No | None (one audit row, D9, covers the R5-61 MFA card *if* merged first — optional, not required) | Audit only — dashboard is the subject, not modified | **In progress** — static pass (GLM, `docs/runbooks/RTL-AUDIT-2026-09-23.md`); fix-now 1/3/4 shipped `db0dcb7` (widget `dir` follows language, localised `timeAgo`, four strings to i18n); fix-now 2 (e-mail RTL) and 5 (Arabic privacy/terms) and the browser pass still pending |
| [R5-66](R5-66-export-and-dsar-runbook.md) | Export verification + DSAR runbook | 1 d | No | DB-11c (identity-erase outcome), DB-12 (audit logging) — both soft/non-blocking; usable against current schema today | No | **Shipped**; `docs/runbooks/DSAR.md` + `EXPORT-VERIFICATION-2026-09-23.md` live; follow-ups filed as DB-16 |
| [R5-67](R5-67-tenant-isolation-ci-probe.md) | Tenant-isolation CI probe (e2e) | 1 d | No | None — independent of DB-11a (re-run recommended after DB-11a merges) | No | **Shipped** `86656df`; green in CI (23 tests) since run 35789122040 |
| [R5-68](R5-68-versioning-policy-and-v1-alias.md) | Versioning policy + `/api/v1/*` alias | 1 d | No | None | No (explicitly none — policy doc + a routing alias) | **Shipped** `435f688`; live, parity verified in production |
| [DB-16](../../db/execution/DB-16-screenshot-purge.md) *(added 2026-09-23)* | Screenshot purge: files of comments soft-deleted > 30 d, orphan sweep (> 48 h, never `uploads/branding/`), fix for the signed-URL delete bug B1, uploads-volume log line, privacy retention row | 1–2 d | **Yes** — `comments.screenshot_purged_at` (nullable; additive) | None (DB-16 and DB-17 independent; DB-16 first) | No (privacy page row only) | **Written by the db-architect, not implemented.** Ordinary deploy |
| [DB-17](../../db/execution/DB-17-demo-as-product.md) *(added 2026-09-23 — R5.7, F4)* | Demo as a product: TTL/extension/conversion on `workspaces`, convert-in-place with optional workspace name, one-time self-service extension (`POST /api/demo/extend`), 15-min expiry sweep + T-2 h reminder, login refused after expiry, hashed demo throttle key (PII fix) | 2–3 d | **Yes** — six nullable `workspaces.demo_*` columns + partial index + check constraint, plus one R3 backfill (`[ContractMigration("DB-17")]` → `pre-db17`); `users` demo columns dual-written until DB-11e | DB-11a, DB-12, DB-14, DB-15 (all live) | Yes — DemoPanel countdown from `/me`, Extend button, workspace-name field, PDPL notice on the demo form, TenantsPage routes re-keyed to `workspaceId` | **Written by the db-architect, not implemented.** Contract deploy; R7 marker line to fill |

All ten depend on nothing outside this list except the soft/non-blocking notes above. DB-11a, DB-11b,
DB-11c, DB-11d, DB-12 (parts 1 + 2), DB-13, DB-14 (pre-db14), DB-15, and R5-62 are deployed to production
(2026-09-23); R5-61 (operator MFA) is implemented on branch with reviews in progress.

## Recommended implementation order

Per the final report's Sequence (§3, step 5–6) and this review's dependency findings:

**Wave 1 — no dependencies, any order/parallel:**
1. R5-60 (restore drill) — cheapest, purely additive script + doc.
2. R5-58 (observability) — foundational (request id useful for later debugging of the rest).
3. R5-59 (security headers + login rate limit).
4. R5-62 (JWT `kid` rotation).
5. R5-68 (versioning policy + `/api/v1` alias).
6. R5-67 (tenant-isolation CI probe).

**Wave 2 — designed independent, but touch identity/audit-adjacent surfaces (kept after Wave 1 as a conservative ordering; this review found neither is actually blocked):**
7. R5-61 (operator MFA) — self-contained by design; revisit to wire `IAuditWriter` calls once DB-12 ships (non-blocking follow-up).
8. R5-66 (export + DSAR runbook) — usable today; the "erase my identity" outcome documented in it will gain an automated endpoint once DB-11c ships, and its manual logging table can be replaced once DB-12 ships.

**Wave 4 — Release 5 tail (added 2026-09-23; docs written, not implemented):**
11. DB-16 (screenshot purge) — ordinary deploy, first; its orphan sweep is the safety net for DB-17's expiry deletes.
12. DB-17 (demo as a product, F4) — contract deploy `pre-db17`; do not batch with DB-16 (both jobs run minutes after boot; keep the deploys attributable). Then DB-11e contracts the `users` demo columns one release later.

**Wave 3 — writing pack, parallel with each other and with Waves 1–2:**
9. R5-63 (privacy + ToS) — flag the erasure/operator-access wording as provisional; revise once DB-11c/DB-12/DB-13 land (already called out in the doc's Release steps).
10. R5-65 (Arabic/RTL audit) — one checklist row (D9) references the R5-61 MFA card; run that row after R5-61 merges, or mark it "not applicable yet" if run first.

## Notes on this review

- Every `file:line` citation in R5-58, R5-59, R5-60, R5-62, R5-63, R5-65, and R5-67 was spot-checked
  extensively and found accurate on first read; only small fixes were needed (R5-58: a missing NuGet
  package for `AddDbContextCheck<T>`; R5-59: a stale test docstring; R5-68: a wrong array line-range
  in `Tests/OnDiskContractTests.cs`).
- R5-61 and R5-66 needed the most correction — see their own Prerequisites/Dependencies sections for
  the fixed facts (wrong local port, an unenforced entitlement described as enforced, a DB-11c
  "hard-delete" claim that doesn't match what DB-11c actually does, a screenshot-deletion claim that
  doesn't match `CommentService.DeleteAsync`, and Guid-vs-int column confusion in SQL snippets).
- No doc in this batch introduces a new served URL or storage key except R5-68's `/api/v1/*` alias,
  which the doc itself already tasks into `docs/ON-DISK-CONTRACT.md` and `Tests/OnDiskContractTests.cs`.
- R5-61 is the only doc introducing a new API surface (`MfaController`); it now explicitly calls out
  the `[Tags("Me")]` requirement so orval doesn't silently drop it, and the `[ProducesResponseType]`
  convention for its new endpoints.

## Follow-ups surfaced during implementation (2026-09-23)

- **Screenshots of soft-deleted comments are never purged** → **now specified as [DB-16](../../db/execution/DB-16-screenshot-purge.md)** (written 2026-09-23; default N = 30 d, plus an orphan sweep and the fix for the signed-URL delete bug). Original note: `CommentService.DeleteAsync` only sets
  `DeletedAt`; the file stays until the workspace is hard-deleted (`docs/runbooks/DSAR.md`, "Follow-up
  code items" #1). Decision needed: either DB-08's retention job purges `comments.deleted_at < now-N`
  rows and their screenshot files (proposed default N = 30 d, additive to the retention table), or the
  operator accepts indefinite retention of hidden screenshots. The privacy page now describes the
  scheduled-clean-up behaviour; implement it as **DB-16** before demo-public.
- Export DTO drops custom fields, page-context snapshots and predefined-action links
  (`Application/DTOs/Export/CommentExportDto.cs`) — portability gap, track with DB-16 or a small R5 item.
- **Widget Updates button hidden (open item):** `61004ac` intentionally hid the Updates button in the widget, leaving notifications unreachable in the widget UI (R2-04 notification e2e specs set to `fixme`). Founder decision pending on whether / when to un-hide or redesign the notification surface.

## Shipped 2026-09-23

| Item | Commit | Live / verified |
|---|---|---|
| R5-67 tenant-isolation CI probe | `86656df` | Green in CI (23 tests) since run 35789122040 |
| R5-60 restore drill | `58f0fe7` | **Drilled on production 2026-09-23** — 4 s, users/comments/projects/replies/migrations matched live, scratch dropped |
| R5-59 headers + login limit | `5a37117`, `c55dc46`, **§12 amendment** `6ca148b`, **hardening** `bc5e9b4` | Verified live: 10×400 → 429 with `Retry-After: 900`; other e-mail unaffected. `bc5e9b4` (GLM review M1/F5/F6/F7): 254-char e-mail cap, SHA-256 cache key, 64 KB login body limit, validated `X-Request-Id`, pseudonymous mail-log recipients. CSP report-only (enforcement and F3/F4 are follow-ups) |
| R5-63 privacy + terms | `3afe171`, `ab0cc98` | Live at pointer.moamen.work/privacy.html and /terms.html; `[LEGAL-REVIEW]` placeholders remain |
| R5-68 versioning + `/api/v1` alias | `435f688` | Live, parity verified in production; `docs/VERSIONING.md` exists |
| R5-66 export verification + DSAR runbook | (doc + runbooks) | `docs/runbooks/DSAR.md`, `docs/runbooks/EXPORT-VERIFICATION-2026-09-23.md` live; follow-ups filed as DB-16 |
| R5-58 observability | `b3160c2` | Live — `/health` Healthy, `X-Request-Id` echoed, JSON logs; `SENTRY_DSN`/`UPTIME_PING_URL` empty in prod pending owner |
| R5-65 RTL audit | (in progress); fix-now 1/3/4 `db0dcb7` | Static pass by GLM (`docs/runbooks/RTL-AUDIT-2026-09-23.md`); fix-now 1/3/4 (widget RTL shadow root, localised `timeAgo`, four strings to i18n) live in `pointer.js`; fix-now 2/5 and the browser pass pending |
| DB-11a identity + memberships | `8140695` (code), deployed `pre-db11a` | **Deployed to production 2026-09-23 00:08 UTC**; migrations 65→68; 0 duplicate e-mails, 5 live memberships; client `@moamen-ui/pointer-react` 1.0.41 published; dashboard TenantsPage update in progress |
| R5.1b (DB-11b login workspace picker + switch) | (API + widget) | **Deployed to production 2026-09-23** (API + widget; dashboard pending deploy). Follow-up fix: `choose-workspace` 200 envelope; widget RTL follow-ups in `650dc50` |
| R5.2 (DB-12 audit log) | Part 1 `30d1b46`, Part 2 `fda2717` | **Complete** — Part 1 deployed (`pre-db12`, 70 migrations); Part 2 (attributes on 103 actions — 71 `[Audited]`, 32 `[NoAudit]` — and writer calls in 20 services, `Audit:StrictCoverage=true` in Development) deployed 2026-09-23 03:42 UTC. First production row verified (`auth.login.failed`, hashed e-mail, request id, ip hash). Dashboard Security log page (`/security-log`, workspace + super-admin /all views) shipped in pointer-dashboard `60bbd0d` and deployed; client `@moamen-ui/pointer-react` 1.0.42 |
| DB-11c deletion semantics | `af4f98b` + fixes `21cb7ee` | **Deployed to production 2026-09-23 ~04:55 UTC** (71 migrations, newest `20260923040830_AddUsersErasedAt`). Remove/disable/leave/erase, S-13 sole-admin guard for every actor incl. super admin, `users.erased_at`, scoped one-time erase tokens, invite scrub, audit rows. Reviews: Gemini Pro no findings, Opus 3 HIGH + 5 MEDIUM applied. Dashboard half pending (client `@moamen-ui/pointer-react` 1.0.43 publishing now) |
| DB-11d change e-mail | `f9d21de` + fixes `c0fbad8` | **Deployed to production 2026-09-23 ~05:50 UTC** (no migration). Endpoints `POST /api/me/change-email` (password-confirmed; verification mail to new address, notice to old) and anonymous `POST /api/auth/confirm-email-change` (scoped token; sets normalised address, rotates identity stamp, clears `recipient_email`; D14 conflict-never-merge, 23505 catch scoped to `ux_users_email_live`). Reviews: Gemini Pro MERGE (1 nit), Opus MERGE WITH FIXES (2 medium + 8) applied. Dashboard change-e-mail UI being built (client 1.0.44 publishing) |
| R5.3 (DB-13 operator impersonation) | `441a462` + `5f1fbc8` + fixes `790b9b8` | **Deployed to production 2026-09-23 ~06:15 UTC** (72 migrations, newest `20260923045038_AddImpersonationSessions`). **Founder decision F2 is live**: super admin sees metadata only since 2026-09-23; impersonation UI pending in the dashboard (until shipped founder's dashboard shows no comment content as super admin). Content only under an audited, time-boxed (≤ 60 min), read-only impersonation session (`POST /api/admin/tenants/{workspaceId}/impersonate`, `POST /api/admin/impersonation/end`, `GET /api/admin/impersonation`); workspace admins e-mailed on start and see sessions in Security log without operator identity; private comments never visible; signed screenshot URLs clamped to the session. Reviews: Gemini Pro MERGE; Opus MERGE WITH FIXES (1 HIGH + 10) applied. Dashboard impersonation UI being built (client 1.0.44 publishing) |
| R5-62 JWT key rotation | `ea436b0` + fixes `4d48bbe` | **Deployed to production 2026-09-23 04:33 UTC**; production logs `[JWT] active kid=k0; configured kids=[k0]`. Review outcome applied: `JWT_SIGNING_KEY` frozen (root secret for keys/tokens/URLs/IP hash); rotation via `JWT_KEY_0_*` / `JWT_KEY_1_*` / `JWT_ACTIVE_KEY_ID` only (runbook in `DEPLOY.md`) |
| R5-61 operator MFA | — | Implemented on branch `feat/r5-61-operator-mfa` (rebased); reviews in progress — not merged |
| R5.4 (DB-14 e-mail verification + password policy) | `335a993` + `7a042c3` + fixes `f0428a1` | **Deployed to production 2026-09-23 ~10:30 UTC** via `POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-db14` (74 migrations in production, newest `20260923062150_BackfillUsersEmailVerifiedAt`). R11 rehearsal 19 s, 6/6 grandfathered live identities verified; production verified 6/6. Consequences live: non-GET `/api/admin/*` gated (`403` + `X-Email-Verification-Required`), passwords 10–128 not in embedded top-1000 and not address. Deployed with settings fix `7bad6b5`. Dashboard UI in progress (client 1.0.45) |
| R5.6 data half (DB-15 activation funnel + usage rollup) | `2eabe72` + fixes `b2da554` + `a0363c6` | **Deployed to production 2026-09-23 ~10:50 UTC** (ordinary deploy; 75 migrations, newest `20260923073100_AddUsageDailyAndWidgetInstalledIndex`). `usage_daily` live; rollup runs before 180 d usage sweep with high-water mark; endpoints `GET /api/admin/stats/funnel` (super admin, metadata) and `GET /api/admin/stats/activation` (workspace). Reviews applied: Gemini Pro nit, Opus BLOCKER Npgsql Kind=Unspecified + change-tracker fixes. Dashboard pages pending (client 1.0.46) |

## CI and CLI status (2026-09-23)

- **CI / e2e**: reset/seed/probe/api/docs/cli/widget/mail phases were green before DB-14; DB-14 required seed personas' passwords ≥ 10 chars (`72168f3`) and admin-created personas now need verification (seed fix in progress); fresh-app phase fixes landed (`e1c3da2`, incl. CLI fix `217262b`: join summary no longer claims widget is embedded for skill-routed installs). Settings fix `7bad6b5`: quick-access invite e-mail toggle was never wired into settings DTOs/controller (found by e2e mail phase) — deployed with DB-14.
- **CLI**: two regressions found by the e2e suite were fixed on `main` — `694a8ea` (stamp reader
  accepts stamp-first sub-skills → 0.6.1) and `decbbb6` (apply no longer clobbers
  `manifest.prev.json` → 0.6.2); plus `217262b` (join summary accuracy). **Not yet published to npm** — owner decision to hold publish.
