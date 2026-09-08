# docs/rebranding

Everything needed to rename the product with no brand residue.

| File | What it is |
|---|---|
| `REBRANDING-PLAN.md` | The plan. Parameterised — it opens with a blocking interview (§1) and cannot be executed with the answers block (§2) empty |
| `answers.template.yml` | The interview answers, machine-readable. Fill in, keep next to the plan |
| `verify-no-pointer.sh` | The acceptance gate. Greps for brand occurrences minus the documented allowlist; exits non-zero with a file:line list. `--protected` also asserts that the 147 DOM/CSS `pointer` tokens were not damaged |
| `REVIEW-AGY.md` | Independent review by Gemini via the Antigravity CLI |
| `REVIEW-GLM.md` | Independent review by GLM via opencode |
| `REVIEW-RESPONSE.md` | Which review findings were accepted, which were rejected, and why |
| `BASELINE.txt` | Generated in phase 1: the pre-rename occurrence counts, so drift is provable |
| `CHANGELOG-rebrand.md` | Generated during execution: old → new for every identifier, plus the data-migration counts. Keep it forever — it is how anyone decodes old commits, old log lines, and old support threads |

## Read this first

The literal goal ("zero occurrences of *pointer* anywhere") is **not achievable**, and the plan says
so in §3: 147 occurrences are web-platform vocabulary — `pointer-events`, `cursor-pointer`,
`pointerdown`, `PointerEvent` — in a product whose whole job is DOM interaction. Renaming those
breaks the element picker and every clickable cursor in three dashboards. The achievable goal, which
the gate enforces, is **zero brand occurrences**, with that vocabulary explicitly allowlisted.

Measured surface as of 2026-09-08: **~12,700 occurrences across ~1,700 files** in two repos,
including 52 hits of the `poitner` misspelling that a naive find/replace misses entirely.
