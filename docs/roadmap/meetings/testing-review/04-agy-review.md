1. COVERAGE GAPS
- **R3-01-tests.md**: The `Covers` section claims to test AC-8, AC-9, and AC-10 from the execution doc, but `docs/roadmap/execution/R3-01-vite-plugin-manifest.md` only defines 7 acceptance criteria.

2. FACTUAL ERRORS
- **`00-HARNESS.md` (Stack / Preconditions)**: Claims the `mailpit` service is present in `docker-compose.yaml` ("the code already assumes it"), but `mailpit` is completely absent from `docker-compose.yaml`.
- **`R1-02-tests.md` (R1-02-06)**: Asserts `GET /check` returns 200 with HTML, but the `/check` route does not exist in `API/Program.cs` or any controller.
- **`R1-04-tests.md` (R1-04-01)**: Tests `GET /api/meta`, but `MetaController.cs` and the `/api/meta` endpoint do not exist in the codebase yet.
- **`R3-03-tests.md` (Preconditions)**: Claims the repo's `Caddyfile` contains the `@widget` matcher with `not query v=*` verbatim, but `Caddyfile` does not contain that query exclusion.

3. UNIMPLEMENTABLE STEPS
- **`H-tests.md` (H-03)**: Step 1 instructs to use `POST /api/auth/login-with-invite {token}`. While R2-05 plans to add this, currently the only invite endpoint is `POST /api/auth/register-invite` which strictly requires `{Code, Email, Password, DisplayName}` (`AcceptInviteRequest.cs`), making the test fail instantly if run against the current API.
- **`R2-06-tests.md` (R2-06-03)**: Tests `CommentSummaryDto` and `ApplyReplyDto` for `payloadFlag` absence, but these DTOs do not exist in `Application/DTOs/` yet (blocked by R2-01).

4. FLAKE RISKS
- **`R1-04-tests.md` (R1-04-05 Rate Limiting)**: The loop sends 121 requests sequentially expecting the 121st to hit a 429. If the fixed 60-second rate-limit window resets *during* the execution of this loop, the 121st request will return 200, causing an intermittent CI failure.
- **`R1-02-tests.md` (R1-02-01/02)**: Step 8 specifies "one automatic whole-stack retry on budget failure" for a 300s timeout. Retrying a 5-minute slow path hides legitimate performance regressions and unpredictably inflates CI duration.

5. TIERING
- **`R1-02-tests.md` (R1-02-06 `/check` page)**: Placed in the `nightly` tier. This is a sub-millisecond API GET request with no state mutations; it belongs in the `PR` tier.
- **`R1-02-tests.md` (R1-02-01 & R1-02-02)**: Placed in the `PR` tier despite taking up to 300s each due to `npm create` and builds. Two of these easily consume 10 minutes, severely threatening the 15-minute PR budget. They should be `nightly`.

6. VERDICT per document
- **`00-HARNESS.md` / `H-tests.md`**: READY-WITH-EDITS (Add instructions to actually insert `mailpit` into `docker-compose.yaml`; fix the invite login step to use `register-invite` with proper DTO payload).
- **`R1-01-tests.md`**: READY.
- **`R1-02-tests.md`**: READY-WITH-EDITS (Move `R1-02-06` to PR tier, move `R1-02-01/02` to nightly, flag `/check` as requiring implementation before merging).
- **`R1-03-tests.md`**: READY.
- **`R1-04-tests.md`**: READY-WITH-EDITS (Send the 429 burst concurrently to avoid bucket-reset flakes; flag `/api/meta` as unimplemented).
- **`R1-05-tests.md`**: READY.
- **`R3-01-tests.md`**: READY-WITH-EDITS (Remove references to non-existent AC-8, 9, 10).
- **`R3-03-tests.md`**: READY-WITH-EDITS (Remove the false claim that `Caddyfile` already includes `not query v=*`).
- **`R2-06-tests.md`**: NOT-READY (Requires R2-01 DTOs to be implemented first).
