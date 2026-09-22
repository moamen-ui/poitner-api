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
echo "offsite copy OK: $(rclone ls "$DEST" | wc -l) objects in $DEST"
# Dead-man's switch: only reached on success. The monitoring service alerts when pings stop.
if [ -n "${OFFSITE_HEALTHCHECK_URL:-}" ]; then curl -fsS -m 10 -o /dev/null "$OFFSITE_HEALTHCHECK_URL" || echo "healthcheck ping failed (copy itself succeeded)" >&2; fi
