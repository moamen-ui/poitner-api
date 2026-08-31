# Pointer E2E suite

Proves/exposes whether an AI coding tool can correctly prioritize and answer natural-language
questions about Pointer comments, given today's system has no priority/role field anywhere. Full
design doc: [`docs/E2E_TEST_PLAN.md`](../docs/E2E_TEST_PLAN.md).

## Setup

```bash
cd e2e
npm install
npx playwright install chromium
```

## Running

```bash
bash run-e2e.sh              # zero-AI: reset → seed → probe-visibility → widget spec
bash run-e2e.sh --with-ai    # also runs TC1-TC5 against installed AI CLIs (spends real tokens)
```

Individual phases (useful while iterating):

```bash
bash scripts/reset.sh                     # docker compose down -v && up -d, wait for /swagger
node scripts/seed.mjs                     # creates the e2e-alpha/e2e-beta ground truth
node scripts/probe-visibility.mjs         # zero-AI regression checks — must pass before Layer B
npx playwright test widget/widget.spec.ts # real browser run against e2e-widget-smoke
```

**Warning**: `scripts/reset.sh` runs `docker compose down -v`, which destroys the local Postgres
volume. Only run it against a stack you're fine wiping. `seed.mjs`/`probe-visibility.mjs` alone
don't require a reset — they create their own uniquely-keyed tenant/projects (`e2e-owner@
example.com`, `e2e-alpha`, `e2e-beta`) and coexist fine with pre-existing data, which is how they
were validated during development.

## Layer B (AI-under-test) prerequisites

- `claude` CLI on `PATH` (headless `-p` mode).
- `opencode` CLI on `PATH`, configured with the `zai-coding-plan/glm-5.2` provider (see
  `~/.claude/skills/delegate-to-glm/SKILL.md`).
- Antigravity CLI invocation is **not yet verified** in `ai/harness.mjs` — see
  `docs/E2E_TEST_PLAN.md`'s "Known limitations."

## Known caveats found while validating this suite

- The widget's picker intercepts clicks at the `document` capture phase and calls
  `stopPropagation()`, so a page's own click handler never fires *while picking* — `widget.spec.ts`
  triggers the real bug with a plain click first, then picks the element afterward, matching how a
  real user actually encounters a bug before opening Pointer to report it.
- `PageContextSnapshot` capture (`web-component/src/pagecontext.ts`) only starts once
  `GET /api/projects/{key}/capture-config` resolves — which happens *after* the toolbar is already
  visible (`element.ts`'s `init()` renders chrome before awaiting it). A test that clicks
  immediately after `#pf-add` becomes visible can race ahead of capture starting.
- The widget's env `<select>` (`#pf-env`) only renders when the `environment` attribute/config
  isn't fixed at install time — set neither on `<pointer-feedback>` nor via
  `window.__POINTER_CONFIG__` if a test needs to switch environments interactively.
