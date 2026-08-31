#!/usr/bin/env bash
# Full suite orchestration, per docs/E2E_TEST_PLAN.md. Zero-AI phases (reset/seed/probe/widget)
# always run; Layer B (AI-under-test cases) only runs when --with-ai is passed, since it needs
# opencode/GLM, Claude Code, and/or the Antigravity CLI installed and will spend real AI tokens.
set -euo pipefail
cd "$(dirname "$0")"

WITH_AI=false
for arg in "$@"; do
  [ "$arg" = "--with-ai" ] && WITH_AI=true
done

echo "=== [1/5] reset ==="
bash scripts/reset.sh

echo "=== [2/5] seed (e2e-alpha, e2e-beta ground truth) ==="
node scripts/seed.mjs

echo "=== [3/5] probe-visibility (zero-AI regression checks) ==="
node scripts/probe-visibility.mjs

echo "=== [4/5] widget (real Playwright browser run against e2e-widget-smoke) ==="
node fixture-app/serve.mjs smoke 4173 &
FIXTURE_PID=$!
trap 'kill "$FIXTURE_PID" 2>/dev/null || true' EXIT
until curl -sf http://localhost:4173/ > /dev/null 2>&1; do sleep 0.5; done
npx playwright test widget/widget.spec.ts
kill "$FIXTURE_PID" 2>/dev/null || true
trap - EXIT

if [ "$WITH_AI" = true ]; then
  echo "=== [5/5] AI-under-test cases (TC1-TC5, budget: 9 invocations/CLI) ==="
  node ai/run-cases.mjs
  node scripts/audit.mjs
else
  echo "=== [5/5] skipped (pass --with-ai to run TC1-TC5 against installed AI CLIs) ==="
fi

echo "=== done — see e2e/state/report.md ==="
