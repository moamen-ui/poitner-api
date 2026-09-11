# Model assignment for implementation (draft — GLM's self-assessment pending)

Sources: `02-agy-self-assessment.md` (Gemini 3.1 Pro, answered), `01-glm-self-assessment.md` (GLM-5.2 —
**not answered, 5-hour usage limit**, re-ask after it resets), Claude's own position, and **observed
behaviour in this repo today**, which outranks all three self-reports.

## 1. What we actually observed today (evidence, not opinion)

| Model | Observed | Implication |
|---|---|---|
| **GLM-5.2** (opencode, legacy lite) | Wrote 18 detailed execution/test docs; its adversarial reviews found real defects (frontmatter stamp, `?v=` SRI contract, false deletion claim). **Hit the 5-hour limit twice under sustained load.** | Excellent quality per token, but **throughput-capped**. Cannot be on the critical path of a long day. |
| **Gemini 3.1 Pro** (agy, Pro) | Ran 25-minute multi-file jobs reliably, repeatedly, no quota trouble. Reviews were **mixed on accuracy** — miscounted R3-01's acceptance criteria (claimed 7, there are 10), called a doc's Caddyfile precondition false when the doc said "post task 7", flagged specs for features that do not exist yet as defects. | **The workhorse.** Highest throughput, biggest context. Verify its *claims*; trust its *volume*. |
| **Claude Opus** (this session) | Three reviews found the deepest defects: the `email_enabled` gate that made every mail scenario impossible, a monetization bypass, a DI cycle in a prescribed refactor, a test suite over its own rate-limit budget. **Fable subagent quota exhausted mid-session; resets weekly.** | **Scarcest and sharpest.** Spend only where a plausible-but-wrong answer is expensive. |

## 2. Roles

**Orchestrator — split, not single.** Gemini nominated itself on context size, but its own answer names the
disqualifier: *"I will hallucinate that a previously discussed task was actually implemented, dropping
items from the queue."* A context window does not fix that; **external state** does.

- **Within a phase — Gemini (agy).** Dispatch tasks, collect diffs, run builds and tests, update the
  state file. Cheap, high-throughput, and never trusted to *remember* anything: the task list, git
  history and CI logs are the memory.
- **At phase boundaries — Claude.** Is this phase actually done, what changed underneath us, what is next.
  A handful of turns per phase, not per task.

**Reviewer — never the implementer, and tiered by stakes.**
- **Claude** reviews: anything touching auth, tenancy, crypto, money, or a migration that moves data.
- **GLM** reviews: routine diffs, doc/spec conformance, test correctness — its strongest observed skill,
  and the tier where its throughput cap does not hurt.
- **Gemini** never reviews its own output. (Its own words: *"I will suffer from model confirmation bias
  and approve my own hallucinations."*)

## 3. Assignment

Ids are the plan's release-table rows. "Review" is who reads the diff before it is called done.

| Item | Implement | Review | Reasoning |
|---|---|---|---|
| R1.1 contract freeze | **GLM** | Gemini | Pure doc + a guard test; small, exact, quota-cheap. |
| R1.2 `init` CLI | **Gemini** | **Claude** (injection logic only) | Bulk TS scaffolding is its HIGH. But it self-flags host-file injection as brittle — and it edits a *customer's* `index.html`, so that one module gets Claude eyes. |
| R1.3 dashboard quick-start | **Gemini** | GLM | Single-component change in one app. |
| R1.4 `doctor` + `/api/meta` | **Gemini** | GLM | Mechanical; the exit-code precedence is fully specced. |
| R1.5 allowed origins | **Gemini** (plumbing) + **Claude** (the matcher) | Claude | Wildcard/suffix matching and the quick-access carve-out are security logic; the DTO/controller wiring is not. |
| R1.6 API-key hardening | **Claude** | Gemini (build/tests only) | Crypto at rest, a backfill that nulls a column, a key-derivation fallback. Gemini explicitly refuses this category. |
| R1.7 harness runner + CI | **Gemini** | GLM | Shell + node + workflow; well-represented, low blast radius. |
| R1.8 tenant invitation | **Claude** (accept path) + **Gemini** (endpoints/UI wiring) | Claude | The accept path writes tenant + user + role + subscription anonymously, atomically. Everything around it is CRUD. |
| R1.9 project env URLs | **Gemini** | **Claude** | Additive CRUD, but the migration moves existing rows and it changes the live widget gate. |
| R1.10 local client loop | **Gemini** | GLM | Scripts + config; verifiable by running it. |
| R2.0/0b/0c infra + rehearsal | **Gemini** | GLM | Compose, Playwright, Verdaccio. Its Playwright rating is HIGH. |
| R2.1 apply core | **Gemini** | **Claude** | Bulk is mechanical; the git-commit semantics and the "AI never pushes" invariant are not. |
| R2.2 MCP server | **Gemini** | GLM | Stdio servers are dense in its training; tool schemas are fully specced. |
| R2.3 version stamp | **GLM** | Gemini | Tiny, exact, frontmatter-sensitive — GLM found that bug. |
| R2.4 notifications | **Gemini** | GLM | Textbook table + endpoints + widget badge. |
| R2.5 quick-access invites | **Gemini** | **Claude** | Passwordless auth path. |
| R2.6 secrets flag | **Gemini** | GLM | Pattern detector + an exposure matrix already written out. |
| R3.1 Vite plugin + manifest | **Claude** | Gemini | Gemini rates itself **LOW** and refuses; already flagged weeks-not-days. |
| R3.2 design tokens | **Gemini** | GLM | File scanning + JSON. |
| R3.3 widget release eng | **Gemini** | **Claude** | Vanilla-TS is its HIGH, but the `?v=`/SRI contract is the one that breaks every pinned host if wrong. |
| R3.4 snapshot privacy | **Gemini** | **Claude** | Its HIGH category, but it is a privacy guarantee — the sanitizer must not be plausible-but-wrong. |
| R3.5 privacy page | **GLM** | Claude | Prose that must be true against code; GLM caught the false deletion claim here. |
| Dashboard UI (per phase) | **Gemini** via `dashboard-agent` + `impeccable` | GLM (parity across the 3 apps) | Gemini self-rates MEDIUM and warns it mixes framework paradigms — so parity is what review checks. |
| E2E authoring | **Gemini** | GLM | HIGH, with the caveat it invents selectors; the docs already pin them, which is the mitigation. |

**Rough split:** Gemini ~65%, Claude ~20% (concentrated in auth/crypto/privacy/build-tooling), GLM ~15%
(small exact items + routine review).

## 4. The rules that make it work

1. **Done = a green CI log the orchestrator has read.** Gemini's own stated top risk is reporting "done"
   over scaffolding. Nothing is done on a model's say-so.
2. **One vertical slice per invocation** (Gemini's own limit: 3–5 tightly coupled files). Never batch
   across domains.
3. **Every task prompt carries**: the execution doc path, `00-API-INVENTORY.md`, `01-OVERVIEW.md`'s
   conventions, and the acceptance criteria verbatim. No implicit context.
4. **Every output returns**: files changed, the acceptance criteria checked off, the build/test output
   pasted, and a `NEEDS_REVIEW` marker on anything the model was unsure about (Gemini's proposal, adopted).
5. **The implementer never reviews itself**, and the reviewer is picked by stakes, not convenience.
6. **GLM is scheduled, not summoned.** It has a 5-hour rolling cap; give it small exact items and
   review batches, never the critical path.
7. **Claude is rationed.** If a task can be specified precisely enough for Gemini, it is not a Claude task.

## 5. Biggest risk

Both Gemini and Claude independently landed on the same one: **an item declared done that is only
scaffolded**, and the queue moves on. Mitigation is rule 1 — the green-log gate — plus the
`e2e-tester-agent` at the phase boundary, which is exactly the failure it exists to catch.

## 6. Open

- GLM's self-assessment (re-ask after its limit resets; it may argue for more or less of its own load).
- Whether Gemini-as-in-phase-orchestrator holds up in practice, or whether the state file is enough to
  let a cheaper model do it.
