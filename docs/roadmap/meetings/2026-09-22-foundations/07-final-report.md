# Foundations before launch — final report (2026-09-22)

**Signed by:** Fable (chair) · Gemini 3.1 Pro via agy (`05-agy-lastcall.md`, conditional → conditions
resolved below) · GLM-5.3 via opencode (`06-glm-lastcall.md`, conditional → condition resolved below).
Inputs: `00-founder-decisions.md` (F1–F4 fixed), round-1 memos `01`/`02`, chair position `03`,
synthesis `04`. Method: two independent memos → chair synthesis → last-call vote, same as the
2026-09-11 roadmap meeting.

The question was: *with one user and full freedom, which foundations must exist before real users
arrive?* The answer is a ranked list of **15 DO NOW items (≈ 25–33 days of implementation plus
≈ 4–6 days of writing)**, a small set of first-customer items, named triggers for the rest, and the
user-deletion design (DB-11) approved with ten amendments.

## 1. User identity and deletion (DB-11) — approved with amendments

Unanimous: one identity per e-mail; per-workspace memberships carrying role, active flag, approval
and security stamp; API keys per membership; widget auto-routed by project key; CLI lands in one
workspace; comments survive erase and are authored by a tombstone. Workspace admins remove or
disable within their workspace and never delete a person; a person deletes their own account; the
super admin can erase any identity; nobody (super admin included) can remove, disable, demote or
erase a workspace's **sole admin** until ownership is transferred. A workspace admin who joins other
workspaces is one identity with several memberships.

Amendments folded into the execution docs (see `docs/db/execution/DB-11a…d`):

| # | Change | From |
|---|---|---|
| D1 | Uniqueness enforced by the database on `lower(email)` + one shared e-mail normaliser | GLM |
| D2 | Erase scrubs the person's e-mail from accepted `invites` rows | GLM |
| D3 | Passwordless stakeholders self-erase via a one-time e-mailed token | GLM |
| D4 | `POST /api/me/change-email` (verify new address, rotate stamp, never merge) — new DB-11d | GLM |
| D5–D8 | Rate-limit fact fix, exact path fence, `DISTINCT ON` in backfill, "forgot password" hint after merge | GLM |
| D9 | Screenshots on erase → founder default F5 **keep** (see §4; agy dissents) | agy |
| D10 | Legal-hold flag: one forward reference; the flag is a later doc | agy |

Kept against the chair's first instinct: the `user_aliases` table (audit record of the merge).

## 2. The ranked list

**DO NOW (pre-launch)**

| # | Item | Effort | Note |
|---|---|---|---|
| 1 | DB-11a→b→c→d identity, memberships, deletion, change-e-mail | 5–7 d | ships first; DB-11a alone via contract deploy |
| 2 | Append-only **audit log** (actor identity+membership, action, target, before/after, request id, impersonation flag) | 3–4 d | un-holds roadmap §16; precondition for F2 |
| 3 | **Audited, time-boxed super-admin impersonation**; metadata-only by default (F2) | 2–3 d | after #2; reuses DB-11b's scoped tokens |
| 4 | **E-mail verification, change-e-mail, password policy** | 3–4 d | verification after launch splits users into cohorts |
| 5 | **Observability**: JSON logs + request id, error tracker, `/health` (DB ping), uptime monitor, alert | 2–3 d | no Serilog/Sentry/OTel exists today |
| 6 | **Security headers + login rate limit + `security.txt`**; limit keyed per e-mail | 1 d | password login is unlimited today; comments and register are already limited |
| 7 | **Production-side restore drill** from the bucket copy, timed, written into DEPLOY.md | 0.5 d | cheapest item most likely to matter |
| 8 | **Operator MFA** (TOTP on the super-admin account) | 1–2 d | one env-seeded account reads every tenant |
| 9 | **JWT `kid` + two-key rotation window** | 1 d | rotation today = global logout |
| 10 | **Privacy policy + ToS** (PDPL-first, GDPR section, Riyadh residency) before demo-public | 3–5 d writing | DPA + subprocessor list at first paying customer |
| 11 | **Activation funnel** defined (demo → converted → widget installed → first comment → first apply), weekly number, pre-retention rollup view | 1–2 d | roll up before the 180 d sweep |
| 12 | **Arabic/RTL completeness audit** (widget, dashboard, e-mails) — audit only | 1 d | Arabic is already first-class; this finds the gaps |
| 13 | **Workspace export round-trip verified + DSAR runbook** | 1 d | export, erase, what survives, invites scrub, timeline |
| 14 | **Tenant-isolation CI probe** (every GET in the API inventory with the wrong tenant's token) | 1 d | cheap while endpoints ≈ 100 |
| 15 | **Versioning & deprecation policy** page + `/api/v1/*` **route alias** (unversioned paths keep working) | 1 d | resolves agy's hold: stable canonical URL from day one, zero client churn |

**DO AT FIRST PAYING CUSTOMER:** payment provider + invoices + trials (F3); DPA + subprocessor
list; second operator named (F7); counsel review of legal text (F6).

**DEFER WITH TRIGGER:** webhooks §32 (first "notify my tool" ask) · MFA for all users and session
list (first external workspace admin) · CAPTCHA (first abuse) · zero-downtime deploys (paying
customers across time zones) · second-region DR and on-call (first paying customer or >10 tenants)
· SSO/OIDC (first enterprise ask) · screenshots to object storage (disk >50 %, second API instance,
or CDN; **verify VM disk encryption now**) · global EF soft-delete filter (not now: ~155
`IgnoreQueryFilters()` calls in production code, 300 with tests, would need auditing; #14 gives the
guarantee cheaper — figure corrected per GLM's last call; GLM's 301 included tests).

**NEVER (this horizon):** Postgres RLS (self-host enterprise only) · per-seat pricing (F3).

## 3. Sequence

1. DB-11a (contract deploy, alone) → DB-11b → DB-11c → DB-11d.
2. DB-12 audit log (schema by the db-architect; scaffold in parallel with DB-11b/c).
3. DB-13 impersonation (code; after DB-12).
4. DB-14 e-mail verification / password policy (one nullable column + token table).
5. Ops pack, parallel, one code doc each: observability, headers + login limiter, operator MFA,
   `kid`, restore drill, disk-encryption check, isolation probe, `/api/v1` alias.
6. Writing pack: privacy + ToS, versioning policy, DSAR runbook, funnel definition, RTL audit.
7. Demo as a product path (F4): convert-to-workspace + 24 h TTL via the retention job, after DB-11.

Every item runs through the pipeline built today: architect doc → agy + GLM cross-review →
Sonnet implementers in worktrees → rehearsal on a production dump → contract or ordinary deploy.

## 4. Founder decisions (defaults apply unless overturned)

| # | Decision | Default | Dissent |
|---|---|---|---|
| F5 | Screenshots when a person is erased | **Keep** with the comment; delete only with the comment or workspace; DSAR runbook allows targeted deletion on request | agy: delete (visual PII risk). GLM and chair: keep — screenshots depict the customer's application |
| F6 | Legal text review | Self-written now; counsel at first paying customer with the DPA | — |
| F7 | Second operator with VM access and restore ability | Name before first paying customer; record in DEPLOY.md | — |
| F8 | `GET /api/public/stacks-summary` (anonymous aggregate stack/tool usage) | Keep; per-workspace opt-out flag before launch | — |
| F9 | Overage at `MaxCommentsPerMonth` once billing exists | Hard stop + in-dashboard upgrade prompt; "applied comments" is informational | — |
| F10 | Session model (12 h JWT, no refresh, no session list) | Keep; revisit with the all-users MFA trigger | — |
| F11 | VM disk encryption | Verify on; enable at next maintenance window if not | — |

## 5. Corrections of record

- Arabic/RTL is already first-class in widget and dashboard (agy's round-1 table said otherwise; conceded).
- Comment POST (30/min/user) and register (5/h) are rate-limited; password login is not (chair's round-1 was wrong; GLM corrected).
- `IgnoreQueryFilters()` count: 155 production / 300 with tests (chair's 148 and GLM's 301 both corrected by direct count).
- S-14 fallback removal was already in DB-11a §3.6 (chair listed it as a new "reverse").

## 6. What happens next

The chair commits this report, updates the roadmap plan (un-hold §16, pull NEW-6 forward, add the
DO NOW items to the catalog), and hands DB-11a to an implementer with the amended doc. Items 2–15
become execution docs in order. Nothing in this report requires a new founder decision to start;
F5–F11 apply by default.
