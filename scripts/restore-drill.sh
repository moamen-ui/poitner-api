#!/usr/bin/env bash
# Non-destructive restore drill. Runs ON the VM. Never touches the live `pointer` database.
# Usage: bash scripts/restore-drill.sh
set -euo pipefail

REPO="${POINTER_REPO:-$HOME/pointer-api}"
cd "$REPO"
COMPOSE=(docker compose --env-file .env.prod -f docker-compose.prod.yml)
DRILL_DB="pointer_drill"
TMP_DIR=$(mktemp -d)
trap 'echo "cleanup…"; "${COMPOSE[@]}" exec -T db psql -U pointer -d postgres -c "DROP DATABASE IF EXISTS $DRILL_DB;" 2>/dev/null || true; rm -rf "$TMP_DIR"' EXIT

START=$(date +%s)

echo "== 1/6 fetch newest dump + uploads from offsite =="
OFFSITE_REMOTE="$(grep -E '^OFFSITE_REMOTE=' .env.prod 2>/dev/null | cut -d= -f2- || true)"
if [ -z "$OFFSITE_REMOTE" ]; then
  echo "OFFSITE_REMOTE not set in .env.prod — falling back to local ~/backups" >&2
  # `|| true`: under `set -euo pipefail` a no-match ls (nonzero exit, suppressed by 2>/dev/null)
  # would otherwise abort the script right here — silently, before the loud-failure check below
  # ever runs.
  DUMP=$(ls -1t ~/backups/pointer-*.dump 2>/dev/null | head -1) || true
  TARBALL=$(ls -1t ~/backups/uploads-*.tgz 2>/dev/null | head -1) || true
  [ -z "$DUMP" ] && { echo "DRILL FAILED: no local dump found" >&2; exit 1; }
else
  REMOTE_PATH="$OFFSITE_REMOTE/pointer"
  # Get the newest dump filename
  DUMP_NAME=$(rclone lsf "$REMOTE_PATH" --include "pointer-*.dump" --files-only | sort | tail -1)
  TARBALL_NAME=$(rclone lsf "$REMOTE_PATH" --include "uploads-*.tgz" --files-only | sort | tail -1)
  [ -z "$DUMP_NAME" ] && { echo "DRILL FAILED: no dump found in $REMOTE_PATH" >&2; exit 1; }
  rclone copy "$REMOTE_PATH/$DUMP_NAME" "$TMP_DIR" --progress
  DUMP="$TMP_DIR/$DUMP_NAME"
  if [ -n "$TARBALL_NAME" ]; then
    rclone copy "$REMOTE_PATH/$TARBALL_NAME" "$TMP_DIR" --progress
    TARBALL="$TMP_DIR/$TARBALL_NAME"
  fi
fi
echo "dump: $DUMP ($(stat -c %s "$DUMP" 2>/dev/null || stat -f %z "$DUMP") bytes)"

echo "== 2/6 create scratch database =="
"${COMPOSE[@]}" exec -T db psql -U pointer -d postgres -c "DROP DATABASE IF EXISTS $DRILL_DB;"
"${COMPOSE[@]}" exec -T db psql -U pointer -d postgres -c "CREATE DATABASE $DRILL_DB OWNER pointer;"

echo "== 3/6 restore dump into $DRILL_DB =="
"${COMPOSE[@]}" exec -T db pg_restore -U pointer -d "$DRILL_DB" --no-owner --no-privileges < "$DUMP"

echo "== 4/6 verify row counts =="
for table in users comments projects replies __EFMigrationsHistory; do
  live=$("${COMPOSE[@]}" exec -T db psql -U pointer -d pointer -tAc "SELECT count(*) FROM \"$table\"" 2>/dev/null || echo "?")
  drill=$("${COMPOSE[@]}" exec -T db psql -U pointer -d "$DRILL_DB" -tAc "SELECT count(*) FROM \"$table\"" 2>/dev/null || echo "?")
  echo "  $table: live=$live drill=$drill"
  if [ "$live" != "?" ] && [ "$drill" != "?" ] && [ "$live" -gt 0 ]; then
    diff=$(( (live - drill) * 100 / live ))
    [ "$diff" -gt 5 ] && echo "  WARNING: $table count differs by ${diff}%" >&2
  fi
done

echo "== 5/6 verify uploads tarball =="
UPLOADS_TMP="$TMP_DIR/uploads-extracted"
mkdir -p "$UPLOADS_TMP"
if [ -n "${TARBALL:-}" ] && [ -f "$TARBALL" ]; then
  tar -xzf "$TARBALL" -C "$UPLOADS_TMP"
  UPLOAD_COUNT=$(find "$UPLOADS_TMP" -type f | wc -l)
  echo "  extracted $UPLOAD_COUNT file(s)"
else
  UPLOAD_COUNT=0
  echo "  no uploads tarball (or empty) — skipped"
fi

echo "== 6/6 drop scratch =="
"${COMPOSE[@]}" exec -T db psql -U pointer -d postgres -c "DROP DATABASE $DRILL_DB;"

END=$(date +%s)
DUR=$((END - START))
echo "DRILL OK: restore of $(basename "$DUMP") into $DRILL_DB took ${DUR}s; $UPLOAD_COUNT uploads files."
