#!/usr/bin/env bash
# Dump the production Postgres database to ~/backups on the VM.
#
# Runs ON the VM (the db container is not exposed to the host). Used two ways:
#   - before every API deploy that may carry a migration — scripts/deploy-api.sh calls it first
#   - nightly from cron:  0 3 * * * /home/ubuntu/pointer-api/scripts/backup-db.sh >> /home/ubuntu/backups/backup.log 2>&1
#
# Output: ~/backups/pointer-<UTC timestamp>[-<label>].dump  (pg_dump custom format, gzip-compressed by
# pg_dump itself via -Z6, restorable with pg_restore — see DEPLOY.md § Backups / Restore).
# Retention: dumps older than $KEEP_DAYS (default 14) are deleted, but never the newest 3.
#
# Usage: backup-db.sh [label]      e.g. backup-db.sh pre-deploy
set -euo pipefail

REPO="${POINTER_REPO:-$HOME/pointer-api}"
BACKUP_DIR="${BACKUP_DIR:-$HOME/backups}"
KEEP_DAYS="${KEEP_DAYS:-14}"
LABEL="${1:-}"

cd "$REPO"
mkdir -p "$BACKUP_DIR"
chmod 700 "$BACKUP_DIR"

ts="$(date -u +%Y%m%dT%H%M%SZ)"
out="$BACKUP_DIR/pointer-${ts}${LABEL:+-$LABEL}.dump"

# The official postgres image trusts local socket connections, so no password is needed inside the
# container. -Fc = custom format (supports selective/parallel restore), -Z6 = compressed.
docker compose --env-file .env.prod -f docker-compose.prod.yml exec -T db \
  pg_dump -U pointer -d pointer -Fc -Z6 > "$out.tmp"

# A custom-format dump starts with the 5-byte magic "PGDMP". Refuse to keep anything else.
if [ "$(head -c 5 "$out.tmp")" != "PGDMP" ] || [ "$(stat -c %s "$out.tmp")" -lt 1024 ]; then
  echo "backup FAILED: $out.tmp is not a valid pg_dump archive" >&2
  rm -f "$out.tmp"
  exit 1
fi
mv "$out.tmp" "$out"
chmod 600 "$out"

# Retention: delete dumps older than KEEP_DAYS, always keeping the 3 newest regardless of age.
mapfile -t all < <(ls -1t "$BACKUP_DIR"/pointer-*.dump 2>/dev/null)
for f in "${all[@]:3}"; do
  if [ -n "$(find "$f" -mtime +"$KEEP_DAYS" 2>/dev/null)" ]; then rm -f "$f"; fi
done

echo "backup OK: $out ($(stat -c %s "$out") bytes); $(ls -1 "$BACKUP_DIR"/pointer-*.dump | wc -l) dumps kept"
