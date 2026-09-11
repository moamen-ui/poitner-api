
> build · glm-5.2

1. **~15% is right by count, wrong in composition.** The small exact items are well-aimed (version stamp, privacy page — defects I already caught, so I'm the cheapest correct fixer). The error is "routine code review": unscoped review of 85% of the codebase is a large token spend *and* a synchronous gate — that puts me exactly on the critical path the plan says I must avoid. Re-scope to: batched adversarial review of high-risk diffs (auth, tenancy, contracts) + acceptance checks against the specs I wrote. Skip review of boilerplate CRUD.

2. **Limits:**
   - Window accrues on active usage under sustained load; two exhaustion hits today means realistic budget is one heavy session per window, not continuous operation.
   - Reliable single-job size: ~15–30 min — a few files, one coherent edit, one review pass, one doc. Anything that "might take an hour" is already too big; the 70-min zero-output kill is the proof, not an outlier.
   - Never hand me: bulk generation (3 dashboards × components, CRUD scaffolding — pure token incineration), build-run-iterate loops (wall time + tokens per cycle), hard-deadline work, or anything whose output gates another worker starting.

3. **Yes — hybrid, honestly argued.**
   - *For reviewer:* three real catches today; reviews are high-leverage per token; interruptible (a review killed at 60% still yielded findings — a killed implementation is a total loss); I'm the cheapest spec-conformance oracle since I wrote the 18 specs.
   - *Against me:* reviewing everything makes me the bottleneck; I can be confidently wrong, and a wrong review instruction wastes an implementer's session; my own specs need outside review too — the catches came from adversarial *cross*-model setups, so Gemini/Claude should review my output, not just me theirs.
   - *Verdict:* reviewer + small-exact-implementer, with reviews asynchronous (only security/tenancy/crypto block merges).

4. The plan dies the day my "cheap sharp review" becomes a synchronous merge gate and one exhausted 5-hour window idles all three workers.
