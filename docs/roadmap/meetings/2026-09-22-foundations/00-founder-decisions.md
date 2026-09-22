# Founder decisions fixed before the brainstorm — 2026-09-22

Recorded by the chair from the founder's instruction "go with your recommendations". Reviewers argue
against these as fixed positions, not open questions; a reviewer who wants one reversed must say what
it costs to reverse it later.

| # | Decision | Consequence for the plan |
|---|---|---|
| F1 | **Target market: KSA/GCC first, global-ready.** All data stays in the Oracle Riyadh region. Privacy and terms are written to PDPL (Saudi Personal Data Protection Law) with a GDPR section. No EU-residency promise yet. | Legal pages, data-residency wording, billing rail (KSA-capable provider) follow this. No code change. |
| F2 | **Operator access to customer comment content: metadata only by default.** The super admin keeps counts, statuses, tenants, billing and health. Reading a workspace's comments requires an audited, time-boxed impersonation session visible to that workspace's admin. | Depends on the audit log. Reverses today's silent filter bypass for super admin on comment/reply content. |
| F3 | **Pricing unit: flat per-workspace tiers with a comments-per-month cap.** "Applied comments" is the reported value metric on invoices, not the price. No per-seat pricing (inviting stakeholders must stay free). | Entitlements (`MaxCommentsPerMonth`) are already the enforcement point; the metering view names the events. Payment provider deferred to the first paying customer. |
| F4 | **Keep the demo workspace as a first-class product path.** A demo is a real workspace with a 24 h TTL, a visible "convert to your workspace" action that keeps its data, and cleanup by the retention job rather than an ad-hoc sweep. Activation funnel = demo → converted → widget installed → first comment → first apply. | DemoService/DemoCleanupService evolve; DB-03's complete hard-delete is the cleanup primitive. |

Standing instruction for the whole exercise: **"don't stop until you achieve the goal"** — the goal is
one report, created and reviewed by all three models, that lists the foundations to build in the
pre-launch phase, including the user-deletion design (DB-11).
