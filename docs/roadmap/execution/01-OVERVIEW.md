# 01 — Execution plan overview & implementer contract

> How to use the documents in this folder. **Every implementer (human or AI) reads this file and
> [`00-API-INVENTORY.md`](00-API-INVENTORY.md) before starting any task.** The roadmap and the
> reasoning behind it live in [`../DX-UX-CX-PLAN.md`](../DX-UX-CX-PLAN.md) and
> [`../meetings/11-final-decisions.md`](../meetings/11-final-decisions.md); do not re-litigate them here.

## Document map

| Doc | Item | Release |
|---|---|---|
| `R1-01-contract-freeze.md` | NEW-1 on-disk contract freeze | R1 |
| `R1-02-cli-init.md` | §1 `npx pointer init` (+ §6 doc fix, §21 usage events) | R1 |
| `R1-03-dashboard-quickstart-key.md` | §2 pre-filled `--key` command | R1 |
| `R1-04-doctor-and-meta.md` | §3 `doctor`, §4 `GET /api/meta` | R1 |
| `R1-05-allowed-origins-ratelimit.md` | §41 | R1 |
| `R1-06-api-key-hardening.md` | NEW-5 | R1 (slip → R2 wk 1) |
| `R1-07-e2e-schedule.md` | NEW-4a | R1 |
| `R2-00-e2e-fresh-app-whitelabel.md` | NEW-4b | R2 |
| `R2-01-apply-core-cli.md` | §7 apply core, §8 `--plan` | R2 |
| `R2-02-mcp-server.md` | §24 | R2 |
| `R2-03-served-version-stamp.md` | NEW-2 | R2 |
| `R2-04-inapp-notifications.md` | §10 | R2 |
| `R2-05-quick-access-invites.md` | §13 | R2 |
| `R2-06-secrets-flag.md` | S6 | R2 |
| `R3-01-vite-plugin-manifest.md` | Phase 4 + §31 deploy awareness | R3 |
| `R3-02-design-tokens.md` | §45 | R3 |
| `R3-03-widget-release-eng.md` | NEW-3 | R3 |
| `R3-04-snapshot-privacy.md` | §33-lite | R3 |
| `R3-05-privacy-page.md` | NEW-6 | R3 |

Dependencies are declared inside each doc under **Prerequisites**. Within a release, docs can be
implemented in parallel unless a prerequisite says otherwise.

## Shared decisions (binding for every doc)

### The CLI package
- Lives in this repo at **`cli/`** (sibling of `web-component/` and `extension/`). TypeScript, **Node ≥ 18**, built with esbuild to a single `dist/cli.js`.
- **npm package name: `pointer-feedback`** (checked 2026-09-11: `pointer` 1.0.2 and `pointer-cli` 0.4.5 are taken; `pointer-feedback` is free and matches the `<pointer-feedback>` element). `"bin": { "pointer": "dist/cli.js" }` — so the documented one-shot command is **`npx pointer-feedback init`** and a local install gives the `pointer` command. Docs and the dashboard quick-start always print the `npx pointer-feedback …` form.
- **Zero runtime dependencies** for everything except `mcp` (may use `@modelcontextprotocol/sdk`). Use `node:readline/promises` for prompts, built-in `fetch`.
- Build-time constant `DEFAULT_SERVER` (esbuild `define`), default `https://api.pointer.moamen.work`; **never** referenced anywhere but `src/config.ts`.
- Every network call goes through one `api()` helper that unwraps the `Result<T>` envelope and throws a typed `ApiError { status, message }`.
- Output rules: `--json` flag on every read command prints raw JSON and nothing else; human output uses `productName` from `/api/branding` for the product's name and never a literal "Pointer".
- Exit codes: 0 ok · 1 generic failure · 2 invalid usage · 3 auth failure · 4 not found · 5 server too old (`minCliVersion`).
- Commands by release: R1 `init`, `doctor`; R2 `list`, `get`, `status`, `reply`, `apply`, `mcp`, `update`; R3 `map` (held), plugin subpath export `pointer-feedback/vite`.

### On-disk contract (frozen — see R1-01)
- `.pointer/config.json` (**committable**): `{ "server": string, "project": string, "environment": "local"|"staging"|"production", "aiTool": string, "cliVersion": string }`.
- `.pointer/credentials.env` (**gitignored**): `POINTER_API_KEY=ptr_…`.
- `.pointer/credentials.env.example`, `.pointer/stack.json`, `.pointer/pointer.sh` committable (existing).
- `.pointer/manifest.json`, `.pointer/.token_cache` gitignored.
- `.gitignore` lines: `.pointer/`, `!.pointer/credentials.env.example`, `!.pointer/stack.json`, `!.pointer/pointer.sh`, `!.pointer/config.json`.
- Env vars per stack exactly as `pointer-init.md:55-65`. Custom element `<pointer-feedback>`. Attributes `data-component-source`, `data-build-sha`, `data-snapshot-mask`.

### Server-side conventions
- `Result<T>` envelope; controllers annotate the inner type with `[ProducesResponseType(typeof(Inner), 200)]`; `[Produces("application/json")]`.
- New tables: `BaseEntity` + `OwnerId` (Guid?) + query filter bucket chosen explicitly in `AppDbContext` (strict-own unless the doc says otherwise) + `TenantStamp.OwnerFor(currentUser)` on write.
- **Migrations are additive-only** (no column drops in the same release that introduces the replacement; drop one release later). `just migrate name="..."`.
- Services in `Application/Services/Implementation`, interfaces in `Application/Services/Interfaces`, DTOs in `Application/DTOs/<Area>`, validators in `Application/Validators`, mappings in `Infrastructure/Mappings`.
- Tests in `Tests/` (xUnit) following the neighbouring `*Tests.cs` fixture pattern; **every doc lists the test class names it must add**.
- Format with `just fmt` before committing; `just test` must be green.

### Dashboard column
Every doc has a **Dashboard tasks** section. It names the DTOs/endpoints the separate `pointer-dashboard` repo must regenerate (`npm run generate-services` with the API on `:8090`) and the UI change, or states "none". API-only PRs for items with dashboard tasks are incomplete.

### Widget
Source in `web-component/src/`; build with `npm run build`; commit the regenerated `API/wwwroot/pointer.{js,css}`. Never hand-edit the artifacts.

### Git rules for implementers
- One branch per doc: `feat/<doc-id>-<slug>` (e.g. `feat/r1-05-allowed-origins`), from `main`.
- Commit early, small, conventional commits (`feat(api): …`, `feat(cli): …`, `docs: …`, `test: …`).
- **Never push, never merge** — report the branch; a human reviews and pushes.
- Do not touch files outside the doc's **Files** list without saying so in the report.

### Definition of done (every doc)
1. All tasks checked; `just fmt`, `dotnet build`, `just test` green; for CLI `npm run typecheck && npm test && npm run build` green in `cli/`.
2. Acceptance criteria in the doc verified and the evidence (command + output) pasted in the report.
3. Dashboard tasks either done in the dashboard repo or listed as a follow-up with the exact DTO names.
4. Docs updated where the doc says so (`AGENTS.md`, `pointer-init.md`, `skill.md`, `install.sh`).
5. Report: files changed, commands run, anything skipped and why.

## Template (each doc follows this order)

```
# <id> — <title>            (item §n / NEW-n · Release · estimate)
## Goal                    one paragraph: user-visible outcome
## Out of scope            explicit list
## Prerequisites           docs that must be done first; facts from 00-API-INVENTORY
## Design                  contracts: endpoints, DTOs, tables, files, CLI UX (exact prompts/output)
## Tasks                   numbered, file-level, in execution order, each independently verifiable
## Dashboard tasks         or "none"
## Tests                   unit (class names) · integration · e2e (scenario names)
## Acceptance criteria     checkbox list, each objectively checkable
## Rollout / compatibility what breaks for existing installs; migration notes
## Report template         what the implementer must paste back
```
