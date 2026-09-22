#!/usr/bin/env bash
# Deploy the API on the production VM: dump the database, pull main, rebuild the api container,
# wait for it to listen, smoke-check.
#
# Runs ON the VM. Either interactively:
#   ssh vm 'bash -s' < scripts/deploy-api.sh
# or after `git pull` in ~/pointer-api:
#   bash ~/pointer-api/scripts/deploy-api.sh
#
# EF migrations auto-apply on API boot (Program.cs → MigrateAsync). That is why the dump comes first:
# docs/db/DB-RULES.md requires a fresh backup before any deploy that may carry a migration, and a
# restore procedure is in DEPLOY.md § Backups. The dump is labelled `pre-deploy` so it is easy to
# find if the migration has to be rolled back.
set -euo pipefail

REPO="${POINTER_REPO:-$HOME/pointer-api}"
cd "$REPO"

echo "== 1/4 pull =="
git pull --ff-only
git log --oneline -1

echo "== 2/4 backup =="
bash "$REPO/scripts/backup-db.sh" pre-deploy

echo "== 3/4 rebuild api =="
docker compose --env-file .env.prod -f docker-compose.prod.yml up -d --build api

echo "== 4/4 verify =="
for i in $(seq 1 30); do
  if docker compose -f docker-compose.prod.yml logs --since 3m api 2>/dev/null | grep -q "Now listening"; then
    break
  fi
  sleep 2
done
docker compose -f docker-compose.prod.yml ps api
docker compose -f docker-compose.prod.yml logs --since 3m api | grep -iE "Now listening|migrat|error|exception" | tail -20 || true
for path in /api/branding /swagger/v1/swagger.json; do
  code=$(curl -s -o /dev/null -w '%{http_code}' "https://api.pointer.moamen.work$path")
  echo "$path $code"
  [ "$code" = "200" ] || { echo "smoke FAILED on $path" >&2; exit 1; }
done
echo "deploy OK"
