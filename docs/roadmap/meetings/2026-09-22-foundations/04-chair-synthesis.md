# Chair synthesis — foundations before launch (2026-09-22)

Participants: Fable (chair), Gemini 3.1 Pro via agy (`01-agy-round1.md`), GLM-5.3 via opencode
(`02-glm-round1.md`). Inputs: the four fixed founder decisions (`00-founder-decisions.md`), the
DB-11a/b/c design, the day's shipped schema work (`docs/db/DB-REVIEW-2026-09-22.md` §7).
This is the draft the two reviewers vote on in round 2; `07-final-report.md` records the result.

## 1. Verdict on DB-11 (identity, memberships, deletion)

**Build it now, in the order DB-11a → DB-11b → DB-11c, with the amendments below folded into the
docs before any implementer starts.** All three reviewers accept the model: one identity per e-mail,
per-workspace memberships carrying role / active flag / approval / security stamp, API keys per
membership, the widget auto-routed by project key, the CLI landing in one workspace without a
picker, comments kept and authored by a tombstone after erase. Nobody found a blocker in the
migration engineering (census first, idempotent blocks, abort guards, same-day-dump rehearsal,
labelled dump, ships alone).

Amendments accepted (owner of the change in brackets):

| # | Amendment | Source | Fix |
|---|---|---|---|
| D1 | Unique identity must be enforced by the database on `lower(email)`, not by five hand-written `.ToLower()` calls | GLM A1 | DB-11a Migration 3: expression index `lower(email) WHERE deleted_at IS NULL` (raw SQL) + one shared e-mail normaliser used by every write path |
| D2 | Erase must scrub the person's e-mail from `invites` (accepted invites survive retention forever) | GLM A2 | DB-11c `EraseAsync`: tombstone `invites.email` for that address, inside the transaction; add to the D9 list |
| D3 | Passwordless (magic-link) stakeholders need a self-erase path; today the only path is "email the operator" | GLM A3 | DB-11c: confirm erase by a fresh magic link or one-time e-mailed token; password confirmation stays for password accounts |
| D4 | No change-e-mail endpoint exists and DB-11 makes e-mail the identity key | GLM A4 | New `POST /api/me/change-email` (password-confirmed, verification mail to the new address, stamp rotation) in DB-11b or a small DB-11d-code doc |
| D5 | DB-11b tells the implementer to copy a rate-limit attribute from `Login` that does not exist | GLM A5 | DB-11b names `[EnableRateLimiting("login")]` explicitly |
| D6 | Selection-token fence uses prefix matching | GLM A6 | exact path comparison |
| D7 | Backfill can create duplicate *ended* memberships | GLM A7 | `DISTINCT ON (user, workspace)` in block 2.4 |
| D8 | Loser of the password merge gets a bare "wrong password" | GLM A8 | login failure for a merged identity suggests "forgot password" |
| D9 | Erase leaves the person's uploaded screenshots on disk | agy A1 | see §4 F5 — founder decision with a default |
| D10 | Legal hold | agy A3 | one sentence in DB-11c: a workspace flag that disables erase and retention for that workspace; the flag itself is a later doc |

Chair concessions: the `user_aliases` table stays (agy E4, GLM E2 — it is the audit record of the
merge and resolves ids that escaped the database; cost ≈ 0). The chair's C.3 (remove the
`?? _currentUser.Id` fallbacks) was already inside DB-11a §3.6 (agy E2).

## 2. Missing foundations — the merged ranking

Legend: **NOW** pre-launch · **PAY** at first paying customer · **TRIG** defer with a named trigger ·
**NEVER**. Where the three memos disagreed, the resolution and reason are in the last column.

| # | Foundation | Verdict | Effort | Resolution / why cheaper now |
|---|---|---|---|---|
| 1 | **DB-11 identity + memberships + deletion** (with §1 amendments) | NOW | 5–7 d | unanimous; census is empty today |
| 2 | **Append-only audit log** (`audit_events`: actor identity+membership, action, target, before/after, request id, impersonation flag) | NOW | 3–4 d | agy said PAY; overruled — F2 is unenforceable without it and history cannot be backfilled. Un-holds roadmap §16 (GLM C1) |
| 3 | **Audited, time-boxed super-admin impersonation**; operator sees metadata only by default (F2) | NOW, after #2 | 2–3 d | chair corrected (B12 → NOW); rides DB-11b's scoped-token machinery |
| 4 | **E-mail verification + change-e-mail + password policy** | NOW | 3–4 d | unanimous; verification after launch splits the user base into cohorts |
| 5 | **Observability baseline**: JSON logs with request id, error tracker, `/health` that pings the DB, external uptime monitor, alert | NOW | 2–3 d | unanimous; no Serilog/Sentry/OTel in any csproj today |
| 6 | **Security headers + login rate limit + `security.txt`** (CSP, HSTS, X-Frame-Options in Caddy; apply the existing `login` policy to password login, keyed per e-mail not per IP) | NOW | 1 d | GLM C2 corrects the chair: comment POST and register are limited; password login is the hole |
| 7 | **Production-side restore drill** from the bucket copy | NOW | 0.5 d | only local restore has been rehearsed; the cheapest item most likely to matter |
| 8 | **Operator MFA (TOTP on the super-admin account only)** | NOW | 1–2 d | GLM; one account, env-seeded, reads every tenant until #3 lands |
| 9 | **JWT `kid` + two-key rotation window** | NOW (design + `kid`), rotate at PAY | 1 d | unanimous |
| 10 | **Privacy policy + ToS now (PDPL-first, GDPR section, Riyadh residency); DPA + subprocessor list at PAY** | NOW / PAY | 3–5 d writing | GLM E3 splits the chair's B18; F4's public demo collects an e-mail, so privacy + ToS precede demo-public (GLM C3 pulls NEW-6 forward) |
| 11 | **Activation funnel definition + weekly number** (demo → converted → widget installed → first comment → first apply) and a pre-retention rollup of `usage_events` | NOW | 1–2 d | unanimous; roll up before the 180 d sweep or lose cohorts |
| 12 | **Arabic/RTL completeness audit** (widget, dashboard, e-mails) | NOW (audit only) | 1 d | agy's "RTL is only a plan" is wrong — Arabic is first-class in widget and dashboard today; GLM's "audit, not build" is right |
| 13 | **Workspace export round-trip verified + DSAR runbook** (export, erase, what survives, invites scrub, timeline) | NOW | 1 d | unanimous |
| 14 | **Tenant-isolation CI probe** (every GET in the API inventory with the wrong tenant's token) | NOW | 1 d | chair; GLM/agy silent — cheap while endpoints ≈ 100 |
| 15 | **Deprecation & versioning policy page** (served files, CLI, client package, API); `/v1` prefix decision | NOW (policy), TRIG (prefix) | 0.5 d | agy wants `/v1/` now; chair and GLM: a policy is free, a URL scheme change touches widget, CLI, skill and rebranding plan — decide in the policy, do not ship the prefix pre-launch |
| 16 | Payment provider, invoices, trials, dunning | PAY | — | F3 defers; entitlements already enforce |
| 17 | Webhooks / events §32 | TRIG: first "notify my tool" ask | — | unchanged |
| 18 | MFA for all users, session/device list | TRIG: first external workspace admin | — | |
| 19 | CAPTCHA / bot protection | TRIG: first abuse (rate-limit floor exists) | — | agy NOW overruled: limits exist; verification (#4) closes the bot-signup hole |
| 20 | Zero-downtime / blue-green deploys | TRIG: paying customers across time zones | — | agy's "stop stopping the API" overruled for this phase: contract migrations are rare and take ~60 s; expand/contract is already the rule (R2) |
| 21 | DR to a second region; on-call | TRIG: first paying customer or >10 tenants | — | |
| 22 | SSO / OIDC | TRIG: first enterprise ask | — | |
| 23 | Screenshots to object storage (from the VM volume) | TRIG: disk > 50 %, second API instance, or CDN need; **enable VM disk encryption now (ops)** | — | agy NOW overruled: backups already include the volume (DB-01); serving from disk is faster and needs no code |
| 24 | Global EF soft-delete filter | NOT NOW | — | agy reverse overruled: 148 `IgnoreQueryFilters` calls to audit; #14 (isolation probe) gives the guarantee cheaper. Revisit with DB-11d |
| 25 | Postgres RLS | NEVER (revisit only for self-host enterprise) | — | DB review §7 |
| 26 | Per-seat pricing | NEVER | — | F3 |

**DO NOW total: ≈ 25–33 days** of mid-tier implementation plus ≈ 4–6 days of writing, all
deliverable through the existing pipeline (architect doc → cross-review → Sonnet implementers in
worktrees → rehearsal on a production dump → contract or ordinary deploy).

## 3. Sequencing

1. **DB-11a** (contract deploy, alone) → **DB-11b** → **DB-11c** with the §1 amendments.
2. **DB-12 audit log** (schema: db-architect) — can be scaffolded in parallel with DB-11b/c; the
   actor model must reference identity + membership from DB-11a.
3. **DB-13 impersonation** (code + scoped token + audit rows + admin notification).
4. **DB-14 e-mail verification / change-e-mail / password policy** (one nullable column +
   token table: db-architect; the rest code).
5. **Ops pack** (no schema): observability, headers + login limiter, operator MFA, `kid`, restore
   drill, disk-encryption check, isolation probe — one code doc each, parallelisable.
6. **Writing pack**: privacy + ToS (before demo-public), versioning policy, DSAR runbook, funnel
   definition + rollup view, RTL audit.
7. **Demo as a product path (F4)**: convert-to-workspace flow and 24 h TTL via the retention job —
   after DB-11 (a demo becomes an ordinary workspace with one membership).

## 4. Founder decisions still needed (each has a default; silence = default)

| # | Question | Default |
|---|---|---|
| F5 | On erase of a person, delete the screenshots attached to *their* comments? | **Keep.** Screenshots depict the customer's application, not the author; they are the customer's asset like the comment text. Delete files only when the comment or the workspace is deleted. The privacy text says so. |
| F6 | Legal text: one-time counsel review before demo-public, or self-written for this phase? | Self-written now with the PDPL/GDPR frame; counsel review at the first paying customer alongside the DPA |
| F7 | Second operator: who besides the founder holds VM access and can run the restore? | Name one person before the first paying customer; document in DEPLOY.md |
| F8 | `GET /api/public/stacks-summary` publishes aggregate customer tech-stack / AI-tool usage anonymously | Keep, but per-workspace opt-out flag in `workspace_settings` before launch |
| F9 | Overage at cap (`MaxCommentsPerMonth`) once billing exists: hard stop (today) or soft overage on the invoice? | Hard stop with an in-dashboard upgrade prompt; the "applied comments" meter is informational |
| F10 | Session model: 12 h JWT, no refresh tokens, no session list | Keep for stakeholders; revisit with #18 |
| F11 | VM disk encryption on the Oracle boot/block volume | Verify on; if off, enable at the next maintenance window (ops task, no code) |

## 5. Rejected or reframed proposals (so they are not re-raised)

- agy B "i18n/RTL DO NOW build" → audit only (#12); Arabic is already first-class.
- agy C1 "global soft-delete filter now" → not now (#24).
- agy C2 "screenshots to object storage now" → trigger (#23).
- agy C3 "never stop the API for migrations" → trigger (#20).
- agy B "audit log at first customer" → NOW (#2).
- chair B12 "impersonation at first customer" → NOW (#3).
- chair B18 "ToS at first customer" → privacy + ToS NOW, DPA at PAY (#10).
- chair A1 "drop the alias table" → keep.
- agy B "API `/v1/` prefix now" → policy now, prefix by trigger (#15).
