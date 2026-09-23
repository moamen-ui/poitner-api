# Deploy

Production runs on a single Linux VM with Docker Compose: **Postgres + API + Caddy**. Caddy
terminates TLS (auto Let's Encrypt), reverse-proxies the API, and serves the built dashboard as
static files. The current deployment:

Since 2026-09-15 only the React dashboard exists (`pointer-dashboard/react`); Angular and Vue were
retired at tag `last-three-apps` / branch `legacy/angular-vue` in that repo. Any dashboard work
targets React only.

| Host | Serves |
|---|---|
| `api.pointer.moamen.work` | this API (Swagger, `/widget.js`, `/embed.js`, skills) |
| `app.pointer.moamen.work` | the React [`pointer-dashboard`](https://github.com/moamen-ui/pointer-dashboard) build (`dashboard/react`) |
| `demo.pointer.moamen.work` | same React build, with the "Try the demo" entry |
| `pointer.moamen.work` | the marketing landing page |

> The dashboard is served from `dashboard/react/`, at `app.pointer.moamen.work` (and
> `demo.pointer.moamen.work`); [`scripts/deploy-dashboards.sh`](scripts/deploy-dashboards.sh) builds
> and places it. The former per-framework `app-<fw>.pointer.moamen.work` hosts were removed from
> DNS and from the Caddyfile on 2026-09-15.

Files: [`docker-compose.prod.yml`](docker-compose.prod.yml), [`Caddyfile`](Caddyfile),
[`.env.prod.example`](.env.prod.example).

## Prerequisites

- A VM with a public IP and **Docker + Compose plugin** installed.
- Ports **80** and **443** open to the world (host firewall **and** any cloud security list/group).
- DNS **A records** for each hostname → the VM's public IP (`api.pointer`, `app.pointer`,
  `demo.pointer`, bare `pointer`). Certs are issued by HTTP-01, so the names must resolve before
  first start.

## VM setup (one-time)

The dashboard depends on the private `@moamen-ui/pointer-*` GitHub Packages, so the VM needs a
**`read:packages`** token to build it. Set it once (used as `NODE_AUTH_TOKEN` by `npm ci`):

```bash
# Create a classic token at github.com/settings/tokens with scope: read:packages
echo 'export GH_PKG_TOKEN=ghp_…' >> ~/.bashrc && source ~/.bashrc
```

`~/.bashrc` is only read by **interactive** shells; one-liner `ssh vm '…'` deploys skip it, which is
why `scripts/deploy-dashboards.sh` greps the `export GH_PKG_TOKEN=` line out of it directly.

## 1. Configure

```bash
git clone https://github.com/moamen-ui/poitner-api && cd poitner-api
cp .env.prod.example .env.prod      # fill in real secrets (openssl rand -hex 32)
```

Adjust hostnames in `Caddyfile` and `Pointer__*` / `POINTER_SERVER` if you use different domains.

## 2. Build the dashboard → `./dashboard/react`

The dashboard is a separate repo. It depends on the published `@moamen-ui/pointer-react`
(GitHub Packages), so the build needs a `read:packages` token as `NODE_AUTH_TOKEN`:

```bash
export GH_PKG_TOKEN=ghp_…        # a GitHub token with read:packages
git clone https://github.com/moamen-ui/pointer-dashboard
docker run --rm -e NODE_AUTH_TOKEN="$GH_PKG_TOKEN" -v "$PWD/pointer-dashboard/react":/app -v /app/node_modules -w /app node:22 \
  bash -lc "npm ci && npm run build"
# Place the build where Compose mounts it:
mkdir -p dashboard && rm -rf dashboard/react && cp -r pointer-dashboard/react/dist dashboard/react
```

> The dashboard's API base for production is configured via its own env/build config. Point it at
> your API host there before building if it differs.

## 3. Run

```bash
docker compose --env-file .env.prod -f docker-compose.prod.yml up -d --build
```

On first start Caddy fetches Let's Encrypt certs for each host (watch `docker compose -f
docker-compose.prod.yml logs caddy`). The API auto-migrates and seeds the admin account.

## 4. Verify

```bash
curl -sI https://api.pointer.moamen.work/swagger/index.html      # 200
curl -s  "https://api.pointer.moamen.work/embed.js?project=pointer-api" | grep "var server"  # https origin
curl -sI https://app.pointer.moamen.work/                         # 200 (React dashboard)
curl -sI https://demo.pointer.moamen.work/                        # 200 (same build, demo entry)
```

## Backups

[`scripts/backup-db.sh`](scripts/backup-db.sh) runs **on the VM** and writes a compressed
`pg_dump` custom-format archive to `~/backups/pointer-<UTC ts>[-<label>].dump` (mode 600, dir 700),
plus a best-effort `uploads-<UTC ts>[-<label>].tgz` archive of the compose `uploads` volume (comment
screenshots — not in Postgres). It refuses to keep a dump that is not a valid archive, and prunes
both patterns older than 14 days while always keeping the newest three of each.

Row retention (DB-08, on by default) deletes `usage_events` > 180 d (never the `first_*` facts),
read `notifications` > 90 d, `page_context_snapshots` > 30 d with no live comment, and never-used
expired/revoked `invites` > 90 d, in batches of 5000, daily from 5 min after boot. Every such row
exists in at least the last 14 nightly dumps. Pause with `RETENTION_ENABLED=false` + `up -d api`.

Two callers:

- **Before every API deploy** — `scripts/deploy-api.sh` calls it with the label `pre-deploy`. That
  same script also **refuses to deploy** when the newest `pointer-*.dump` in `~/backups` is older
  than 26 hours (the nightly cron should always have produced one), overridable once with
  `POINTER_SKIP_BACKUP_FRESHNESS=1`.
- **Nightly** — a cron entry on the VM (installed 2026-09-22):

  ```
  0 3 * * * /home/ubuntu/pointer-api/scripts/backup-db.sh >> /home/ubuntu/backups/backup.log 2>&1
  ```

Dumps live on the same VM disk as the database. That protects against a bad migration or a bad
deploy, **not** against losing the VM — see "Off-box copy" below.

### Deploy history

- **2026-09-23**: `pre-db12` contract deploy (`POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-db12 bash scripts/deploy-api.sh`, commit `30d1b46`), 70 migrations now in production (newest `20260923003835_AddAuditEventsAppendOnlyTrigger`).

### Off-box copy

Nightly (and pre-deploy) backups are also copied off the VM to an Oracle Object Storage bucket
(`pointer-backups`) via [`scripts/offsite-backup.sh`](scripts/offsite-backup.sh), which runs
`rclone` under an rclone remote named `offsite`. Setup on a fresh VM:

```bash
curl https://rclone.org/install.sh | sudo bash
rclone config   # add a remote named `offsite`: type s3, provider chosen to match the bucket
                # (Oracle Object Storage today; Cloudflare R2 / Backblaze B2 work identically —
                # all are rclone's `s3` backend with the matching `provider`), fill in the
                # endpoint/access key/secret from the storage console. Config lands in
                # ~/.config/rclone/rclone.conf (mode 600) — credentials live ONLY there, never in
                # this repo or in .env.prod.
```

Then set `OFFSITE_REMOTE=offsite:pointer-backups` (and optionally `OFFSITE_HEALTHCHECK_URL`, a
dead-man's-switch URL such as healthchecks.io/UptimeRobot/Cronitor) in `.env.prod`
(`.env.prod.example` documents both keys). The nightly cron then copies both the dump and the
uploads tarball to `offsite:pointer-backups/pointer/` and prunes remote objects older than 30 days
(`rclone delete --min-age 30d`); verify with `rclone ls offsite:pointer-backups/pointer`.

Uploads archive caveat: `tar` over a live volume is best-effort — a file written mid-run may be
torn or missing from that night's archive (it is in the next one).

Monitoring: with `OFFSITE_HEALTHCHECK_URL` set, the monitoring service alerts when pings stop.
Until then, run `rclone ls offsite:pointer-backups/pointer | tail -3` weekly and check the newest
timestamps are yesterday's.

### Restore

Restoring replaces the live database. Stop the API first so no request runs against a half-restored
schema, and take one more dump of the current state before you overwrite it.

```bash
cd ~/pointer-api
docker compose -f docker-compose.prod.yml stop api
bash scripts/backup-db.sh pre-restore                       # keep what is there now
DUMP=~/backups/pointer-<ts>-pre-deploy.dump                 # the one you want back
docker compose --env-file .env.prod -f docker-compose.prod.yml exec -T db \
  psql -U pointer -d postgres -c "DROP DATABASE pointer;" -c "CREATE DATABASE pointer OWNER pointer;"
docker compose --env-file .env.prod -f docker-compose.prod.yml exec -T db \
  pg_restore -U pointer -d pointer --no-owner --no-privileges < "$DUMP"
docker run --rm -v pointer-api_uploads:/u -v ~/backups:/b alpine:3 sh -c 'cd /u && tar -xzf /b/uploads-<ts>.tgz'
docker compose --env-file .env.prod -f docker-compose.prod.yml up -d api
```

**Last rehearsed:** 2026-09-22, locally — the `pointer-20260922T071358Z-initial.dump` restored into a
scratch `pointer_rehearsal` database on the dev container with exactly the `pg_restore` line above
(26 tables, all 58 migration rows, row counts matched). The rehearsal recipe is `docs/db/DB-RULES.md`
§R11; repeat it whenever `backup-db.sh` or the restore steps change.

**Last drilled:** 2026-09-23 (UTC), on the VM from the off-box copy in `offsite:pointer-backups` — `pointer-20260922T201244Z-pre-db11a-rehearsal.dump` restored into `pointer_drill` in 4 s; users/comments/projects/replies/migrations matched live (7/122/27/140/65); 3 upload files extracted; scratch dropped. Runbook: `scripts/restore-drill.sh` (R5-60).
Repeat monthly or after any change to `backup-db.sh`, `offsite-backup.sh`, or the restore procedure above.

### Restore drill

[`scripts/restore-drill.sh`](scripts/restore-drill.sh) rehearses the restore above against real
production data without touching the live `pointer` database. It runs **on the prod VM**: fetches
the newest `pointer-*.dump` and `uploads-*.tgz` from `offsite:pointer-backups/pointer/`, restores
the dump into a scratch database `pointer_drill` on the same Postgres container, compares row
counts against the live `pointer` DB (`users`, `comments`, `projects`, `replies`,
`__EFMigrationsHistory`), verifies the uploads tarball extracts, times the whole run, prints one
result line, and drops `pointer_drill` (via a `trap`, so it is dropped even on failure). The API is
never stopped and `pointer` is only ever read, never written.

```bash
# on the VM
bash ~/pointer-api/scripts/restore-drill.sh
```

Suggested monthly cron (not installed by default — add it on the VM if you want the drill to run
unattended):

```
# Monthly restore drill (1st of each month at 04:00 UTC). Review ~/drill.log periodically.
0 4 1 * * /home/ubuntu/pointer-api/scripts/restore-drill.sh >> /home/ubuntu/drill.log 2>&1
```

Uploads archive: best-effort snapshot of a live volume (a file written during the run may be torn);
restore it **before** starting the API.

If the VM itself is gone, fetch the newest dump and uploads tarball from the off-box bucket first
(`rclone copy offsite:pointer-backups/pointer/pointer-<ts>.dump .` and the matching
`uploads-<ts>.tgz`, from any machine with `rclone` configured against the same remote), then follow
the same steps above against the fresh VM.

If the restore is undoing a migration, check out the matching commit **before** starting the API
again (`git checkout <commit>` then `up -d --build api`), otherwise boot re-applies the migration you
just rolled back. Rehearse this once on a scratch database (`just up` locally, restore into it) so the
first real restore is not the first attempt.

## Updating

The VM has both repos checked out as git clones (`~/pointer-api`, `~/pointer-dashboard`), so shipping
a local change is push-then-pull.

**API change** — from your machine `git push origin main`, then run
[`scripts/deploy-api.sh`](scripts/deploy-api.sh) **on the VM**. It pulls `main`, **dumps the database
first** (`scripts/backup-db.sh pre-deploy`, see § Backups), rebuilds only the `api` container, waits for
`Now listening`, and smoke-checks `/api/branding` and the Swagger spec:

```bash
# one-liner from your machine
ssh -i <key> ubuntu@<vm> 'bash -s' < scripts/deploy-api.sh
# or, on the VM
bash ~/pointer-api/scripts/deploy-api.sh
```

EF migrations auto-apply on boot; db + caddy stay up. Because of that auto-apply, **never deploy a
migration without the dump** — the script is the only supported path. Schema changes themselves are
planned by the `db-architect` agent and follow [`docs/db/DB-RULES.md`](docs/db/DB-RULES.md).

**Two deploy paths (DB-09).** A migration whose class carries `[ContractMigration]` — every
migration that carries a DB-RULES approval marker — never auto-applies on an ordinary deploy:

- **Ordinary**: `bash scripts/deploy-api.sh`. Additive (unmarked) migrations auto-apply as above. If
  a pending migration is marked, the script's pre-flight refuses before rebuilding anything and
  prints `deploy REFUSED (DB-09): pending migration(s) carry [ContractMigration]` — the running API
  is untouched, the checkout is simply ahead of it.
- **Contract**: `POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-<slug> bash
  scripts/deploy-api.sh`. Stops the API first, takes a labelled dump
  (`scripts/backup-db.sh pre-<slug>`), then rebuilds with `DBApplyContractMigrations=true` so the
  marked migration(s) apply on boot.

If `DB-09 REFUSED` ever appears in `docker compose logs api` after an *ordinary* deploy, that is a
bug — see [`docs/db/DB-RULES.md`](docs/db/DB-RULES.md) R7 and
[`docs/db/execution/DB-09-migration-apply-gate.md`](docs/db/execution/DB-09-migration-apply-gate.md).

If endpoints/DTOs changed, **republish the typed clients** once the new API is live (the workflow
reads the live spec and auto-bumps the patch version). From your machine (gh authed):

```bash
gh workflow run publish-clients.yml -R moamen-ui/poitner-api    # or: just publish-clients
```

Then bump `@moamen-ui/pointer-react` in each consumer (e.g. the dashboard) to the new version.

> **Fully automatic option:** the workflow also accepts a `repository_dispatch` of type `api-deployed`.
> Fire it from the VM at the end of the deploy with a token that has `repo` scope:
> `curl -s -X POST -H "Authorization: Bearer $GH_DISPATCH_TOKEN" -H "Accept: application/vnd.github+json" https://api.github.com/repos/moamen-ui/poitner-api/dispatches -d '{"event_type":"api-deployed"}'`

### JWT key rotation (R5-62)

> **`JWT_SIGNING_KEY` is also the root secret for API-key encryption, password-reset tokens, signed
> upload URLs and audit IP hashing** (`ApiKeyProtector`, `ResetTokenService`, `UploadSigner`,
> `AuditWriter` all read `JWT:SigningKey` directly when their own dedicated key is unset — production
> never sets `Auth:ApiKeyEncryptionKey`, so it always falls into this case). **Never change
> `JWT_SIGNING_KEY`.** Rotate JWT signing keys through the `JWT_KEY_0_*` / `JWT_KEY_1_*` slots and
> `JWT_ACTIVE_KEY_ID` only — never by repointing `JWT_SIGNING_KEY` itself. (Setting a dedicated
> `Auth__ApiKeyEncryptionKey` later, to fully decouple API-key encryption from the JWT secret,
> requires a re-encryption migration for existing stored API keys — out of scope here.)

Rotating a JWT signing key used to invalidate every outstanding token — a global logout for every
user and every CLI session. Tokens now carry a `kid` (key id) header and the API validates against
every configured key, so a rotation is a config change + redeploy with zero user impact. A rotation
only ever edits `JWT_KEY_0_*`, `JWT_KEY_1_*` and `JWT_ACTIVE_KEY_ID` in `.env.prod` — `JWT_SIGNING_KEY`
is never touched:

1. Generate a new key: `openssl rand -hex 32`
2. Add it to `.env.prod` as the new key-in-waiting (slot 1):
   ```
   JWT_KEY_1_ID=k1
   JWT_KEY_1_SECRET=<new key>
   ```
3. Deploy: `bash scripts/deploy-api.sh`. Both keys now validate; new tokens still use k0
   (`JWT_ACTIVE_KEY_ID` is still unset ⇒ defaults to `k0`).
4. Switch the active key: set `JWT_ACTIVE_KEY_ID=k1` in `.env.prod`. Deploy again. New tokens now
   carry `kid: k1`; existing k0 tokens still validate (k0 is still in the list).
5. Wait 12 hours (max token lifetime, `JWT__LifetimeHours`). All k0 tokens have expired.
6. Fold k1 into slot 0: set `JWT_KEY_0_SECRET` to the same value as `JWT_KEY_1_SECRET` (slot 0's
   secret becomes the new key — `JWT_KEY_0_ID` stays `k0`; ids are just labels, not secrets), then
   clear `JWT_KEY_1_ID`/`JWT_KEY_1_SECRET` and reset `JWT_ACTIVE_KEY_ID=` (empty, or delete the
   line — `k0` is the default) in `.env.prod`. Deploy. The config is back to a single active key —
   ready for the next rotation. `JWT_SIGNING_KEY` itself was never touched by any of these steps.

A token's `kid` is a hint, not a filter: when an incoming token's `kid` names a key that is no
longer configured, the validator does not reject it outright for that reason — it falls back to
trying every configured key (`TryAllIssuerSigningKeys`, the .NET default) and rejects the token
only if none of them match its signature. So actually retiring a key depends on its *secret* no
longer being present in the ring, not on its id being removed. Always wait a further 12 h (one JWT
lifetime) after a key id stops being used to sign new tokens before removing its secret from the
ring — step 5 above already covers this for the normal rotation flow.

Verify a rotation step took effect from `docker compose logs api` (only key **ids** are logged, never
secrets): `[JWT] active kid=k1; configured kids=[k0, k1]`. Decode a fresh token's header to confirm
its `kid` directly: `echo '<token>' | cut -d. -f1 | base64 -d | jq .kid`.

**Landing page change** — from your machine `git push origin main`, then on the VM.

`landing/` is bind-mounted read-only into Caddy (`./landing:/srv/landing:ro`) and served by
`file_server`, which reads from disk per request. So routine content edits are just a pull:

```bash
cd ~/pointer-api && git pull --ff-only        # bind-mounted → served live, no restart needed
```

Only restart Caddy when the **`Caddyfile` itself** changed (e.g. the first time this landing
block + mount were added) — to pick up the new config / bind-mount inode:

```bash
docker compose -f docker-compose.prod.yml up -d --force-recreate caddy
```

This recreates **only the Caddy container** (a few seconds) — the API and DB keep running. Deploys
are per-service; nothing here rebuilds the API or the dashboard.

**Dashboard change** — from your machine `git push origin main` in `pointer-dashboard`, then run
[`scripts/deploy-dashboards.sh`](scripts/deploy-dashboards.sh) **on the VM**. It pulls
`~/pointer-dashboard`, builds the React app in a `node:24` container (`npm ci` authenticates to
GitHub Packages with `GH_PKG_TOKEN` as `NODE_AUTH_TOKEN`), copies the build to
`~/pointer-api/dashboard/react` (removing any stale `dashboard/angular` / `dashboard/vue` dirs),
restarts Caddy and curls the two live hosts (`app`, `demo`):

```bash
# one-liner from your machine (streams the script over SSH, no pull of pointer-api needed)
ssh -i <key> ubuntu@<vm> 'bash -s' < scripts/deploy-dashboards.sh
# or, on the VM, after `cd ~/pointer-api && git pull --ff-only`
bash ~/pointer-api/scripts/deploy-dashboards.sh
```

> **Token:** set `GH_PKG_TOKEN` on the VM once (a GitHub token with `read:packages`) —
> `echo 'export GH_PKG_TOKEN=ghp_…' >> ~/.bashrc`. Non-interactive shells (`ssh vm '…'`, `bash -s`)
> skip `~/.bashrc`, so the script greps the `export` line out of `~/.bashrc`/`~/.profile` itself —
> no need to `source` anything or pass the token inline.
> If the API's endpoints/DTOs changed, first republish the clients (the *Publish API clients* workflow
> in this repo) and bump `@moamen-ui/pointer-react` in the dashboard before deploying.

## Notes

- `.env.prod` holds secrets and is **gitignored** — never commit it.
- The API runs behind Caddy and isn't published to the host; Caddy is the only public entrypoint.
- `ASPNETCORE` honors `X-Forwarded-Proto/For` (see `API/Program.cs`) so generated URLs use `https`.
- Volumes `pgdata` (database) and `uploads` (screenshots) persist across redeploys.
