# Fable (chair) — position, 2026-09-22

Written before reading the agy/GLM round-1 memos, from the day's work on the data layer and the
existing roadmap. Evidence is by file or doc reference; anything marked *verify* is a claim the
reviewers should confirm or refute in code.

## A. DB-11 (identity, memberships, deletion) — chair's stance

The requirements the founder gave are the right ones for this product: most users are **invited
stakeholders** who exist in one workspace, a few are developers/agencies who will legitimately sit in
several. One identity per email with per-workspace memberships is the only model that makes
"remove from workspace ≠ delete the person" expressible. Points I want the reviewers to attack:

- **A1 — merge safety.** Today the same email can be two live rows with two passwords and two
  `public_id`s referenced from `comments.author_id` and audit columns without FKs. The merge must keep
  every existing `author_id` resolving. Default I proposed: newest password wins, the other rows'
  ids become historical aliases. Is an alias table cheaper than rewriting `author_id` now, while there
  are 121 comments? I lean **rewrite now** (a one-time UPDATE per table, in the same migration, behind
  the DB-09 gate) and keep no alias machinery forever.
- **A2 — JWT shape.** The widget and CLI carry the same JWT as the dashboard. A workspace picker at
  login must not require widget changes: the widget always logs in *inside a project*, so the tenant is
  implied. Reviewers: confirm the design keeps `tenant` as a claim chosen once, not per request.
- **A3 — tombstone vs hard delete.** Keeping comments authored by a deleted person as "Deleted user" is
  the right default for a feedback product (the comment is the customer's asset, not the author's).
  The tombstone must still be *unlinkable*: no email, no name, no IP in `usage_events.meta`. Reviewers:
  grep `usage_events.meta` writers for personal data.
- **A4 — sole-admin lock.** "Cannot remove/disable/delete the sole Workspace Admin without transfer"
  must be enforced in one place (a domain rule the four call sites share), with a test that walks every
  mutation path. This also closes S-13.

## B. Missing foundations — ranked

| # | Area | Exists today | Verdict | Why now |
|---|---|---|---|---|
| 1 | **Identity + memberships (DB-11)** | one row per workspace, arbitrary login pick | **DO NOW** | merging rows with 121 comments is trivial; with 10k it is a project |
| 2 | **Audit log** (append-only: actor, tenant, action, target, before/after, request id) | scattered `created_by/updated_by`, no history | **DO NOW** | cannot be backfilled; every later compliance/support question needs it |
| 3 | **Email verification + password policy** on self-serve register and invite accept | none (*verify*: `RegisterAdminAsync` creates an active admin from an unverified email) | **DO NOW** | abuse vector on a public endpoint; trivial to add before users, awkward after |
| 4 | **Abuse controls on public endpoints** (register, login, invite accept, widget comment POST, uploads): per-IP and per-key rate limits, body caps, honeypot | partial (`widget-status` has a limiter; *verify* the rest) | **DO NOW** | a single bot run today fills the DB and the bucket; limits are config, not architecture |
| 5 | **Workspace data export** (JSON + screenshots zip) and a DSAR/erase runbook | export/import doc exists for comments (`docs/superpowers/plans/2026-07-01-comment-export-import.md`, *verify* what shipped) | **DO NOW** | portability is a trust promise you make on day one; also your own rebranding/migration escape hatch |
| 6 | **Rebuild-from-nothing runbook** (new VM → DNS → compose → restore dump + uploads from bucket), rehearsed once | backups + restore of the DB rehearsed; VM loss path only written | **DO NOW** | one afternoon; the only way to know the bucket copy is sufficient |
| 7 | **Observability baseline**: request id in every log line, structured JSON logs, error tracker (Sentry — *verify* wiring), uptime monitor + alert on 5xx rate | logs to docker; healthcheck ping only for backups | **DO NOW** | you find out about outages from users otherwise; ~1 day |
| 8 | **Secrets rotation**: JWT signing key with `kid` + dual-key window, Brevo/rclone key rotation runbook | single static `JWT_SIGNING_KEY`, stamps validated | **DO NOW (design), rotate at first customer** | adding `kid` later forces a global logout |
| 9 | **Tenant-isolation CI**: a cross-tenant probe suite (every list/get endpoint hit with tenant B's token expects empty/404) | unit tests on filters (12) | **DO NOW** | cheap while endpoints are ~100; the strongest promise a multi-tenant product makes |
| 10 | **Privacy policy + widget consent posture** (what the widget stores in the visitor's browser, what screenshots capture, retention numbers now real) | NEW-6 planned (Release 3) | **DO NOW** | the widget runs on *customers'* sites; they will ask before installing |
| 11 | **Deprecation & versioning policy** (one page: served files, CLI, client package, API) | ON-DISK-CONTRACT freezes names | **DO NOW** | a policy is free; a versioned URL scheme is not — decide which |
| 12 | Super-admin **impersonation with audit** ("view as workspace X", read-only) | super admin bypasses filters silently | DO AT FIRST PAYING CUSTOMER | needs the audit log first |
| 13 | **Billing provider** (Stripe/Paddle/Moyasar for KSA), invoices, trials | entitlements + plans tables, no payments | DO AT FIRST PAYING CUSTOMER | pricing is a business decision, not a foundation |
| 14 | Roles → permissions, per-project membership | roles + `environment_selector_role_ids` | DEFER — trigger: first customer with >1 team | |
| 15 | Webhooks / events (§32) | decided held | DEFER — trigger: first "notify my tool" ask | |
| 16 | MFA, SSO/OIDC | none | DEFER — trigger: first enterprise ask | |
| 17 | Zero-downtime deploys, second region DR | compose recreate (seconds down); contract migrations stop the API by design | DEFER — trigger: first SLA | |
| 18 | Legal: ToS, DPA, subprocessor list (Oracle, Brevo, GitHub) | none | DO AT FIRST PAYING CUSTOMER (subprocessor list: now, it is a list) | |
| 19 | Product analytics: activation funnel (signup → widget installed → first comment → first apply) | `usage_events` + insights endpoints | DO NOW (definition only, 1 hour) | the events exist; name the funnel before launch |

### The DO NOW items, in one paragraph each

1. **DB-11** — planned today; ship in the same rehearsed-batch style as DB-03…08. ~4–6 days incl. dashboard.
2. **Audit log** — one table (`audit_events`: id, occurred_at, workspace_id, actor_id, actor_kind, action, target_type, target_id, diff jsonb, request_id, ip_hash), written by a single `IAuditWriter` from every admin mutation, read by a super-admin/admin page later. Append-only via DB grant or trigger. 2 days.
3. **Email verification + password policy** — verification token on register/invite-accept, `email_verified_at`, block admin actions until verified; zxcvbn-style minimum. 1–2 days.
4. **Abuse controls** — ASP.NET rate limiter policies per endpoint class + `MaxRequestBodySize` on uploads + a per-workspace daily comment cap tied to entitlements (exists as monthly). 1 day.
5. **Export + DSAR runbook** — `POST /api/admin/workspace/export` producing a zip in the uploads volume with a signed download link; the same code path feeds DB-11c's erase. 2 days.
6. **Rebuild runbook** — write and *execute once* against a throwaway VM/compose on the laptop: restore dump + uploads tarball from the bucket, boot, smoke. ½ day.
7. **Observability** — Serilog JSON + request id middleware + Sentry DSN (if not wired) + external uptime monitor on `/api/branding` with alerting to the same healthchecks account. 1 day.
8. **Key rotation design** — accept `kid` in tokens now; document rotation. ½ day now.
9. **Tenant-isolation CI** — e2e job: seed two workspaces, hit every GET in `00-API-INVENTORY.md` with the wrong tenant's token, assert no data. 1 day.
10. **Privacy page** — pull NEW-6 forward; it is writing, not code. ½ day.
11. **Versioning policy page** — ½ day.
19. **Funnel definition** — name four events, one SQL view. 1 hour.

Total DO NOW ≈ **13–16 days** of mid-tier implementation, all rehearsable with the pipeline built today.

## C. Three things I would stop or reverse

1. **Super admin reads customer comment content by default.** Today the operator bypasses every filter. For a feedback product, customers' comments are confidential product plans. Reverse to: operator sees metadata (counts, status, tenants) and needs an audited, time-boxed impersonation to see content. Depends on #2 (audit log).
2. **Self-serve admin registration creates a live workspace with no verification.** Stop until #3 lands; keep invite-based creation.
3. **`?? _currentUser.Id` fallbacks** (S-14) — remove; a principal without a tenant that is not super admin is a bug, not a case to handle.

## D. Open questions for the founder — ANSWERED

All four were decided on 2026-09-22 (see `00-founder-decisions.md`): F1 KSA/GCC first, global-ready;
F2 operator sees metadata only, content behind audited impersonation; F3 flat per-workspace tiers with a
comments-per-month cap, applied comments as the value metric; F4 keep the demo as a first-class,
convertible workspace with a 24 h TTL. Items #10, #12, #13, #18 and C.1 above are to be read against
those decisions.
