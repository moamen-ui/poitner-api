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
`pg_dump` custom-format archive to `~/backups/pointer-<UTC ts>[-<label>].dump` (mode 600, dir 700).
It refuses to keep a file that is not a valid archive, and prunes dumps older than 14 days while
always keeping the newest three.

Two callers:

- **Before every API deploy** — `scripts/deploy-api.sh` calls it with the label `pre-deploy`.
- **Nightly** — a cron entry on the VM (installed 2026-09-22):

  ```
  0 3 * * * /home/ubuntu/pointer-api/scripts/backup-db.sh >> /home/ubuntu/backups/backup.log 2>&1
  ```

Dumps live on the same VM disk as the database. That protects against a bad migration or a bad
deploy, **not** against losing the VM. Copying the newest dump off-box (object storage, or an
`rsync` from your machine) is the next step and is tracked in `docs/db/`.

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
docker compose --env-file .env.prod -f docker-compose.prod.yml up -d api
```

**Last rehearsed:** 2026-09-22, locally — the `pointer-20260922T071358Z-initial.dump` restored into a
scratch `pointer_rehearsal` database on the dev container with exactly the `pg_restore` line above
(26 tables, all 58 migration rows, row counts matched). The rehearsal recipe is `docs/db/DB-RULES.md`
§R11; repeat it whenever `backup-db.sh` or the restore steps change.

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

If endpoints/DTOs changed, **republish the typed clients** once the new API is live (the workflow
reads the live spec and auto-bumps the patch version). From your machine (gh authed):

```bash
gh workflow run publish-clients.yml -R moamen-ui/poitner-api    # or: just publish-clients
```

Then bump `@moamen-ui/pointer-react` in each consumer (e.g. the dashboard) to the new version.

> **Fully automatic option:** the workflow also accepts a `repository_dispatch` of type `api-deployed`.
> Fire it from the VM at the end of the deploy with a token that has `repo` scope:
> `curl -s -X POST -H "Authorization: Bearer $GH_DISPATCH_TOKEN" -H "Accept: application/vnd.github+json" https://api.github.com/repos/moamen-ui/poitner-api/dispatches -d '{"event_type":"api-deployed"}'`

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
