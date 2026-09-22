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
# Migrations auto-apply on boot except those marked [ContractMigration] (DB-09) — see the block
# below for how those ship.
set -euo pipefail

REPO="${POINTER_REPO:-$HOME/pointer-api}"
cd "$REPO"

# DB-01 freshness gate: the nightly cron must have produced a dump in the last 26 h. If it has not,
# the backup system is silently broken and the pre-deploy dump below would be the only recent copy.
BACKUP_DIR="${BACKUP_DIR:-$HOME/backups}"
if [ "${POINTER_SKIP_BACKUP_FRESHNESS:-0}" != "1" ] \
   && [ -z "$(find "$BACKUP_DIR" -maxdepth 1 -name 'pointer-*.dump' -mmin -1560 2>/dev/null | head -1)" ]; then
  echo "deploy REFUSED: no pointer-*.dump newer than 26 h in $BACKUP_DIR — check 'crontab -l' and $BACKUP_DIR/backup.log," >&2
  echo "fix the nightly backup, or export POINTER_SKIP_BACKUP_FRESHNESS=1 to override this once." >&2
  exit 2
fi

# DB-09 (DB-RULES R7): a migration whose class carries [ContractMigration] — every migration with a
# DB-RULES approval marker — never auto-applies on an ordinary deploy. It ships through this script as
#   POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-<slug> bash scripts/deploy-api.sh
# which stops the API first, dumps under that label, and boots with DBApplyContractMigrations=true.
APPLY_CONTRACT="${POINTER_APPLY_CONTRACT:-0}"
export DB_APPLY_CONTRACT=false
COMPOSE=(docker compose --env-file .env.prod -f docker-compose.prod.yml)

echo "== 1/5 pull =="
git pull --ff-only
git log --oneline -1

echo "== 2/5 contract-migration pre-flight =="
applied="$("${COMPOSE[@]}" exec -T db psql -U pointer -d pointer -tAc 'SELECT "MigrationId" FROM "__EFMigrationsHistory"' 2>/dev/null || true)"
pending_contract=""
for f in "$REPO"/Infrastructure/Migrations/[0-9]*_*.cs; do
  case "$f" in *.Designer.cs) continue ;; esac
  id="$(basename "$f" .cs)"
  grep -qx "$id" <<<"$applied" && continue
  grep -q '\[ContractMigration' "$f" && pending_contract+="$id"$'\n'
done
if [ -n "$pending_contract" ]; then
  if [ "$APPLY_CONTRACT" != "1" ]; then
    echo "deploy REFUSED (DB-09): pending migration(s) carry [ContractMigration]:" >&2
    printf '  %s\n' $pending_contract >&2
    echo "Nothing was rebuilt; the running API is unchanged (the checkout is now ahead of it)." >&2
    echo "Read the execution doc named in the attribute, run its pre-checks on prod, then:" >&2
    echo "  POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-<slug> bash scripts/deploy-api.sh" >&2
    exit 2
  fi
  : "${POINTER_CONTRACT_LABEL:?POINTER_APPLY_CONTRACT=1 requires POINTER_CONTRACT_LABEL=pre-<slug> (the dump label from the execution doc)}"
  echo "will apply contract migration(s):"; printf '  %s\n' $pending_contract
elif [ "$APPLY_CONTRACT" = "1" ]; then
  echo "note: POINTER_APPLY_CONTRACT=1 but no pending contract migration — proceeding as an ordinary deploy"
  APPLY_CONTRACT=0
fi

echo "== 3/5 backup =="
if [ "$APPLY_CONTRACT" = "1" ]; then
  "${COMPOSE[@]}" stop api                       # R7: no request may hit a half-migrated schema
  bash "$REPO/scripts/backup-db.sh" "$POINTER_CONTRACT_LABEL"
  export DB_APPLY_CONTRACT=true
else
  bash "$REPO/scripts/backup-db.sh" pre-deploy
fi

echo "== 4/5 rebuild api =="
"${COMPOSE[@]}" up -d --build api

echo "== 5/5 verify =="
for i in $(seq 1 30); do
  logs="$("${COMPOSE[@]}" logs --since 3m api 2>/dev/null || true)"
  grep -q "Now listening" <<<"$logs" && break
  if grep -q "DB-09 REFUSED" <<<"$logs"; then
    echo "deploy FAILED: the API refused to auto-apply a contract migration and is restarting in a loop." >&2
    grep "DB-09" <<<"$logs" | tail -3 >&2
    echo "Either re-run with POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-<slug>, or roll back:" >&2
    echo "  git checkout <previous commit> && ${COMPOSE[*]} up -d --build api" >&2
    exit 3
  fi
  sleep 2
done
"${COMPOSE[@]}" ps api
"${COMPOSE[@]}" logs --since 3m api | grep -iE "Now listening|migrat|DB-09|error|exception" | tail -20 || true
if [ "$DB_APPLY_CONTRACT" = "true" ]; then
  "${COMPOSE[@]}" exec -T db psql -U pointer -d pointer -c 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY 1 DESC LIMIT 3'
fi
for path in /api/branding /swagger/v1/swagger.json /health; do
  code=$(curl -s -o /dev/null -w '%{http_code}' "https://api.pointer.moamen.work$path")
  echo "$path $code"
  [ "$code" = "200" ] || { echo "smoke FAILED on $path" >&2; exit 1; }
done
# Housekeeping: every `up --build` leaves the previous image dangling (268 MB each; 110 had piled up
# by 2026-09-22 = 5 GB). Remove unreferenced images only — the running image and volumes are untouched.
docker image prune -f >/dev/null 2>&1 || true
echo "deploy OK"
