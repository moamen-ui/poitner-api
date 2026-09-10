1. FACTUAL ERRORS
- `R3-05-privacy-page.md:35` claims deleting a project deletes its screenshots and files. Reality: `ProjectService.DeleteAsync` (`Application/Services/Implementation/ProjectService.cs:365-440`) performs only a database soft-delete (`DeletedAt = now`) and does not cascade to file storage.
- `R3-04-snapshot-privacy.md:49, 63` claims the server sanitizer should run on "the edit path". Reality: `EditCommentRequest` (`PUT /api/comments/{id}`) only updates `Body` and `RemoveScreenshot` (`00-API-INVENTORY.md:50`); the DTO does not accept a snapshot to sanitize.
- `R3-01-vite-plugin-manifest.md:159` omits `[Produces("application/json")]` for `ProjectBuildsController`, despite `01-OVERVIEW.md:56` explicitly mandating it for all controllers.

2. AMBIGUITIES
- `R3-01-vite-plugin-manifest.md:131`: "Widget path... marks DeployedAt for Applied comments". Implementer must guess the tenant filter. Add: "Verify `ProjectBuildService` restricts updates strictly to the user's tenant (`_currentUser.TenantId`) to prevent cross-tenant comment modifications."
- `R3-04-snapshot-privacy.md:34`: Claims `parentInfo` text is masked. `00-API-INVENTORY.md:24` states `parentInfo` "carries only tag/classes/id", so masking text is impossible. Add: "Skip masking `parentInfo` as it contains no text."

3. CONTRADICTIONS
- `01-OVERVIEW.md:45` states `R3 map (held)`. However, `R3-01-vite-plugin-manifest.md:96` and `153` implement `pointer map --from-source`. Fix: Update `01-OVERVIEW.md` to clarify that `map --from-source` is shipping in R3 for offline manifest generation, while the selector lookup variant is held.

4. SECURITY / DATA
- `R3-03-widget-release-eng.md:92`: CSP fallback uses `fetch(CSS_URL)` when `<link>` fails. If the user pinned the widget with SRI, this `fetch` bypasses the SRI check. Add: "Pass `integrity: link.integrity` in the `fetch` options to preserve SRI validation during the CSP fallback."
- `R3-01-vite-plugin-manifest.md:129`: `POST /builds` allows the widget to report arbitrary `Sha` values. While rate-limited, a malicious stakeholder could flood bogus `ProjectBuild` rows. Add: "Validate `Sha` format strictly (`^[0-9a-f]{7,40}$`) in the DTO validator."

5. TESTABILITY
- `R3-01-vite-plugin-manifest.md:182`: "Same repo cloned on two machines → byte-identical entries". Cannot be objectively tested in standard CI. Fix: "Unit test `hashFor()` asserts that absolute paths from two different mocked git roots yield the exact same hash."

6. OPEN QUESTIONS
- (a) **R3-01 Babel/Vue exception**: Yes, acceptable. They are build-time `peerDependencies` for the plugin consumer, preserving the zero-dependency runtime rule for the CLI (`dist/cli.js`). To avoid Babel, one could provide a Babel plugin that users inject into `@vitejs/plugin-react`'s options (reusing its AST), but the proposed approach is standard for isolated plugins.
- (b) **R3-03 pinned SRI contract**: Serving the current build bytes breaks SRI on every deploy. The correct contract (if no history is kept) is: "Return `404 Not Found` (or `410 Gone`) if `v != current_hash`, so the failure is explicit rather than a silent SRI block." Alternatively, keep the historical files on disk.
- (c) **R3-03 perf long-task guard**: It should hard-fail once a performance baseline is proven stable in CI across multiple builds (e.g., in Release 4) to avoid blocking deploys due to CI flakiness.
- (d) **R3-01 DeployedAt vs enum**: Confirmed correct. `CommentStatus` is checked as a strict integer (1=Open, 2=ReadyToApply, 3=Applied, 4=Archived) in `pointer.sh:114` `jq` filters, `skill.md:234`, and the dashboard. A new `Deployed=5` enum value would fall through these clients silently. Nullable `DeployedAt` is safely additive.
- (e) **R3-05 project deletion files**: No, it is not true today. Verified `ProjectService.DeleteAsync` (`Application/Services/Implementation/ProjectService.cs:365-440`) only sets `DeletedAt = DateTime.UtcNow` (soft delete) on `Project` and relations. It does not trigger any deletion of screenshot files from upload storage.

7. VERDICT
- `R3-01-vite-plugin-manifest.md`: READY-WITH-EDITS (Fix OVERVIEW map contradiction, add `[Produces]`, clarify tenant filter).
- `R3-02-design-tokens.md`: READY.
- `R3-03-widget-release-eng.md`: READY-WITH-EDITS (Fix pinned SRI mismatch response, add `integrity` to fallback fetch).
- `R3-04-snapshot-privacy.md`: READY-WITH-EDITS (Remove edit path from sanitizer, drop `parentInfo` masking).
- `R3-05-privacy-page.md`: NOT-READY (Falsely claims file deletion; requires server-side file cascade implementation or the legal claim removed).
