# R3-04 — DOM-snapshot privacy (drop input values, `data-snapshot-mask`, per-project text toggle)

(§33-lite · Release 3 · 2–3 days)

## Goal

Every comment silently ships a DOM snapshot — the clicked element's own tag with its attributes
(≤ 120 chars each) and ≤ 160 chars of `textContent` (`web-component/src/capture.ts:128-146`). That can
carry a customer's name in a table row or a typed `value="…"`. After this item: form values are never
captured, host developers can mask any subtree once with `data-snapshot-mask`, and a project can opt
out of text capture entirely (selector, classes and source path still work for the AI). Screenshots
are untouched (opt-in per comment already; the full §33 image policy is a later item).

## Out of scope

- Screenshot consent notice, blur-inputs toggle, image deletion UI, retention job on `RetentionDays` (§33 full — held).
- Masking inside screenshots.
- Redacting `PageContextSnapshot` (console/network capture, bug reports) — separate follow-up; note it in `pointer-init.md`.
- Server-side redaction of historical comments.

## Prerequisites

- **R3-03 §G** (vitest + jsdom harness in `web-component/`) — the widget unit tests below need it; if R3-03 is not merged yet, add the harness here exactly as R3-03 §G specifies and note it in the report.
- Independent of the CLI.
- Facts: snapshot builder `shallowSnapshot(el)` (`capture.ts:134-146`) is called from `captureMetadata(el, sourceAttr)` (`capture.ts:200-202`); `Meta.snapshot` → `ElementCapture.Snapshot` (`Domain/ValueObjects/ElementCapture.cs`, `Snapshot`, `Classes`, `ComputedStyles`, `AppliedCssRules`, `SourcePath`, `ParentInfo`, `PageUrl`, `Route`, `PageTitle`, …); the widget reads project settings once at boot via `GET /api/projects/{key}/capture-config` (`API/Controllers/CaptureConfigController.cs:20`, `ProjectService.GetCaptureConfigAsync` `ProjectService.cs:674-690`, DTO `Application/DTOs/Project/CaptureConfigResponse.cs` `{ Id, PageContextCaptureEnabled, Name, ShowEnvironmentSelector, CommitStyle, CanEditSettings }`) and stores flags on the element (`element.ts:64,485-500`); project settings are updated via `PATCH /api/admin/projects/{id}` `UpdateProjectRequest` (`ProjectService.UpdateAsync` `ProjectService.cs:~200-215`, admin/creator gate). Attribute name `data-snapshot-mask` is frozen (R1-01).

## Design

### A. Widget capture rules (always on, no configuration)

In `shallowSnapshot` (`capture.ts:134`):
1. **Form values**: for `input`, `textarea`, `select`, `option` — **always drop the `value` attribute** and emit no text content; for `input` keep `type`, `name`, `id`, `placeholder`, `aria-*`, `data-*` (except `data-snapshot-mask`). Emit `value="•••"` only when the element had a **non-empty DOM value property** (`(el as HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement).value !== ''` — typed values live in the property, not the attribute; reading the attribute would miss every user-typed value), so the AI still knows a value existed. `textarea`/`select` text → `•••`. (`class` and `style` are already excluded from every snapshot — `capture.ts:137` — do not re-add them.)
2. **`data-snapshot-mask`**: if the element **or any ancestor** has the attribute (`el.closest('[data-snapshot-mask]')`), the text content becomes `•••`; attribute **names** are all kept, and attribute **values** are replaced by `•••` except for `id`, `type`, `role` and `aria-*` (structural anchors). **Inside a masked subtree `data-*` values also become `•••`** — `data-customer-name="Jane"` is exactly what a mask is added for; only the names (`data-customer-name`) survive so selectors/AI anchors still work. This rule is **client-side only**: the server never sees the host DOM, so `data-snapshot-mask` requires a current widget build; the server guarantees only §C.
3. **Sensitive attribute names** are always dropped regardless (attribute names lower-cased first): exact `value` (per rule 1), `data-value`, `data-email`, `data-token`, `data-secret`, `authorization`, `srcdoc`, plus any name with the **prefix `data-user`** (`^data-user` — matches `data-user`, `data-username`, `data-user-id`).
4. `parentInfo` needs **no masking** — it carries only `tag`, `classes`, `id` (`capture.ts:232-240`), never text. `pageTitle`: if `document.documentElement` has `data-snapshot-mask`, `pageTitle` → `•••`.
5. Selector generation (`dom.ts generateSelector`) is unchanged — it uses ids/classes/nth-child, never text; verify with a test that a masked element still yields a selector.
6. **Escaping**: `shallowSnapshot` currently emits attribute values unescaped (`capture.ts:139-141`); a value containing `"` corrupts the single-tag string the server sanitizer parses. Escape `"` as `&quot;` (and `<` as `&lt;`) in attribute values before emitting.

### B. Per-project toggle: `CaptureTextContent` (default `true`)

- `Project.CaptureTextContent: bool = true` (additive column). When `false`, the widget emits **no text content** in the snapshot for any element (attributes still captured under rules A.1–A.3) and sets `pageTitle` to `•••`.
- Exposed in `CaptureConfigResponse.CaptureTextContent` and settable via `UpdateProjectRequest.CaptureTextContent?` (same admin/creator gate).
- Widget: read once at boot with the other capture-config flags (`element.ts:485-500`), pass to `captureMetadata(el, sourceAttr, { captureText })`.

### C. Server-side safety net (defence in depth)

`CommentService.CreateAsync` (`CommentService.cs:~85-115`) — **create path only**: `PUT /api/comments/{id}` (`EditCommentRequest { Body, RemoveScreenshot }`, `CommentService.EditAsync` `:529-561`) never receives a snapshot and never loads the project, so there is nothing to sanitize there. After validation, run `SnapshotSanitizer.Sanitize(element.Snapshot, captureText)`:
- strip `value="…"` on `input|textarea|select|option` tags (regex on the single-tag snapshot, case-insensitive), replace with `value="•••"` when non-empty;
- strip attributes in the sensitive list (A.3);
- if the project has `CaptureTextContent == false`, remove inner text (`>…<` between the open and close tag) → `>•••<`;
- **malformed input** (odd number of `"` in the opening tag, no closing `>`, length > 4 000) → return the input **unchanged** (never throw, never truncate silently) and log at Debug; the widget's escaping (A.6) makes this the exception path.
Applied to `POST /api/projects/{key}/comments` only; idempotent. Old widget versions therefore cannot bypass rules C.1–C.3 — but they *can* bypass A.2 (mask attribute) and A.6, which is why those are documented as client-side guarantees.

### D. Docs & discoverability

- `pointer-init.md`: new "Privacy: what the widget captures" subsection — the 160/120-char snapshot, form values never captured, `data-snapshot-mask` usage (`<table data-snapshot-mask>`), project toggle, screenshots opt-in per comment, pointer to the public privacy page (R3-05).
- Dashboard project settings: checkbox "Capture element text in comments" (default on) with help text.
- `pointer doctor`: warns when the app's `index.html`/root layout has no `data-snapshot-mask` and the project handles personal data? **Decision:** no — not detectable; skip.

## Tasks

1. `web-component/src/capture.ts` — implement §A in `shallowSnapshot` (new helpers `maskAttrValue`, `isFormValueTag`, `isMasked(el)`, `escapeAttr`), thread `{ captureText }` through `captureMetadata`; `pageTitle` masking where it is set (grep `pageTitle` in `capture.ts`/`element.ts`).
2. `web-component/src/element.ts` — store `captureTextContent` from capture-config (default `true` until resolved), pass to `captureMetadata`. `web-component/src/types.ts` — extend `Meta` call signature if needed. `npm run build`; commit artifacts.
3. `Domain/Entity/Project.cs` — `CaptureTextContent` (default `true`). `Infrastructure/Mappings/ProjectMapping.cs` — default value. `just migrate name="AddProjectCaptureTextContent"`.
4. `Application/DTOs/Project/CaptureConfigResponse.cs`, `UpdateProjectRequest.cs`, `ProjectResponse.cs` — `CaptureTextContent`; `ProjectService.GetCaptureConfigAsync` (`:674-690`) select + map; `ProjectService.UpdateAsync` (`:~208`) apply when `HasValue`.
5. `Application/Common/SnapshotSanitizer.cs` (static, pure) — §C; call from `CommentService.CreateAsync` only; it needs the project's `CaptureTextContent` — `CreateAsync` already resolves the project via `EnsureAsync` (`CommentService.cs:48`) and loads it for entitlements, so extend that load rather than re-querying. (`EditAsync` does not load the project and has no snapshot — leave it untouched.)
6. `API/wwwroot/pointer-init.md` — privacy subsection (§D). `API/wwwroot/skill.md` — one line in Step 4: "`•••` in a snapshot means masked content; do not ask the author to reveal it".
7. Dashboard tasks (below).

## Dashboard tasks

- Regenerate services (`ProjectResponse`, `UpdateProjectRequest`, `CaptureConfigResponse` gain `captureTextContent`).
- Project settings form: checkbox "Capture element text in comments" bound to `captureTextContent`, only enabled when `canEdit`; help text linking to the privacy page.

## Tests

- **Unit (widget, vitest + jsdom — harness from R3-03 §G):** `snapshot-privacy.test.ts` — `<input>` with a **typed** value (set via the `.value` property, no attribute) → `value="•••"`; `<input value="x">` with the property cleared → attribute dropped; attribute value containing `"` is emitted as `&quot;`; `data-customer-name` inside a masked subtree → `data-customer-name="•••"`; `data-username` dropped everywhere; `<textarea>` text masked; `<select>` options not leaked; element inside `<div data-snapshot-mask>` → text `•••`, structural attrs kept, `data-email` dropped; `captureText=false` → no text for any element, attrs intact; selector still generated for a masked element; unmasked `<button>Save</button>` unchanged (regression).
- **Unit (API, xUnit):** `SnapshotSanitizerTests` — the same matrix on raw snapshot strings, idempotency, `captureText=false` path, malformed snapshot (odd quotes / no `>` / oversized) passes through unchanged without throwing. `ProjectCaptureTextContentTests` — default true; PATCH by admin/creator toggles; PATCH by other user → 403; `capture-config` reflects it.
- **E2E scenarios:** `snapshot-no-form-values` (fill the fixture signup form, comment on the email input → stored `element.snapshot` contains `•••`, not the typed email), `snapshot-mask-attribute` (comment on a cell inside `<table data-snapshot-mask>` → text masked, selector present), `project-no-text-capture` (toggle off in dashboard/API → next comment's snapshot has no text; `pageTitle` masked), `legacy-widget-sanitized` (POST a raw comment with `value="x"` via the API → stored as `•••`).

## Acceptance criteria

- [ ] No comment created by the widget contains an `input/textarea/select` value; existing tests in `web-component`/`Tests` still pass.
- [ ] `data-snapshot-mask` on an ancestor masks text and all non-structural attribute values including `data-*` values; selector, classes, computed styles, source path unaffected.
- [ ] A typed (property-only) input value never reaches the server; an attribute value containing `"` round-trips as `&quot;` and the server sanitizer still parses the tag.
- [ ] `CaptureTextContent=false` removes all snapshot text and `pageTitle`; toggle is admin/creator-only and visible in the dashboard.
- [ ] Server sanitizer rewrites a raw API POST carrying `value="x"` to `•••` (defence in depth verified).
- [ ] `pointer-init.md` documents the capture surface and the mask attribute; `skill.md` explains `•••`.
- [ ] `just test`, widget build, dashboard regen green.

## Rollout / compatibility

- Additive column with default `true` → no behaviour change for existing projects except form values (always masked from now on — intended).
- Historical snapshots are not rewritten (document; a one-off admin script is a possible follow-up).
- Older cached widget copies are covered by the server sanitizer (§C) for form values, the sensitive-name list and the text toggle — **not** for `data-snapshot-mask`, which is a client-side guarantee (stated in `pointer-init.md`).
- AI apply quality: masked text removes one anchor; `selector`, `classes`, `appliedCssRules`, `sourcePath` remain — acceptable and stated in `skill.md`.

## Report template

```
Branch: feat/r3-04-snapshot-privacy
Files: …
widget: typecheck/test/build → … (artifacts committed)
api: just fmt / build / test → … (SnapshotSanitizerTests, ProjectCaptureTextContentTests)
E2E: snapshot-no-form-values ✓ snapshot-mask-attribute ✓ project-no-text-capture ✓ legacy-widget-sanitized ✓
Dashboard: regenerated + settings checkbox / follow-up
Skipped / open: …
```
