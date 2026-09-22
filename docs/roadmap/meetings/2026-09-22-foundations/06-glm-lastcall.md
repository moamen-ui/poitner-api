# GLM — last call

Round-2 vote on `04-chair-synthesis.md`. Read after: founder decisions, my round-1 memo, agy's
round-1 memo. Every ACCEPT below is a vote, not a re-argument; evidence is cited only where the
synthesis text itself needed checking against the tree.

## §1 — DB-11 verdict + amendments D1–D10

**ACCEPT.** Build-now in a→b→c order is the right call and the amendment table transcribes my
A1–A8 faithfully (D1–D8), agy's screenshot and legal-hold items correctly routed to F5 and D10.
D10 as "one sentence in DB-11c, flag is a later doc" is the right size. The chair's concession
keeping `user_aliases` (my E2) is noted and agreed.

## §2 — Ranking rows 1–26

- Rows 1–13, 15–23, 25, 26: **ACCEPT.** Each matches either my round-1 verdict or a resolution I
  already argued for (rows 2, 3, 6, 10, 12 record the chair adopting my positions over agy's or
  the chair's own — correct in every case). Row 14 (isolation probe) is the chair's addition;
  cheap, mechanical, and complementary to `Tenancy__StrictNullTenantIsolation` — accept.
- Row 24 (global EF soft-delete filter, NOT NOW): **ACCEPT-WITH-CHANGE.** The verdict stands —
  but the stated justification carries a wrong number: "148 `IgnoreQueryFilters` calls to audit"
  is actually **301** in production code (`rg -o 'IgnoreQueryFilters\(\)' --glob '!Tests/**'` →
  301; 361 with tests; agy's 162 was also wrong). The report should not print an audit scope 2×
  smaller than reality — fix the figure; the corrected count *strengthens* the not-now verdict.

## §3 — Sequencing

**ACCEPT.** Contract-before-code (a→b→c), audit log scaffolded in parallel with actor semantics
taken from DB-11a, impersonation after the log it depends on, ops/writing packs parallelisable,
and demo-last respects the privacy-before-demo-public ordering I argued in C3. No change.

## §4 — F5–F11 defaults

**ACCEPT** all seven. F5's "keep screenshots on person-erase, delete with comment/workspace" is
consistent with the tombstone-comments model and is backed by existing code
(`LocalFileStorage.cs:68-73` owner-directory deletion, called from tenant/project/demo cleanup
services) — the promise is not aspirational. F8 (opt-out before launch), F9 (hard stop +
upgrade prompt), F10 (keep 12 h), F11 (verify disk encryption now) match my round-1 D-questions;
F6/F7 defaults are sensible.

## §5 — Rejected proposals

**ACCEPT.** The list is accurate and the rejections are all argued elsewhere in the synthesis;
nothing here should be re-raised, including agy's four.

## Overrules against me

The chair overruled **none** of my round-1 positions; E1 (impersonation NOW), E2 (keep aliases),
E3 (legal split) and C2 (login limiter) were all adopted into rows 3, §1-concessions, 10 and 6.
Nothing to concede, nothing held.

## New items

None. I looked for blockers I missed in round 1 and found none; the only defect found this round
is the row-24 count above.

## Verdict

I sign once the row-24 `IgnoreQueryFilters` figure is corrected from 148 to ~301 (production
code).
