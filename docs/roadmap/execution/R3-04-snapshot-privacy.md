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

- None. Independent of the CLI.
- Facts: snapshot builder `shallowSnapshot(el)` (`capture.ts:134-146`) is called from `captureMetadata(el, sourceAttr)` (`capture.ts:200-202`); `Meta.snapshot` → `ElementCapture.Snapshot` (`Domain/ValueObjects/ElementCapture.cs`, `Snapshot`, `Classes`, `ComputedStyles`, `AppliedCssRules`, `SourcePath`, `ParentInfo`, `PageUrl`, `Route`, `PageTitle`, …); the widget reads project settings once at boot via `GET /api/projects/{key}/capture-config` (`API/Controllers/CaptureConfigController.cs:20`, `ProjectService.GetCaptureConfigAsync` `ProjectService.cs:674-690`, DTO `Application/DTOs/Project/CaptureConfigResponse.cs` `{ Id, PageContextCaptureEnabled, Name, ShowEnvironmentSelector, CommitStyle, CanEditSettings }`) and stores flags on the element (`element.ts:64,485-500`); project settings are updated via `PATCH /api/admin/projects/{id}` `UpdateProjectRequest` (`ProjectService.UpdateAsync` `ProjectService.cs:~200-215`, admin/creator gate). Attribute name `data-snapshot-mask` is frozen (R1-01).

## Design

### A. Widget capture rules (always on, no configuration)

In `shallowSnapshot` (`capture.ts:134`):
1. **Form values**: for `input`, `textarea`, `select`, `option` — drop the `value` attribute and emit no text content; for `input` keep `type`, `name`, `id`, `placeholder`, `aria-*`, `data-*` (except `data-snapshot-mask`). Emit `value="•••"` only when the element had a non-empty value, so the AI still knows a value existed. `textarea`/`select` text → `•••`.
2. **`data-snapshot-mask`**: if the element **or any ancestor** has the attribute (`el.closest('[data-snapshot-mask]')`), the text content becomes `•••` and attribute values other than `id`, `class`, `type`, `role`, `aria-*`, `data-*` (structural) are replaced by `•••`. Structural attrs stay so selectors/AI anchors survive.
3. **Sensitive attribute names** are always dropped regardless: `value` (per rule 1), `data-value`, `data-email`, `data-user*`, `data-token`, `data-secret`, `authorization`, `srcdoc`.
4. Also apply masking to **`parentInfo`** text (none today — it carries only tag/classes/id: `capture.ts:236-240`) and to `pageTitle`: if `document.documentElement` has `data-snapshot-mask`, `pageTitle` → `•••`.
5. Selector generation (`dom.ts generateSelector`) is unchanged — it uses ids/classes/nth-child, never text; verify with a test that a masked element still yields a selector.

### B. Per-project toggle: `CaptureTextContent` (default `true`)

- `Project.CaptureTextContent: bool = true` (additive column). When `false`, the widget emits **no text content** in the snapshot for any element (attributes still captured under rules A.1–A.3) and sets `pageTitle` to `•••`.
- Exposed in `CaptureConfigResponse.CaptureTextContent` and settable via `UpdateProjectRequest.CaptureTextContent?` (same admin/creator gate).
- Widget: read once at boot with the other capture-config flags (`element.ts:485-500`), pass to `captureMetadata(el, sourceAttr, { captureText })`.

### C. Server-side safety net (defence in depth)

`CommentService.CreateAsync` (`CommentService.cs:~85-115`): after validation, run `SnapshotSanitizer.Sanitize(element.Snapshot)`:
- strip `value="…"` on `input|textarea|select|option` tags (regex on the single-tag snapshot, case-insensitive), replace with `value="•••"` when non-empty;
- strip attributes in the sensitive list (A.3);
- if the project has `CaptureTextContent == false`, remove inner text (`>…<` between the open and close tag) → `>•••<`.
Applied to `POST /api/projects/{key}/comments` and `PUT /api/comments/{id}` (edit keeps the original snapshot; sanitizer is idempotent). Old widget versions therefore cannot bypass the rule.

### D. Docs & discoverability

- `pointer-init.md`: new "Privacy: what the widget captures" subsection — the 160/120-char snapshot, form values never captured, `data-snapshot-mask` usage (`<table data-snapshot-mask>`), project toggle, screenshots opt-in per comment, pointer to the public privacy page (R3-05).
- Dashboard project settings: checkbox "Capture element text in comments" (default on) with help text.
- `pointer doctor`: warns when the app's `index.html`/root layout has no `data-snapshot-mask` and the project handles personal data? **Decision:** no — not detectable; skip.

## Tasks

1. `web-component/src/capture.ts` — implement §A in `shallowSnapshot` (new helper `maskAttrValue`, `isFormValueTag`, `isMasked(el)`), thread `{ captureText }` through `captureMetadata`; `pageTitle` masking where it is set (grep `pageTitle` in `capture.ts`/`element.ts`).
2. `web-component/src/element.ts` — store `captureTextContent` from capture-config (default `true` until resolved), pass to `captureMetadata`. `web-component/src/types.ts` — extend `Meta` call signature if needed. `npm run build`; commit artifacts.
3. `Domain/Entity/Project.cs` — `CaptureTextContent` (default `true`). `Infrastructure/Mappings/ProjectMapping.cs` — default value. `just migrate name="AddProjectCaptureTextContent"`.
4. `Application/DTOs/Project/CaptureConfigResponse.cs`, `UpdateProjectRequest.cs`, `ProjectResponse.cs` — `CaptureTextContent`; `ProjectService.GetCaptureConfigAsync` (`:674-690`) select + map; `ProjectService.UpdateAsync` (`:~208`) apply when `HasValue`.
5. `Application/Common/SnapshotSanitizer.cs` (static, pure) — §C; call from `CommentService.CreateAsync` and the edit path; needs the project's `CaptureTextContent` (already loads the project for `EnsureAsync`/entitlements — reuse).
6. `API/wwwroot/pointer-init.md` — privacy subsection (§D). `API/wwwroot/skill.md` — one line in Step 4: "`•••` in a snapshot means masked content; do not ask the author to reveal it".
7. Dashboard tasks (below).

## Dashboard tasks

- Regenerate services (`ProjectResponse`, `UpdateProjectRequest`, `CaptureConfigResponse` gain `captureTextContent`).
- Project settings form: checkbox "Capture element text in comments" bound to `captureTextContent`, only enabled when `canEdit`; help text linking to the privacy page.

## Tests

- **Unit (widget, vitest + jsdom):** `snapshot-privacy.test.ts` — `<input value="secret">` → `value="•••"`, empty value → attribute dropped; `<textarea>` text masked; `<select>` options not leaked; element inside `<div data-snapshot-mask>` → text `•••`, structural attrs kept, `data-email` dropped; `captureText=false` → no text for any element, attrs intact; selector still generated for a masked element; unmasked `<button>Save</button>` unchanged (regression).
- **Unit (API, xUnit):** `SnapshotSanitizerTests` — the same matrix on raw snapshot strings, idempotency, `captureText=false` path, malformed snapshot passes through unchanged. `ProjectCaptureTextContentTests` — default true; PATCH by admin/creator toggles; PATCH by other user → 403; `capture-config` reflects it.
- **E2E scenarios:** `snapshot-no-form-values` (fill the fixture signup form, comment on the email input → stored `element.snapshot` contains `•••`, not the typed email), `snapshot-mask-attribute` (comment on a cell inside `<table data-snapshot-mask>` → text masked, selector present), `project-no-text-capture` (toggle off in dashboard/API → next comment's snapshot has no text; `pageTitle` masked), `legacy-widget-sanitized` (POST a raw comment with `value="x"` via the API → stored as `•••`).

## Acceptance criteria

- [ ] No comment created by the widget contains an `input/textarea/select` value; existing tests in `web-component`/`Tests` still pass.
- [ ] `data-snapshot-mask` on an ancestor masks text and non-structural attribute values; selector, classes, computed styles, source path unaffected.
- [ ] `CaptureTextContent=false` removes all snapshot text and `pageTitle`; toggle is admin/creator-only and visible in the dashboard.
- [ ] Server sanitizer rewrites a raw API POST carrying `value="x"` to `•••` (defence in depth verified).
- [ ] `pointer-init.md` documents the capture surface and the mask attribute; `skill.md` explains `•••`.
- [ ] `just test`, widget build, dashboard regen green.

## Rollout / compatibility

- Additive column with default `true` → no behaviour change for existing projects except form values (always masked from now on — intended).
- Historical snapshots are not rewritten (document; a one-off admin script is a possible follow-up).
- Older cached widget copies are covered by the server sanitizer (§C).
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
