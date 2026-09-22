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

| # | Title | Effort | Schema change? | Depends on | Dashboard change? |
|---|---|---|---|---|---|
| [R5-58](R5-58-observability-baseline.md) | Observability baseline (JSON logs, request id, `/health`, Sentry, uptime ping) | 2–3 d | No | None — independent of DB-11/12/13 | No |
| [R5-59](R5-59-security-headers-and-login-limit.md) | Security headers + login rate limit + `security.txt` | 1 d | No | None — independent of DB-11/12/13 | No (Caddy config only) |
| [R5-60](R5-60-production-restore-drill.md) | Production-side restore drill | 0.5 d | No | None | No |
| [R5-61](R5-61-operator-mfa.md) | Operator MFA (TOTP on the super-admin account) | 1–2 d | **Yes** — `users.totp_secret`/`totp_enabled_at` (nullable), new `user_recovery_codes` table | None hard — designed independent of DB-11c/DB-12 (soft follow-up: audit MFA enroll/disable once DB-12 ships) | Yes — MFA settings card + login-flow MFA step |
| [R5-62](R5-62-jwt-key-rotation.md) | JWT `kid` + two-key rotation window | 1 d | No | Coordinate with DB-11a/b (`ITokenService.Issue` signature changes) — not blocking; independent of DB-12/13 | No |
| [R5-63](R5-63-privacy-and-terms.md) | Privacy policy + Terms of Service | 3–5 d (writing) | No | Text describes the DB-11c/DB-12/DB-13 posture (operator access, erasure) — must be revised once those ship; not blocked from publishing now | No (landing pages, not the dashboard app) |
| [R5-65](R5-65-arabic-rtl-audit.md) | Arabic/RTL completeness audit (checklist only, no fixes) | 1 d | No | None (one audit row, D9, covers the R5-61 MFA card *if* merged first — optional, not required) | Audit only — dashboard is the subject, not modified |
| [R5-66](R5-66-export-and-dsar-runbook.md) | Export verification + DSAR runbook | 1 d | No | DB-11c (identity-erase outcome), DB-12 (audit logging) — both soft/non-blocking; usable against current schema today | No |
| [R5-67](R5-67-tenant-isolation-ci-probe.md) | Tenant-isolation CI probe (e2e) | 1 d | No | None — independent of DB-11a (re-run recommended after DB-11a merges) | No |
| [R5-68](R5-68-versioning-policy-and-v1-alias.md) | Versioning policy + `/api/v1/*` alias | 1 d | No | None | No (explicitly none — policy doc + a routing alias) |

All ten depend on nothing outside this list except the soft/non-blocking notes above. None of DB-11a/b/c/d,
DB-12, or DB-13 are implemented in this tree yet (all are "written 2026-09-22; not implemented" —
DB-12 and DB-13 execution docs already exist at `docs/db/execution/DB-12-audit-log.md` and
`DB-13-operator-impersonation.md`, contrary to any assumption that they don't exist yet).

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
