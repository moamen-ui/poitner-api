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
    --fresh|--whitelabel|--mock-domain|--mock-domain-tls|--apply|--mcp|--mail|--429|--upgrade|--registry|--dashboard|--all)
      if [ "$1" = "--mock-domain-tls" ]; then
        export E2E_MOCK_TLS=1
        export E2E_MOCK_DOMAIN="${E2E_MOCK_DOMAIN:-pick-it.test}"
        FLAGS+=("mock-domain")
      elif [ "$1" = "--mock-domain" ]; then
        export E2E_MOCK_DOMAIN="${E2E_MOCK_DOMAIN:-pick-it.test}"
        FLAGS+=("mock-domain")
      else
        FLAGS+=("${1#--}")
      fi
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

# Four specs gate their nightly-only scenarios on process.env.TIER. TIER was a plain shell
# variable, so those guards saw nothing and the scenarios skipped themselves even in a nightly
# run — the tier existed in the runner's own bookkeeping and nowhere a test could read it.
export TIER

# Resolve tier to phases
RUN_CLI=0
if [ "${run_cli:-}" = "true" ]; then RUN_CLI=1; fi

if [ "$TIER" = "pr" ]; then
  FLAGS+=("reset" "seed" "probe" "api" "widget" "mail")
  [ "$RUN_CLI" = "1" ] && FLAGS+=("cli")
elif [ "$TIER" = "nightly" ]; then
  FLAGS+=("reset" "seed" "probe" "api" "widget" "cli" "mail" "fresh" "whitelabel" "mock-domain" "dashboard" "mcp" "apply" "registry" "upgrade" "429")
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
# run.mjs only SCAFFOLDS the throwaway app; the scenarios that assert against it live in
# fresh-app/fresh.spec.ts. Running the driver alone made this phase report PASS for doing nothing
# but create a directory.
run_phase "fresh" "if [ -f fresh-app/run.mjs ]; then node fresh-app/run.mjs && bash scripts/pw.sh fresh-app 'fresh\\.spec\\.ts'; else echo 'fresh-app/run.mjs not written yet' >&2; exit 97; fi"
run_phase "whitelabel" "( npx playwright test fresh-app/fresh.spec.ts -g 'whitelabel'; wl=\$?; node scripts/reset-branding.mjs; node scripts/assert-branding-default.mjs; exit \$wl )"
run_phase "mock-domain" "( if [ -n \"\${E2E_MOCK_DOMAIN:-}\" ]; then npx playwright test rebrand/; md=\$?; node scripts/reset-branding.mjs; node scripts/restart-api.mjs; node scripts/assert-branding-default.mjs; node scripts/assert-origin-default.mjs; exit \$md; else node scripts/lib/report.mjs record R2-00-09 \"\${TIER:-nightly}\" api SA SKIP 0 1 'E2E_MOCK_DOMAIN unset'; node scripts/lib/report.mjs record R2-00-10 \"\${TIER:-nightly}\" widget 'SA, DEV' SKIP 0 1 'E2E_MOCK_DOMAIN unset'; node scripts/lib/report.mjs record R2-00-11 \"\${TIER:-nightly}\" mail 'SA, DEV, WA' SKIP 0 1 'E2E_MOCK_DOMAIN unset'; node scripts/lib/report.mjs record R2-00-12 \"\${TIER:-nightly}\" api SA SKIP 0 1 'E2E_MOCK_DOMAIN unset'; node scripts/lib/report.mjs record R2-00-13 manual 'api+widget' SA SKIP 0 1 'E2E_MOCK_DOMAIN unset'; fi )"
# The dashboard specs self-skip when DASHBOARD_DIR is unset (they need a pointer-dashboard
# checkout), so this is safe to run unconditionally in the tiers that include it.
run_phase "dashboard" "bash scripts/pw.sh dashboard"
run_phase "mcp" "bash scripts/pw.sh mcp"
run_phase "apply" "bash scripts/pw.sh apply"

if [[ " ${FLAGS[*]:-} " =~ " registry " ]] || [[ " ${FLAGS[*]:-} " =~ " all " ]]; then
  echo "=== phase: registry ==="
  start=$(node -e "process.stdout.write(Date.now().toString())")
  set +e
  docker compose up -d verdaccio
  E2E_REGISTRY=1 bash scripts/pw.sh cli 'registry\.spec\.mjs'
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

# The upgrade phase owns the specs that restart the api container. E2E_DESTRUCTIVE is what
# their guards read; without it they skip, which is what keeps them out of the api phase.
# The destructive phase: every scenario that restarts the api container lives here and nowhere
# else. Both files also contain non-destructive scenarios, which simply pass again — a cheap
# duplicate beats a scenario that can restart the stack from inside a shared phase.
run_phase "upgrade" "( E2E_DESTRUCTIVE=1 bash scripts/pw.sh api 'key-rotation\\.spec\\.mjs' && E2E_DESTRUCTIVE=1 bash scripts/pw.sh cli 'doctor\\.spec\\.mjs' )"
# E2E_429 is what the spec's own guard reads. Its fallback heuristic (an argv containing
# "429") does NOT match the file path we pass, so setting it explicitly is what actually
# lets the burst scenario run instead of skipping itself in its own dedicated phase.
run_phase "429" "E2E_429=1 bash scripts/pw.sh api 'rate-limits\\.spec\\.mjs'"

if [[ " ${FLAGS[*]:-} " =~ " ai " ]]; then
  run_phase "ai" "node ai/run-cases.mjs && node scripts/audit.mjs"
else
  node scripts/lib/report.mjs phase "ai" "SKIP" "0s" "Skipped by tier/flags"
fi

# How much of the documented suite actually exists. Appended to every report so the distance
# between "specced" and "implemented" is visible on each run rather than inferred from phases that
# quietly had nothing to run.
node scripts/coverage.mjs --markdown >> state/report.md 2>/dev/null || true
node scripts/coverage.mjs | head -1

echo "=== done — see e2e/state/report.md ==="
