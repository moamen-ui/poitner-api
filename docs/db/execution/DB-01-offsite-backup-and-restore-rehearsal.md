# DB-01 — Off-box backup copy, uploads in the backup, backup freshness gate

Review finding: P0-2, P0-3 (P0-1 closed locally — see below) ([`DB-REVIEW-2026-09-22.md`](../DB-REVIEW-2026-09-22.md) §2).
Rules: R5, R6, R11 ([`DB-RULES.md`](../DB-RULES.md)). **No schema change.** Ops-only: two script
edits, one new script, two `.env.prod.example` lines, one DEPLOY.md section.

**Re-scoped 2026-09-22** (cross-review GLM A1/B3/B8): the restore rehearsal is **done** (local,
2026-09-22, recorded in `DEPLOY.md` § Restore "Last rehearsed"), so it leaves this doc. Added: a
backup-freshness gate in `deploy-api.sh`, a success ping for the off-box copy, and the uploads-tar
caveat. **Two halves:** part A (uploads tar, freshness gate, ping variable) needs no owner decision
and may ship any time; part B (rclone off-box copy) waits for Q1.

**Status 2026-09-22 (deployed ~10:15 UTC): both parts shipped.** Q1 answered — **Oracle Object
Storage**, bucket `pointer-backups`, rclone remote `offsite`. Production now runs the nightly cron
and pre-deploy dumps with the uploads tarball, uploads both to the offsite bucket, has the
freshness gate live in `deploy-api.sh`, and pings the configured healthcheck URL on a successful
copy. Restore has been rehearsed **locally** only (§2); a production-side restore is still
unexercised. Nothing left in this doc otherwise.

## 1. Goal

Every nightly and pre-deploy backup ends up (a) also containing the comment screenshots and (b)
also stored outside the VM, and (c) a silently broken nightly job is noticed — by the next deploy
refusing to run, and by a monitoring ping — instead of by the first restore. User-visible reason: a
lost VM today loses every comment, screenshot and backup at once, and nobody reads
`~/backups/backup.log`.

## 2. Prerequisites (verified facts)

- `scripts/backup-db.sh` (47 lines): dumps via `docker compose --env-file .env.prod -f docker-compose.prod.yml exec -T db pg_dump -U pointer -d pointer -Fc -Z6` (`:29-30`), validates the `PGDMP` magic (`:33-37`), writes `~/backups/pointer-<UTC ts>[-label].dump` (`:24-25`), prunes `pointer-*.dump` older than `KEEP_DAYS=14` keeping newest 3 (`:42-45`), prints `backup OK: …` (`:47`). Variables: `REPO`, `BACKUP_DIR`, `KEEP_DAYS`, `LABEL` (`:15-18`). `set -euo pipefail` (`:13`).
- `scripts/deploy-api.sh:23-24` calls `bash "$REPO/scripts/backup-db.sh" pre-deploy`.
- Cron on the VM (installed 2026-09-22): `0 3 * * * /home/ubuntu/pointer-api/scripts/backup-db.sh >> /home/ubuntu/backups/backup.log 2>&1` (`DEPLOY.md:104`).
- Uploads live in the compose named volume `uploads` mounted at `/app/wwwroot/uploads` (`docker-compose.prod.yml:50,70`); files are `uploads/<ownerId N-format>/<projectKey>/<file>` (`Infrastructure/Storage/LocalFileStorage.cs:14,26`). Compose prefixes volume names with the project (directory) name: expected Docker volume name **`pointer-api_uploads`** (same rule the rebranding plan cites for `pointer-api_pgdata`, §10.3). **Verify on the VM** with `docker volume ls | grep uploads` before relying on the name.
- `.env.prod.example` keys: `DB_PASSWORD, JWT_SIGNING_KEY, ADMIN_EMAIL, ADMIN_PASSWORD, POINTER_SERVER, EMAIL_ENABLED, EMAIL_API_KEY, EMAIL_FROM_EMAIL, EMAIL_FROM_NAME`. `.env.prod` on the VM is docker `KEY=VALUE` format (no quotes/spaces).
- Restore procedure: `DEPLOY.md` § Restore (stop api → `backup-db.sh pre-restore` → `DROP/CREATE DATABASE` → `pg_restore --no-owner --no-privileges` → `up -d api`). **Rehearsed locally 2026-09-22** (`DEPLOY.md` "Last rehearsed": `pointer-20260922T071358Z-initial.dump` → `pointer_rehearsal`, 26 tables, 58 history rows, counts matched). Production-side restore still unexercised (review P0-1, now P2).
- `scripts/deploy-api.sh:16-17` sets `REPO` and `cd`s into it; `:19-24` pull + `backup-db.sh pre-deploy`. DB-09 also edits this script (a separate block after the pull); the freshness block below goes **before** `== 1/4 pull ==` and does not overlap.
- Local dev Postgres: `docker compose up -d db` → `localhost:5433`, user/db/password `pointer` (`docker-compose.yaml:3-9`). `just db-update` = `dotnet ef database update -p Infrastructure -s API` (`justfile:7`).
- `rclone` is not installed on the VM (nothing in `DEPLOY.md` mentions it). Owner decision Q1 (review §8) picks the remote; this doc assumes **an rclone remote named `offsite`**. The owner is choosing between Cloudflare R2, Backblaze B2, Oracle Object Storage (all S3-compatible: rclone `s3` backend with the matching `provider`) and Google Drive (rclone `drive` backend) — every script line below is identical for all four; only the one-time `rclone config` differs (see §3 "If Google Drive").

## 3. Design

Backup set per run = `pointer-<ts>[-label].dump` + `uploads-<ts>[-label].tgz`, both in `$BACKUP_DIR`,
both mode 600, both pruned by the same retention loop, both copied to `offsite:<bucket>/pointer/`
by `scripts/offsite-backup.sh` when `OFFSITE_REMOTE` is set in `.env.prod`. Remote retention 30
days via `rclone delete --min-age 30d`. Nothing else changes: same cron line, same deploy script.

Freshness gate: `deploy-api.sh` refuses to start when no `pointer-*.dump` in `$BACKUP_DIR` is
newer than 26 h (the nightly runs at 03:00 UTC, so a healthy VM always has one), overridable once
with `POINTER_SKIP_BACKUP_FRESHNESS=1`. Success signal: when `OFFSITE_HEALTHCHECK_URL` is set in
`.env.prod`, `offsite-backup.sh` GETs it after a successful copy (any dead-man's-switch service:
healthchecks.io, UptimeRobot heartbeat, Cronitor); the service alerts when the ping stops. Until
one is configured, `DEPLOY.md` schedules a weekly `rclone ls` eyeball.

**If Google Drive (Q1 variant).** Same scripts, same `OFFSITE_REMOTE=offsite:<folder>` (a folder
name instead of a bucket). One-time setup differs: `rclone config` → backend `drive`, scope `drive`
(or `drive.file` — the remote then only sees files it created, which is enough and safer). The VM
has no browser, so authenticate **headless**: either run `rclone authorize "drive"` on a laptop and
paste the printed token into the VM's config prompt, or use a **service account** (`service_account_file
= /home/ubuntu/.config/rclone/sa.json`, mode 600) and share the target folder with the service
account's e-mail — note a service account has its own quota and cannot own files inside a personal
"My Drive" unless the folder is shared to it or lives in a Shared Drive (`team_drive` in the config).
Set `--drive-use-trash=false` on the `rclone delete` line in `offsite-backup.sh` (otherwise remote
retention only moves dumps to Trash, which still counts against quota); no other line changes.
Drive rate-limits parallel uploads, so keep `--transfers 2`. Versioning/lifecycle rules do not
exist on Drive — the `rclone delete --min-age` line is the only remote retention. Everything else in
this doc (freshness gate, healthcheck ping, uploads tar, acceptance criteria) is unchanged.

Known stray paths on the volume (DB-03a §3.6): `uploads/95b7f3ee1dfe4e76a8ecb6c113a04d42/…`
(the super admin's retired owner id; its one screenshot belongs to a comment now owned by
`98699076…`) and a pre-tenancy `uploads/pointer-api/…` with no owner folder. Both are inside the
volume and therefore inside every `uploads-*.tgz`; no exclusion, no move. They are listed here so a
restore operator does not treat them as junk.

Uploads archive caveat (GLM B8): `tar` over a live volume is **best-effort** — an upload written
during the run may be torn or missing from that night's archive (it is in the next one). No size
guard: the volume is expected to stay small until §33 blob deletion ships; if `du -sh` of the
volume exceeds a few GB, revisit (rclone `--transfers`, or `pg_dump`-style compression level).

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
   OFFSITE_HEALTHCHECK_URL="$(grep -E '^OFFSITE_HEALTHCHECK_URL=' .env.prod 2>/dev/null | cut -d= -f2- || true)"
   export OFFSITE_HEALTHCHECK_URL
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
   # Dead-man's switch: only reached on success. The monitoring service alerts when pings stop.
   if [ -n "${OFFSITE_HEALTHCHECK_URL:-}" ]; then curl -fsS -m 10 -o /dev/null "$OFFSITE_HEALTHCHECK_URL" || echo "healthcheck ping failed (copy itself succeeded)" >&2; fi
   ```
3. **`.env.prod.example`** — append:
   ```
   # Optional: rclone remote:bucket for nightly off-box copies (scripts/offsite-backup.sh). Empty = disabled.
   OFFSITE_REMOTE=
   # Optional: heartbeat URL pinged after each successful off-box copy (healthchecks.io / UptimeRobot / Cronitor). Empty = no ping.
   OFFSITE_HEALTHCHECK_URL=
   ```
3b. **`scripts/deploy-api.sh`** — insert directly after line 17 (`cd "$REPO"`), before `echo "== 1/4 pull =="`:
   ```bash
   # DB-01 freshness gate: the nightly cron must have produced a dump in the last 26 h. If it has not,
   # the backup system is silently broken and the pre-deploy dump below would be the only recent copy.
   BACKUP_DIR="${BACKUP_DIR:-$HOME/backups}"
   if [ "${POINTER_SKIP_BACKUP_FRESHNESS:-0}" != "1" ] \
      && [ -z "$(find "$BACKUP_DIR" -maxdepth 1 -name 'pointer-*.dump' -mmin -1560 2>/dev/null | head -1)" ]; then
     echo "deploy REFUSED: no pointer-*.dump newer than 26 h in $BACKUP_DIR — check 'crontab -l' and $BACKUP_DIR/backup.log," >&2
     echo "fix the nightly backup, or export POINTER_SKIP_BACKUP_FRESHNESS=1 to override this once." >&2
     exit 2
   fi
   ```
   Nothing else in the script changes (DB-09 owns the other edits).
4. **`DEPLOY.md`** § Backups (after line 109, before `### Restore`): add a paragraph "Off-box copy" —
   install `rclone` (`curl https://rclone.org/install.sh | sudo bash`), `rclone config` → remote named
   `offsite`: S3-compatible provider chosen by the owner (R2 / B2 / Oracle — bucket with versioning
   off and a lifecycle rule of 30 days as belt-and-braces) **or** Google Drive per §3 "If Google
   Drive" (headless token or service account; `--drive-use-trash=false`); set
   `OFFSITE_REMOTE=offsite:<bucket-or-folder>` in `.env.prod`; the
   nightly cron then copies both files; verify with `rclone ls offsite:<bucket>/pointer`. Note that
   credentials live only in `~/.config/rclone/rclone.conf` (mode 600), never in the repo.
   In § Restore, add after the `pg_restore` line:
   ```bash
   docker run --rm -v pointer-api_uploads:/u -v ~/backups:/b alpine:3 sh -c 'cd /u && tar -xzf /b/uploads-<ts>.tgz'
   ```
   The existing "Last rehearsed" line stays; add under it: "Uploads archive: best-effort snapshot of a live volume (a file written during the run may be torn); restore it **before** starting the API." Add a "Monitoring" sentence: set `OFFSITE_HEALTHCHECK_URL` to a dead-man's-switch URL; until then, run `rclone ls offsite:<bucket>/pointer | tail -3` weekly and check the newest timestamps are yesterday's.
5. **Uploads archive sanity check (operator, once, local)** — after the first `manual-test` run, copy `uploads-<ts>-manual-test.tgz` locally, `mkdir /tmp/up && tar -xzf uploads-<ts>-manual-test.tgz -C /tmp/up && find /tmp/up -type f | wc -l`, and compare to `SELECT count(*) FROM comments WHERE coalesce(element->>'ScreenshotUrl', element->>'screenshotUrl') IS NOT NULL` on the restored rehearsal copy (order of magnitude only; the JSON key casing is whichever the serializer wrote — `REBRANDING-PLAN.md` §11.4 step 0 shows how to check). The database restore rehearsal itself is **not** repeated here — it is done; repeat it only when `backup-db.sh` or the restore steps change (R11).

## 6. Tests

No C# tests. Mechanical checks: `bash -n scripts/backup-db.sh scripts/offsite-backup.sh scripts/deploy-api.sh`;
on the VM run `bash scripts/backup-db.sh manual-test` once and confirm both files appear and
`rclone ls` lists them. Freshness gate, on the VM: `mkdir -p /tmp/empty-dir && BACKUP_DIR=/tmp/empty-dir bash scripts/deploy-api.sh`
must print `deploy REFUSED` and exit 2 **before** the `== 1/… pull ==` line (no `git pull`, no
dump). Then a normal `bash scripts/deploy-api.sh` on the same VM (which has last night's dump)
proceeds past the gate. Do not test the override flag by sourcing fragments of the script.

## 7. Acceptance criteria

1. `ls ~/backups` on the VM shows a `pointer-<ts>-manual-test.dump` **and** `uploads-<ts>-manual-test.tgz`, both mode `-rw-------`.
2. `rclone ls offsite:<bucket>/pointer` lists both files with matching sizes.
3. The next nightly run appends `backup OK` and `offsite OK` lines to `~/backups/backup.log`.
4. `DEPLOY.md` contains the "Off-box copy" paragraph, the uploads caveat and the "Monitoring" sentence; the existing "Last rehearsed" line is untouched.
5. `BACKUP_DIR=/tmp/empty-dir bash scripts/deploy-api.sh` exits 2 with `deploy REFUSED` and performs no `git pull` (`git log -1` unchanged) and no dump.
6. `bash -n` passes for all three scripts; `git diff scripts/deploy-api.sh` shows **only** the freshness block inserted after `cd "$REPO"`.
7. If `OFFSITE_HEALTHCHECK_URL` is set: the monitoring service shows a ping at ~03:0x UTC the next morning.

## 8. Rollback

Delete `scripts/offsite-backup.sh`, revert `backup-db.sh` and the `deploy-api.sh` block, clear
`OFFSITE_REMOTE`/`OFFSITE_HEALTHCHECK_URL`. No data path is affected; local dumps continue as
before. No dump required (nothing destructive). Emergency bypass of the freshness gate without a
revert: `POINTER_SKIP_BACKUP_FRESHNESS=1`.

## 9. Release steps

**Part A (no owner decision; may ship first, even before DB-05):** tasks 1 (uploads block + retention loop only), 3 (both env lines), 3b, 4 (caveat + monitoring sentence). Merge; on the VM `git pull`; `bash scripts/backup-db.sh manual-test` → an `uploads-*.tgz` appears; criterion 5.

**Part B (after Q1):**
1. Owner answers Q1 (provider) and creates the bucket + access key; optionally creates a heartbeat check and pastes its URL.
2. Merge; on the VM `git pull`, `sudo` install rclone, `rclone config` as `ubuntu`, set `OFFSITE_REMOTE` (and `OFFSITE_HEALTHCHECK_URL`) in `.env.prod`.
3. `bash scripts/backup-db.sh manual-test` → check criteria 1-2.
4. Next morning: check criterion 3 in `backup.log` and criterion 7.
5. Task 5 (uploads archive sanity check) within the week.

## 10. Out of scope

`docker-compose.prod.yml`, `Caddyfile`, the database itself, the API, any schema change, the
dashboard repo, GitHub Actions (DB-10), the DB-09 blocks in `deploy-api.sh` (pre-flight, contract
path, verify step — a different PR). Do not change cron frequency or `KEEP_DAYS`. Do not re-run the
database restore rehearsal as part of this doc.
