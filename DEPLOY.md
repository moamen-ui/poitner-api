# Deploy

Production runs on a single Linux VM with Docker Compose: **Postgres + API + Caddy**. Caddy
terminates TLS (auto Let's Encrypt), reverse-proxies the API, and serves the built dashboard as
static files. The current deployment:

| Host | Serves |
|---|---|
| `api.pointer.moamen.work` | this API (Swagger, `/pointer.js`, `/embed.js`, skills) |
| `app-angular.pointer.moamen.work` | the Angular [`pointer-dashboard`](https://github.com/moamen-ui/pointer-dashboard) build (`dashboard/angular`) |
| `app.pointer.moamen.work` | default dashboard host → also the Angular build (back-compat) |
| `pointer.moamen.work` | 301 → `app.pointer.moamen.work` |

> Dashboards are served per-framework from `dashboard/<fw>/` (e.g. `dashboard/angular`), each at
> `app-<fw>.pointer.moamen.work`. React/Vue dashboards can be added later under the same scheme.

Files: [`docker-compose.prod.yml`](docker-compose.prod.yml), [`Caddyfile`](Caddyfile),
[`.env.prod.example`](.env.prod.example).

## Prerequisites

- A VM with a public IP and **Docker + Compose plugin** installed.
- Ports **80** and **443** open to the world (host firewall **and** any cloud security list/group).
- DNS **A records** for each hostname → the VM's public IP (e.g. `api.pointer`, `app-angular.pointer`,
  `app.pointer`, bare `pointer`). Certs are issued by HTTP-01, so the names must resolve before first start.

## VM setup (one-time)

The dashboard depends on the private `@moamen-ui/pointer-*` GitHub Packages, so the VM needs a
**`read:packages`** token to build it. Set it once (used as `NODE_AUTH_TOKEN` by `npm ci`):

```bash
# Create a classic token at github.com/settings/tokens with scope: read:packages
echo 'export GH_PKG_TOKEN=ghp_…' >> ~/.bashrc && source ~/.bashrc
```

`~/.bashrc` is read for **interactive** SSH (the deploy flow below). For one-liner `ssh vm '…'`
deploys it's skipped — keep the token in a `chmod 600` file and `source` it instead.

## 1. Configure

```bash
git clone https://github.com/moamen-ui/poitner-api && cd poitner-api
cp .env.prod.example .env.prod      # fill in real secrets (openssl rand -hex 32)
```

Adjust hostnames in `Caddyfile` and `Pointer__*` / `POINTER_SERVER` if you use different domains.

## 2. Build the dashboard → `./dashboard/angular`

The dashboard is a separate repo. It depends on the published `@moamen-ui/pointer-angular`
(GitHub Packages), so the build needs a `read:packages` token as `NODE_AUTH_TOKEN`:

```bash
export GH_PKG_TOKEN=ghp_…        # a GitHub token with read:packages
git clone https://github.com/moamen-ui/pointer-dashboard
# The dashboard is a monorepo (angular/ react/ vue/) — build the app you want:
docker run --rm -e NODE_AUTH_TOKEN="$GH_PKG_TOKEN" -v "$PWD/pointer-dashboard/angular":/app -v /app/node_modules -w /app node:22 \
  bash -lc "npm ci && npx ng build --configuration production"
# Place the build where Compose mounts it (one dir per framework):
mkdir -p dashboard && rm -rf dashboard/angular && cp -r pointer-dashboard/angular/dist/admin-web/browser dashboard/angular
```

> The dashboard's `apiBase` for production lives in its `environment.prod.ts` (swapped in by
> `angular.json` `fileReplacements`). Point it at your API host there before building if it differs.

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
curl -sI https://app-angular.pointer.moamen.work/                # 200 (Angular dashboard)
curl -sI https://app.pointer.moamen.work/                        # 200 (back-compat)
```

## Updating

The VM has both repos checked out as git clones (`~/pointer-api`, `~/pointer-dashboard`), so shipping
a local change is push-then-pull.

**API change** — from your machine `git push origin main`, then on the VM:

```bash
cd ~/pointer-api
git pull --ff-only
docker compose --env-file .env.prod -f docker-compose.prod.yml up -d --build api
# EF migrations auto-apply on boot; db + caddy stay up.
```

If endpoints/DTOs changed, **republish the typed clients** once the new API is live (the workflow
reads the live spec and auto-bumps the patch version). From your machine (gh authed):

```bash
gh workflow run publish-clients.yml -R moamen-ui/poitner-api    # or: just publish-clients
```

Then bump `@moamen-ui/pointer-<framework>` in each consumer (e.g. the dashboard) to the new version.

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
are per-service; nothing here rebuilds the API or dashboards.

**Dashboard change** — from your machine `git push origin main`, then on the VM. The dashboard
depends on the published `@moamen-ui/pointer-angular` (GitHub Packages), so `npm ci` needs a
**`read:packages` token** passed as `NODE_AUTH_TOKEN`:

```bash
cd ~/pointer-dashboard && git pull --ff-only && cd angular
docker run --rm -e NODE_AUTH_TOKEN="$GH_PKG_TOKEN" -v "$PWD":/app -v /app/node_modules -w /app node:22 \
  bash -lc "npm ci && npx ng build --configuration production"
mkdir -p ~/pointer-api/dashboard && rm -rf ~/pointer-api/dashboard/angular \
  && cp -r dist/admin-web/browser ~/pointer-api/dashboard/angular
docker compose -f ~/pointer-api/docker-compose.prod.yml restart caddy
# React: cd ~/pointer-dashboard/react → build → copy to ~/pointer-api/dashboard/react (served at app-react.pointer)
# Vue:   cd ~/pointer-dashboard/vue   → build → copy to ~/pointer-api/dashboard/vue   (served at app-vue.pointer)
```

> **Token:** set `GH_PKG_TOKEN` on the VM (a GitHub token with `read:packages`) —
> `echo 'export GH_PKG_TOKEN=ghp_…' >> ~/.bashrc && source ~/.bashrc`. This works for **interactive**
> SSH sessions (the flow above). For one-liner `ssh vm '…'` deploys, `~/.bashrc` is skipped — keep the
> token in a file and `source` it, or pass it inline.
> If the API's endpoints/DTOs changed, first republish the client (the *Publish API clients* workflow
> in this repo, bump the version) and bump `@moamen-ui/pointer-angular` in the dashboard.

## Notes

- `.env.prod` holds secrets and is **gitignored** — never commit it.
- The API runs behind Caddy and isn't published to the host; Caddy is the only public entrypoint.
- `ASPNETCORE` honors `X-Forwarded-Proto/For` (see `API/Program.cs`) so generated URLs use `https`.
- Volumes `pgdata` (database) and `uploads` (screenshots) persist across redeploys.
