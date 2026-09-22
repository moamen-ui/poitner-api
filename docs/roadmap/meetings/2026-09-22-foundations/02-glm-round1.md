# GLM — round 1

Reviewer: GLM (zai-coding-plan/glm-5.3) via opencode, 2026-09-22. Evidence read from the working
tree; `file:line` citations. Founder decisions F1–F4 treated as fixed; where I push back I state
the reversal cost. Scope: DB-11 review + missing-foundations brainstorm.

## A. DB-11 review

Read in full: DB-11a (636 lines), DB-11b, DB-11c, DB-RULES (R1–R16), DB-REVIEW §7/§8, plus the
code the docs cite. Overall verdict up front: **the model is right, the migration engineering is
genuinely careful (abort guards, census-first, idempotent backfill, labelled-dump rollback), and
now — one real user, census expected empty — is the only cheap moment to build it.** No blockers.
Four majors, all cheap to fold in before implementation.

- **A1. Major — the one-identity invariant is enforced by convention, not by the database.**
  Migration 3 creates `ux_users_email_live` unique on **raw** `email` (DB-11a §3.2), and block 2.6
  lowercases all live rows **once**. Every write path must then remember `.Trim().ToLower()`
  itself — today that is five separate hand-normalizations (`AuthService.cs:236,344,454`,
  `UserService.cs:62`, `DemoService.cs:267`), not a shared routine. One future path that forgets
  (admin edit, import, a new invite flow) writes `User@x.com` beside `user@x.com` → two identities,
  silently — and re-merging after real users exist is a second operator-run uuid-rewrite migration
  with support tickets attached. **Change:** make the unique index an expression index on
  `lower(email) WHERE deleted_at IS NULL` (raw SQL in the migration; lookups already normalize),
  and/or a `SaveChanges` interceptor that normalizes `User.Email` centrally. ~0.5 d now.

- **A2. Major — erase leaves PII behind in `invites`.** `EraseAsync` (DB-11c §3.4) hard-deletes
  keys, device logins, quick-access links, the inbox and personal AI rules — but not the invite
  rows addressed to the erased person (`Invite.Email`, `Domain/Entity/Invite.cs:38`, "only this
  (normalized) email may accept"). DB-08's retention sweeps only *never-used* expired/revoked
  invites; an accepted invite row with its email survives indefinitely. A DSAR finds exactly this
  kind of residue. **Change:** inside `EraseAsync`'s transaction, null (or tombstone) `invites.email`
  where it equals the identity's email, and audit it in D9's list. ~0.5 d.

- **A3. Major — passwordless stakeholders have no self-service deletion path.** `DELETE /api/me`
  requires the password; `PasswordlessOnly` → `Forbidden(EraseNeedsPassword)` ("Magic-link accounts
  are removed by the workspace admin", DB-11c §3.5) — but a workspace admin can only **remove from
  workspace**, never erase (§1 verb table). Quick-access Clients are the least-technical users and
  the most likely to ask for deletion; their only path is emailing the operator. An erasure right
  that requires a password the account never had is not a working right. **Change:** accept erase
  confirmation via a fresh magic link (the quick-access rail already exists) or a one-time emailed
  token; keep password confirmation for password accounts. 1–2 d.

- **A4. Major — no email change exists, and DB-11 makes email the permanent identity key.** No
  change-email endpoint anywhere (only change-password / api-key on `MeController`; grep
  `ChangeEmail` over `API/Controllers` → nothing). After DB-11, email is the sole identity join;
  "I changed jobs" becomes an operator hand-edit of `users.email` that can *create* a duplicate-
  email state the merge machinery was built to remove. **Change:** `POST /api/me/change-email`
  (password-confirmed, verification mail to the new address, identity-stamp rotation) before
  launch. ~2 d. After users it is a migration-grade feature; now it is a controller.

- **A5. Minor — DB-11b's rate-limit prerequisite is factually wrong.** DB-11b §2: "the login policy
  name applied to `POST /api/auth/login` (read the attribute on `AuthController.Login`)"; §5 task 6:
  "copy the rate-limit attribute from `Login`". There is **no** `[EnableRateLimiting]` on `Login`
  (`API/Controllers/AuthController.cs:20` — bare `[HttpPost("login")]`; the `login` policy sits on
  `login-with-invite` at `:38-39`, and `Program.cs:90-92` documents password login as deliberately
  unlimited). An implementer following the doc copies nothing. **Change:** DB-11b names
  `[EnableRateLimiting("login")]` explicitly (the policy exists, `RateLimitingExtensions.cs:124-132`).

- **A6. Minor — selection-token fence uses prefix matching.** The `scope=select_workspace` fence is
  `StartsWithSegments("/api/auth/switch-workspace")` (DB-11b §3.2); a future sub-route would also
  match. Prefer exact `Path ==`. The doc already extracts the fence into a testable static (§6
  test 5) — tighten the comparison while it is free.

- **A7. Minor — duplicate *ended* memberships are possible after the backfill.** The 2.4 guard
  `(w.left_at IS NULL) = (u.deleted_at IS NULL)` (DB-11a §3.2) permits two ended rows for one
  (identity, workspace) when both pre-merge rows were soft-deleted. No code reads ended pairs
  except audit, but `SoleAdminWorkspacesAsync` and any future history UI should not assume
  uniqueness. **Change:** `DISTINCT ON (user, workspace)` in 2.4, or state the possibility in the
  doc. Cosmetic.

- **A8. Minor — D1's losing password dies silently.** Most-recent-hash-wins (2.2) is fine, but the
  user of the discarded password sees plain "wrong password". Optional one-liner: on login failure
  for an identity with `merged_into_user_id` rows, always suggest "forgot password".

- **A9. Answers to the chair's Part-A questions, explicitly.**
  - *Is the model right for invited stakeholders + few developers?* Yes — email identity +
    per-membership role/disable/approval + per-membership API keys (D3) is the standard shape this
    product category converged on; the widget auto-route by project key (D11) correctly keeps the
    picker out of customer apps; the CLI's key landing deterministically in one workspace is the
    right property for non-interactive agents.
  - *Are the deletion rules complete?* Nearly — see A2/A3. Otherwise the verb table is complete:
    remove/disable per workspace, leave, self-erase, super-admin erase, sole-admin guard including
    super admins (S-13), D10's reversible-suspension exemption is sound. Audit trail =
    `left_reason` + audit columns; acceptable, but see B1 for the real audit log.
  - *Is the merge safe?* Yes, as engineered: census first (§9.1, expected empty today), idempotent
    blocks, abort guards (2.7), rehearsal on a same-day dump, labelled `pre-db11a` dump, ships
    alone (no R7.1 batching). The single non-invertible step (2.5) is recorded forever in
    `user_aliases`. With zero duplicates everything but the membership insert is a no-op.
  - *Does the picker / `switch-workspace` break widget or CLI?* No. Old tokens die once (no
    `mstamp`) — documented as expected in DB-11a §9.5. The CLI needs nothing. One dependency that
    must not slip: the **widget rebuild** (DB-11b §5 task 8, `projectKey` in the login body) —
    without it a multi-membership stakeholder meets the picker inside a customer's app, which D11
    exists to prevent.
  - *Impossible to change later?* The in-place uuid rewrite, `user_aliases`, the `mstamp` claim and
    per-membership keys all become load-bearing the day a second workspace exists — building them
    now, while the census is empty, is the whole point. **Build DB-11a→b→c now, with A1–A4 folded
    in.**

## B. Missing foundations

Legend: **NOW** = pre-launch · **PAY** = at first paying customer · **TRIG** = defer with trigger ·
**NEVER** = do not build in any foreseeable phase. Ranked by my confidence × cheapness-now.

| # | Foundation | Today (evidence) | Verdict | Effort |
|---|---|---|---|---|
| 1 | Append-only audit log | Nothing — no entity (`Domain/Entity/` has none); §16 held on a trigger | **NOW** | 3–4 d |
| 2 | Super-admin impersonation, audited + time-boxed (F2) | Nothing — grep `Impersonat` → 1 test string; F2 explicitly reverses today's silent filter bypass | **NOW** | 2–3 d |
| 3 | Email verification + change-email | No `EmailVerified`/`ConfirmEmail` anywhere (grep: zero); no change-email endpoint; only rail is invite emails | **NOW** | 3–4 d |
| 4 | Observability baseline: JSON logs, request ids, Sentry, uptime ping, status page | Default console logging (`appsettings.json:2-7`); no Serilog/Sentry/OTel in any csproj; handler logs method+path only (`Program.cs:210`); no monitor | **NOW** | 2–3 d |
| 5 | Security headers + login rate limit + security.txt | Caddyfile emits cache headers only (no CSP/HSTS/X-Frame-Options); `POST /api/auth/login` unlimited per IP (`AuthController.cs:20`, deliberate per `Program.cs:90-92`) | **NOW** | 1 d |
| 6 | Prod-side restore drill (off-box copy → real restore) | DB-01 "production-side restore still unexercised (rehearsed locally only)" (DB-REVIEW §7) | **NOW** | 0.5 d |
| 7 | TOTP MFA for the operator (super admin) account | `ADMIN__EMAIL/PASSWORD` env seeding (`docker-compose.prod.yml:47-48`); single account, no MFA | **NOW** | 1–2 d |
| 8 | JWT signing-key rotation (`kid`, two-key overlap) | Single static `JWT__SigningKey` (`docker-compose.prod.yml:44`); no `kid` claim (`JwtTokenService`); rotation = global logout | **NOW** | 1 d |
| 9 | Legal pack: ToS, privacy (PDPL-first per F1), DPA, subprocessor list | Draft privacy page exists (`landing-redesign/glm/privacy.html`); no ToS/DPA/subprocessors found | **NOW** (before demo-public) | 3–5 d writing |
| 10 | Activation funnel defined + weekly report | `usage_events` exists (`UsageEvent.cs`: type/source/meta); F4 names the funnel; no definition/report anywhere | **NOW** | 1–2 d |
| 11 | Arabic/RTL completeness audit (widget + dashboard + emails) | `comments.language` detected (`CommentService.cs:1359`); translate skill served; dashboard RTL parity sits inside a held "full-app UX audit" (plan hold list) | **NOW** (audit only) | 1 d |
| 12 | Workspace data export verification + DSAR runbook | `ExportImportService` exists (export/import); no documented DSAR path; erase is DB-11c | **NOW** | 1 d |
| 13 | Payment provider, invoices, trials, dunning | Entitlements enforced (`MaxCommentsPerMonth`, `CommentService.cs:89-100`); `GET /api/plans` public; no billing rail | **PAY** (F3 defers — agreed) | — |
| 14 | Webhooks/events §32 | Held ("first 'notify my tool'"); `workspace_settings` named as future home | **TRIG** (keep) | — |
| 15 | MFA for all users; session/device list UI | Stamp revocation exists (`Auth__ValidateSecurityStamp`, prod true); no session inventory | **TRIG** (first external workspace admin) | — |
| 16 | CAPTCHA/bot protection on public surfaces | Rate limits: signup 5/h, comments 30/min/user, demo 3/h, plans 60/min (`RateLimitingExtensions.cs`) — reasonable floor | **TRIG** (first abuse) | — |
| 17 | Zero-downtime / blue-green deploys | Contract migrations stop the API (R7 path); acceptable maintenance-window model today | **TRIG** (paying customers across time zones) | — |
| 18 | DR to a second region; on-call rotation | Single Riyadh VM (F1-consistent); off-box backups exist | **TRIG** (first paying customer or >10 tenants) | — |
| 19 | SSO/OIDC for tenants | Password + magic-link + API keys + device flow | **TRIG** (first enterprise deal asks) | — |
| 20 | Per-tenant encryption of screenshots | HMAC-URL serving, `/uploads/*` blocked (`Program.cs:300-308`); volume plaintext at rest | **TRIG** (enable VM disk encryption now — ops, not code) | — |
| 21 | Postgres RLS | Explicitly not endorsed (DB-REVIEW §7); app filters + FKs + `Tenancy__StrictNullTenantIsolation=true` | **NEVER** (revisit only at self-host enterprise) | — |
| 22 | Per-seat pricing | F3 forbids (inviting stakeholders stays free) | **NEVER** | — |

### DO NOW, one paragraph each

**1. Audit log (3–4 d).** F2 says operator access is "metadata only by default" and any content
read requires "an audited, time-boxed impersonation session visible to that workspace's admin" —
neither clause exists today, and both are *unimplementable* without an audit log. It is also the
evidence backbone for DB-11c's removal/erase verbs (`left_reason` says what, not who-when-from-
where). Design: one append-only table (no update/delete code paths, excluded from soft delete),
actor identity + membership, action, target type/id, before/after for security-relevant fields
(role, isActive, approval, plan), request id, impersonation flag. Cheaper now because DB-11's
actor model (identity vs membership) is being defined this week — building the log after means
re-deriving actor semantics, and every pre-launch action is history that can never be
reconstructed. Note: the plan's hold-list trigger for §16 ("before first non-founder apply") is
backwards — see C1.

**2. Impersonation (2–3 d, after 1).** Today the super admin silently bypasses tenant filters;
F2 replaces that with an audited, time-boxed session the workspace admin can see. Cheap now
because the auth pipeline is being rewritten for DB-11 anyway (`mstamp`, scope-fenced tokens) —
an `scope=impersonate` token with hard expiry, an audit row at start/end, and a notification to
the workspace admin reuse exactly the machinery DB-11b builds for selection tokens. After launch
it means touching a live validator twice and explaining the first unaudited reads retroactively.

**3. Email verification + change-email (3–4 d).** Nothing verifies address ownership
(`EmailVerified` grep: zero hits). Today that is survivable because invites are admin-driven and
stakeholder self-signup is approval-gated — but F4's public demo path collects an email and mints
a workspace, F3 will email invoices/receipts, and password reset already emails unverified
addresses. Adding verification *after* users exist forks the population into verified/unverified
cohors and needs a migration + comms plan; adding it now is a column, a token, and one email
template (Brevo is wired, `docker-compose.prod.yml:55-59`). Fold in A4 (change-email) — same rail.

**4. Observability (2–3 d).** The API runs on default console logging with no request
correlation and no error telemetry (no Sentry/Serilog/OTel in any `.csproj`; the global handler
logs `{Method} {Path}`, `Program.cs:210`). With zero users you debug by reproduction; with users
you debug by guessing. Minimum: Serilog JSON to stdout with a per-request `RequestId` pushed into
every log line, Sentry (or a GLM-neutral equivalent — any hosted error tracker) on the API,
one external uptime ping on `/api/meta`, and a status page. `/api/meta` already exists
(`e2e/api/meta.spec.mjs:14` — the 2026-09-11 inventory line "no /api/meta" is stale), but it is
not a health check: add a `/health` that pings the DB, for the monitor.

**5. Security headers + login limiter (1 d).** The dashboard is served by Caddy with cache
headers only — no CSP, no HSTS, no `X-Frame-Options` (Caddyfile: only `header` directives found
are Cache-Control). And password login is unlimited per IP (`AuthController.cs:20`; the NAT
argument in `Program.cs:90-92` argued against the *5/h signup* budget, not against the existing
60/min `login` policy — apply the latter to `Login`). Both are one-evening changes that become
"why didn't we" questions after the first incident.

**6. Prod restore drill (0.5 d).** DB-01 shipped off-box copies to Oracle Object Storage and a
freshness gate, but the *production-side* restore has never been exercised (DB-REVIEW §7, DB-01
row). Until a restore has actually run from the off-box copy, the backup system is a hypothesis.
Do it once against a scratch DB on the VM (or a second VM), time it, write the number into
DEPLOY.md. This is the cheapest item on the list and the one most likely to matter.

**7. Operator MFA (1–2 d).** One super-admin account, seeded from env vars, no second factor
(`docker-compose.prod.yml:47-48`). Its compromise reads every tenant's metadata and — until F2's
impersonation ships — every tenant's comments. TOTP on the super-admin login only; tenants keep
the simple flow. Trivial now, embarrassing after.

**8. JWT key rotation (1 d).** One static signing key from env (`docker-compose.prod.yml:44`),
no `kid`; any leak or routine rotation logs out every user of every tenant. Add a `kid` claim and
accept two keys during an overlap window. Small now; after launch it is a coordinated-events
change.

**9. Legal pack (3–5 d of writing, before demo-public).** F1 fixes the frame: PDPL-first,
GDPR section, Riyadh residency — and the subprocessor list is already knowable (Oracle Cloud
compute+storage, Brevo email, GitHub Packages npm). The demo path (F4) collects an email and
creates a tenant, so ToS + privacy must precede demo-public; a DPA template is what the first
serious KSA buyer will ask for. A draft privacy page exists (`landing-redesign/glm/privacy.html`)
— ToS/DPA/subprocessors do not.

**10. Activation funnel (1–2 d).** F4 defines the funnel (demo → converted → widget installed →
first comment → first apply); `usage_events` records type/source/meta with 180 d retention
(DB-08). Missing is the *definition*: which event names constitute each step, one SQL view, a
weekly number. Also decide now whether cohort retention needs a daily aggregate rollup (usage
events are deleted at 180 d — roll up before deleting, or lose cohorts older than two quarters).

**11. Arabic/RTL audit (1 d).** F1 makes KSA the first market; comments carry language
(`CommentService.cs:1359`), a translate skill is served, the widget has i18n strings — but
dashboard RTL parity lives inside a *held* "full-app UX audit". Pull just the RTL/Arabic pass
forward: it is the difference between "Arabic works" and "Arabic is first-class", and it is a
walk-through now versus a rework after Arabic-speaking customers have formed opinions.

**12. Export verification + DSAR runbook (1 d).** `ExportImportService` exists; verify a full
workspace export→import round-trip on the current schema, then write the one-page runbook:
export (workspace), erase (DB-11c), what survives (tombstone semantics), invites scrub (A2),
timeline. This document is also what makes A2/A3 findings visible to a future auditor.

## C. Three things I would stop or reverse

1. **Stop holding §16 (audit log) until "before first non-founder apply"** (DX-UX-CX-PLAN hold
   list, line 144). The trigger is inverted: the audit log is a *precondition* for F2's operator-
   access policy and for credible erase/DSAR answers — the first external apply happening
   un-audited is precisely the event that makes the gap real. It is 3–4 days; un-hold it now and
   sequence it with DB-11 (B1).

2. **Reverse the unlimited password-login stance** (`Program.cs:90-92`, `AuthController.cs:20`).
   The recorded rationale — a shared NAT would exhaust any per-IP budget — is an argument against
   the 5/hour signup budget, not against the 60/min `login` policy that already exists for
   magic-link redemption (`RateLimitingExtensions.cs:121-132`). Unlimited online password guesses
   on an endpoint embedded in every customer's app (F4 makes the widget's login modal the public
   face) is the single hygiene item I would fix before demo-public. If NAT lockout is the worry,
   partition per email, not per IP.

3. **Reverse the ordering of R3.5 (public privacy page) vs the demo path (F4).** The plan ships
   the privacy/self-host page in Release 3 (`DX-UX-CX-PLAN` R3.5) while F4 promotes the demo —
   which collects an email and creates a tenant — to a first-class product path now. A public
   demo that captures personal data before the privacy page exists is the wrong order for a
   PDPL-first product. Pull NEW-6 forward to before demo-public (it is ½ d of honest writing;
   the harder legal pack is B9).

## D. Open questions for the founder

1. **PDPL review:** will founder-drafted legal text get a one-time counsel review before
   demo-public, or is self-written the plan for this phase? (Determines how much B9 is worth.)
2. **Second pair of hands:** who besides you can run the restore (C-runbook) and hold VM access?
   Bus factor is one; the DR trigger in B18 should name a human.
3. **Email change policy:** confirm verified-email-on-change (A4/B3) — it slightly lengthens the
   invite/change flow in exchange for a much stronger identity story.
4. **`GET /api/public/stacks-summary`** (`StacksPublicController.cs:19-25`, anon, rate-limited):
   it discloses aggregate customer tech-stack/AI-tool usage to anyone. Intended as landing social
   proof? If yes, fine — but consider per-workspace opt-in before competitors find it.
5. **Overage behavior at first paying customer:** `MaxCommentsPerMonth` enforcement is a hard stop
   today (`CommentService.cs:89-100`). When F3's invoicing arrives, is cap-overflow still a hard
   stop (current), and is that what the invoice's "applied comments" meter should count?
6. **Session model:** 12 h JWT, no refresh tokens, no session list. Acceptable for stakeholders
   (widget re-login friction) — confirm, because changing lifetimes post-launch is a support
   event.
7. **Screenshot residency:** uploads live on the same Riyadh VM volume (plaintext at rest unless
   the VM disk is encrypted). Confirm disk encryption is on, and that the privacy text says
   screenshots stay in-region (F1 wording).

## E. Where I disagree with the chair

Written after reading `03-fable-position.md`; everything else in this memo stands unchanged.

1. **Impersonation timing (chair B12: "at first paying customer"; me: NOW).** F2 is a *fixed
   founder decision* that reverses today's silent filter bypass — and it is unenforceable until
   the audit log and the impersonation session exist. Until then the operator's choices are
   "silent full access" (contradicting F2 every day) or "no support path into a broken tenant".
   The chair's own C.1 depends on this landing. The chair's *sequencing* (audit log first) is
   right; the verdict should be NOW, 2–3 days, riding the DB-11 auth rewrite (B2 above).

2. **The alias table (chair A1: leans "rewrite now, keep no alias machinery forever").** Keep it.
   `user_aliases` is four columns with no code path beyond a resolver fallback — it is not
   "machinery", it is the **audit record of the merge itself**: delete it and the one-time uuid
   rewrite becomes unverifiable after the fact. It also resolves uuids that escaped the database —
   a workspace export a customer downloaded, an old notification email, a dashboard cache — which
   a DB-only rewrite can never fix. Cost of keeping: ~zero. Cost of regretting deletion:
   unexplainable dangling ids.

3. **Legal timing (chair B18: ToS/DPA at first paying customer).** Split it: **privacy policy +
   ToS now** (they gate F4's public demo path, which collects an email and creates a tenant, in a
   PDPL-first market), DPA at first paying customer. The chair already concedes the subprocessor
   list is "a list" — the privacy page is the same class of work (writing, not code).

4. **Two of the chair's *verify* items, answered.** (a) A2 — confirmed: `tenant` is chosen once
   per token; `switch-workspace` mints a fresh JWT, nothing is per-request (DB-11b §3.2), and the
   widget auto-routes by project key so it never needs the picker. (b) A3 — `usage_events.meta`
   is clean today: the only writer is `RecordEventAsync` (`UsageEventService.cs:19,40-44`) and
   **no production caller passes a `meta` value** (grep: only `Tests/UsageEventServiceTests.cs:47`);
   `UserId` is the public_id uuid, which post-erase resolves to the "Deleted user" tombstone. The
   real unlinkability gap on erase is `invites.email` (my A2), which the chair's own standard
   argues for scrubbing. Also B4: comment POST *is* limited (30/min/user,
   `CommentsController.cs:18`) and register at 5/h — the actual hole is unlimited password login
   (`AuthController.cs:20`, my C2).
