# Gemini (agy) — round 1

## A. DB-11 review
1. **[Major] GDPR Erase leaves screenshots behind.** Are the deletion rules complete? No. DB-11c leaves comments (which is fine), but comments include screenshots. Screenshots are stored on a Docker volume (`DB-REVIEW` §1) and are completely orphaned when a user or tenant is erased. A screenshot may contain the user's PII. *Change:* `EraseAsync` must either nullify `ScreenshotUrl` and delete the file, or provide an audit log for manual deletion.
2. **[Major] Password merge confusion.** Is the merge safe? DB-11a merges duplicate emails and silently picks the most recent password (D1). This is safe *only* because there's just one real user today. *Change:* None to the code, but this enforces that DB-11a must deploy immediately. If delayed until real users exist, users will be locked out and confused.
3. **[Minor] Missing Legal Hold bypass in Erase.** If a workspace is under legal hold (a standard enterprise feature), a user erasing their account shouldn't destroy audit trails or specific metadata, though DB-11c is careful to leave comments intact. *Change:* Document that "legal hold" will just disable the Erase endpoint or retain the alias map.
4. **[Minor] Widget UX is preserved.** Does the login picker break the widget? No. The decision (D11) to auto-route the widget using `ProjectKey` and skip the `choose-workspace` picker is excellent. It preserves the frictionless stakeholder experience.
5. **[Minor] Identity model validation.** Is the identity+membership model right? Yes. Stakeholders often use the same email (e.g. gmail) to comment on multiple products. Not forcing them to sign up separately each time makes the experience much better.
6. **[Blocker] Impossible to change later.** Anything impossible to change once users exist? Yes, the `erased_at` tombstoning design. If comments were deleted upon user erase, that would destroy customer value. Changing this contract later would either anger users (if they expected comments to delete) or customers (if they lose data). Do it now.

## B. Missing foundations
| Area | Verdict | Exists Today | Missing |
|---|---|---|---|
| **Security Hygiene** | **DO NOW** | Rate limits on auth | Email verification, CAPTCHA on signup, CSP for dashboard, Incident runbook |
| **Observability** | **DO NOW** | Basic ILogger | Sentry integration, Uptime checks, Alerts, Status page |
| **Legal & Trust** | **DO NOW** | Nothing | ToS, Privacy Policy (PDPL/GDPR), Cookie consent for the widget |
| **i18n/RTL** | **DO NOW** | Angular UI plan | Arabic first-class support in the widget and dashboard |
| **Super-admin impersonation** | **DO NOW** | Silent bypass today | Audited, time-boxed impersonation sessions (Founder decision F2) |
| **API Stability** | **DO NOW** | Internal API | API versioning, deprecation policy |
| **Data Lifecycle** | **DO AT FIRST CUSTOMER** | DB-08 retention | GDPR DSAR runbook, Legal hold mechanism |
| **Billing & Plans** | **DO AT FIRST CUSTOMER** | Entitlements (`MaxComments`) | Payment provider, invoices |
| **Audit Log** | **DO AT FIRST CUSTOMER** | `created_by` | Immutable audit log of who did what |
| **Developer Platform** | **DO AT FIRST CUSTOMER** | Demo workspaces | Public API docs, SDK versioning |
| **Identity (MFA/SSO)** | **DEFER** | Passwords/Magic links | MFA, SSO/OIDC (Enterprise demand trigger) |
| **Operations (Zero-downtime)** | **DEFER** | DB-09 stops API | Blue/green deploys (Trigger: first SLA customer) |

**Security Hygiene (Effort: 3 days)**
Without email verification and CAPTCHA, the public API is completely exposed to bot signups which can easily fill up the DB and consume the 5/hour rate limits. CSP is a mandatory defense-in-depth measure for the dashboard. It is vastly cheaper to add email verification now, before users exist, than to force existing unverified users through a verification gate later.

**Observability (Effort: 1 day)**
The backend is a single VM. If it goes down, the widget on customer sites fails silently or throws errors. Sentry for exception tracking and a basic uptime monitor (e.g., UptimeRobot) with an email alert is trivial to set up now but critical for early credibility.

**Legal & Trust (Effort: 2 days)**
Because the product is a drop-in widget on *other people's sites*, those sites inherit Pointer's cookie and privacy footprint. Without a clear privacy policy, DPA, and cookie consent mechanism, customers legally cannot install the widget. Writing this now dictates the technical cookie boundaries.

**i18n/RTL (Effort: 2-3 days)**
Target market is KSA/GCC (F1). Retrofitting RTL into a complex React dashboard and a Web Component widget after CSS architecture has ossified is painful and expensive. Defining RTL properties and mirroring logic now ensures it's built-in from day one.

**Super-admin impersonation (Effort: 3 days)**
Founder decision F2 mandates metadata-only access by default, and audited impersonation for reading comments. This requires an audit log table and a UI flow. Doing this before real customer data arrives avoids any liability of unauthorized super-admin access.

**API Stability (Effort: 1 day)**
The CLI and AI agents consume the API. Once a customer's AI agent is hardcoded to an API shape, changing it breaks their workflow. Adding a `/v1/` route prefix and `ApiVersion` header expectations now costs almost nothing, but saves immense versioning pain later.

## C. Three things I would stop or reverse
1. **Reverse: Soft deletes as convention only.** `DB-REVIEW` notes there are 230 manual `DeletedAt == null` checks, and the reviewer decided *against* a global EF query filter because of 162 `IgnoreQueryFilters()` calls. **Reverse this.** Missing a manual check causes critical data leaks. Fix the 162 calls and add the global filter now while there are only 25 tables.
2. **Reverse: Screenshots on Docker volume.** Uploads are saved to a local volume (`wwwroot/uploads`). They are not in DB backups, and deleting a tenant does not delete their screenshots (orphaned files). **Reverse this.** Move screenshot uploads to the same Oracle Object Storage used for backups, and prefix them by `tenant_id` so they can be securely wiped on tenant deletion/erase.
3. **Stop: API stops during migrations (DB-09).** DB-09 enforces that the API stops during contract migrations. For a SaaS dashboard, this is acceptable. For a Web Component widget embedded in customer apps, this is an outage. **Stop this practice.** All migrations must be additive and support the old and new code running simultaneously, ensuring 99.99% widget uptime.

## D. Open questions for the founder
- If a stakeholder requests GDPR erase, should their uploaded screenshots be physically deleted, or just stripped of identifying DB links?
- The widget is injected into customer sites. Does it set any non-essential cookies that require the customer to update their cookie consent banner?
- Is email verification a hard block for the widget (e.g. can a stakeholder post a comment without verifying their email)?

## E. Where I disagree with the chair (Fable)
1. **Fable completely missed the GDPR screenshot hazard.** Fable's A3 focuses heavily on stripping PII from `usage_events.meta` during tombstoning, but fails to mention that screenshots (which routinely contain PII and screenshares) sit untouched in `wwwroot/uploads`. You cannot claim a GDPR DSAR erase is compliant if the visual evidence is kept forever.
2. **Fable's C3 (S-14 fallbacks) is redundant.** Fable proposes removing the `?? _currentUser.Id` fallbacks as a "stop/reverse" item. DB-11a §3.6 *already does exactly this*. Fable missed that this was solved in the very doc they were reviewing.
3. **Fable contradicts themselves on Super-admin Impersonation.** In B #12, Fable defers impersonation to "DO AT FIRST PAYING CUSTOMER". But in C1, they argue we must *reverse* the current silent bypass and require impersonation now. Given Founder Decision F2 explicitly mandates this, it must be **DO NOW**. You cannot defer the founder's explicit requirement.
4. **Fable's A1 (Dropping the alias table).** DB-11a §3.2 step 2.5 already rewrites the IDs, but keeps `user_aliases` for future-proofing and unstructured JSON payloads. Fable wants to drop the alias machinery entirely. I disagree; an alias table costs roughly 0 bytes, and deleting it destroys the ability to ever resolve an old `public_id` hiding in a cold audit log or third-party webhook payload.
