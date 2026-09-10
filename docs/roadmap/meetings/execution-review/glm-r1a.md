1. FACTUAL ERRORS

1. R1-04:32 — "Add `<InformationalVersion>` / `<Version>` to `API/API.csproj`" — no such file; it is `API/Pointer.API.csproj` (Dockerfile publishes `API/Pointer.API.csproj`).
2. R1-01:34 — storage row is incomplete vs. code: widget also writes `localStorage pointer_user` (`web-component/src/element.ts:413,426,439,446`) and `sessionStorage pointer_page_session_id` (`web-component/src/pagecontext.ts:179`). The guard test as specced ("every storage key literal ∈ the storage row") fails on day one.
3. R1-01:43-48 — the guard test cannot pass against today's tree: `skill.md:324` contains `data-testid` (example snapshot JSON); `landing/index.html` contains `data-theme` (:928), `data-step` (:848), `data-brand-logo`/`data-brand-name` (:796-797), `data-i`, `data-path` — none are widget-internal, so neither the frozen allowlist nor the `InternalAttributes` carve-out covers them.
4. R1-02:59 — "`--tool` default from env detection (same table as `pointer.sh:72-84`)": that table detects only claude-code/antigravity/cursor/windsurf/other — there is no `opencode` detection rule to port. Also the established aiTool vocabulary is `opencode-glm`, not `opencode` (`pointer-init.md:311`, `SetProjectStackRequest.cs:13-15`).
5. R1-02:110 — "the block from `pointer-init.md:71-91`": line 71 is prose ("Add to `index.html`…"); the snippet is `:73-91`.
6. Minor: R1-02:39 fallback `productName:"Pointer"` conflicts with the white-label rule (overview:43); see C4.

Verified-correct (no action): `install.sh:29-34` symlinks, `install.sh:56` `ptr_`, `install.sh:76-80` gitignore block without `config.json`; `pointer-init.md:11-12` wrong self-register claim vs `:25` + `ProjectService.EnsureAsync` (`ProjectService.cs:601-641`, no self-create); `CommentService.cs:54-61` project-owner stamping and `:89-100` monthly cap; `CreateProjectValidator.cs:15`; `Policies.Admin == "Admin"` (`API/Auth/Policies.cs:6`); `RateLimitingExtensions.cs` policies; `Program.cs` embed.js/`Safe()`/placeholder-rewrite/forwarded-headers line ranges; storage keys otherwise; the three `window.__*` globals; `landing/` exists; `/vendor/snapdom.js` served; `just up/test/fmt/migrate` in justfile; `BaseEntity.Id` is `int` (UsageEvent.`ProjectId int?` consistent); `UpdateStatusAsync` name (`CommentService.cs:477`); `docs/ON-DISK-CONTRACT.md` and `cli/` don't exist yet (correct starting state).

2. AMBIGUITIES

1. R1-02 §C:3 — project-create error handling beyond 409: `CreateAsync` also returns 403 (super-admin `ProjectService.cs:45-46`; quick-access `:51-52`) and plan-limit 400 (`:62-71`, `Result.LimitReached`). Add: "403 → 'This account cannot create projects' exit 3; 400 with `isLimitReached` → print `message` + limit, exit 1."
2. R1-02 §I — the swallow rule is under-specified. Add: "catch only `DbUpdateException` whose inner `PostgresException.SqlState == \"23505\"`; rethrow everything else. After swallowing, set `context.Entry(ev).State = Detached`." (The UnitOfWork shares one DbContext; a failed `Added` entry replays on the next `SaveChangesAsync`, e.g. `CommentService.cs:522`.)
3. R1-04 — exit precedence. Add: "If the `meta` check fails → exit 5 immediately (skip remaining checks); else if `key` ✘ → exit 3; else 1 on any ✘."
4. R1-02 §C:8 — `POST /stack` failure handling unstated. Add: "non-2xx → print ⚠, continue."
5. R1-02 §C:8 — the CLI never writes `.pointer/stack.json` (pointer.sh:87-101 and the skill do). Add: "write the response's `data` object verbatim to `.pointer/stack.json` (committed)" — otherwise R1-04's `stack` check can never be ✔ after a CLI init (see C5/T6).
6. R1-02 §I — `Meta?: object` serialization: add "serialize with `JsonSerializer.Serialize` (default options); reject with 400 when the serialized length > 2000."
7. R1-04:31 — "`IMetaService.GetAsync()` in `Application/Services`" contradicts the convention (overview:59). Use `Application/Services/Interfaces` + `Implementation`; note `IBrandingService.GetAsync(publicBase, existingKinds)` needs a public base URL — resolve from HttpContext as `BrandingController.cs:34-42` does.
8. Tool-id mismatch (FE4): add a row "`opencode` — detected via `$OPENCODE`/`$CODEX` unset→prompt; registers as `opencode`" and update `skill.md`/`SetProjectStackRequest` vocabulary, or adopt `opencode-glm` everywhere.
9. `npx` vs `npx -y`: R1-02/R1-03 print `npx pointer-feedback init` (interactive "Ok to proceed?" on first run) while R1-04:60 uses `npx -y`. Pick `npx -y` in all printed commands.

3. CONTRADICTIONS

1. Rate-limit policy ownership: R1-04:19 defines the `meta` policy itself; R1-05:73 says R1-05 is "the single owner of all policies; R1-04/R2-05 reference them". Same for `events`: R1-02:170 creates it, R1-05:70 creates it. Overview:32-33 allows R1 docs in parallel and neither R1-02 nor R1-04 lists R1-05 as prerequisite → duplicate `AddPolicy` (startup `ArgumentException`) or a missing policy (`[EnableRateLimiting("meta")]` fails per request). Fix: R1-02 owns `events`, R1-04 owns `meta`; rewrite R1-05 §C to "reuse `events`/`meta`; add `comments`/`login` here".
2. R1-04 internal: ":40 exit 0 (all ✔/⚠) or 1 (any ✘)" vs ":46 (exit 5)" and ":48 (exit 3)". Fix via A3.
3. 11-final-decisions:37 "`npx pointer init`" vs overview:39/R1-02 "`npx pointer-feedback init`" — superseded by the npm-name check; annotate the meeting doc, no code effect.
4. R1-02:39 literal fallback "Pointer" vs overview:43 and 11-final-decisions rule 4. Make branding non-OK a hard exit 1 and delete the fallback string.
5. R1-04:81 acceptance "all ✔ (or ⚠ only for `widget` on Next)" vs R1-02 never writing `.pointer/stack.json` → the `stack` check (R1-04:54) is always ⚠ after CLI init. Fix via A5 (preferred) or amend the acceptance.

4. SECURITY / DATA

1. R1-02 §I design is tenant-safe as written: `EnsureAsync` is tenant-scoped (`ProjectService.cs:619-625`), strict-own filter bucket matches `AppDbContext.cs` strict-own rows, `OwnerId` from project (mirrors `CommentService.cs:54-61`), summary endpoint Admin-gated (`Policies.Admin`), UserId server-stamped. Client events are whitelisted/pollution-only.
2. Null-owner projects (`ProjectService.cs:605-613`): `first_comment` rows stamped `OwnerId = project.OwnerId` can be null → invisible to tenant-admin summary under the strict-own filter. Same behavior as comments today; acceptable, but state it in R1-02 §I so nobody "fixes" it with `IgnoreQueryFilters`.
3. Swallow hazard (A2): as literally written ("try/catch for DbUpdateException → swallow"), a genuine DB failure is silently eaten and the poisoned change tracker can fail subsequent saves — the fix in A2 is mandatory, not optional.
4. R1-03: no new exposure — key already rendered (`GET /api/me/api-key`, `MeController.cs:42-50`); mask-by-default + full-command copy is right; `ptr_` keys are URL/shell-safe (`Program.cs:289` alphabet, `install.sh:56`).
5. `/check` page: reflection guarded by the same `Safe()` as embed.js (mandated in R1-02 §J) ✓.
6. Migrations additive (AddUsageEvents; partial unique index) — safe on boot for self-hosters; no drops. "AI never pushes" — no doc instructs push; CLI is human-run.

5. TESTABILITY

1. R1-02:223 `UsageEventServiceTests` "first_comment emitted once": the `Tests/` fixtures are EF **InMemory** (`ChangePasswordTests.cs:81` etc.) which enforces neither unique nor partial indexes and never throws `DbUpdateException` — the once-only assertion cannot work there. Fix: mandate the SQLite provider (already referenced, `Pointer.Tests.csproj:15`) for this test class.
2. R1-01:68 deliberate-failure criterion is checkable, but the base test must first be re-scoped (FE2/FE3), else criterion 2 ("`just test` green including the two new tests") is unsatisfiable.
3. R1-04:81 — unachievable as written until C5 is resolved.
4. R1-02:227-235 — objective (exit codes, grep for "Pointer", git diff idempotency, events summary) ✓. R1-04:80-84 ✓ (`jq .data`, `Cli__MinVersion=99.0.0`).

6. OPEN QUESTIONS

(c) `.env` not `.env.local` — **right**. Vite loads both; `.env` is the shared/committed layer while `.env.local` is the per-developer, gitignored-by-convention override; the four `VITE_POINTER_*` values are non-secrets teammates need, and the key never goes there (credentials.env, mode 0600). Add one sentence: "if the repo gitignores `.env`, values must be copied per developer — the `.env.example` upsert documents the keys either way."
(d) **Sound mechanism** (constraint beats COUNT for races) with three required hardenings: filter the catch to `SqlState 23505` on that index; detach the failed entity; guarantee the event insert is its own `SaveChangesAsync` after the comment's (`CommentService.cs:177`), so a swallowed failure can never take the comment down with it. Then yes.
(e) **Fine.** Anonymous, cheap, `ResponseCache`-shielded; the NAT-vs-`plans` isolation rationale is correct. Two notes: assign single ownership (C1), and `ResponseCache 60s` makes `ServerTime` up to 60 s stale — safely under the 5-min skew threshold, but say so.

7. VERDICT

- **R1-01 — READY-WITH-EDITS**: add `pointer_user` + `pointer_page_session_id` to the storage row; re-scope the guard test to "no pointer-namespace attribute outside {`data-component-source`,`data-build-sha`,`data-snapshot-mask`}" for md/sh/Program.cs/landing (enumerate widget-internal attrs — `data-id, data-act, data-toggle, data-placement, data-private, data-c, data-i, data-path, data-testid` — in the doc itself); line-cite FE2/FE3.
- **R1-02 — READY-WITH-EDITS**: A1-A9 (esp. tool vocabulary, create 403/400 handling, 23505+detach, write `stack.json`, SQLite test fixture, snippet `:73-91`); drop the "Pointer" fallback.
- **R1-03 — READY** (dashboard claims taken as given; flags match R1-02 §B; nit: `npx -y`).
- **R1-04 — READY-WITH-EDITS**: fix `API/Pointer.API.csproj` path; exit-code precedence; `meta` policy ownership; service placement/branding signature; stack-check acceptance vs R1-02.
