# R2-06 — Secrets / payload advisory flag on comments  (S6 · Release 2 · 2 h)

## Goal
When a comment or reply contains something that looks like a credential or an executable payload
(an API key, a `<script>` tag, a `curl … | sh`), the card shows a small advisory badge so a reviewer
notices before an AI or a teammate acts on it. **Advisory only** — nothing is blocked, nothing is
rewritten, and the flag is **never** sent in the payloads AI tools consume (apply-queue, summary view,
MCP), so it can't itself become an injection surface. Replaces the cut §17 regex "injection scanner".

## Out of scope
- Blocking, redaction, or auto-rejecting comments. Detecting "instruction-like" text (explicitly
  rejected in review — too many false positives). Scanning element snapshots or page context.

## Prerequisites
- None. Facts: comment body is set at `CommentService.CreateAsync` (`CommentService.cs:111`) and
  `EditAsync` (`:546`); reply body at `UpdateStatusAsync` (`:512`) and the reply service (`:593`);
  DTOs `CommentResponse`, `CommentListItemDto` (widget/dashboard) vs `CommentSummaryDto`
  (`?view=summary`, AI) and `CommentApplyItemDto` (apply-queue, AI) — `00-API-INVENTORY.md` §3.

## Design

### Detector — `Application/Common/PayloadFlagDetector.cs` (pure, static)
`static IReadOnlyList<string> Detect(string text)` returns the **names** of matched patterns (empty = clean).
Patterns (compiled `Regex`, `RegexOptions.CultureInvariant`, 100 ms match timeout):
| name | regex (intent) |
|---|---|
| `openai_key` | `\bsk-[A-Za-z0-9_-]{20,}\b` |
| `aws_access_key` | `\bAKIA[0-9A-Z]{16}\b` |
| `github_token` | `\bgh[pousr]_[A-Za-z0-9]{36,}\b` |
| `pointer_key` | `\bptr_[A-Za-z0-9]{24,}\b` |
| `jwt` | `\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b` |
| `private_key_block` | `-----BEGIN [A-Z ]*PRIVATE KEY-----` |
| `long_base64` | `\b[A-Za-z0-9+/]{64,}={0,2}\b` (only when no whitespace inside) |
| `script_tag` | `<\s*script\b` (case-insensitive) |
| `pipe_to_shell` | `\b(curl\|wget)\b[^\n]{0,200}\|\s*(sh\|bash\|zsh)\b` |
| `password_assignment` | `\b(password\|passwd\|secret\|token)\s*[:=]\s*\S{8,}` (case-insensitive) |
Decision: the list is a constant; adding a pattern is a code change (no admin UI).

### Storage (additive columns)
- `Comment.HasPayloadFlag` (bool, default false), `Comment.PayloadFlags` (jsonb string array).
- `Reply.HasPayloadFlag`, `Reply.PayloadFlags` (same).
Computed server-side at create and edit (comment) and at create (reply — including the reply created by
`UpdateStatusAsync`). Never computed client-side.

### Exposure rules (the security-relevant part — tested)
| DTO | gets `hasPayloadFlag`, `payloadFlags`? |
|---|---|
| `CommentResponse` (widget/dashboard detail) | **yes** |
| `CommentListItemDto` (widget list) | **yes** |
| `ReplyResponse` | **yes** |
| `CommentSummaryDto` (`?view=summary`, AI/CLI) | **no** |
| `CommentApplyItemDto` (apply-queue, AI/CLI/MCP) | **no** (and its embedded `ReplyResponse` list is mapped through a projection that drops the two fields) |
| MCP tool results (R2-02) | **no** — R2-02's `partitionItem` must never copy these fields; add to its test |

### UI
- Widget card (`web-component/src/templates.ts` `card()`): when `c.hasPayloadFlag`, a pill
  `⚠ contains a secret/payload?` with `title` listing `payloadFlags` joined by `, `; same on replies.
  Muted colour (`--pf-warn`), no behaviour change.
- Dashboard: same badge in list and detail; a filter chip "flagged" (client-side).

## Tasks
1. `Application/Common/PayloadFlagDetector.cs` + the pattern table as `static readonly (string Name, Regex Rx)[]`.
2. `Domain/Entity/Comment.cs`, `Reply.cs` — the two columns each; mappings (`Infrastructure/Mappings/CommentMapping.cs`, `ReplyMapping.cs` — jsonb via the same converter used for `PickedActions`); `just migrate name="AddPayloadFlags"`.
3. `CommentService.CreateAsync` (`:111`), `EditAsync` (`:546`), `UpdateStatusAsync` reply (`:512`), reply service (`:593`) — call the detector and set both fields.
4. DTOs: add fields to `CommentResponse`, `CommentListItemDto`, `ReplyResponse`; **do not** touch `CommentSummaryDto`/`CommentApplyItemDto`; adjust the apply-queue reply projection to a new `ApplyReplyDto { AuthorName, Body, CreatedAt }` if the current mapping reuses `ReplyResponse` (check `CommentApplyItemDto.cs:26` — it does: `List<ReplyResponse> Replies`; replace with `ApplyReplyDto`).
5. Mappers in `CommentService` (`MapToResponse`, list/summary/apply projections).
6. Widget: `types.ts` (`hasPayloadFlag?`, `payloadFlags?` on `Comment` and `Reply`), `templates.ts` pill, `styles/_variables.scss` `--pf-warn`; `npm run build`; commit artifacts.
7. `skill.md` — one sentence under SECURITY-adjacent notes: "The server may flag secret-shaped text for human reviewers; that flag is intentionally not exposed to you." (Does not alter the SECURITY section text itself — place it after the section.)

## Dashboard tasks
Regenerate for `CommentResponse`, `CommentListItemDto`, `ReplyResponse` (+ `ApplyReplyDto` if the admin apply-queue view is rendered there). UI: badge + "flagged" filter.

## Tests
- `Tests/PayloadFlagDetectorTests.cs`: one positive and one negative per pattern; a 5 KB normal comment matches nothing; regex timeout does not throw (returns empty + logs).
- `Tests/PayloadFlagExposureTests.cs`: create a comment with `sk-…` → `CommentResponse.hasPayloadFlag=true`; `GET …/comments?view=summary` JSON contains **no** `payloadFlag` key (string search on serialized JSON); `GET …/apply-queue` JSON contains none; reply flagged via `UpdateStatusAsync` path; edit that removes the secret clears the flag.
- R2-02 `tools.test.ts` — assert MCP results contain no `payloadFlag` keys (add when R2-02 lands or now if it exists).
- E2E scenario `flag: secret in comment shows badge, absent from CLI list --json`.

## Acceptance criteria
- [ ] A comment containing `ghp_` + 36 chars shows the ⚠ pill in the widget with `github_token` in its tooltip.
- [ ] `curl …/comments?view=summary` and `…/apply-queue` responses contain the literal substring `payloadFlag` **zero** times.
- [ ] Editing the comment to remove the token clears the pill on reload.
- [ ] Detector runs on replies too (reply with a `<script>` tag is flagged).
- [ ] `just test` green; widget build green; artifacts committed.

## Rollout / compatibility
Additive migration; existing rows default to unflagged (no backfill — Decision: not worth a scan; flags appear on next edit).

## Report template
Files; migration name; detector test matrix output; the two `grep -c payloadFlag` results (must be 0); widget screenshot/DOM dump.
