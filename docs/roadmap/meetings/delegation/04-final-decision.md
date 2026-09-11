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

## 2. GLM could not participate — and that is the finding

- Earlier today, under light load: wrote 18 execution/test documents and produced the sharpest reviews
  of the session (the frontmatter stamp, the unsound `?v=`/SRI contract, the false deletion claim).
- Then: hit its 5-hour cap twice. After the cap reset, a 9-document self-assessment ran **70 minutes and
  produced nothing**. A compact, no-file-reading, four-question version ran **>10 minutes and produced
  nothing**. A one-line probe answers in seconds.

**Diagnosis: alive but throughput-collapsed.** Not a quota wall — a crawl. It cannot be scheduled.

**Consequence — GLM is removed as an assignee.** It is not "15% of the work"; it is an *opportunistic
reviewer*. Give it a review batch when it happens to be responsive, never an item with a deadline, never
anything another model is waiting on. Its earlier output proves the quality is real; today proves the
availability is not.

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
| **Any of the above** | — | **GLM, opportunistically**, when responsive |

**Split: Gemini Pro ~55%, Gemini Flash ~25%, Claude ~20%, GLM ad-hoc.**

## 4. Roles

- **Orchestration within a phase — Gemini Pro**, against external state (task list, git, CI logs). Never
  trusted to *remember*: its own stated failure mode is believing an item was implemented when it was not.
- **Phase boundaries — Claude.** Is the phase done, what changed underneath, what is next.
- **Review is tiered by stakes, never self-review.** Claude: auth, tenancy, crypto, privacy, money,
  data-moving migrations, and the contracts that break customers. Gemini (the *other* Gemini): routine
  conformance. GLM: whenever it answers.

## 5. Rules

1. **Done = a green test log the orchestrator has read.** Never a model's assertion. Both Gemini and
   Claude independently named "reports done over scaffolding" as the top risk.
2. **One vertical slice per invocation** — 3–5 tightly coupled files (Gemini's own stated ceiling).
3. **Every task prompt is one line**: *read `docs/roadmap/execution/IMPLEMENTER-BRIEF.md`, implement
   `<doc>`, branch `<name>`*. The brief carries the conventions, the definition of done, the
   `Decision:`/`SPEC-CONFLICT:` protocol and the report format.
4. **Every delegated run is wrapped by the guard** (`run-delegate.sh`): it always writes a sentinel with
   `verdict=OK|QUOTA|ERROR|EMPTY`, byte count, exit code and duration. An empty output can never again be
   mistaken for a slow one — this is how GLM's 70-minute silent failure was caught.
5. **The implementer never reviews itself.**
6. **Isolation is a git worktree per task**, branch `feat/<doc-id>`. Models never share a checkout.

## 6. Honest gaps

- GLM never delivered a self-assessment; its share was decided *for* it on observed behaviour. Re-ask when
  it recovers — it may legitimately argue for the reviewer role it has earned on quality.
- Gemini Flash was proven on **one** task. Expand its share incrementally, and keep Claude reviewing its
  first few items until the pattern holds.
- Neither Gemini trial exercised the multi-file, cross-repo work (the dashboard's three apps). That
  remains unvalidated, and `dashboard-agent`'s first run should be treated as a second trial.
