# Implementer brief — read this first, every time

One file that every delegated task prompt points at, so the task prompt itself can be three lines.
Works with any tool (Claude Code, `agy`, `opencode`) because it is just a file you read.

**A task prompt should say only:** *"Read `docs/roadmap/execution/IMPLEMENTER-BRIEF.md`, then implement
`<doc path>` §`<section>`. Branch `<name>`."* Everything else is here.

---

## 1. Before you write anything

1. Read the execution doc you were given, in full. It is the contract — contracts, file-level tasks,
   acceptance criteria and out-of-scope are all stated. If it disagrees with this brief, **the doc wins**.
2. Read [`01-OVERVIEW.md`](01-OVERVIEW.md) — binding conventions for this repo.
3. Read [`00-API-INVENTORY.md`](00-API-INVENTORY.md) — verified facts about the existing API. **Do not
   re-derive them**; if something there is wrong, say so in your report rather than quietly working around it.
4. Open every file you intend to touch and every symbol you intend to call. You may cite nothing you
   have not read.

## 2. Conventions that are not negotiable

- **Responses** are wrapped in `Result<T>`; controllers annotate the **inner** type —
  `[ProducesResponseType(typeof(Inner), 200)]` — plus `[Produces("application/json")]`. The generated
  clients break otherwise.
- **New tables** get `OwnerId` and an explicit query-filter bucket in `AppDbContext` (strict-own unless
  the doc says otherwise), and writes stamp it via `TenantStamp.OwnerFor(currentUser)` — except where the
  doc names a different owner (a project's rows take the *project's* owner).
- **Migrations are additive.** No column drops in the release that introduces a replacement; drop one
  release later. Never rename a migration id.
- **Layout**: services in `Application/Services/{Interfaces,Implementation}`, DTOs in
  `Application/DTOs/<Area>`, validators in `Application/Validators`, EF mappings in
  `Infrastructure/Mappings` with **explicit `HasColumnName`**, tests in `Tests/` following the nearest
  existing `*Tests.cs` fixture pattern.
- **Style**: match the surrounding file exactly. The repo formats with CSharpier (`just fmt`); if it is
  not installed, mimic the neighbouring code rather than reformatting anything.
- **The widget** (`web-component/src/`) is built with `npm run build`; `API/wwwroot/pointer.{js,css}` are
  artifacts — never hand-edit them, and commit the rebuilt files when you change the source.
- **Frozen names**: anything in [`../../ON-DISK-CONTRACT.md`](../../ON-DISK-CONTRACT.md) (once R1-01 lands)
  or listed in `R1-01-contract-freeze.md` is customer-visible and must not be renamed, however tempting.
- **The AI never runs `git push`.** You may commit. You may not push, merge, or rebase onto `main`.

## 3. Definition of done — all four, no exceptions

1. `dotnet build` succeeds with **0 errors**.
2. `dotnet test` is **green**, including the tests the doc told you to add. For CLI work:
   `npm run typecheck && npm test && npm run build` in `cli/`.
3. Every acceptance criterion in the doc is either met, or listed as not-met **with the reason**.
4. Your work is committed on your branch with a conventional-commit message.

**"Done" is a green test log, not your opinion.** An item reported done over scaffolding is the single
most expensive failure in this programme — if you ran out of room, say what is missing.

## 4. When the spec is silent

Make a decision, implement it, and list it in your report prefixed `Decision:` with one line of
reasoning. Do not stop to ask, and do not leave a `TODO` where a decision was needed.

When the spec is **wrong** — it cites a symbol that does not exist, or contradicts the code — stop and
report it as `SPEC-CONFLICT:` with the evidence. Do not silently "fix" the spec by guessing intent.

## 5. Scope

Touch only what the task requires. Never modify `docs/roadmap/**` (the plan is not yours to edit), files
outside your task's file list, or another branch's work. If you believe something outside scope is
broken, report it; do not fix it.

## 6. Report format (your final message)

```
1. Files created/changed — one line each
2. Acceptance criteria — each met / not-met, with how you verified it
3. Build + test output — the summary lines, pasted verbatim
4. Decisions — every "Decision:" you made
5. SPEC-CONFLICT — anything where the doc and the code disagree
6. NEEDS_REVIEW — anything you were unsure about, so the reviewer looks there first
7. Not done — what you could not complete, and why
```

Sections 3 and 7 are what the next model and the orchestrator actually act on. A report without pasted
build/test output is treated as **not done**.
