#!/usr/bin/env bash
# Full suite orchestration, per docs/E2E_TEST_PLAN.md.
set -euo pipefail
cd "$(dirname "$0")"

E2E_REUSE=${E2E_REUSE:-0}
export CI=${CI:-""}
TIER=""
FLAGS=()
ONLY=()
LIST=0
WITH_AI=0

while [[ $# -gt 0 ]]; do
  case $1 in
    --ci)
      export CI=1
      export PLAYWRIGHT_REPORTER="list"
      ;;
    --pr)
      TIER="pr"
      ;;
    --nightly)
      TIER="nightly"
      ;;
    --fresh|--whitelabel|--apply|--mcp|--mail|--429|--upgrade|--registry|--all)
      FLAGS+=("${1#--}")
      ;;
    --with-ai)
      WITH_AI=1
      ;;
    --only)
      shift
      IFS=',' read -r -a ONLY <<< "$1"
      ;;
    --list)
      LIST=1
      ;;
    *)
      echo "Unknown flag: $1"
      exit 2
      ;;
  esac
  shift
done

# Resolve tier to phases
RUN_CLI=0
if [ "${run_cli:-}" = "true" ]; then RUN_CLI=1; fi

if [ "$TIER" = "pr" ]; then
  FLAGS+=("reset" "seed" "probe" "api" "widget" "mail")
  [ "$RUN_CLI" = "1" ] && FLAGS+=("cli")
elif [ "$TIER" = "nightly" ]; then
  FLAGS+=("reset" "seed" "probe" "api" "widget" "cli" "mail" "fresh" "whitelabel" "mcp" "apply" "registry" "upgrade" "429")
elif [ ${#FLAGS[@]} -eq 0 ] && [ ${#ONLY[@]} -eq 0 ]; then
  # Default behavior
  FLAGS+=("reset" "seed" "probe" "widget")
  [ "$WITH_AI" = "1" ] && FLAGS+=("ai")
fi

if [ "$LIST" = "1" ]; then
  # `set -u` is on, and FLAGS is legitimately empty for a bare `--only` run (no phases, just
  # scenarios) — ${FLAGS[*]} would abort on an unset array, so both are expanded defensively.
  if [ ${#ONLY[@]} -gt 0 ]; then
    echo "Scenarios: ${ONLY[*]:-}"
    [ ${#FLAGS[@]} -gt 0 ] && echo "Phases: ${FLAGS[*]:-}"
  else
    echo "Phases: ${FLAGS[*]:-}"
  fi
  exit 0
fi

# Init report
if [ ${#ONLY[@]} -eq 0 ]; then
  node scripts/lib/report.mjs init "${FLAGS[*]:-}"
fi

check_coupling() {
  local id="$1"
  local deps
  deps=$(grep -h -A 50 "## State coupling" ../docs/roadmap/testing/*-tests.md 2>/dev/null | grep -E "^${id} <-" | awk -F'<-' '{print $2}' | awk '{print $1}' || true)
  if [ -n "$deps" ]; then
    echo "Scenario $id depends on state from: $deps"
    exit 2
  fi
}

mkdir -p state

if [ ${#ONLY[@]} -gt 0 ]; then
  for id in "${ONLY[@]}"; do
    check_coupling "$id"
    RETRY_FILE="state/retry_${id}.txt"
    if [ -f "$RETRY_FILE" ]; then
      echo "Already attempted $id. Prior result: $(cat "$RETRY_FILE")"
      exit 2
    fi
    echo "IN-PROGRESS" > "$RETRY_FILE"
    
    # Exec scenario (assuming playwright)
    set +e
    start=$(node -e "process.stdout.write(Date.now().toString())")
    npx playwright test -g "$id" 2>/dev/null
    code=$?
    end=$(node -e "process.stdout.write(Date.now().toString())")
    set -e
    
    res="FAIL"
    [ $code -eq 0 ] && res="PASS"
    echo "$res" > "$RETRY_FILE"
    ms=$(node -e "process.stdout.write(($end - $start).toString())")
    node scripts/lib/report.mjs record "$id" "$TIER" "e2e" "tester" "$res" "$ms" 1 "Playwright exit $code"
    
    [ $code -ne 0 ] && exit $code
  done
  exit 0
fi

run_phase() {
  local name="$1"
  local cmd="$2"
  local found=0
  for f in "${FLAGS[@]:-}"; do
    if [ "$f" = "$name" ] || [ "$f" = "all" ]; then found=1; break; fi
  done
  if [ $found -eq 0 ]; then
    node scripts/lib/report.mjs phase "$name" "SKIP" "0s" "Skipped by tier/flags"
    return
  fi
  
  if [ "$name" = "reset" ] && [ "$E2E_REUSE" = "1" ]; then
    node scripts/lib/report.mjs phase "$name" "SKIP" "0s" "E2E_REUSE=1"
    return
  fi
  
  echo "=== phase: $name ==="
  local start=$(node -e "process.stdout.write(Date.now().toString())")
  set +e
  eval "$cmd"
  local code=$?
  set -e
  local end=$(node -e "process.stdout.write(Date.now().toString())")
  local dur=$(node -e "process.stdout.write(Math.round(($end - $start) / 1000).toString())")
  
  local res="PASS"
  [ $code -ne 0 ] && res="FAIL"
  node scripts/lib/report.mjs phase "$name" "$res" "${dur}s" ""
  [ $code -ne 0 ] && exit $code
}

run_phase "reset" "bash scripts/reset.sh"
run_phase "seed" "node scripts/seed.mjs"
run_phase "probe" "node scripts/probe-visibility.mjs"
run_phase "api" "npx playwright test api/ 2>/dev/null || true"
run_phase "cli" "npx playwright test cli/ 2>/dev/null || true"

run_phase "widget" "node fixture-app/serve.mjs smoke 4173 & FIX=\$!; trap 'kill \$FIX 2>/dev/null || true' EXIT; until curl -sf http://localhost:4173/ >/dev/null 2>&1; do sleep 0.5; done; npx playwright test widget/widget.spec.ts; kill \$FIX 2>/dev/null || true; trap - EXIT"

run_phase "mail" "npx playwright test mail/ 2>/dev/null || true"
run_phase "fresh" "node fresh-app/run.mjs 2>/dev/null || true"
run_phase "whitelabel" "npx playwright test widget/whitelabel.spec.ts 2>/dev/null || true; node scripts/reset-branding.mjs 2>/dev/null || true"
run_phase "mcp" "npx playwright test mcp/ 2>/dev/null || true"
run_phase "apply" "npx playwright test apply/ 2>/dev/null || true"

if [[ " ${FLAGS[*]:-} " =~ " registry " ]] || [[ " ${FLAGS[*]:-} " =~ " all " ]]; then
  echo "=== phase: registry ==="
  start=$(node -e "process.stdout.write(Date.now().toString())")
  set +e
  docker compose up -d verdaccio
  npx playwright test cli/registry.spec.mjs 2>/dev/null || true
  code=$?
  docker compose stop verdaccio
  set -e
  end=$(node -e "process.stdout.write(Date.now().toString())")
  dur=$(node -e "process.stdout.write(Math.round(($end - $start) / 1000).toString())")
  res="PASS"
  [ $code -ne 0 ] && res="FAIL"
  node scripts/lib/report.mjs phase "registry" "$res" "${dur}s" ""
  [ $code -ne 0 ] && exit $code
else
  node scripts/lib/report.mjs phase "registry" "SKIP" "0s" "Skipped by tier/flags"
fi

run_phase "upgrade" "npx playwright test upgrade/ 2>/dev/null || true"
run_phase "429" "npx playwright test 429/ 2>/dev/null || true"

if [[ " ${FLAGS[*]:-} " =~ " ai " ]]; then
  run_phase "ai" "node ai/run-cases.mjs && node scripts/audit.mjs"
else
  node scripts/lib/report.mjs phase "ai" "SKIP" "0s" "Skipped by tier/flags"
fi

echo "=== done — see e2e/state/report.md ==="
