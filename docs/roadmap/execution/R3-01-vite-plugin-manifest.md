# R3-01 — Vite source-stamp plugin, local manifest, deploy awareness

(Phase 4 + §31 · Release 3 · 1–2 weeks)

## Goal

In **production builds** (where React/Vue dev metadata is stripped) every element a stakeholder clicks
still resolves to the exact source file: a Vite plugin stamps each component's root host element(s)
with `data-component-source="<hash>"`, writes a **local, gitignored** `.pointer/manifest.json` mapping
hash → file, and the CLI/AI resolves the hash without grepping. The same plugin stamps
`data-build-sha` on `<html>` so a comment's status can move from *applied* to *deployed* once its
commit is actually live. Stakeholders see nothing new; the developer's apply gets cheaper and exact.

## Out of scope

- Angular builder plugin, Next.js/RSC plugin (follow-ups: `R3-01b-angular`, `R3-01c-next`; effort-flagged 2–4 w each).
- `npx pointer map` (selector/text → file index for non-Vite stacks) — held.
- Source-map based resolution — held.
- Changing the widget's capture tiers (`capture.ts:242-257` stays as is; tier 1 already reads the attribute).
- Default-on. The plugin is **opt-in** until hash stability is proven on 2–3 real apps.
- Auto re-screenshot after deploy (§28, held).
- Batch-by-file in `apply` (§11) — separate follow-up once the manifest is live.

## Prerequisites

- **R2-01** (`apply` core + CLI `get/status`) — the CLI resolves hashes and reports deploys.
- **R1-01** (on-disk contract) — `.pointer/manifest.json` is listed as gitignored; attribute names `data-component-source`, `data-build-sha` are frozen.
- Facts (from `00-API-INVENTORY.md`): the widget walks ancestors for the attribute named by `source-attr` (default `data-component-source`) — `web-component/src/capture.ts:242-254`; dev-mode fallbacks live in `web-component/src/framework-source.ts:50-90` (`__reactFiber*`, `_debugSource`, `_debugStack`, `__vueParentComponent.__file`) and are **absent in production**. `embed.js` already sets `source-attr="data-component-source"` (`API/Program.cs:309`). `pointer-init.md:348-355` currently tells users to hand-build such a plugin. `Comment.CommitUrl` exists (`Domain/Entity/Comment.cs:21`), set by `PATCH /api/comments/{id}` (`UpdateCommentStatusRequest.CommitUrl`). `CommentStatus { Open=1, ReadyToApply=2, Applied=3, Archived=4 }` (`Domain/Enums/CommentStatus.cs:3`). `CommentSummaryDto.SourcePath` (`Application/DTOs/Comment/CommentSummaryDto.cs:19`) and `ApplyElementDto.SourcePath` (`ApplyElementDto.cs:18`) already carry the captured value. Anonymous `GET /api/public/projects/{key}/widget-status?origin=` exists (`element.ts:275-290`); `GET /api/projects/{key}/capture-config` is `[Authorize]`.
- Package name is **`pointer-feedback`** (bin `pointer`) per `01-OVERVIEW.md`; the plugin is the subpath export **`pointer-feedback/vite`**.

## Design

### A. Hash

`hash = sha1(`${repoRelativePath}#${exportName}`).hex.slice(0, 8)`

- `repoRelativePath`: POSIX-separated path from the **git root** (`git rev-parse --show-toplevel`, fallback: Vite `config.root`) to the source file, e.g. `apps/web/src/components/PlanSelect.tsx`.
- `exportName`: the component's declared name (`function PlanSelect`, `const PlanSelect = …`, `export default function PlanSelect`); anonymous default export → `default`. One file with N components → N hashes.
- No line numbers, no content → deterministic across machines/builds; changes only on file move/rename or component rename.
- Collision policy: 8 hex chars = 32 bits; the plugin errors at build time if two distinct `(path, export)` pairs collide and tells the user to rename one (practically never happens; documented).

### B. Stamping rule (Decision)

For each **component function/arrow** in a `.jsx/.tsx` (React) or `<template>` root of a `.vue` file:

- Every **top-level JSX host element** the component returns (lowercase tag: `div`, `button`, `section`, …) gets `data-component-source="<hash>"` **added only if the element does not already carry that attribute**.
- `return <>…</>` / `<Fragment>` → **all** top-level host children are stamped.
- Top-level element is another component (`<Card>`) → **nothing stamped for that branch**; the child component stamps its own root, and the widget's ancestor walk (`capture.ts:246-254`) finds the nearest stamped ancestor.
- Conditional/ternary/`&&`/`map` at the top level: stamp each host element that appears as a direct result expression; JSX inside `.map(...)` callbacks at the top level is treated as a root (stamped).
- HOCs (`export default memo(PlanSelect)`) → the inner function is stamped; `exportName` = inner function name.
- Vue SFC: stamp each top-level element of `<template>` (multi-root supported); `exportName` = SFC file basename.
- The transform is compile-time and inert: it adds a static attribute; it never wraps, reorders, or changes runtime behaviour.

### C. Plugin API (`cli/src/vite/index.ts`, exported as `pointer-feedback/vite`)

```ts
import pointerSource from 'pointer-feedback/vite';
export default defineConfig({
  plugins: [pointerSource({ enabled: process.env.VITE_POINTER_SOURCE === 'true' })],
});
```

```ts
export interface PointerSourceOptions {
  /** Default: true when the plugin is listed. Set from an env flag to gate per environment. */
  enabled?: boolean;
  /** Default: '.pointer/manifest.json' relative to the git root. */
  manifest?: string;
  /** Default: ['**\/*.jsx', '**\/*.tsx', '**\/*.vue']; node_modules always excluded. */
  include?: string[];
  exclude?: string[];
  /** Default: true. Emit data-build-sha on <html> from `git rev-parse HEAD` (or options.buildSha). */
  buildSha?: boolean | string;
  /** Default: 'data-component-source'. Do NOT change in normal use (frozen contract). */
  attribute?: string;
}
```

Plugin hooks: `enforce: 'pre'`; `transform(code, id)` for matching ids. **Decision (dependencies):** the plugin depends on `@babel/parser` + `@babel/traverse` + `@babel/generator` (JSX/TSX syntax plugins are built into `@babel/parser`) **as optional `peerDependencies`** — all three are transitive deps of `@babel/core`, which every `@vitejs/plugin-react` project already has; for `.vue` use `@vue/compiler-sfc` (ships with `@vitejs/plugin-vue`). The CLI's own zero-dep rule is preserved for `dist/cli.js` (`01-OVERVIEW.md` records this exception); the `vite` subpath lazy-imports the peers and fails with a clear install message if missing. *Rejected zero-dep alternative:* an `enforce: 'post'` transform over Vite's already-compiled output using Rollup's `this.parse()` and stamping `jsx(...)`/`createElement(...)`/`_createVNode(...)` calls by enclosing function — it must special-case every JSX runtime's call shape, loses TS-level structure (generics, HOC wrappers) and breaks when host minifiers mangle function names. esbuild exposes no AST. `transformIndexHtml` injects `<html data-build-sha="<sha>">` (attribute on the root `<html>` tag; string replace `/<html(\s|>)/`). `buildEnd`/`closeBundle` writes the manifest; in dev (`configureServer`) the manifest is rewritten on each file change (debounced 250 ms).

### D. Manifest (`.pointer/manifest.json`, gitignored)

```json
{
  "version": 1,
  "generatedAt": "2026-09-11T10:22:00Z",
  "root": "apps/web",
  "buildSha": "3f2a9c1e…",
  "entries": {
    "a3f9c2b1": { "path": "apps/web/src/components/PlanSelect.tsx", "component": "PlanSelect" },
    "7c0d11ee": { "path": "apps/web/src/pages/Signup.tsx", "component": "default" }
  }
}
```

- Written atomically (`tmp` + rename). **On every manifest write, rename the existing `manifest.json` to
  `manifest.prev.json` first; never merge contents** (`prev` is what the stale-hash hint reads).
  Regenerated by every dev/build run. `pointer doctor` and `pointer apply` regenerate it when missing/stale
  — **Decision:** they do **not** run a build (too slow/side-effectful); they run the same transform
  **offline**: `pointer map --from-source` walks `include` globs with the same visitor and writes the
  manifest (no bundling). This shares `cli/src/vite/stamp.ts` between the plugin and the CLI. (The
  selector/text → file index variant of `map` for non-Vite stacks stays held; `01-OVERVIEW.md` records
  this split.)
- `.gitignore` already covers `.pointer/` and does **not** whitelist `manifest.json` (R1-01).

### E. Resolution in the CLI / MCP

`cli/src/source/resolve.ts`:
1. `resolveSource(sourcePath: string | null)`:
   - `null` → `{ kind: 'none' }`.
   - matches `/^[0-9a-f]{8}$/` → look up manifest → `{ kind: 'manifest', path, component }`; miss → `{ kind: 'stale', hash }`.
   - contains `/` or `:` (legacy `path:line` from dev mode or a custom plugin) → `{ kind: 'path', path, line? }`.
2. `apply`/`get --json` print `resolvedSource` next to `sourcePath`. On `stale`: print `⚠ comment #42: source hash a3f9c2b1 is not in the current manifest (file renamed since capture?) — falling back to search by component name` and include `hint: search for "PlanSelect"` when a **previous** manifest is cached in `.pointer/manifest.prev.json` (kept from the last regeneration) and still knows the hash.
3. MCP tool `resolve_source(hash)` (R2-02) returns the same object.

### F. Deploy awareness (§31)

**Decision — status model:** do **not** add a `Deployed` enum value. `CommentStatus` is used as an int by the widget (`STATUS_STR` map, `constants.ts:46-51`; an unknown value is coerced to `'open'` at `element.ts:630`, so a `5` would be **actively misclassified as open**, not just blank), dashboard filters, `pointer.sh:114` `jq` filters, skill.md (`status=3` literals) and the apply-queue query (`Status == 2`); a new value is additive in EF but breaks three clients silently. Use **`Comment.DeployedAt: DateTime?`** + **`Comment.CommitSha: string?`** (nullable, additive). "Deployed" is derived (`Status == Applied && DeployedAt != null`).

New columns on `Comment`:
- `CommitSha` (varchar 40, nullable) — set by the CLI on apply (`git rev-parse HEAD`), alongside `CommitUrl`.
- `DeployedAt` (timestamptz, nullable), `DeployedSha` (varchar 40, nullable) — which build proved it.

New table `ProjectBuild` (strict-own filter bucket):

| Column | Type | Notes |
|---|---|---|
| `Id` | int PK | `BaseEntity` |
| `ProjectId` | int FK | |
| `Sha` | varchar(40) | unique `(ProjectId, Sha)` |
| `FirstSeenAt` | timestamptz | |
| `Source` | smallint | 1=Widget, 2=Cli |
| `OwnerId` | uuid | tenant — **stamped from the resolved project's `OwnerId`** (as `CommentService.CreateAsync` does, `CommentService.cs:54-61`), not from the caller |

Endpoints:
- `POST /api/projects/{key}/builds` `[Authorize]` — `ReportBuildRequest { Sha: string, ContainsCommitShas?: string[] }` → `ReportBuildResponse { Sha, FirstSeen: bool, DeployedCommentIds: int[] }`.
  - **Validation** (`ReportBuildRequestValidator`): `Sha` and every `ContainsCommitShas[i]` — `Trim().ToLowerInvariant()` then must match `^[0-9a-f]{7,40}$`; `ContainsCommitShas` ≤ 200 items; otherwise 400.
  - **Authorization**: `IsQuickAccess` callers are rejected with `403` (marking deployed is a lifecycle action — mirrors the guard in `CommentService.UpdateStatusAsync`, `CommentService.cs:482-483`).
  - **Tenancy**: `ProjectBuildService` resolves the project through the tenant-filtered `IProjectService.EnsureAsync(key)` and updates **only comments of that project** (`c.ProjectId == project.Id`) — the strict-own query filter plus this predicate guarantee no cross-tenant comment can be marked; unknown/foreign key → 404.
  - Upserts `ProjectBuild` (unique `(ProjectId, Sha)`; repeat report → `FirstSeen: false`).
  - **Widget path** (`ContainsCommitShas` absent): marks `DeployedAt` for Applied comments of this project whose `CommitSha == Sha` exactly.
  - **CLI path (primary)**: the CLI computes, for every Applied-but-not-deployed comment (`GET /api/projects/{key}/comments?status=3` filtered client-side by `deployedAt == null`), `git merge-base --is-ancestor <commitSha> <sha>` and sends the passing ones in `ContainsCommitShas`; server marks them `DeployedAt = now, DeployedSha = Sha` **only if `DeployedAt` is still null**.
  - `DeployedCommentIds` = the ids **newly marked by this call only**; a repeat report of the same sha returns `[]`.
  - Rate limit: new `builds` policy 30/min partitioned by user id (`sub`) — same helper as R1-05's `UserOrIp`.
- `UpdateCommentStatusRequest` gains `CommitSha?: string` (validator: `^[0-9a-f]{7,40}$` after trim/lowercase).
- `CommentResponse`, `CommentListItemDto` gain `CommitSha?`, `DeployedAt?`, `DeployedSha?`. `CommentSummaryDto` and `CommentApplyItemDto` unchanged.

Widget: on boot after login, if `document.documentElement.dataset.buildSha` matches `/^[0-9a-f]{7,40}$/`, `POST /api/projects/{key}/builds { sha }` **once per page load** (module-level flag), fire-and-forget, errors ignored. Card: when `deployedAt` is set, the "✓ completed" pill reads "✓ live" with `title="Deployed in <DeployedSha[0..7]>"`.

CLI: `pointer status --deployed <sha>` (default `<sha>` = `git rev-parse HEAD`, must be a commit reachable in the repo) runs the CLI path above and prints `N comments marked deployed in <sha[0..7]>`. `pointer apply` sends `commitSha` with the existing `commitUrl`.

Why the CLI path is primary: `pointer-init.md:29,356` recommends shipping production **without** the widget, so the widget beacon is absent exactly where "is it live?" matters; the CLI has git and can answer for any deployed sha.

## Tasks

### Plugin & CLI (`cli/`)
1. `cli/package.json`: add `"exports": { ".": "./dist/cli.js", "./vite": "./dist/vite/index.js" }`, optional `peerDependencies` (`vite >=5`, `@babel/parser`, `@babel/traverse`, `@babel/generator`, `@vue/compiler-sfc`) with `peerDependenciesMeta` optional; add `build:vite` esbuild entry for `src/vite/index.ts` (format `esm`, external peers).
2. `cli/src/vite/hash.ts` — `hashFor(repoRelativePath, exportName)`; `repoRoot()` (git toplevel, fallback cwd). Unit-tested.
3. `cli/src/vite/stamp.ts` — the shared visitor: `stampJsx(code, filePath, attr) → { code, entries }` implementing §B for JSX/TSX; `stampVueSfc(...)` for `.vue`. Pure functions; no I/O.
4. `cli/src/vite/manifest.ts` — read/write/merge manifest (`version:1`), atomic write, `manifest.prev.json` rotation, `isStale(manifest, files)`.
5. `cli/src/vite/index.ts` — the Vite plugin (options §C, hooks: `transform`, `transformIndexHtml`, `buildEnd`, `configureServer`), lazy-imports the optional peers with a clear error message: `pointer-feedback/vite needs @babel/parser… run: npm i -D @babel/parser @babel/traverse @babel/generator`.
6. `cli/src/source/resolve.ts` — §E; wire into `apply`, `get`, `list --json` output (`resolvedSource`) and MCP `resolve_source`.
7. `cli/src/commands/map.ts` — `pointer map --from-source` (offline manifest regeneration via `stamp.ts` with `include` globs from `vite.config` when parseable, else defaults). `doctor` gains a check: "manifest present & not stale (when plugin configured)".
8. `cli/src/commands/init.ts` — new flag `--source-map`: appends the plugin import + `plugins: [pointerSource({ enabled: process.env.VITE_POINTER_SOURCE === 'true' })]` to `vite.config.{ts,js,mjs}` (idempotent; if the file cannot be parsed, print the snippet instead) and adds `VITE_POINTER_SOURCE=true` to `.env.development` and `.env.staging` if those exist. Prints: `Source stamping enabled for dev/staging. Turn it on for production when you are ready: VITE_POINTER_SOURCE=true`.
9. `cli/src/commands/status.ts` — `--deployed [sha]` (§F CLI path). `cli/src/commands/apply.ts` — send `commitSha`.

### API
10. `Domain/Entity/Comment.cs` — add `CommitSha`, `DeployedAt`, `DeployedSha`. `Domain/Entity/ProjectBuild.cs` — new entity. `Infrastructure/Mappings/ProjectBuildMapping.cs` — unique index `(ProjectId, Sha)`, `Sha` max 40. `Infrastructure/AppDbContext.cs` — `DbSet<ProjectBuild>`, strict-own query filter. `just migrate name="AddCommitShaDeployedAtAndProjectBuilds"`.
11. `Application/DTOs/Comment/UpdateCommentStatusRequest.cs` — `CommitSha?`; `Application/Validators/UpdateCommentStatusValidator.cs` (or existing) — regex. `CommentResponse`, `CommentListItemDto` — three new fields; mappings in `Infrastructure/Mappings/CommentMapping.cs` (or wherever `CommitUrl` is mapped — grep `CommitUrl`).
12. `Application/DTOs/Project/ReportBuildRequest.cs`, `ReportBuildResponse.cs`; `Application/Validators/ReportBuildRequestValidator.cs` (§F regex + list cap); `Application/Services/Interfaces/IProjectBuildService.cs`; `Application/Services/Implementation/ProjectBuildService.cs` — upsert + both marking paths (§F), project-scoped predicate, quick-access rejection, `OwnerId` from the project; `API/Controllers/ProjectBuildsController.cs` — `[ApiController]`, `[Route("api/projects/{key}/builds")]`, `[Produces("application/json")]`, `[Authorize]`, `[EnableRateLimiting("builds")]`, `[ProducesResponseType(typeof(ReportBuildResponse), 200)]`. `API/Extensions/RateLimitingExtensions.cs` — `builds` policy 30/min partitioned by user id claim (`sub`).
13. `CommentService.UpdateStatusAsync` (`CommentService.cs:~497`) — persist `CommitSha` when `Status == Applied`.
13b. **Vite fixture** — create `e2e/fixture-app/vite-react/`: minimal React + Vite + **Tailwind** app (`package.json` with pinned minor versions, `vite.config.ts` using `pointer-feedback/vite` from the local `cli/` build via `file:` dependency, `tailwind.config.ts` with two custom colours + `src/index.css` with `--brand` CSS vars — this is also R3-02's detection fixture), three components: `Card` (single root), `PlanList` (returns a Fragment with two roots), `WithTracking(Card)` (HOC via `memo`) plus one component that returns only `<Card/>` (no host root). `npm run build` produces `dist/` served by `e2e/scripts/serve-dir.mjs` (R2-00). The existing `smoke|alpha|beta` fixtures are plain static HTML and cannot host the plugin.

### Widget
14. `web-component/src/element.ts` — after successful login/boot, read `document.documentElement.getAttribute('data-build-sha')`, validate, `POST …/builds` once per page (module flag `buildReported`). `web-component/src/templates.ts` `card()` — "✓ live" variant when `deployedAt`. `web-component/src/types.ts` — `commitSha?`, `deployedAt?`, `deployedSha?`. `npm run build`; commit artifacts.

### Docs
15. `API/wwwroot/pointer-init.md:348-355` — replace "build your own plugin" with `npx pointer-feedback init --source-map` / manual plugin snippet; keep the "enable in the environments where stakeholders give feedback" guidance. `API/wwwroot/skill.md` Step 5 — "when `sourcePath` is an 8-hex hash, resolve it via `.pointer/manifest.json` (or `pointer get <id> --json` → `resolvedSource`); never grep first". `AGENTS.md` — one paragraph on the plugin + manifest. `.pointer/pointer.sh` — no change (deprecated path).

## Dashboard tasks

- Regenerate services (`CommentResponse`, `CommentListItemDto` gain `commitSha`, `deployedAt`, `deployedSha`; new `ReportBuildRequest/Response` may be ignored).
- Comment detail/list: show a "Live" badge when `deployedAt` is set, tooltip `Deployed in <sha7>`; filter "Applied but not live" (client-side on `status==3 && !deployedAt`).

## Tests

- **Unit (cli, vitest):** `hash.test.ts` (determinism; path normalisation Windows→POSIX; `default` export; **two mocked git roots** — `/home/a/repo` and `C:\Users\b\repo` — with the same repo-relative file yield the identical hash), `stamp.test.ts` (single root, Fragment multi-root, component-only root → no stamp, conditional/ternary/map roots, existing attribute preserved, `memo()` HOC, TSX generics, Vue multi-root), `manifest.test.ts` (atomic write, prev rotation, stale detection), `resolve.test.ts` (hash/path/legacy/stale).
- **Unit (API, xUnit):** `ProjectBuildServiceTests` (upsert idempotent + `FirstSeen` false on repeat; widget path exact-match marks; CLI path `ContainsCommitShas` marks; repeat report returns empty `DeployedCommentIds`; already-deployed comment not re-stamped; cross-tenant project key → NotFound; quick-access caller → 403; `OwnerId` equals the project's; `Sha` validation incl. uppercase normalisation and > 200 items → 400), `CommentCommitShaTests` (PATCH persists `CommitSha` only when Applied; DTOs round-trip), `RateLimitingTests` extension for `builds`.
- **Integration (cli):** build `e2e/fixture-app/vite-react` (task 13b) with the plugin → assert every rendered component root has `data-component-source` of 8 hex chars, the Fragment component stamps both roots, the component-only root adds none, `<html data-build-sha>` present, manifest entries == distinct components.
- **CI determinism (matrix):** the workflow builds the fixture in **two clean clones** (matrix of two runners / two checkout paths) and diffs `.pointer/manifest.json#entries` — must be identical; evidence pasted in the report.
- **E2E scenarios (Playwright, `e2e/`):** `source-stamp-prod-build` (production build of a fixture Vite app with the plugin; click element in the widget; created comment has `element.sourcePath` = hash; `pointer get --json` shows `resolvedSource.path`), `stale-hash-warning` (rename component, regenerate manifest, `pointer get` prints the warning), `deploy-awareness-widget` (page with `data-build-sha` = the applied comment's `commitSha` → card shows "live"), `deploy-awareness-cli` (`pointer status --deployed HEAD` marks a comment whose commit is an ancestor).

## Acceptance criteria

- [ ] Two clean clones in the CI matrix produce byte-identical `entries` in `.pointer/manifest.json` (evidence: both files' sha256 in the report); `hash.test.ts` two-git-roots case passes.
- [ ] `POST /builds` with an uppercase or malformed sha → 400; from a quick-access token → 403; for another tenant's project key → 404; repeat report → `firstSeen:false`, `deployedCommentIds: []`.
- [ ] Production build of a Vite React app with the plugin: every component's top-level host elements carry `data-component-source` (8 hex); Fragment components stamp all roots; components returning only components add no attribute; no console errors; bundle behaviour unchanged (snapshot test of one route's HTML apart from the attributes).
- [ ] `.pointer/manifest.json` is gitignored and never appears in `git status` after a build.
- [ ] Widget comment created on the prod build has `sourcePath` = the hash; `pointer get <id> --json` shows `resolvedSource: { kind: "manifest", path, component }`.
- [ ] Renaming a component → next `pointer get` prints the stale warning with the previous component name hint; `apply` proceeds via fallback search.
- [ ] `pointer apply` stores `commitSha` (+ existing `commitUrl`); `pointer status --deployed <sha>` marks only comments whose commit is an ancestor of `<sha>`; response lists their ids.
- [ ] A page with `<html data-build-sha="<sha>">` and an authenticated widget → exactly one `POST /builds` per page load; Applied comments with `commitSha == sha` get `deployedAt`; card shows "✓ live".
- [ ] Old comments (no `commitSha`) never become deployed and render exactly as before.
- [ ] `just test` and `cli` tests green; dashboard regenerated.

## Rollout / compatibility

- Additive migration only (3 nullable columns + 1 table). Existing installs unaffected; the widget's ancestor walk already handles the attribute.
- Plugin is opt-in; `init` without `--source-map` changes nothing. Existing custom `data-component-source="path:line"` values keep working (`resolve.ts` treats them as `kind: 'path'`).
- Hash format vs legacy path values is disambiguated by shape; never mix both in one app (document).
- `pointer.sh` (deprecated) ignores the new fields.
- Default-on decision is a separate follow-up after 2–3 real apps show stable hashes across ≥ 10 builds.

## Report template

```
Branch: feat/r3-01-vite-plugin-manifest
Files changed: …
cli: npm run typecheck / test / build → …
api: just fmt / dotnet build / just test → … (new tests: ProjectBuildServiceTests, CommentCommitShaTests)
Fixture build: components=N stamped=N html data-build-sha=<sha>
E2E: source-stamp-prod-build ✓ stale-hash-warning ✓ deploy-awareness-widget ✓ deploy-awareness-cli ✓
Dashboard: regenerated / follow-up: <DTO names>
Skipped / open: …
```
