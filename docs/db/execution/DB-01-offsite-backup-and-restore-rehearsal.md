# DB-01 — Off-box backup copy, uploads in the backup, restore rehearsal

Review finding: P0-1, P0-2, P0-3 ([`DB-REVIEW-2026-09-22.md`](../DB-REVIEW-2026-09-22.md) §2).
Rules: R5, R6, R11 ([`DB-RULES.md`](../DB-RULES.md)). **No schema change.** Ops-only: one script
edit, one new script, one `.env.prod.example` line, one DEPLOY.md section, one rehearsal.

## 1. Goal

Every nightly and pre-deploy backup ends up (a) also containing the comment screenshots and (b)
also stored outside the VM, and the written restore procedure has been executed once end-to-end on
a local copy, so the first real restore is not the first attempt. User-visible reason: a lost VM
today loses every comment, screenshot and backup at once.

## 2. Prerequisites (verified facts)

- `scripts/backup-db.sh` (47 lines): dumps via `docker compose --env-file .env.prod -f docker-compose.prod.yml exec -T db pg_dump -U pointer -d pointer -Fc -Z6` (`:29-30`), validates the `PGDMP` magic (`:33-37`), writes `~/backups/pointer-<UTC ts>[-label].dump` (`:24-25`), prunes `pointer-*.dump` older than `KEEP_DAYS=14` keeping newest 3 (`:42-45`), prints `backup OK: …` (`:47`). Variables: `REPO`, `BACKUP_DIR`, `KEEP_DAYS`, `LABEL` (`:15-18`). `set -euo pipefail` (`:13`).
- `scripts/deploy-api.sh:23-24` calls `bash "$REPO/scripts/backup-db.sh" pre-deploy`.
- Cron on the VM (installed 2026-09-22): `0 3 * * * /home/ubuntu/pointer-api/scripts/backup-db.sh >> /home/ubuntu/backups/backup.log 2>&1` (`DEPLOY.md:104`).
- Uploads live in the compose named volume `uploads` mounted at `/app/wwwroot/uploads` (`docker-compose.prod.yml:50,70`); files are `uploads/<ownerId N-format>/<projectKey>/<file>` (`Infrastructure/Storage/LocalFileStorage.cs:14,26`). Compose prefixes volume names with the project (directory) name: expected Docker volume name **`pointer-api_uploads`** (same rule the rebranding plan cites for `pointer-api_pgdata`, §10.3). **Verify on the VM** with `docker volume ls | grep uploads` before relying on the name.
- `.env.prod.example` keys: `DB_PASSWORD, JWT_SIGNING_KEY, ADMIN_EMAIL, ADMIN_PASSWORD, POINTER_SERVER, EMAIL_ENABLED, EMAIL_API_KEY, EMAIL_FROM_EMAIL, EMAIL_FROM_NAME`. `.env.prod` on the VM is docker `KEY=VALUE` format (no quotes/spaces).
- Restore procedure: `DEPLOY.md:111-131` (stop api → `backup-db.sh pre-restore` → `DROP/CREATE DATABASE` → `pg_restore --no-owner --no-privileges` → `up -d api`). Never executed.
- Local dev Postgres: `docker compose up -d db` → `localhost:5433`, user/db/password `pointer` (`docker-compose.yaml:3-9`). `just db-update` = `dotnet ef database update -p Infrastructure -s API` (`justfile:7`).
- `rclone` is not installed on the VM (nothing in `DEPLOY.md` mentions it). Owner decision Q1 (review §8) picks the remote; this doc assumes **an S3-compatible bucket configured as rclone remote `offsite`**.

## 3. Design

Backup set per run = `pointer-<ts>[-label].dump` + `uploads-<ts>[-label].tgz`, both in `$BACKUP_DIR`,
both mode 600, both pruned by the same retention loop, both copied to `offsite:<bucket>/pointer/`
by `scripts/offsite-backup.sh` when `OFFSITE_REMOTE` is set in `.env.prod`. Remote retention 30
days via `rclone delete --min-age 30d`. Nothing else changes: same cron line, same deploy script.

Restore rehearsal happens **locally** (R11 shape), never against prod, and its outcome is written
into `DEPLOY.md` as "Last rehearsed …".

What happens to existing rows: nothing — no database change.

## 4. Safety classification

Ops-only. Rules R5 (n/a), R6 (this doc *is* R6's completion), R11 (the rehearsal). No migration.

## 5. File-level tasks

1. **`scripts/backup-db.sh`** — after line 39 (`chmod 600 "$out"`) and before the retention block, add:
   ```bash
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
   ```
   Replace the retention block (`:41-45`) so it prunes both patterns:
   ```bash
   for pattern in "pointer-*.dump" "uploads-*.tgz"; do
     mapfile -t all < <(ls -1t "$BACKUP_DIR"/$pattern 2>/dev/null)
     for f in "${all[@]:3}"; do
       if [ -n "$(find "$f" -mtime +"$KEEP_DAYS" 2>/dev/null)" ]; then rm -f "$f"; fi
     done
   done
   ```
   After the final `echo "backup OK…"` line add:
   ```bash
   OFFSITE_REMOTE="$(grep -E '^OFFSITE_REMOTE=' .env.prod 2>/dev/null | cut -d= -f2- || true)"
   if [ -n "$OFFSITE_REMOTE" ]; then
     bash "$REPO/scripts/offsite-backup.sh" "$BACKUP_DIR" "$OFFSITE_REMOTE" || echo "offsite copy FAILED (local backup is intact)" >&2
   fi
   ```
   Update the header comment (`:8-10`) to mention the `uploads-*.tgz` file and the optional off-site copy.
2. **`scripts/offsite-backup.sh`** (new, `chmod +x`):
   ```bash
   #!/usr/bin/env bash
   # Copy local backups to an rclone remote and apply remote retention. Runs ON the VM, called by
   # backup-db.sh when OFFSITE_REMOTE is set in .env.prod (e.g. OFFSITE_REMOTE=offsite:pointer-backups).
   # One-time setup: `rclone config` as the ubuntu user (S3-compatible provider) — see DEPLOY.md § Backups.
   # Usage: offsite-backup.sh <local dir> <remote:bucket>
   set -euo pipefail
   SRC="$1"; DEST="$2/pointer"
   command -v rclone >/dev/null || { echo "rclone not installed" >&2; exit 1; }
   rclone copy "$SRC" "$DEST" --include "pointer-*.dump" --include "uploads-*.tgz" --checksum --transfers 2 --quiet
   rclone delete "$DEST" --min-age "${OFFSITE_KEEP:-30d}" --include "pointer-*.dump" --include "uploads-*.tgz" --quiet
   echo "offsite OK: $(rclone ls "$DEST" | wc -l) objects in $DEST"
   ```
3. **`.env.prod.example`** — append:
   ```
   # Optional: rclone remote:bucket for nightly off-box copies (scripts/offsite-backup.sh). Empty = disabled.
   OFFSITE_REMOTE=
   ```
4. **`DEPLOY.md`** § Backups (after line 109, before `### Restore`): add a paragraph "Off-box copy" —
   install `rclone` (`curl https://rclone.org/install.sh | sudo bash`), `rclone config` → remote named
   `offsite`, S3-compatible provider chosen by the owner, bucket with versioning off and a lifecycle
   rule of 30 days as belt-and-braces; set `OFFSITE_REMOTE=offsite:<bucket>` in `.env.prod`; the
   nightly cron then copies both files; verify with `rclone ls offsite:<bucket>/pointer`. Note that
   credentials live only in `~/.config/rclone/rclone.conf` (mode 600), never in the repo.
   In § Restore, add after the `pg_restore` line:
   ```bash
   docker run --rm -v pointer-api_uploads:/u -v ~/backups:/b alpine:3 sh -c 'cd /u && tar -xzf /b/uploads-<ts>.tgz'
   ```
   and a final line `**Last rehearsed:** <date> — dump <file>, restore took <n> s, API booted with 0 pending migrations.`
5. **Rehearsal (operator, local machine)** — run R11 steps 1-2 with the newest prod dump, then boot
   the API against `pointer_rehearsal`:
   ```bash
   ConnectionStrings__Default="Host=localhost;Port=5433;Database=pointer_rehearsal;Username=pointer;Password=pointer" \
   DBMigrationEnabled=true dotnet run --project API   # watch for "Now listening"; Ctrl-C
   ConnectionStrings__Default="…pointer_rehearsal…" dotnet ef migrations list -p Infrastructure -s API | grep -c "(Pending)"   # expect 0
   ```
   Restore the uploads archive into a scratch dir (`mkdir /tmp/up && tar -xzf uploads-<ts>.tgz -C /tmp/up && find /tmp/up -type f | wc -l`) and compare to `SELECT count(*) FROM comments WHERE coalesce(element->>'ScreenshotUrl', element->>'screenshotUrl') IS NOT NULL` (order of magnitude only; the JSON key casing is whichever the serializer wrote — `REBRANDING-PLAN.md` §11.4 step 0 shows how to check). Fill in DEPLOY.md's "Last rehearsed" line.

## 6. Tests

No C# tests. Mechanical checks: `bash -n scripts/backup-db.sh scripts/offsite-backup.sh`; on the VM
run `bash scripts/backup-db.sh manual-test` once and confirm both files appear and `rclone ls` lists
them.

## 7. Acceptance criteria

1. `ls ~/backups` on the VM shows a `pointer-<ts>-manual-test.dump` **and** `uploads-<ts>-manual-test.tgz`, both mode `-rw-------`.
2. `rclone ls offsite:<bucket>/pointer` lists both files with matching sizes.
3. The next nightly run appends `backup OK` and `offsite OK` lines to `~/backups/backup.log`.
4. `DEPLOY.md` contains the "Off-box copy" paragraph and a filled-in "Last rehearsed" line.
5. Locally, `dotnet ef migrations list` against the restored copy reports 0 pending migrations, and the API boots against it.
6. `bash -n` passes for both scripts; `scripts/deploy-api.sh` is unchanged.

## 8. Rollback

Delete `scripts/offsite-backup.sh`, revert `backup-db.sh`, clear `OFFSITE_REMOTE`. No data path is
affected; local dumps continue as before. No dump required (nothing destructive).

## 9. Release steps

1. Owner answers Q1 (provider) and creates the bucket + access key.
2. Merge; on the VM `git pull`, `sudo` install rclone, `rclone config` as `ubuntu`, set `OFFSITE_REMOTE` in `.env.prod`.
3. `bash scripts/backup-db.sh manual-test` → check criteria 1-2.
4. Next morning: check criterion 3 in `backup.log`.
5. Perform the local rehearsal (task 5) within the week; commit the DEPLOY.md line.

## 10. Out of scope

`docker-compose.prod.yml`, `Caddyfile`, the database itself, the API, any schema change, the
dashboard repo, GitHub Actions. Do not change cron frequency or `KEEP_DAYS`.
