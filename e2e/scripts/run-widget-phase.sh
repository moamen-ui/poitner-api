#!/usr/bin/env bash
# Widget phase: serve the smoke fixture, run the widget spec against it, always stop the server,
# and exit with the TEST's status.
#
# This lives in a file rather than an inline `eval` string in run-e2e.sh because that form ended
# with `trap - EXIT`, whose exit status is always 0 — so a failing widget spec was reported PASS.
set -uo pipefail
cd "$(dirname "$0")/.."

PORT="${WIDGET_FIXTURE_PORT:-4173}"

node fixture-app/serve.mjs smoke "$PORT" &
FIXTURE_PID=$!
cleanup() { kill "$FIXTURE_PID" 2>/dev/null || true; }
trap cleanup EXIT

# Wait for it to actually accept connections; give up rather than hang a CI job forever.
for _ in $(seq 1 60); do
  if curl -sf "http://localhost:${PORT}/" >/dev/null 2>&1; then break; fi
  if ! kill -0 "$FIXTURE_PID" 2>/dev/null; then
    echo "fixture server exited before serving on :${PORT}" >&2
    exit 1
  fi
  sleep 0.5
done

if ! curl -sf "http://localhost:${PORT}/" >/dev/null 2>&1; then
  echo "fixture server never came up on :${PORT}" >&2
  exit 1
fi

# The whole directory, not one file. widget/origins.spec.ts and widget/project-env-urls.spec.ts
# were authored, merged, and counted as coverage while no phase ever ran them — they start their
# own fixture servers, so the only thing missing was this filter. Anchored so it cannot match the
# repo path (see scripts/pw.sh).
npx playwright test '(^|/)widget/'
exit $?
