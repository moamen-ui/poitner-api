# Admin-Managed Status Presentation — Design

Date: 2026-06-29
Status: Approved (extends the status-catalog feature)

## Summary

Let an admin edit comment-status **presentation** (label / color / order) from the
dashboard at runtime, with no redeploy. The `CommentStatus` enum remains the
behavior-bearing identity; a new `status_presentations` table stores per-status
**overrides**. `GET /api/statuses` merges code defaults + overrides, so the widget
and all three dashboards reflect changes on next load. Admin endpoints + an
admin-only dashboard page manage the overrides, including reset-to-default.

Presentation-only: adding/removing statuses is still out of scope (a new status
needs backend behavior). This edits the existing four.

## Data model

New entity `StatusPresentation : BaseEntity` (table `status_presentations`,
snake_case columns per the existing mapping convention, applied automatically by
`ApplyConfigurationsFromAssembly`):

- `StatusValue` (int, column `status_value`, UNIQUE) — the `CommentStatus` int.
- `Label` (string?, `label`, nullable) — override; null = use code default.
- `Color` (string?, `color`, nullable) — override; null = use code default.
- `DisplayOrder` (int?, `display_order`, nullable) — override; null = use code default.

A row exists only when at least one field is overridden. Reset = delete the row.
Migration `AddStatusPresentations` (auto-applied on API startup, like existing
migrations).

## API

The existing code defaults stay in `StatusCatalogService` as the fallback source.

**Public (changed):** `GET /api/statuses` — now merges defaults with DB
overrides (override wins per-field), returns the effective list ordered by
effective order. `StatusCatalogService.GetAll()` becomes async and reads the
`status_presentations` table via `IUnitOfWork`. `StatusesController.Get()` becomes
async. Response shape unchanged (`StatusItem[]`).

**Admin (new, `[Authorize(Policy = Admin)]`, tag `Statuses` so orval picks it up):**
- `GET /api/admin/statuses` → `StatusAdminItem[]`:
  `{ value, name, defaultLabel, defaultColor, defaultOrder, label, color, order, isOverridden }`
  where `label/color/order` are the effective values and `default*` are the code
  defaults (so the page can show current vs default and enable Reset).
- `PATCH /api/admin/statuses/{value:int}` body `{ label?, color?, order? }` →
  upsert the override row for that status value. Validates: `value` is a known
  `CommentStatus`; `color` matches `^#[0-9a-fA-F]{6}$` when provided; `label`
  non-empty and ≤ 64 chars when provided; `order` ≥ 0 when provided.
- `DELETE /api/admin/statuses/{value:int}` → delete the override row (reset to
  default). 404 if value unknown.

New `IStatusAdminService`/`StatusAdminService` (auto-registered by the Scrutor
`*Service` scan). Validators via FluentValidation (auto-registered).

## Clients

New admin endpoints → regenerate orval clients, republish `@moamen-ui/pointer-*`
(auto-bump to the next patch, e.g. 1.0.6). The publish workflow generates from the
deployed API, so the API must be deployed before publishing (the user has approved
deploying at the end).

## Dashboard (all three frameworks)

A new admin-only **"Statuses"** nav item / route opening a Statuses admin page:
- Lists the four statuses (from `GET /api/admin/statuses`), each row editable:
  **label** (text), **color** (color picker + hex), **order** (number).
- **Save** persists via `PATCH`; **Reset** calls `DELETE` and reverts the row to
  its `default*` values.
- After a successful Save/Reset, invalidate/refetch the status catalog query so
  badges/labels across the dashboard reflect the change immediately.
- Built consistently across Angular, React, Vue (parallel subagents), reusing each
  stack's existing form/table/toast patterns and the access-tier (admin-only) guard
  added in the prior feature.

## Deploy

After implementation + per-task review: merge both branches to `main`, deploy the
API (rebuild container on the VM), run the publish workflow, then build + deploy
all three dashboards (app-{angular,react,vue}.pointer.moamen.work).

## Out of scope

Adding/removing statuses from the UI; per-environment or per-project status
overrides; localized labels per language (labels are global strings).
