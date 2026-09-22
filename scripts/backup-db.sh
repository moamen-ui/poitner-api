#!/usr/bin/env bash
# Dump the production Postgres database to ~/backups on the VM.
#
# Runs ON the VM (the db container is not exposed to the host). Used two ways:
#   - before every API deploy that may carry a migration — scripts/deploy-api.sh calls it first
#   - nightly from cron:  0 3 * * * /home/ubuntu/pointer-api/scripts/backup-db.sh >> /home/ubuntu/backups/backup.log 2>&1
#
# Output: ~/backups/pointer-<UTC timestamp>[-<label>].dump  (pg_dump custom format, gzip-compressed by
# pg_dump itself via -Z6, restorable with pg_restore — see DEPLOY.md § Backups / Restore), plus a
# best-effort ~/backups/uploads-<UTC timestamp>[-<label>].tgz archive of the compose `uploads` volume
# (comment screenshots — not in Postgres).
# Retention: both patterns older than $KEEP_DAYS (default 14) are deleted, but never the newest 3 of each.
# Optional off-site copy: when OFFSITE_REMOTE is set in .env.prod, both files are also copied to that
# rclone remote (scripts/offsite-backup.sh) after a successful backup — see DEPLOY.md § Backups.
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

# Screenshots live in the compose `uploads` volume, not in Postgres — archive them beside the dump.
UPLOADS_VOLUME="${UPLOADS_VOLUME:-pointer-api_uploads}"
up="$BACKUP_DIR/uploads-${ts}${LABEL:+-$LABEL}.tgz"
if docker volume inspect "$UPLOADS_VOLUME" >/dev/null 2>&1; then
  docker run --rm -v "$UPLOADS_VOLUME":/u:ro -v "$BACKUP_DIR":/b alpine:3 \
    tar -C /u -czf "/b/$(basename "$up.tmp")" .
  mv "$up.tmp" "$up"; chmod 600 "$up"
else
  echo "backup WARN: docker volume $UPLOADS_VOLUME not found — uploads not archived" >&2
fi

# Retention: delete dumps/uploads archives older than KEEP_DAYS, always keeping the 3 newest of each.
for pattern in "pointer-*.dump" "uploads-*.tgz"; do
  mapfile -t all < <(ls -1t "$BACKUP_DIR"/$pattern 2>/dev/null)
  for f in "${all[@]:3}"; do
    if [ -n "$(find "$f" -mtime +"$KEEP_DAYS" 2>/dev/null)" ]; then rm -f "$f"; fi
  done
done

echo "backup OK: $out ($(stat -c %s "$out") bytes); $(ls -1 "$BACKUP_DIR"/pointer-*.dump | wc -l) dumps kept"

OFFSITE_REMOTE="$(grep -E '^OFFSITE_REMOTE=' .env.prod 2>/dev/null | cut -d= -f2- || true)"
OFFSITE_HEALTHCHECK_URL="$(grep -E '^OFFSITE_HEALTHCHECK_URL=' .env.prod 2>/dev/null | cut -d= -f2- || true)"
export OFFSITE_HEALTHCHECK_URL
if [ -n "$OFFSITE_REMOTE" ]; then
  bash "$REPO/scripts/offsite-backup.sh" "$BACKUP_DIR" "$OFFSITE_REMOTE" || echo "offsite copy FAILED (local backup is intact)" >&2
fi
