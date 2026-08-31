#!/usr/bin/env bash
# Full deterministic reset: drops the Postgres volume, brings the stack back up, waits for the
# API to be ready. Zero AI involvement — see docs/E2E_TEST_PLAN.md's "Token-cost" framing.
set -euo pipefail
cd "$(dirname "$0")/../.."

echo "==> Resetting stack (docker compose down -v)"
docker compose down -v --remove-orphans

# `down -v` can report the volume "still in use" if a container is slow to fully exit (observed:
# a prior interrupted run's containers hadn't finished tearing down yet), which then makes the
# next `up -d` fail with a container-name conflict. Wait for both named containers to actually
# disappear before proceeding, retrying the teardown once if they haven't.
wait_for_removal() {
  for _ in $(seq 1 15); do
    if ! docker ps -a --format '{{.Names}}' | grep -qE '^pointer-api-(api|db)-1$'; then
      return 0
    fi
    sleep 1
  done
  return 1
}

if ! wait_for_removal; then
  echo "==> Containers still present after down -v — retrying teardown"
  docker compose down -v --remove-orphans
  wait_for_removal || { echo "==> Containers still present — remove them manually and re-run"; exit 1; }
fi

echo "==> Starting stack (docker compose up -d)"
docker compose up -d

echo "==> Waiting for API on :8090 ..."
until curl -sf http://localhost:8090/swagger/v1/swagger.json > /dev/null 2>&1; do
  sleep 2
done

echo "==> API ready."
