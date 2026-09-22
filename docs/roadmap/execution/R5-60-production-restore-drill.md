# R5-60 — Production-side restore drill (§60 · Release 5 · 0.5 d)

**Status (2026-09-23):** shipped `58f0fe7`; **drilled on production 2026-09-23** from the off-box
copy — 4 s, users/comments/projects/replies/migrations matched live, scratch dropped (see
`DEPLOY.md` "Last drilled").

## 1. Goal

Prove that the backup-and-restore chain works end-to-end on the production VM by scripting a
non-destructive drill that fetches the newest dump and uploads tarball from the off-site bucket,
restores them into a scratch database `pointer_drill`, verifies counts against the live `pointer`
DB, verifies the uploads tarball extracts, times the whole operation, and tears down the scratch.
The live `pointer` database is **never touched**.

Effort: 0.5 d.

## 2. Prerequisites (verified facts)

- **Backup script**: `scripts/backup-db.sh:1-72` — produces `pointer-<ts>[-<label>].dump` (pg_dump
  custom format, `-Fc -Z6`) and `uploads-<ts>[-<label>].tgz` to `~/backups/`. Validates the
  `PGDMP` magic header at `:36-41`.
- **Offsite script**: `scripts/offsite-backup.sh:1-13` — `rclone copy` to
  `$DEST/pointer/` (`:7,9`). Remote named `offsite`, bucket `pointer-backups` → remote path
  `offsite:pointer-backups/pointer/`. Retention: `--min-age 30d` (`:10`).
- **Compose DB**: `docker-compose.prod.yml:5-12` — `postgres:15`, user `pointer`, db `pointer`.
- **Restore procedure**: `DEPLOY.md:148-182` — stop api, drop+create DB, `pg_restore`, restore
  uploads, start api. **Last rehearsed**: 2026-09-22 locally (`DEPLOY.md:166-169`).
- **rclone remote**: `offsite` (S3-compatible, Oracle Object Storage). Setup docs in
  `DEPLOY.md:119-139`.
- **DEPLOY.md**: `DEPLOY.md:1-279` at repo root (not `docs/DEPLOY.md`).
- **Dependencies**: none. Independent of DB-11a/b/c/d, DB-12 (audit log, written but not yet
  implemented) and DB-13 (impersonation, written but not yet implemented) — this doc only touches
  ops scripts and DEPLOY.md.

## 3. Design

New script `scripts/restore-drill.sh`, designed to run **on the prod VM** (same environment as
`backup-db.sh` and `deploy-api.sh`). It:

1. **Fetches** the newest `pointer-*.dump` and `uploads-*.tgz` from `offsite:pointer-backups/pointer/`
   into a temp dir using `rclone copy --include`.
2. **Creates** a scratch database `pointer_drill` in the existing Postgres container.
3. **Restores** the dump into `pointer_drill` via `pg_restore`.
4. **Verifies row counts** by comparing `SELECT count(*) FROM <table>` in `pointer_drill` vs
   `pointer` for key tables: `users`, `comments`, `projects`, `replies`,
   `__EFMigrationsHistory`. A count mismatch > 5% (to allow for rows created since the dump)
   prints a warning but does not fail the drill.
5. **Verifies the uploads tarball** extracts without error into a temp dir (checks that at least
   one file exists if the tarball is non-empty).
6. **Times** the whole operation and prints a one-line result:
   `DRILL OK: restore of <dump file> into pointer_drill took <N>s; <K> uploads files.`
7. **Drops** `pointer_drill` and cleans up the temp dirs.

**Safety**: the script `set -euo pipefail`; `pointer_drill` is created with `CREATE DATABASE … OWNER pointer`
and dropped at the end (also in a trap). The live `pointer` database is only read (for count
comparison). The API is never stopped.

## 4. Safety / impact

**Read-only on production data.** The drill creates and destroys a scratch database in the same
Postgres container. The API is not stopped. The only write is to the scratch DB, which is dropped
even on failure (via `trap`). Disk usage: one decompressed dump (temporarily), cleaned up.

## 5. File-level tasks

1. **`scripts/restore-drill.sh`** (new) — the full script:

```bash
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
  DUMP=$(ls -1t ~/backups/pointer-*.dump 2>/dev/null | head -1)
  TARBALL=$(ls -1t ~/backups/uploads-*.tgz 2>/dev/null | head -1)
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
```

2. **`DEPLOY.md`** — in the "Backups" section, after the "Last rehearsed" line (`:166-169`), add:
   ```markdown
   **Last drilled:** <date> on the prod VM — `bash scripts/restore-drill.sh` restored the newest
   off-site dump into `pointer_drill`, verified row counts matched, and dropped the scratch DB in
   <N>s. Repeat monthly or after any change to `backup-db.sh`, `offsite-backup.sh`, or the restore
   procedure above.
   ```
   Leave `<date>` and `<N>s` as placeholders for the operator to fill in after the first run.

3. **Monthly cron suggestion** (document in `DEPLOY.md` only, do not install):
   ```
   # Monthly restore drill (1st of each month at 04:00 UTC). Review ~/drill.log periodically.
   0 4 1 * * /home/ubuntu/pointer-api/scripts/restore-drill.sh >> /home/ubuntu/drill.log 2>&1
   ```

## 6. Tests

No automated test (the script runs on the prod VM with a real Postgres and rclone). The drill
itself is the test. The acceptance criteria below are mechanically checkable on the VM.

## 7. Acceptance criteria

1. `bash -n scripts/restore-drill.sh` → exit 0 (syntax valid).
2. On the VM: `bash scripts/restore-drill.sh` → prints `DRILL OK: restore of pointer-<ts>.dump …`
   and exits 0.
3. After the drill: `"${COMPOSE[@]}" exec -T db psql -U pointer -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname='pointer_drill'"` → empty (scratch dropped).
4. The live `pointer` DB is untouched: `"${COMPOSE[@]}" exec -T db psql -U pointer -d pointer -c 'SELECT count(*) FROM comments'` → same count as before.
5. `DEPLOY.md` contains "Last drilled:" line.
6. `grep -c 'DRILL_DB' scripts/restore-drill.sh` → at least 5 (the script names the scratch
   database once, via `DRILL_DB="pointer_drill"`, and references the `$DRILL_DB` variable
   everywhere else — the literal string `pointer_drill` itself appears only once).

## 8. Rollback

Delete `scripts/restore-drill.sh` and revert the `DEPLOY.md` addition. No data, no migration, no
service change.

## 9. Release steps

1. Merge PR.
2. On the VM: `git pull --ff-only && bash scripts/restore-drill.sh`.
3. Fill in the `<date>` and `<N>s` in `DEPLOY.md`, commit and push.
4. Optionally install the monthly cron.

## 10. Out of scope

Automated CI testing of the drill (requires a live Postgres + rclone), restoring into the live
`pointer` DB, point-in-time recovery (PITR), WAL archiving, snapshot-based backups,
cross-region replication, the drill verifying API boot against the restored DB.
