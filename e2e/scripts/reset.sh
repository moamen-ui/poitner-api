#!/usr/bin/env bash
# Full deterministic reset: drops the Postgres volume, brings the stack back up, waits for the
# API to be ready. Zero AI involvement — see docs/E2E_TEST_PLAN.md's "Token-cost" framing.
set -euo pipefail
cd "$(dirname "$0")/../.."

# scripts/local-e2e-gate.sh points these at an isolated compose project (alternate ports) so it
# never touches the shared dev stack. Unset — the normal case, every CI run today — every `dc`
# call below is exactly the bare `docker compose` this script always ran, and the URLs/container
# names below default to exactly what CI has always waited on.
COMPOSE_ARGS=()
[ -n "${E2E_COMPOSE_PROJECT:-}" ] && COMPOSE_ARGS+=(-p "$E2E_COMPOSE_PROJECT")
if [ -n "${E2E_COMPOSE_FILES:-}" ]; then
  IFS=':' read -r -a _e2e_compose_files <<< "$E2E_COMPOSE_FILES"
  for _f in "${_e2e_compose_files[@]}"; do COMPOSE_ARGS+=(-f "$_f"); done
fi
dc() { docker compose "${COMPOSE_ARGS[@]}" "$@"; }

# Never hardcode the project name: with no `-p`/`COMPOSE_PROJECT_NAME`, Compose derives it from
# the checkout directory's basename — which on CI is "poitner-api" (the GitHub repo name is
# misspelled there), not "pointer-api". Asking Compose itself for its own resolved name is what
# actually matches every container's `com.docker.compose.project` label in both places.
detect_project_name() {
  if [ -n "${E2E_COMPOSE_PROJECT:-}" ]; then
    printf '%s' "${E2E_COMPOSE_PROJECT}"
    return
  fi
  local name
  name=$(dc config --format json 2>/dev/null \
    | node -e "try{process.stdout.write(JSON.parse(require('fs').readFileSync(0,'utf8')).name||'')}catch{}" 2>/dev/null || true)
  if [ -z "$name" ]; then name="${COMPOSE_PROJECT_NAME:-}"; fi
  if [ -z "$name" ]; then name=$(basename "$PWD" | tr '[:upper:]' '[:lower:]' | sed -E 's/[^a-z0-9_-]+/-/g'); fi
  printf '%s' "$name"
}

PROJECT_NAME="$(detect_project_name)"
API_URL="${E2E_API_URL:-http://localhost:8090}"
VERDACCIO_URL="${VERDACCIO_URL:-http://localhost:4873}"

echo "==> Resetting stack (docker compose down -v) [project: ${PROJECT_NAME:-unknown}]"
dc down -v --remove-orphans

# `down -v` can report the volume "still in use" if a container is slow to fully exit (observed:
# a prior interrupted run's containers hadn't finished tearing down yet), which then makes the
# next `up -d` fail with a container-name conflict. Wait for both containers to actually disappear
# before proceeding, retrying the teardown once if they haven't. Matched by compose labels rather
# than by an assumed "<project>-<service>-1" name string — robust to the project name above being
# empty (docker ps then simply finds nothing, matching before this label filter existed) too.
wait_for_removal() {
  local svc
  for _ in $(seq 1 15); do
    local found=0
    for svc in api db; do
      if [ -n "$(docker ps -a --filter "label=com.docker.compose.project=${PROJECT_NAME}" --filter "label=com.docker.compose.service=${svc}" -q)" ]; then
        found=1
      fi
    done
    [ "$found" -eq 0 ] && return 0
    sleep 1
  done
  return 1
}

if ! wait_for_removal; then
  echo "==> Containers still present after down -v — retrying teardown"
  dc down -v --remove-orphans
  wait_for_removal || { echo "==> Containers still present — remove them manually and re-run"; exit 1; }
fi

echo "==> Starting stack (docker compose up -d)"
dc up -d

echo "==> Waiting for API on ${API_URL} ..."
api_waited=0
until curl -sf "${API_URL}/swagger/v1/swagger.json" > /dev/null 2>&1; do
  if [ "$api_waited" -ge 180 ]; then
    echo "==> API did not come up in 180 s — container status and last 200 log lines:"
    dc ps
    dc logs --no-color --tail 200 api
    dc logs --no-color --tail 200 db
    exit 1
  fi
  sleep 2
  api_waited=$((api_waited + 2))
done

echo "==> API ready."

if dc ps --services 2>/dev/null | grep -q '^verdaccio$'; then
  echo "==> Waiting for Verdaccio on ${VERDACCIO_URL} ..."
  verdaccio_waited=0
  until curl -sf "${VERDACCIO_URL}/-/ping" > /dev/null 2>&1; do
    if [ "$verdaccio_waited" -ge 120 ]; then
      echo "==> Verdaccio did not come up in 120 s — last 100 log lines:"
      dc logs --tail 100 verdaccio
      exit 1
    fi
    sleep 1
    verdaccio_waited=$((verdaccio_waited + 1))
  done
  echo "==> Verdaccio ready."
fi

