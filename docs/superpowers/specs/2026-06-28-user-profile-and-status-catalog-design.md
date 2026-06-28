# User Profile & Status Catalog — Design

Date: 2026-06-28
Status: Approved design, pending implementation plan

## Summary

Two related features:

1. **User Profile** — a per-user activity view in the dashboard: the projects a
   user is involved in, with their comment counts broken down by status
   (Open / Ready-to-apply / Applied / Archived), reply counts, and an
   environment split, plus a grand-total headline. Admins can view any user's
   profile; each user can view their own.
2. **Status Catalog** — a single backend-defined source of truth for the comment
   statuses (value, name, label, color, order), served by `GET /api/statuses`
   and consumed by every client (widget + all three dashboards) so labels,
   colors, and order live in one place.

No database migration is required for either feature — all underlying fields
already exist.

## Background / current state

- `Comment` has `AuthorId` (Guid → User), `ProjectId`, `Environment`
  (`EnvironmentTag`: Local=1, Staging=2, Production=3), and `Status`
  (`CommentStatus`: Open=1, ReadyToApply=2, Applied=3, Archived=4).
- `Reply` has `CommentId`, `AuthorId`, `Body` — **no status, no direct project or
  environment** (both are reached by joining through `Comment`).
- The dashboard is **admin-only** today: everything under `/api/admin/*` requires
  the `is_admin` claim (`Policies.Admin`). `/api/auth/login` works for any user;
  `/api/me/*` is plain `[Authorize]`.
- Statuses are **hardcoded and inconsistent** across apps. The widget's
  `constants.ts` maps `ReadyToApply→"pending"` and `Applied→"completed"`; the
  API's `StatsResponse` uses `Pending`/`Completed`; the enum says
  `ReadyToApply`/`Applied`. Three vocabularies for the same four states.
- Existing `StatsService` already demonstrates the grouped-count query pattern to
  mirror.

## Feature 1 — User Profile

### Audience & access model

Both audiences are supported ("Both"):

- **Admin views anyone.** A "View profile" action on each row of the existing
  admin Users list opens that user's profile.
- **Each user views self.** A new lightweight access tier opens the dashboard to
  non-admins: on login the client reads the `is_admin` claim. Admins get the full
  dashboard as today; non-admins are routed straight to their own `/profile` and
  blocked (route guard) from every admin route. Self-service needs no admin
  endpoint because the "my profile" call is plain `[Authorize]`.

### API contract

Two endpoints over one shared `IProfileService`:

- `GET /api/me/profile` — `[Authorize]`, any authenticated user. Derives `userId`
  from the JWT. Lives in `MeController`.
- `GET /api/admin/users/{id}/profile` — `[Authorize(Policy = Admin)]`, admin views
  any user. Lives in `Admin/UsersController`.

Response shape (`Result<UserProfileResponse>` envelope, matching `StatsResponse`
style):

```
user:     { id, displayName, email, roleName }
totals:   { projectsInvolved, comments, replies, open, readyToApply, applied, archived }
projects: [{
   projectId, key, name, isActive,
   comments, replies, open, readyToApply, applied, archived,
   environments: [{ environment, comments, replies, open, readyToApply, applied, archived }]
}]
```

### Query approach

Mirror `StatsService`'s grouped-count technique, filtered by author:

- **Comments**: group by `(ProjectId, Environment, Status)` where
  `AuthorId == userId && DeletedAt == null`.
- **Replies**: group by `(Comment.ProjectId, Comment.Environment)` where
  `Reply.AuthorId == userId` (joined through `Comment`, respecting soft-delete).
- "Projects involved in" = the distinct set of projects appearing in either
  group.

**Replies have no status.** The status breakdown (open/readyToApply/applied/
archived) is therefore **comments-only**; replies appear as a separate count at
every level (environment, project, grand total). The UI labels reply counts
distinctly so they are not mistaken for a status bucket.

### Dashboard UI (built for angular, react, vue)

- **Headline**: projects-involved, total comments, total replies, and an overall
  status split (bar/donut), reusing each stack's existing chart/card components.
- **Per-project**: a card/row per project with its status breakdown + reply count
  + project total, **expandable to reveal the environment split**
  (Local/Staging/Production).
- One profile component, reached two ways: non-admin lands on it as their own
  page (`/api/me/profile`); admin opens it for any user from the Users list
  (`/api/admin/users/{id}/profile`).

## Feature 2 — Status Catalog (single source of truth)

### Endpoint

`GET /api/statuses` — public (no auth, consistent with the widget's other open
calls), cacheable. Returns the canonical, ordered list:

```
[ { value: 1, name: "Open",         label: "Open",      color: "#…", order: 1 },
  { value: 2, name: "ReadyToApply", label: "Ready",     color: "#…", order: 2 },
  { value: 3, name: "Applied",      label: "Completed", color: "#…", order: 3 },
  { value: 4, name: "Archived",     label: "Archived",  color: "#…", order: 4 } ]
```

### Source of truth

The catalog is defined in **one place in the API** (e.g. a `StatusCatalog`
definition keyed off `CommentStatus`) and served by the endpoint. Renaming,
recoloring, or reordering a status = edit that one definition and deploy the API
once; all clients reflect the change on next load with **no client redeploy**.

### Honest limitation (intentional)

The four statuses carry behavior, not just labels (`ReadyToApply` is the CLI's
pull queue; `Applied` is apply-workflow done; etc.). Therefore:

- Renaming / recoloring / reordering an existing status → fully data-driven. ✅
- Adding a genuinely new status → still requires backend code for its behavior
  (legal transitions, whether it counts as "pending" for the CLI, what the apply
  flow does). The catalog makes a new status render everywhere automatically, but
  its behavior cannot be pure data. This is inherent, not a limitation of the
  design.

### Client consumption

- **Widget**: `constants.ts` stops hardcoding labels and filter chips; it fetches
  the catalog on init and derives status strings, filter chips, and colors from
  it. (Keeps a minimal built-in fallback so the toolbar still renders if the
  catalog fetch fails.)
- **Dashboards (×3)**: status columns, badges, and the new profile breakdown read
  labels/colors/order from the catalog instead of local constants.

This also resolves the existing naming inconsistency: all apps converge on the
catalog's vocabulary.

## Cross-cutting plumbing

- New endpoints → regenerate the orval clients, auto-publish bumped
  `@moamen-ui/pointer-{angular,react,vue}`, dashboards consume the new generated
  hooks/services.
- **No DB migration** — all fields already exist.
- Dashboard work is built for **all three frameworks**, in parallel via
  subagents, per the dashboard multi-client rule.
- **No deployment** will be performed unless the user explicitly requests it.

## Implementation order (high level)

1. API: `GET /api/statuses` + `StatusCatalog` definition.
2. API: `IProfileService` + `GET /api/me/profile` + `GET /api/admin/users/{id}/profile`.
3. Regenerate + publish clients.
4. Widget: consume the status catalog (replace hardcoded constants).
5. Dashboards (×3): non-admin access tier + profile page + catalog-driven status
   rendering.

## Out of scope (roadmap)

- DB-backed / admin-CRUD statuses (runtime add/rename without deploy).
- Time-series / activity-over-time charts; CSV export.
- A widget "my activity on this project" mini card.
