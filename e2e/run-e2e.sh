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
    if [ $code -eq 0 ]; then res="PASS"; fi
    echo "$res" > "$RETRY_FILE"
    ms=$(node -e "process.stdout.write(($end - $start).toString())")
    node scripts/lib/report.mjs record "$id" "$TIER" "e2e" "tester" "$res" "$ms" 1 "Playwright exit $code"
    
    if [ $code -ne 0 ]; then exit $code; fi
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
  local note=""
  if [ $code -eq 97 ]; then
    # Exit 97 from scripts/pw.sh: the phase's spec directory holds no test files. Surfaced as its
    # own state so an unwritten phase is visible in the report rather than counted as a pass.
    res="EMPTY"; note="no specs authored yet"; code=0
  elif [ $code -ne 0 ]; then
    res="FAIL"
  fi
  node scripts/lib/report.mjs phase "$name" "$res" "${dur}s" "$note"
  # Must be an `if`, not `[ ... ] && exit`: that form returns 1 when the phase PASSED, and with
  # `set -e` on it terminated the whole run after the first successful phase.
  if [ $code -ne 0 ]; then exit $code; fi
  return 0
}

run_phase "reset" "bash scripts/reset.sh"
run_phase "seed" "node scripts/seed.mjs"
run_phase "probe" "node scripts/probe-visibility.mjs"
run_phase "api" "bash scripts/pw.sh api"
run_phase "cli" "bash scripts/pw.sh cli"

run_phase "widget" "bash scripts/run-widget-phase.sh"

run_phase "mail" "bash scripts/pw.sh mail"
run_phase "fresh" "if [ -f fresh-app/run.mjs ]; then node fresh-app/run.mjs; else echo 'fresh-app/run.mjs not written yet' >&2; exit 97; fi"
run_phase "whitelabel" "( bash scripts/pw.sh widget 'whitelabel\\.spec\\.ts'; wl=\$?; node scripts/reset-branding.mjs 2>/dev/null; exit \$wl )"
run_phase "mcp" "bash scripts/pw.sh mcp"
run_phase "apply" "bash scripts/pw.sh apply"

if [[ " ${FLAGS[*]:-} " =~ " registry " ]] || [[ " ${FLAGS[*]:-} " =~ " all " ]]; then
  echo "=== phase: registry ==="
  start=$(node -e "process.stdout.write(Date.now().toString())")
  set +e
  docker compose up -d verdaccio
  bash scripts/pw.sh cli 'registry\.spec\.mjs'
  code=$?
  docker compose stop verdaccio
  set -e
  end=$(node -e "process.stdout.write(Date.now().toString())")
  dur=$(node -e "process.stdout.write(Math.round(($end - $start) / 1000).toString())")
  res="PASS"
  if [ $code -ne 0 ]; then res="FAIL"; fi
  node scripts/lib/report.mjs phase "registry" "$res" "${dur}s" ""
  if [ $code -ne 0 ]; then exit $code; fi
else
  node scripts/lib/report.mjs phase "registry" "SKIP" "0s" "Skipped by tier/flags"
fi

run_phase "upgrade" "bash scripts/pw.sh upgrade"
run_phase "429" "bash scripts/pw.sh 429"

if [[ " ${FLAGS[*]:-} " =~ " ai " ]]; then
  run_phase "ai" "node ai/run-cases.mjs && node scripts/audit.mjs"
else
  node scripts/lib/report.mjs phase "ai" "SKIP" "0s" "Skipped by tier/flags"
fi

echo "=== done — see e2e/state/report.md ==="
