# R3-02 — Design-system awareness (`design` block in `.pointer/stack.json`)

(§45 · Release 3 · 1–2 days)

## Goal

When the AI applies "make this blue / bigger / rounder", it uses the host app's **existing design
tokens** (`var(--primary)`, Tailwind `text-primary`, `$brand-blue`) instead of inventing hex values.
`pointer init` (and `pointer doctor --refresh-stack`) detects the token sources once and writes a
compact summary into the **committable, repo-local** `.pointer/stack.json`; `skill.md`/the apply
prompt tell the AI to prefer those tokens. Nothing is uploaded to the server.

## Out of scope

- Uploading tokens to the API (`Project.TechStack` stays write-once `{frontend, backend}` — `ProjectService.cs:717-720`). **Decision:** tokens are repo data and may reveal internal naming; they stay local.
- Parsing every CSS framework. Only the sources listed in §A; anything else is reported as `"unknown"`.
- Enforcing token usage (no lint); this is guidance to the AI.
- Dashboard changes.

## Prerequisites

- **R1-02** (`init` writes `.pointer/stack.json` via `POST /api/projects/{key}/stack`, `API/Controllers/ProjectStackController.cs:32-40`; response shape `ProjectStackResponse { Frontend?, Backend?, AiTools }`, `Application/DTOs/Project/ProjectStackResponse.cs`).
- **R2-01** (`apply` prompt builder consumes `stack.json`).
- Facts: `stack.json` is committable per `install.sh:79` (`!.pointer/stack.json`); `pointer-init.md:293-337` documents detection of `frontend` tokens (`react`, `tailwind`, `scss`, `css-modules`, …); `skill.md:181-196` reads `stack.json` in Step 1.

## Design

### A. Detection (`cli/src/stack/design.ts`), in order, all that match

| Source | Detect | Extract |
|---|---|---|
| Tailwind | `tailwind.config.{js,ts,cjs,mjs}` or `@import "tailwindcss"` / `@theme` in any `*.css` under `src/` | `theme.extend.colors` keys (flatten one level: `primary`, `primary.500`), `fontFamily` keys, `borderRadius` keys, `spacing` keys count; for v4 `@theme { --color-primary: … }` → var names |
| CSS custom properties | `:root {` or `html {` blocks in `src/**/*.{css,scss}` (the **20 smallest** files, ≤ 200 KB each; sorted by size ascending, then path) | property names `--*` (max 60), grouped by prefix (`--color-*`, `--pf-*`, `--radius-*`) |
| SCSS variables | `src/**/_variables.scss`, `src/**/variables.scss`, `src/styles/**/*.scss` | `$name:` declarations (max 60) |
| CSS-in-JS theme | `theme.{ts,js}` exporting an object with `colors`/`palette` (MUI/Chakra/styled-components) | top-level keys of `colors`/`palette` |
| Angular Material | `@use '@angular/material' as mat;` + `mat.define-theme`/`define-palette` | palette names passed |
| Component library | `package.json` deps: `@mui/material`, `@chakra-ui/react`, `antd`, `@angular/material`, `bootstrap`, `@radix-ui/*`, `shadcn` (`components.json`), `vuetify`, `element-plus`, `primeng`, `primevue` | library name + version |

Limits: total scan ≤ 2 s / ≤ 500 files; never read `node_modules`, `dist`, `build`, `.next`.

### B. Output — `design` block appended to `.pointer/stack.json`

```json
{
  "frontend": ["react", "tailwind"],
  "backend": ["dotnet", "postgres"],
  "aiTools": ["claude-code"],
  "design": {
    "version": 1,
    "libraries": [{ "name": "shadcn", "version": null }, { "name": "@radix-ui/react-dialog", "version": "1.1.2" }],
    "tokens": {
      "tailwind": { "config": "tailwind.config.ts", "colors": ["primary", "secondary", "muted"], "radius": ["sm", "md", "lg"], "fontFamily": ["sans", "mono"] },
      "cssVars": { "files": ["src/styles/globals.css"], "names": ["--background", "--primary", "--radius"] },
      "scss": { "files": [], "names": [] }
    },
    "guidance": "Prefer existing tokens: Tailwind classes (text-primary, rounded-md) or CSS vars (var(--primary)). Do not introduce raw hex colors or px radii when a token exists."
  }
}
```

`guidance` is generated from what was found (one sentence per source present). When nothing is detected: `"design": { "version": 1, "libraries": [], "tokens": {}, "guidance": "No design tokens detected; match the nearest sibling element's existing classes/styles." }`.

**No timestamps anywhere in `design`** (a `detectedAt` field would make the byte-identical guarantee below impossible). **Canonical form:** the whole `stack.json` is written with fixed key order — top level `frontend, backend, aiTools, design`; inside `design`: `version, libraries, tokens, guidance`; inside `tokens`: `tailwind, cssVars, scss, theme, angularMaterial` (present keys only, in that order); arrays sorted alphabetically unless order is semantic (`libraries` sorted by `name`) — 2-space indent, `\n` line endings, single trailing newline.

The server response from `POST /stack` is merged with the local `design` block on write (`design` is never sent: strip it from the request body). `stack.json` remains committable; `design` is deterministic for a given tree, so teammates see the same content after `init`/`doctor --refresh-stack`.

### C. Consumers

- `apply` prompt builder (R2-01, `cli/src/apply/prompt.ts`): adds a section
  `## Design system\n<guidance>\nTokens: <compact list, ≤ 40 names>`.
- `skill.md` Step 5: "Read `.pointer/stack.json → design.guidance` and follow it before styling."
- MCP (R2-02): `get_project_context` (if present) includes `design`; otherwise no change.

### D. CLI UX

- `pointer init` step 7 prints: `✔ Design tokens: tailwind (12 colors), css vars (8) → .pointer/stack.json`.
- `pointer doctor --refresh-stack` re-runs detection and rewrites `design` (frontend/backend untouched; server not called).
- `--no-design` flag skips detection (CI, huge monorepos).

## Tasks

1. `cli/src/stack/design.ts` — detectors §A (pure functions over a file list + `readFile` injected for tests), `buildDesignBlock()`, `renderGuidance()`.
2. `cli/src/stack/stackfile.ts` — read/merge/write `.pointer/stack.json`; strip `design` before `POST /stack`; atomic write.
3. `cli/src/commands/init.ts` — call detection after stack registration; `--no-design`; output line. `cli/src/commands/doctor.ts` — `--refresh-stack`.
4. `cli/src/apply/prompt.ts` — "Design system" section (R2-01 file).
5. `API/wwwroot/skill.md` Step 1.4 + Step 5 — read `design.guidance`. `API/wwwroot/pointer-init.md:293-337` — mention the `design` block is written by the CLI (`npx pointer-feedback init`), and that it must never be sent to the server.
6. Fixtures: `cli/test/fixtures/design/{tailwind-v3,tailwind-v4,cssvars,scss,mui,none}/` minimal trees.

## Dashboard tasks

none.

## Tests

- **Unit (cli):** `design.test.ts` — one test per fixture (expected `tokens`/`libraries`/`guidance`), size/time limits respected (fixture with 600 files → only 500 scanned), `node_modules` ignored; `stackfile.test.ts` — merge keeps `frontend/backend/aiTools`, strips `design` from the POST body (`buildRequestBody`), canonical key order, deterministic output (two runs → identical bytes).
- **E2E scenario:** `init-writes-design-tokens` (`e2e/fixture-app/vite-react`, R3-01 task 13b → `stack.json.design.tokens.tailwind.colors` contains `primary`; `git status` shows `stack.json` tracked, `design` present).

## Acceptance criteria

- [ ] `pointer init` on `e2e/fixture-app/vite-react` writes the `design` block with `colors` from `theme.extend.colors`, `cssVars.names` containing `--brand`, and the generated `guidance`.
- [ ] Unit test: `stackfile.buildRequestBody(stack)` output has no `design` key for every fixture, **and** E2E scenario `R3-02-03` (recording proxy in front of the API, `docs/roadmap/testing/R3-02-tests.md`) shows no `design` key in the real `POST /api/projects/{key}/stack` body.
- [ ] Running `init` twice (or `doctor --refresh-stack`) produces byte-identical `stack.json`.
- [ ] Detection completes in < 2 s on the unit fixtures and on `e2e/fixture-app/vite-react` (the Tailwind + CSS-vars fixture created by R3-01 task 13b; the older `smoke|alpha|beta` fixtures are plain HTML with no tokens and are **not** a valid check).
- [ ] `apply` prompt (R2-01 `--plan` output) contains the "Design system" section when `design.tokens` is non-empty and the fallback sentence when empty.

## Rollout / compatibility

- Existing `stack.json` files without `design` keep working (`design` optional everywhere).
- `skill.md` change is guidance only; older skill copies ignore the block.
- No API or DB change.

## Report template

```
Branch: feat/r3-02-design-tokens
Files: …
cli: typecheck/test/build → …
Fixtures: tailwind-v3 ✓ tailwind-v4 ✓ cssvars ✓ scss ✓ mui ✓ none ✓
E2E: init-writes-design-tokens ✓
Skipped / open: …
```
