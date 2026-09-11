
> build · glm-5.2

→ Read docs/roadmap/meetings/07-agy-round2.md
→ Read docs/roadmap/meetings/08-claude-chair-synthesis.md
# 1. VERDICTS

**S1–S14: ACCEPT (all 14).** Each matches either my position or a concession I already made; S13's failure-mode documentation matches what I read in element.ts (`_checkWidgetActive` fail-closed at :275-288, `disableSilently` at :294-300, toast suppression at :632-635). No objections.

**D1: ACCEPT.** The chair's counter to agy is correct and decisive: hashing is server-side, the key string format is unchanged, so nothing `init` writes to disk differs — agy's precedence argument conflated DB-compromise protection with disk-compromise protection. The 12-char prefix + SHA-256-full-key schema is sound (prefix needs lookup uniqueness only).

**D2: ACCEPT.** The rebuttal is exactly right: fiber `__source` is dev-mode only — `framework-source.ts` already reads it as tier 2, and Phase 4 exists *because production strips it*. agy's recommendation re-implements the status quo. The "stamp every top-level host element of a Fragment" refinement improves my original design.

**D3: ACCEPT** (my position).

**Section C, items 1–15:**
1–14: **ACCEPT.**
15: **ACCEPT with note** — bundling NEW-3 (widget infra) with §33-lite (privacy) is odd packaging; if it ever needs splitting, split there.

# 2. EFFORT SANITY

Nothing is off by 2x+. Closest call:

- **Item 15 (NEW-3 + §33-lite), 3–5d → budget 5–7d.** Versioned serving touches embed.js emission, the current `no-cache`/ETag logic (Program.cs:249-254), a deploy smoke, SRI docs, budget CI, plus widget rebuild for §33 — that's ~1.5x, under the 2x bar but the likeliest slip.
- **Release 1 total**, summed honestly: 13.5–21.5 working days ≈ 2.7–4.3 weeks. Fits 3 weeks only at the optimistic edge — see below.
- (Release 2 has the same shape: §7 + §24 at 1–2w each plus three more items ≈ 2.5–4w.)

# 3. RELEASE 1 CUT LINE (3 weeks, one founder + agents)

Keep: NEW-1, §1, §2, §3+§4, §41 — the funnel plus hygiene is non-negotiable.

- **NEW-4 splits**: keep "cron-schedule the existing `e2e/` suite" (½d — deferring this is how the 5-minute promise rots); move the **fresh-app init scenario and white-label job to day 1 of Release 2**, where they attach naturally to §7 (the apply E2E needs the CLI anyway).
- **NEW-5 is the designated second slip** if §1 takes its full 2w: per D1 it's order-independent of §1 and protects against DB compromise only — one week's delay costs nothing functional. Lands no later than R2-week-1, before MCP.

Everything else in R1 stays.

# 4. ONE THING TO ADD

**A public privacy/self-host answer page.** Adopted from my round-1 list, then dropped in every synthesis since: S14 took the dashboard column, migrations, filters, and CSP note — but the buyer-facing artifact never landed anywhere. Screenshots + DOM snapshots of customer apps is the first agency/enterprise procurement question; a half-day of honest writing (what's captured, retention, self-host = your own Postgres) unblocks evals no feature can. Neither the top 15 nor the hold list contains it.
