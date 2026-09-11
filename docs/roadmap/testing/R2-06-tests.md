# R2-06-tests — Secrets / payload advisory flag on comments

Harness: [`00-HARNESS.md`](00-HARNESS.md). Execution doc: [`../execution/R2-06-secrets-flag.md`](../execution/R2-06-secrets-flag.md).

## Covers

AC-1 (`ghp_` + 36 chars → ⚠ pill in the widget, `github_token` in tooltip) → R2-06-01 · AC-2 (with `X-Pointer-Client: widget` detail contains `"hasPayloadFlag":true`; **without** the header, zero `payloadFlag` substrings) → R2-06-03 · AC-3 (`view=summary` and `apply-queue` (admin key) contain `payloadFlag` **zero** times) → R2-06-03 · AC-4 (`./.pointer/pointer.sh get <id>` contains `payloadFlag` zero times) → R2-06-02 · AC-5 (edit removes the token → pill cleared on reload) → R2-06-04 · AC-6 (detector runs on replies) → R2-06-01/03 · AC-7 (`just test`, widget build) → CI.

## Preconditions

- Seed complete; personas: QA (`USERS.tester` — comment author + widget viewer), WA (`TENANT_OWNER` — apply-queue fetch via `keys.json.wsAdmin`), DEV (MCP repo key).
- Prerequisites merged: R2-01 (`AiCommentView`, CLI `get`), R2-02 (`lib/mcp.mjs`, `pointer_get_comment`). R2-06 itself adds the columns/DTO gating/widget pill.
- Fixture: alpha fixture on 4173 (`serve.mjs alpha`) for the widget rows.
- Secret canaries are **fake**: `ghp_` + 36 chars of `[A-Za-z0-9]`, `<script>alert(1)</script>` — never a real credential.
- `lib/api.mjs` supports a `headers` option (harness §4: "+ header option for `X-Pointer-Client`"); **substring counts always run on the raw response `text()`**, never on the envelope-parsed object (`api.mjs` returns `json.data` and would hide key-absence).
- Shared helper `installPointerSh(repo)` (from R2-03-tests) provides the legacy `./.pointer/pointer.sh` for R2-06-02.

## Scenarios

| id | intent | tier | layer | role | steps | expected | evidence |
|---|---|---|---|---|---|---|---|
| R2-06-01 | `flag: secret in comment shows badge in widget` | PR | widget | QA | 1. QA `POST /api/projects/e2e-alpha/comments { body: 'leaked key ghp_<36 chars> please rotate', environment: 1, element: { selector: '.token', route: '/' } }` → id F. 2. Widget: `preAuthWidget(tester)`; `page.goto('http://localhost:4173/')`; `waitForResponse('**/capture-config')`; `widget.locator('#pf-toggle').click()`. 3. Card: `widget.locator('.pf-card[data-id="<F>"] .pf-pill.pf-flag')` (**Decision:** pill class `pf-flag`, `--pf-warn` muted colour). 4. QA `POST /api/comments/{F}/replies { body: '<script>alert(1)</script>' }`; refresh the list (`#pf-refresh`). 5. Reply pill on the same card. | 3 → pill visible with text `⚠ contains a secret/payload?` and `title` attribute containing `github_token`; 5 → reply carries the same pill shape with `title` containing `script_tag` (detector runs on replies — `AddReplyAsync` path) | report row; widget DOM dump |
| R2-06-02 | `flag: absent from pointer get --json and pointer.sh get` | PR | cli | WA | 1. Temp repo: `.pointer/config.json` (project `e2e-alpha`), `credentials.env` with `keys.json.wsAdmin`; export `POINTER_SERVER=http://localhost:8090`, `POINTER_PROJECT=e2e-alpha`. 2. `spawnCli({ cwd: repo, args: ['get', String(F), '--json'] })`. 3. `installPointerSh(repo)`; run `bash .pointer/pointer.sh get <F>` (its token cache at `.pointer/.token_cache` is fresh). 4. Count `payloadFlag` occurrences (case-sensitive substring) in both outputs' stdout+stderr. | 2 → exit 0; the flag count is **0** and the parsed keys match `AiCommentView` (cross-ref R2-01-06 — the CLI prints the whitelist and never sends the header); 3 → exit 0, output **contains the comment body** (proves it actually fetched F) yet the flag count is **0**; 4 → both zero | report row; the two `grep -c payloadFlag` results (0 and 0) |
| R2-06-03 | header gate both ways + zero-flag AI surfaces (summary, apply-queue, MCP) | PR | api | QA, WA, DEV | Raw `fetch` throughout (substring semantics): 1. `GET /api/comments/{F}` headers `{ Authorization: Bearer <qa>, 'X-Pointer-Client': 'widget' }` → body text contains `"hasPayloadFlag":true` and `github_token`. 2. Same URL **without** the header → `payloadFlag` substring count 0 (keys absent via `WhenWritingNull`, not `null`-valued). 3. `GET /api/projects/e2e-alpha/comments?view=summary` (QA token, with **and** without the header) → count 0 both times (fields do not exist on `CommentSummaryDto` — DTO-shape guarantee #1). 4. `GET /api/admin/projects/e2e-alpha/apply-queue?status=2` (WA token) → count 0; items' embedded replies are `ApplyReplyDto` (`authorName, body, createdAt`) — assert a reply-bearing queued item also yields 0. 5. List DTO gate: `GET /api/projects/e2e-alpha/comments` with `X-Pointer-Client: widget` (QA) → the flagged item's JSON contains `"hasPayloadFlag":true` (`CommentListItemDto` honours the same gate). 6. MCP: `connectMcp({ cwd: mcpRepo })` (DEV key) → `callTool('pointer_get_comment', { id: F })` → serialized result `payloadFlag` count 0 (cross-ref R2-02-03). | 1 → true + pattern name present; 2/3/4/6 → **0** substrings each; 5 → true. The header is forgeable by design — the guarantee being asserted is that the *documented AI paths* (summary, apply-queue, CLI, MCP, legacy `pointer.sh`) never see it, stacked with the DTO/projection rules | report row; per-surface `grep -c payloadFlag` list (all 0, header case > 0) |
| R2-06-04 | `flag: edit removes secret → flag cleared on reload` | PR | api + widget | QA | 1. QA `PUT /api/comments/{F}` `{ body: 'safe text now, token removed' }` → 200. 2. `GET /api/comments/{F}` with `X-Pointer-Client: widget` → raw text: no `hasPayloadFlag` key at all, no `github_token`. 3. Widget: `page.reload()` → sidebar re-renders; `widget.locator('.pf-card[data-id="<F>"] .pf-pill.pf-flag')` → `toHaveCount(0)`. 4. Re-add via edit: `PUT` body `rotate again ghp_<new 36 chars>` → `GET` with header → `"hasPayloadFlag":true` again (edit recomputes, not create-only). | 1–3 → flag cleared end-to-end; 4 → flag returns on re-edit | report row; before/after raw JSON snippets |

## Spec files

- `e2e/widget/secrets-flag.spec.ts` — R2-06-01, R2-06-04 steps 3–4 (reuses `preAuthWidget`, `waitForResponse('**/capture-config')`).
- `e2e/api/secrets-flag.spec.mjs` — R2-06-03, R2-06-04 steps 1–2, 4 (raw `fetch`; the `headers` option of `lib/api.mjs` for the gated calls).
- `e2e/cli/secrets-flag.spec.mjs` — R2-06-02 (`spawnCli` + `installPointerSh`).
- `lib/mcp.mjs` — consumed by R2-06-03 step 6 (defined in R2-02-tests).

## Not covered here

- `Tests/PayloadFlagDetectorTests.cs` — the full pattern matrix (one positive + one negative per pattern, 5 KB clean comment, regex-timeout → empty + caller-side warning).
- `Tests/PayloadFlagExposureTests.cs` — the unit-level mirror of R2-06-03 (incl. the `UpdateStatusAsync` reply-flagging cell) at xUnit speed.
- `cli/test` projection/tools flag assertions (R2-01 `projection.test.ts`, R2-02 `tools.test.ts` — mocked-API variants of steps 2/6).
- Dashboard badge + "flagged" filter chip + interceptor header → dashboard repo (`DASH-R2-06-01…`).
- Element snapshots / page-context scanning — explicitly out of scope of R2-06 (body + replies only).

## Flake notes

- Substring counts (`payloadFlag`, `hasPayloadFlag`) must be computed on **raw response text**; `lib/api.mjs`'s parsed envelope turns absent keys into `undefined` and would silently pass a leak.
- Fake tokens only; if a canary ever matches a real provider's live key rotation window, regenerate — never redact a real secret into a spec.
- R2-06-01's flagged comment lives on `e2e-alpha` — later specs that count `e2e-alpha` items (e.g. R2-04's widget chain) must filter by id, not by index.
- `pointer.sh get` caches its JWT in `.pointer/.token_cache` — the helper uses a fresh temp repo so a stale token from another scenario can't produce a 401 misread as "no flag".
