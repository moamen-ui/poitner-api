# R1-01-tests — On-disk contract freeze

Harness: [`00-HARNESS.md`](00-HARNESS.md). Execution doc: [`../execution/R1-01-contract-freeze.md`](../execution/R1-01-contract-freeze.md).

## Covers

- AC-3 (`install.sh` gitignore block contains `!.pointer/config.json`) → **R1-01-01**, asserted on the
  *served* artifact (the unit guard reads the on-disk file only).
- AC-1 (contract doc exists with 13 rows), AC-2 (`just test` green + the deliberate
  `data-pointer-x` failure check), AC-4 (AGENTS.md links the contract) — **no E2E scenario**; see
  "Not covered here" for why each is unit-level or review-level.

**Decision:** there is exactly one E2E scenario in this doc. The guard test
(`Tests/OnDiskContractTests.cs`) is unit-level by nature: its scopes are repository *source* paths
(`web-component/src/**/*.ts`, `API/Program.cs`, `landing/**/*.html`, on-disk `API/wwwroot/*`) that
exist only in the checkout, never in the running stack. The served variants of `wwwroot` files
differ from disk only by the `<POINTER_SERVER>` substitution (`API/Program.cs` injected-files
middleware), so a served re-scan of attributes/storage-keys/globals would be a strict subset of the
unit guard's signal — a duplicate. The one genuinely non-duplicate runtime claim is that the
serving middleware delivers `install.sh` with the right content type, the AC-3 gitignore line, and
the origin injected — nothing in the guard covers the middleware.

## Preconditions

- Stack up (`e2e/scripts/reset.sh` completed; no seed needed — the scenario is anonymous static
  serving). R1-01 merged (task 4: `!.pointer/config.json` present in `API/wwwroot/install.sh`).
- Mailpit not required.

## Scenarios

| id | intent | tier | layer | role | steps | expected | evidence |
|---|---|---|---|---|---|---|---|
| R1-01-01 | `served-install-sh-contract` | PR | api | — | 1. `GET http://localhost:8090/install.sh` (no `Authorization`). 2. `GET http://localhost:8090/skill.md`. 3. Grep both bodies for the literal placeholder `<POINTER_SERVER>`. | 1 → 200; header `content-type` === `text/x-shellscript; charset=utf-8` (Program.cs injected-files branch); body contains the gitignore line `!.pointer/config.json` (AC-3 on the served artifact) and the frozen names `.pointer/` and `!.pointer/stack.json`; body does **not** contain `<POINTER_SERVER>` (origin rewrite ran). 2 → 200; `content-type` === `text/markdown; charset=utf-8`; body contains `.pointer/credentials.env`; no `<POINTER_SERVER>`. 3 → zero matches in both. | `report.md` scenario row; bodies diffed against `API/wwwroot/` on failure |

## Spec files

- `e2e/api/served-contract.spec.mjs` — R1-01-01 (node `node:test`).
- New helper: `getRaw(path)` in `e2e/scripts/lib/api.mjs` returning `{ status, headers, text }` —
  the existing `call()` JSON-unwraps and cannot assert content types or raw text.

## Not covered here

- `FrozenNames_AreDocumented` and `ServedFiles_UseOnlyFrozenNames` (attribute/storage-key/global
  allowlists) — unit, `Tests/OnDiskContractTests.cs`: they must read the repo tree, which only
  exists in the checkout; the running stack exposes a strictly smaller surface (see the Decision in
  Covers).
- AC-2's deliberate-failure check (`data-pointer-x` added to `pointer-init.md` makes the unit test
  fail naming the literal, then reverted) — manual, one-time, belongs in the R1-07-style execution
  report per the doc's own Report template.
- AC-1 (doc exists, 13 rows) and AC-4 (AGENTS.md paragraph) — code review; nothing executable.
- Served `/pointer.js` / `/embed.js` behaviour — not this doc's surface (R1-04's `widget-served`
  doctor check and R3-03 own those).

## Flake notes

- None beyond running after `reset.sh` (server readiness is H-01's job). The middleware runs before
  `UseStaticFiles`, so caching is not a factor; both GETs are cheap and order-independent.

## State coupling

None — its single scenario is anonymous and stateless, so every scenario here is independently runnable and eligible for a solo retry (harness §9).
