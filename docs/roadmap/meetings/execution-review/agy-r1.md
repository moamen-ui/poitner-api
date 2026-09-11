1. FACTUAL ERRORS
- **R1-05 line 120**: States "if the request body's Environment == EnvironmentTag.Local". Real code: `AddReplyRequest` (`Application/DTOs/Comment/AddReplyRequest.cs:5-6`) does not contain an `Environment` property. `ReplyService` must fetch the parent `Comment` from the database to determine its environment.
- **R1-06 line 229**: States "derive with HKDF-SHA256 from `Jwt:Key`". Real code: `JwtTokenService.cs:10` shows the options class binds the signing key to `SigningKey`, so the correct config path is `Jwt:SigningKey`, not `Jwt:Key`.
- **R1-01 line 48**: The test regex `data-pf[a-z-]*` checks a prefix that was explicitly rejected and renamed to `data-component-source` in `11-final-decisions.md` line 241. Testing for it provides no guard against new arbitrary names.

2. AMBIGUITIES
- **R1-02 line 227**: `RecordEventRequest` provides `ProjectKey` (string), but `UsageEvent` (line 224) requires `ProjectId` (int). Add: `EventsController must use IProjectService to resolve ProjectKey to ProjectId before saving the UsageEvent.`
- **R1-05 line 118**: The dashboard URL itself is not automatically exempted from origin enforcement. If the project owner replies via the dashboard (`POST /api/comments/{id}/replies`), the dashboard's `Origin` will be rejected unless explicitly whitelisted. Add: `Always exempt the dashboard's own URL (IBrandingService.Get().Urls.App) alongside localhost.`

3. CONTRADICTIONS
- **R1-06 line 216 & 228 (AES-GCM encryption)** vs **11-final-decisions.md line 256**: The meeting explicitly decided on "SHA-256" (hash-only) for NEW-5. R1-06 introduces reversible AES-GCM encryption so keys remain "re-viewable", directly contradicting the meeting's explicit hash-only directive.
- **R1-01 line 48** vs **11-final-decisions.md line 288**: R1-01 demands `data-pf` have zero matches, but the final decisions expressly mandate "brand-neutral attribute names (data-component-source...)". The R1-01 doc relies on a stale attribute name.

4. SECURITY / DATA
- **R1-06 (AES-GCM for API Keys)**: Reversible encryption for API keys is a major vulnerability. If a self-hoster's environment leaks (exposing both DB and `Auth:ApiKeyEncryptionKey`), all tenant API keys are fully compromised.
- **R1-05 (Wildcard Origins)**: Allowing `*.` hosts without restriction means a user setting `*.vercel.app` accidentally authorizes every other tenant on Vercel to post comments to their project, bypassing isolation entirely.

5. TESTABILITY
- **R1-01 line 48 & 61**: `ServedFiles_UseOnlyFrozenNames` cannot reliably detect new customer-visible names because it only searches for the abandoned `data-pf` prefix. Fix: The test should parse `API/wwwroot/*.md` and HTML files for all `data-*` attributes and assert they belong to a strict predefined allowlist (`data-component-source`, `data-build-sha`, `data-snapshot-mask`).

6. OPEN QUESTIONS
- **(a) R1-06 encrypt-for-display**: Incorrect call. Keys MUST become one-time-reveal (hash-only). AES-GCM retains reversible secrets, risking total key compromise on env+DB leak. R1-03's quick-start flow shows the key once on creation, which is sufficient. If lost, users must regenerate.
- **(b) R1-05 tokens and wildcards**: Staff JWT without Origin is sound (CLI/agents don't send it). Quick-access rejection without Origin is sound (they only come via the widget). Wildcard `*.host` rows are DANGEROUS for shared public suffixes (like `.vercel.app`) as it breaks cross-tenant isolation.
- **(c) R1-02 `.env` vs `.env.local`**: Right call. `VITE_POINTER_SERVER`, `PROJECT`, and `ENV` are public widget identifiers, not secrets. Committing them in `.env` ensures all teammates automatically point to the correct project without running `init` themselves.
- **(d) R1-02 `first_comment` race**: Not race-safe. Concurrent first comments evaluated in `CommentService.CreateAsync` after save could both see `count == 1`. It requires an `EXISTS` check inside the transaction or a unique constraint on `(ProjectId, Type) WHERE Type = 'first_comment'` catching `DbUpdateException`.
- **(e) R1-04 reusing `plans` rate-limit**: Unacceptable. `plans` limits by IP (60/min/IP). Sharing it means marketing site visitors and CLI runs from the same NAT IP will consume each other's quota, leading to spurious CLI failures. Create a dedicated `meta` limit.

7. VERDICT per doc
- **R1-01**: READY-WITH-EDITS (Fix the guard test regex to match an allowlist of valid `data-*` attributes rather than just checking for the dead `data-pf` prefix).
- **R1-02**: READY-WITH-EDITS (Explicitly map `ProjectKey` to `ProjectId` in `EventsController`; fix the `first_comment` TOCTOU race using a unique constraint).
- **R1-03**: READY
- **R1-04**: READY-WITH-EDITS (Assign `/api/meta` its own isolated rate-limit policy).
- **R1-05**: READY-WITH-EDITS (Exempt the dashboard app URL from origin checks; look up `Environment` from the parent comment for replies; restrict wildcards on public suffixes).
- **R1-06**: NOT-READY (Violates security best practices and meeting intent. Must be rewritten to use one-time reveal and store only SHA-256 hashes).
- **R1-07**: READY
