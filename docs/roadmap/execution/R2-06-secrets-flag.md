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
- None hard; R2-01's `projection.ts` and R2-02's `partitionItem` must honour the exposure rules below
  (they reference this doc). Facts: comment body is set at `CommentService.CreateAsync`
  (`CommentService.cs:111`) and `EditAsync` (`:546`); reply body at `UpdateStatusAsync` (`:512`) and
  `CommentService.AddReplyAsync` (`:589`);
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
Two guarantees stack: (1) **DTO shape** — AI-facing DTOs never carry the fields; (2) **caller
gating** on the shared detail DTO — `CommentResponse` is consumed by humans (dashboard, widget) *and*
by AI paths (`pointer.sh get <id>` → `GET /api/comments/{id}`, `API/wwwroot/pointer.sh:131`; the CLI
`get` and MCP `get_comment` before projection). The legacy `pointer.sh` copies already installed in
customer repos cannot be fixed client-side, so the server gates the fields itself.

**Decision — `X-Pointer-Client` header gate:** `CommentResponse.HasPayloadFlag`/`PayloadFlags` and
`ReplyResponse.HasPayloadFlag`/`PayloadFlags` are populated **only when the request carries the header
`X-Pointer-Client: dashboard` or `X-Pointer-Client: widget`**; for every other caller they are
serialised as `null` (nullable `bool?`/`List<string>?`, `JsonIgnore(Condition = WhenWritingNull)` so
the keys are absent, not `null`). The widget (`web-component/src/element.ts` `api()` helper) and the
dashboard's HTTP interceptor send the header on every request. An AI tool that copies a curl from a
skill never sends it. This is not an auth boundary (the header is trivially forgeable) — it is the
guarantee that *the documented AI paths* never see the flag, plus the projection guarantees below.

| Surface | gets `hasPayloadFlag`, `payloadFlags`? |
|---|---|
| `CommentResponse` (detail) | **only with `X-Pointer-Client: dashboard\|widget`**; otherwise keys absent |
| `CommentListItemDto` (widget list) | same header gate |
| `ReplyResponse` (embedded in the two above) | same header gate |
| `CommentSummaryDto` (`?view=summary`, AI/CLI) | **no** (fields do not exist on the DTO) |
| `CommentApplyItemDto` (apply-queue, AI/CLI/MCP) | **no** — its embedded replies become `ApplyReplyDto { AuthorName, Body, CreatedAt }` (today it embeds `List<ReplyResponse>`, `CommentApplyItemDto.cs:26`) |
| CLI `pointer get <id>` (R2-01) | **no** — prints `AiCommentView` (R2-01 `projection.ts`), a whitelist that never includes the fields, and does not send the header |
| MCP `pointer_get_comment` / `pointer_get_queue` (R2-02) | **no** — same projection; `partitionItem` never copies these fields; asserted in R2-02 `tools.test.ts` |
| Legacy `pointer.sh get <id>` | **no** — header not sent → keys absent |

### UI
- Widget card (`web-component/src/templates.ts` `card()`): when `c.hasPayloadFlag`, a pill
  `⚠ contains a secret/payload?` with `title` listing `payloadFlags` joined by `, `; same on replies.
  Muted colour (`--pf-warn`), no behaviour change.
- Dashboard: same badge in list and detail; a filter chip "flagged" (client-side).

## Tasks
1. `Application/Common/PayloadFlagDetector.cs` + the pattern table as `static readonly (string Name, Regex Rx)[]`.
2. `Domain/Entity/Comment.cs`, `Reply.cs` — the two columns each; mappings (`Infrastructure/Mappings/CommentMapping.cs`, `ReplyMapping.cs`). **There is no reusable converter**: `PickedActions` is an owned JSON collection (`b.OwnsMany(x => x.PickedActions, a => a.ToJson("picked_actions"))`, `CommentMapping.cs:66`). For `List<string> PayloadFlags` use EF Core 8's **primitive-collection** mapping — `e.Property(x => x.PayloadFlags).HasColumnType("jsonb")` (Npgsql maps `List<string>` to jsonb natively) — or, if the provider version rejects it, `.HasConversion(v => JsonSerializer.Serialize(v), v => JsonSerializer.Deserialize<List<string>>(v) ?? new())` with a `ValueComparer<List<string>>`. `just migrate name="AddPayloadFlags"`.
3. `CommentService.CreateAsync` (`:111`), `EditAsync` (`:546`), `UpdateStatusAsync` reply (`:512`), `AddReplyAsync` (`:589`) — call the detector and set both fields.
4. DTOs: add **nullable** `HasPayloadFlag`/`PayloadFlags` to `CommentResponse`, `CommentListItemDto`, `ReplyResponse` with `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]`; **do not** touch `CommentSummaryDto`; replace `CommentApplyItemDto.Replies` (`CommentApplyItemDto.cs:26`, `List<ReplyResponse>`) with `List<ApplyReplyDto { AuthorName, Body, CreatedAt }>`.
4b. `Application/Common/ClientKind.cs` — `IClientKindAccessor` reading `X-Pointer-Client` from `IHttpContextAccessor` (`dashboard` | `widget` | `other`); mappers populate the flag fields only for `dashboard`/`widget`.
5. Mappers in `CommentService` (`MapToResponse`, list/summary/apply projections) — gate per 4b.
6. Widget: `types.ts` (`hasPayloadFlag?`, `payloadFlags?` on `Comment` and `Reply`), `element.ts` `api()` adds `X-Pointer-Client: widget` to every request, `templates.ts` pill, `styles/_variables.scss` `--pf-warn`; `npm run build`; commit artifacts.
6b. Dashboard: HTTP interceptor adds `X-Pointer-Client: dashboard` (see Dashboard tasks).
7. `skill.md` — one sentence under SECURITY-adjacent notes: "The server may flag secret-shaped text for human reviewers; that flag is intentionally not exposed to you." (Does not alter the SECURITY section text itself — place it after the section.)

## Dashboard tasks
Regenerate for `CommentResponse`, `CommentListItemDto`, `ReplyResponse` (+ `ApplyReplyDto` if the admin apply-queue view is rendered there). Add `X-Pointer-Client: dashboard` to the existing envelope-unwrapping HTTP interceptor (the one described in `docs/skills/orval-codegen/SKILL.md`). UI: badge + "flagged" filter.

## Tests
- `Tests/PayloadFlagDetectorTests.cs`: one positive and one negative per pattern; a 5 KB normal comment matches nothing; a regex timeout does not throw — the detector returns empty and **the caller** (`CommentService`) logs a warning (the detector is a pure static class with no logger).
- `Tests/PayloadFlagExposureTests.cs`: create a comment with `sk-…` → with header `X-Pointer-Client: widget`, `GET /api/comments/{id}` has `hasPayloadFlag=true`; **without the header the serialized JSON contains no `payloadFlag` substring**; `GET …/comments?view=summary` JSON contains none; `GET …/apply-queue` (needs an **admin** key — the endpoint is `[Authorize(Policy="Admin")]`, `Admin/ProjectsController.cs:99-108`) JSON contains none; reply flagged via `UpdateStatusAsync` path; edit that removes the secret clears the flag.
- R2-01 `projection.test.ts` and R2-02 `tools.test.ts` — assert CLI/MCP outputs contain no `payloadFlag` keys even when the API is mocked to return them.
- E2E scenarios `flag: secret in comment shows badge in widget`, `flag: absent from pointer get --json and pointer.sh get`.

## Acceptance criteria
- [ ] A comment containing `ghp_` + 36 chars shows the ⚠ pill in the widget with `github_token` in its tooltip.
- [ ] `curl -H "X-Pointer-Client: widget" …/comments/{id}` contains `"hasPayloadFlag":true`; the same curl **without** the header contains the substring `payloadFlag` zero times.
- [ ] `curl …/comments?view=summary` and `…/apply-queue` (admin key) responses contain the literal substring `payloadFlag` **zero** times.
- [ ] `./.pointer/pointer.sh get <id>` output contains `payloadFlag` zero times.
- [ ] Editing the comment to remove the token clears the pill on reload.
- [ ] Detector runs on replies too (reply with a `<script>` tag is flagged).
- [ ] `just test` green; widget build green; artifacts committed.

## Rollout / compatibility
Additive migration; existing rows default to unflagged (no backfill — Decision: not worth a scan; flags appear on next edit).

## Report template
Files; migration name; detector test matrix output; the `grep -c payloadFlag` results for summary, apply-queue, header-less detail and `pointer.sh get` (all must be 0) plus the with-header detail (must be > 0); widget screenshot/DOM dump.
