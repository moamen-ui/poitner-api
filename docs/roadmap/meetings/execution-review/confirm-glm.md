R1-01: CONFIRMED
R1-02: CONFIRMED
R1-04: CONFIRMED
R1-05: CONFIRMED
R1-06: CONFIRMED
R1-07: CONFIRMED
R2-01: CONFIRMED
R2-02: CONFIRMED
R2-03: CONFIRMED
R2-04: CONFIRMED
R2-05: CONFIRMED
R2-06: CONFIRMED
R3-01: CONFIRMED
R3-03: CONFIRMED
R3-04: CONFIRMED
R3-05: CONFIRMED

NEW blocking problem: R2-01's newly-added `context.ts` comment (docs/roadmap/execution/R2-01-apply-core-cli.md:42) says `productName from GET /api/branding (fallback "Pointer" only if unreachable)` — introduced by this edit round, it directly contradicts R1-02's hardened rule ("NO literal fallback name — unreachable /api/branding is a hard exit 1") and 01-OVERVIEW.md:43 ("never a literal 'Pointer'"); the apply prompt would print "Pointer" on a white-labeled install with a flaky branding endpoint.
