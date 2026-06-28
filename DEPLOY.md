# Deploy

Production runs on a single Linux VM with Docker Compose: **Postgres + API + Caddy**. Caddy
terminates TLS (auto Let's Encrypt), reverse-proxies the API, and serves the built dashboard as
static files. The current deployment:

| Host | Serves |
|---|---|
| `api.pointer.moamen.work` | this API (Swagger, `/pointer.js`, `/embed.js`, skills) |
| `app.pointer.moamen.work` | the [`pointer-dashboard`](https://github.com/moamen-ui/pointer-dashboard) static build |
| `pointer.moamen.work` | 301 → `app.pointer.moamen.work` |

Files: [`docker-compose.prod.yml`](docker-compose.prod.yml), [`Caddyfile`](Caddyfile),
[`.env.prod.example`](.env.prod.example).

## Prerequisites

- A VM with a public IP and **Docker + Compose plugin** installed.
- Ports **80** and **443** open to the world (host firewall **and** any cloud security list/group).
- DNS **A records** for each hostname → the VM's public IP (e.g. `api.pointer`, `app.pointer`, and
  bare `pointer`). Certs are issued by HTTP-01, so the names must resolve before first start.

## 1. Configure

```bash
git clone https://github.com/moamen-ui/poitner-api && cd poitner-api
cp .env.prod.example .env.prod      # fill in real secrets (openssl rand -hex 32)
```

Adjust hostnames in `Caddyfile` and `Pointer__*` / `POINTER_SERVER` if you use different domains.

## 2. Build the dashboard → `./dashboard-dist`

The dashboard is a separate repo; build it and drop its static output where Compose mounts it:

```bash
git clone https://github.com/moamen-ui/pointer-dashboard
# Build in a Node container so the host needs no Node (production config bakes in the API host):
docker run --rm -v "$PWD/pointer-dashboard":/app -v /app/node_modules -w /app node:22 \
  bash -lc "npm ci && npx ng build --configuration production"
# Place the build where docker-compose.prod.yml expects it:
rm -rf dashboard-dist && cp -r pointer-dashboard/dist/admin-web/browser dashboard-dist
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
curl -sI https://app.pointer.moamen.work/                        # 200 (dashboard)
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

# Regenerate API clients + sync to dashboard (if endpoints/DTOs changed):
npm run generate-clients
```

**Dashboard change** — from your machine `git push origin main`, then on the VM. The dashboard
depends on the published `@moamen-ui/pointer-angular` (GitHub Packages), so `npm ci` needs a
**`read:packages` token** passed as `NODE_AUTH_TOKEN`:

```bash
cd ~/pointer-dashboard
git pull --ff-only
docker run --rm -e NODE_AUTH_TOKEN="$GH_PKG_TOKEN" -v "$PWD":/app -v /app/node_modules -w /app node:22 \
  bash -lc "npm ci && npx ng build --configuration production"
rm -rf ~/pointer-api/dashboard-dist && cp -r dist/admin-web/browser ~/pointer-api/dashboard-dist
docker compose -f ~/pointer-api/docker-compose.prod.yml restart caddy
```

> Set `GH_PKG_TOKEN` once on the VM (a GitHub token with `read:packages`), e.g. add
> `export GH_PKG_TOKEN=ghp_…` to `~/.bashrc`. The committed `.npmrc` reads `${NODE_AUTH_TOKEN}`.
> If the API's endpoints/DTOs changed, first republish the client (the *Publish API clients*
> workflow in this repo, bump the version) and bump `@moamen-ui/pointer-angular` in the dashboard.

## Notes

- `.env.prod` holds secrets and is **gitignored** — never commit it.
- The API runs behind Caddy and isn't published to the host; Caddy is the only public entrypoint.
- `ASPNETCORE` honors `X-Forwarded-Proto/For` (see `API/Program.cs`) so generated URLs use `https`.
- Volumes `pgdata` (database) and `uploads` (screenshots) persist across redeploys.
