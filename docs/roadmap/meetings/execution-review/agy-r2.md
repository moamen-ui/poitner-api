1. FACTUAL ERRORS
- **R2-06 `CommentResponse` exposure**: R2-06:51 claims AI tools do not consume `CommentResponse`. This is false. `API/wwwroot/pointer.sh:131` (`get <id>`) directly calls `GET /api/comments/$ID`, which returns `CommentResponse`. The AI will see `payloadFlags`.
- **R2-00 widget branding**: R2-00:64 claims widget UI text consumes branding, but `web-component/src/templates.ts:133` contains hardcoded `title="Open Pointer feedback" aria-label="Open Pointer feedback"`.

2. AMBIGUITIES
- **R2-01 clean index definition**: R2-01:79 says Single style `--mark all` "requires a clean index → runs git commit... over the already-staged changes". If changes are staged, the index is not "clean". *Propose:* Change to "requires the AI to have already staged its changes" or "requires a clean working tree (no unstaged changes)".
- **R2-02 `git add` responsibility**: R2-02:39 states `pointer_commit_and_mark` takes `files?: string[]` and runs R2-01 semantics. But R2-01 `--mark` expects files to already be staged. It is unclear if the MCP tool runs `git add` itself. *Propose:* Add "The MCP tool must run `git add -- <files>` before invoking R2-01 mark semantics."

3. CONTRADICTIONS
- **R2-01 SECURITY drift test vs AI commit**: R2-01:121 mandates keeping `skill.md`'s SECURITY section byte-identical for a drift test. However, `skill.md:82-84` explicitly tells the AI "`git commit` is permitted... (see Step 5)". R2-01 replaces Step 5 and revokes the AI's commit authority. *Propose:* Drop the byte-identical requirement and remove the `git commit` exception from both `skill.md` and `security-text.ts`.
- **R2-01 dual path vs CLI design**: R2-01:114 keeps `pointer.sh` in the appendix for the AI to use as a fallback. In `pointer.sh`, the AI runs `git commit`, contradicting R2-01's design where the CLI handles the commit.

4. SECURITY / DATA
- **R2-04 Tenant isolation gap**: R2-04:27 specifies a strict-own filter (Tenant ID) for notifications. However, `GET /api/me/notifications` (R2-04:50) must also explicitly filter by user. Without it, any user can query and see all notifications for all users in their tenant. *Propose:* Add `.Where(n => n.UserId == currentUser.PublicId)` to `NotificationService.ListAsync`.
- **R2-05 Rate limit DoS**: R2-05:59 and Task 6 (`[EnableRateLimiting("signup")]`) apply the `signup` rate limit to `login-with-invite`. `00-API-INVENTORY.md:70` shows this is `5/h/IP`. This will instantly DoS offices/agencies sharing an IP. *Propose:* Use a standard `login` limit (e.g. `60/min/IP`).

5. TESTABILITY
- **R2-02 missing `git add` in E2E**: R2-02:85 dictates testing `pointer_commit_and_mark` on one id, but omits staging the edits first. If it enforces R2-01 CLI semantics (which fail on an empty index), the test will fail. *Propose:* Ensure the E2E test explicitly runs `git add` or passes the `files` array to prove the MCP tool stages them.

6. OPEN QUESTIONS
- **(a) R2-01 dual path**: Unacceptable. Retaining the `pointer.sh` AI commit fallback while the primary CLI path handles the commit internally creates conflicting instructions for the LLM. Pick one: Standardize on the CLI making the commit, and delete the `pointer.sh` fallback from `skill.md`.
- **(b) R2-04 `PagedData` untouched**: Fine. Adding `/unread-count` avoids mutating the generic `PagedData` envelope, preventing breaking changes to other dashboard and API consumers.
- **(c) R2-05 magic link posture**: Acceptable. It functions as a bounded bearer token for a specific, low-privilege reviewer account (`Role.QuickAccess`). Unlimited uses within the 14-day TTL prevents multi-device support headaches, and manual revocation acts as the safety valve.
- **(d) R2-05 plaintext password**: Confirmed. `InviteService.cs:546` generates a random password and `:586` embeds it directly in the HTML email body. Severity: High (CWE-319 Insecure Credential Transmission), though mitigated since it's an auto-generated string for a restricted role. R2-05 completely eliminates this vulnerability.
- **(e) R2-00 widget branding**: The widget consumes `/api/branding` via `getBrandName()` in `web-component/src/constants.ts`. However, hardcoded strings remain in `web-component/src/templates.ts:133` (`title="Open Pointer feedback"`).

7. VERDICT per doc
- **R2-00**: READY-WITH-EDITS (Fix hardcoded "Pointer" strings in `templates.ts:133`).
- **R2-01**: NOT-READY (SECURITY text contradicts new commit logic; dual-path commit authority must be resolved; "clean index" terminology is contradictory).
- **R2-02**: READY-WITH-EDITS (Specify if `pointer_commit_and_mark` automatically runs `git add`).
- **R2-03**: READY.
- **R2-04**: READY-WITH-EDITS (Add explicit `UserId` query filter to endpoints).
- **R2-05**: READY-WITH-EDITS (Change `signup` rate limit to a higher `login` rate limit for the invite endpoint).
- **R2-06**: NOT-READY (Payload flags leak into `CommentResponse` which the AI consumes via `get <id>`. Must sanitize the CLI/MCP output).
