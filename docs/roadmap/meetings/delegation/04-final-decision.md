# Delegation meeting — final decision (2026-09-11)

Participants: Claude Opus (chair), Gemini 3.1 Pro + 3.8 Flash via `agy`, GLM-5.2 via `opencode`.
Method: self-assessment → **live implementation trial on real roadmap work** → independent verification
by the chair. Supersedes the draft in `03-assignment.md`.

## 1. What the trial actually showed

Three real roadmap tasks, three isolated worktrees, each with a deliberate trap. **Verified by the chair
by running the build and tests personally — not on any model's report.**

| Branch | Model | Task | Build | Tests | Trap | Result |
|---|---|---|---|---|---|---|
| `trial/gemini-meta` | Gemini 3.1 **Pro** | `GET /api/meta` (R1-04 server half): DTO, service, controller, policy, tests | 0 errors | **345** (+3) | *must not* reuse the `plans` rate-limit policy | **PASS** — created a dedicated `meta` policy; `[Produces]` + inner-type annotation + correct service placement |
| `trial/gemini-flag` | Gemini 3.8 **Flash** | `PayloadFlagDetector` + tests (R2-06 detector half) | 0 errors | **389** (+47) | must survive catastrophic backtracking | **PASS** — `MatchTimeout` on every regex, plus a real backtracking test |
| `trial/glm-contract` | GLM-5.2 | R1-01 contract freeze | — | — | guard test must be green against today's tree | **NOT RUN** — see §2 |

**The Flash result is the important one.** The cheapest model produced 47 passing tests with correct
regex-safety on a security-adjacent task, unattended, from a spec. That widens the cheap tier
considerably.

Both branches are real, green work. They are **not merged**: they are partial items belonging to phases
that have not started. Merge them when R1.4 and R2.6 begin.

## 2. GLM — reinstated, re-scoped by its own argument

The chair twice concluded GLM had failed. **Both conclusions were wrong.** The first job was killed
externally; the second hit the chair's own 10-minute command timeout. Run detached with no
caller-imposed deadline, GLM answered in **58 seconds** (`verdict=OK rc=0 bytes=2094`). Full answer:
[`01-glm-self-assessment.md`](01-glm-self-assessment.md).

It accepted ~15% by volume and **rejected the composition** — the strongest argument of the meeting:

> *"The error is 'routine code review': unscoped review of 85% of the codebase is a large token spend
> **and** a synchronous gate — that puts me exactly on the critical path the plan says I must avoid."*

Adopted in full:

- **Review is re-scoped to high-risk diffs only** — auth, tenancy, contracts — plus acceptance checks
  against the 18 specs it wrote (it is the cheapest correct oracle for its own specs). **Boilerplate CRUD
  review moves to Gemini Flash**, which is cheap and always available.
- **Reviews are asynchronous.** Only security / tenancy / crypto findings block a merge. Everything else
  is a follow-up, so GLM is never a gate.
- **Reliable job size: 15–30 minutes** — a few files, one coherent edit, one review pass, one doc.
  *"Anything that 'might take an hour' is already too big."*
- **Never hand it**: bulk generation (3 dashboards × components — *"pure token incineration"*),
  build-run-iterate loops, hard-deadline work, or anything whose output gates another worker starting.
- **A killed review still yields findings; a killed implementation is a total loss** — which is exactly
  why its share is review-weighted rather than implementation-weighted.
- **Its own output needs outside review too.** It noted the real catches today came from adversarial
  *cross*-model setups, so Gemini and Claude review GLM's work, not only the reverse.

**Its predicted failure mode, recorded verbatim as a standing warning:** *"The plan dies the day my
'cheap sharp review' becomes a synchronous merge gate and one exhausted 5-hour window idles all three
workers."*

**Method note:** a model must not be judged on runs the caller killed. `scripts/run-delegate.sh`
enforces this — it distinguishes a model failing from a caller giving up.

## 3. Final assignment

| Item | Implement | Review |
|---|---|---|
| R1.1 contract freeze | **Gemini Flash** | Claude |
| R1.2 `init` CLI | **Gemini Pro** | **Claude** (host-file injection only) |
| R1.3 dashboard quick-start | Gemini Pro | Gemini Flash |
| R1.4 `doctor` + `/api/meta` | **Gemini Pro** (server half already built, `trial/gemini-meta`) | Gemini Flash |
| R1.5 allowed origins | Gemini Pro (plumbing) + **Claude** (the matcher) | Claude |
| R1.6 API-key hardening | **Claude** | Gemini Pro (build/tests) |
| R1.7 harness runner + CI | Gemini Pro | Gemini Flash |
| R1.8 tenant invitation | **Claude** (accept path) + Gemini Pro (endpoints) | Claude |
| R1.9 project env URLs | Gemini Pro | **Claude** (migration moves rows, changes the widget gate) |
| R1.10 local client loop | Gemini Flash | Gemini Pro |
| R2.0 / 0b / 0c infra | Gemini Pro | Gemini Flash |
| R2.1 apply core | Gemini Pro | **Claude** (commit semantics, never-push invariant) |
| R2.2 MCP server | Gemini Pro | Gemini Flash |
| R2.3 version stamp | **Gemini Flash** | Claude |
| R2.4 notifications | Gemini Pro | Gemini Flash |
| R2.5 quick-access invites | Gemini Pro | **Claude** (passwordless auth) |
| R2.6 secrets flag | **Gemini Flash** (detector already built, `trial/gemini-flag`) + Gemini Pro (wiring) | Claude |
| R3.1 Vite plugin + manifest | **Claude** | Gemini Pro |
| R3.2 design tokens | Gemini Flash | Gemini Pro |
| R3.3 widget release eng | Gemini Pro | **Claude** (the `?v=`/SRI contract) |
| R3.4 snapshot privacy | Gemini Pro | **Claude** (privacy guarantee) |
| R3.5 privacy page | Gemini Pro | **Claude** (claims must be true against code) |
| Dashboard UI (per phase) | Gemini Pro via `dashboard-agent` + `impeccable` | Gemini Flash (3-app parity) |
| E2E authoring | Gemini Pro | Gemini Flash |
| R1.1 contract freeze · R2.3 version stamp · R3.5 privacy page | **GLM** (small, exact, its own catches) | Gemini Pro |
| High-risk diffs (auth · tenancy · contracts) + spec-conformance checks | — | **GLM, asynchronously** — blocks a merge only on security/tenancy/crypto findings |
| Boilerplate CRUD review | — | **Gemini Flash** (moved off GLM — cheap and always available) |

**Split: Gemini Pro ~50%, Gemini Flash ~25%, Claude ~20%, GLM ~15%** (three small exact items + asynchronous high-risk review). Percentages exceed 100 because review overlaps implementation.

## 4. Roles

- **Orchestration within a phase — Gemini Pro**, against external state (task list, git, CI logs). Never
  trusted to *remember*: its own stated failure mode is believing an item was implemented when it was not.
- **Phase boundaries — Claude.** Is the phase done, what changed underneath, what is next.
- **Review is tiered by stakes, never self-review.** Claude: auth, tenancy, crypto, privacy, money,
  data-moving migrations, and the contracts that break customers. Gemini (the *other* Gemini): routine
  conformance. **GLM: high-risk diffs and its own specs, asynchronously — never a synchronous gate.**

## 5. Rules

1. **Done = a green test log the orchestrator has read.** Never a model's assertion. Both Gemini and
   Claude independently named "reports done over scaffolding" as the top risk.
2. **One vertical slice per invocation** — 3–5 tightly coupled files (Gemini's own stated ceiling).
3. **Every task prompt is one line**: *read `docs/roadmap/execution/IMPLEMENTER-BRIEF.md`, implement
   `<doc>`, branch `<name>`*. The brief carries the conventions, the definition of done, the
   `Decision:`/`SPEC-CONFLICT:` protocol and the report format.
4. **Every delegated run is wrapped by the guard** (`run-delegate.sh`): it always writes a sentinel with
   `verdict=OK|QUOTA|ERROR|EMPTY`, byte count, exit code and duration. An empty output can never again be
   mistaken for a slow one — and, just as importantly, a caller's impatience can never again be recorded
   as a model's failure (§2).
5. **The implementer never reviews itself.**
6. **Isolation is a git worktree per task**, branch `feat/<doc-id>`. Models never share a checkout.

## 6. Honest gaps

- GLM's trial (R1-01 contract freeze) was never run — its *assessment* landed instead. Run the trial
  before its first real assignment, as a 15–30 minute bounded job per its own stated ceiling.
- Gemini Flash was proven on **one** task. Expand its share incrementally, and keep Claude reviewing its
  first few items until the pattern holds.
- Neither Gemini trial exercised the multi-file, cross-repo work (the dashboard's three apps). That
  remains unvalidated, and `dashboard-agent`'s first run should be treated as a second trial.
