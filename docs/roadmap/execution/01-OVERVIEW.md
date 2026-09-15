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
- **Zero runtime dependencies** for everything except `mcp` (may use `@modelcontextprotocol/sdk`) and the optional **build-time** peers of the `pointer-feedback/vite` subpath (`@babel/parser|traverse|generator`, `@vue/compiler-sfc` — R3-01; `dist/cli.js` itself stays dependency-free). Use `node:readline/promises` for prompts, built-in `fetch`.
- Build-time constant `DEFAULT_SERVER` (esbuild `define`), default `https://api.pointer.moamen.work`; **never** referenced anywhere but `src/config.ts`.
- Every network call goes through one `api()` helper that unwraps the `Result<T>` envelope and throws a typed `ApiError { status, message }`.
- Output rules: `--json` flag on every read command prints raw JSON and nothing else; human output uses `productName` from `/api/branding` for the product's name and never a literal "Pointer".
- Exit codes: 0 ok · 1 generic failure · 2 invalid usage · 3 auth failure · 4 not found · 5 server too old (`minCliVersion`).
- Commands by release: R1 `init`, `doctor`; R2 `list`, `get`, `status`, `reply`, `apply`, `mcp`, `update`; R3 `map --from-source` (offline manifest regeneration only — the selector/text-index variant of `map` for non-Vite stacks is held), plugin subpath export `pointer-feedback/vite`.

### On-disk contract (frozen — see R1-01)
- `.pointer/config.json` (**committable**): `{ "server": string, "project": string, "environment": "local"|"staging"|"production", "aiTool": string, "skillsDir"?: string, "cliVersion": string }` (`skillsDir` only when `--skills-dir` overrode the default).
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
Every doc has a **Dashboard tasks** section naming the DTOs/endpoints and the UI change, or "none".
It is **an input to the phase-end `dashboard-agent` run, not a same-PR obligation** — see *Cross-repo
sync agents* below. An API-only PR is complete; a *phase* that ends without the dashboard sync is not.

The pipeline those tasks feed is **not** "regenerate in the dashboard repo" — there is no
`generate-services` script anywhere. The client is generated **here**:

```
orval.config.ts ──▶ npm run generate-clients ──▶ clients/react/src   (gitignored)
                ──▶ npm run build-clients    ──▶ clients/react/dist
                ──▶ published to GitHub Packages as @moamen-ui/pointer-react
pointer-dashboard/react ──▶ installs that package; uses only its generated hooks
```

`npm run generate-clients` honours `POINTER_SWAGGER_URL` (default `http://localhost:8090/…`), so it
works against a local API; the *Publish API clients* workflow deliberately generates from production,
which means a not-yet-deployed endpoint cannot be published — the agent handles that case explicitly.
A controller tag missing from `orval.config.ts` `filters.tags` silently generates nothing.

### Cross-repo sync agents
Since 2026-09-15 only the React dashboard exists (`pointer-dashboard/react`); Angular and Vue were
retired at tag `last-three-apps` / branch `legacy/angular-vue`. Any dashboard work targets React only.

Two repos move with this one: `pointer-dashboard` (the React app) and the rebranding plan on the
`docs/rebranding-plan` branch. Each has a dedicated agent, and they are invoked on **different
cadences** — getting that wrong is the failure mode this section exists to prevent.

| | [`rebranding-agent`](../../../.claude/agents/rebranding-agent.md) | [`dashboard-agent`](../../../.claude/agents/dashboard-agent.md) |
|---|---|---|
| Cadence | **Eagerly** — as soon as a tracked surface changes; may fire several times in one phase | **Once per phase** — never per doc, never per PR |
| Trigger | new/renamed table, column, entity, migration, endpoint, DTO, config key, served file, storage key, package/bin, domain, or any new customer-visible name | all backend work for the phase merged and green |
| Input | what changed + where it landed | the phase's accumulated **Dashboard tasks** sections |
| Writes | `docs/rebranding/REBRANDING-PLAN.md` (its own worktree) | `orval.config.ts`/tags here + the React dashboard app |
| Never | renames anything; pushes | publishes a fake version; hand-edits generated code; pushes |

**Phase lifecycle, in order:**

1. **BE implementation** — the phase's execution docs, each on its own branch.
2. **`rebranding-agent`** — invoked *during* the phase, every time a tracked surface lands. Do not batch
   it to the end: the plan's inventory is what a future rename executes against, and a surface added
   after the last sync is a surface the rename misses silently.
3. **Phase BE complete** — everything merged, `dotnet build` + `just test` green.
4. **`dashboard-agent`** — invoked **once**, with the phase's whole Dashboard-tasks backlog. It fixes
   tags/annotations, regenerates and builds the client, and brings the React app up to date
   including `en` **and** `ar` i18n.
5. **[`e2e-tester-agent`](../../../.claude/agents/e2e-tester-agent.md)** — invoked with the phase id and
   its scenario ids, and deliberately **after** the dashboard sync, because the phase's UI changes are
   part of what the suite exercises. It runs the harness suite (`docs/roadmap/testing/`) and returns a
   **triaged** report: every failure classified `product bug` / `test bug` / `environment` / `flake`,
   each with its citation, evidence path and a fix addressed to an owner.
6. **Orchestrator fixes**, then **re-invokes the tester** — previously-failing ids plus the rows the
   docs bind to them by state or ordering, then one full clean run of the tier. **Capped at three
   rounds**; a fourth escalates to the human with a diff of what changed between rounds.
7. **Phase done.**

If the dashboard agent reports it is working from a **local** client build (the API for this phase is
not deployed yet), that is expected at step 4 — but it must be re-pointed at the published package once
the API ships, and the phase is not closed until it is.

The tester is the one agent here with **no write tools at all**. It never edits product code, specs or
scenario docs — not even an assertion it can prove is wrong; that is a `test bug` finding addressed to
the doc's owner. An agent that can fix its own failures makes them disappear instead of explaining them,
and a fix that turns one scenario green and another red is the signal to stop iterating and decide what
the product should actually do.

### Widget
Source in `web-component/src/`; build with `npm run build`; commit the regenerated `API/wwwroot/pointer.{js,css}`. Never hand-edit the artifacts.

### Git rules for implementers
- One branch per doc: `feat/<doc-id>-<slug>` (e.g. `feat/r1-05-allowed-origins`), from `main`.
- Commit early, small, conventional commits (`feat(api): …`, `feat(cli): …`, `docs: …`, `test: …`).
- **Never push, never merge** — report the branch; a human reviews and pushes.
- Do not touch files outside the doc's **Files** list without saying so in the report.

### Definition of done (every doc)
1. All tasks checked; `just fmt`, `dotnet build`, `just test` green; for CLI `npm run typecheck && npm test && npm run build` green in `cli/`; for widget changes `npm run typecheck && npm test && npm run build` green in `web-component/` (`npm test` exists once R3-03 adds the vitest + jsdom harness).
2. Acceptance criteria in the doc verified and the evidence (command + output) pasted in the report.
3. Dashboard tasks either done in the dashboard repo or listed as a follow-up with the exact DTO names.
4. Docs updated where the doc says so (`AGENTS.md`, `pointer-init.md`, `skill.md`, `install.sh`).
5. **The public page named in the doc's `## Docs` section is written or updated in the same change** —
   by the implementer, while the feature is still in context. `## Docs` saying "none — internal only" is
   a valid discharge; a missing page is not. Written after the fact by someone re-reading the code, a
   docs page costs several times as much and comes out wrong. The site shell is
   [`R2-07-docs-site.md`](R2-07-docs-site.md); pages written before it lands are adopted by its task 8.
6. Report: files changed, commands run, anything skipped and why.

## Template (each doc follows this order)

```
# <id> — <title>            (item §n / NEW-n · Release · estimate)
## Goal                    one paragraph: user-visible outcome
## Out of scope            explicit list
## Prerequisites           docs that must be done first; facts from 00-API-INVENTORY
## Design                  contracts: endpoints, DTOs, tables, files, CLI UX (exact prompts/output)
## Tasks                   numbered, file-level, in execution order, each independently verifiable
## Dashboard tasks         or "none"
## Docs                    the public page(s) this item creates/updates, or "none — internal only" + why
## Tests                   unit (class names) · integration · e2e (scenario names)
## Acceptance criteria     checkbox list, each objectively checkable
## Rollout / compatibility what breaks for existing installs; migration notes
## Report template         what the implementer must paste back
```
