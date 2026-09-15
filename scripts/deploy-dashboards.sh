#!/usr/bin/env bash
# Rebuild and publish all three dashboards (Angular / React / Vue) on the production VM.
#
# Runs ON the VM. Either interactively:
#   ssh vm 'bash -s' < scripts/deploy-dashboards.sh
# or after `git pull` in ~/pointer-api:
#   bash ~/pointer-api/scripts/deploy-dashboards.sh
#
# Expects `~/pointer-dashboard` and `~/pointer-api` to be git clones (see DEPLOY.md § Updating) and
# `export GH_PKG_TOKEN=ghp_…` (read:packages) in ~/.bashrc or ~/.profile — it is grepped out of
# those files because non-interactive shells skip ~/.bashrc entirely.
set -euo pipefail

eval "$(grep -hE '^\s*export GH_PKG_TOKEN=' ~/.bashrc ~/.profile 2>/dev/null | head -1)" || true
: "${GH_PKG_TOKEN:?GH_PKG_TOKEN not set on the VM (see DEPLOY.md § VM setup)}"

cd ~/pointer-dashboard && git pull --ff-only && git log --oneline -1

build() { # $1=app dir  $2=build command
  docker run --rm -e NODE_AUTH_TOKEN="$GH_PKG_TOKEN" \
    -v "$HOME/pointer-dashboard/$1":/app -v "/app/node_modules" -w /app node:24 \
    bash -lc "npm ci --no-audit --no-fund && $2" 2>&1 | tail -3
}
build angular "npx ng build --configuration production"
build react   "npm run build"
build vue     "npm run build"

# Caddy serves ~/pointer-api/dashboard/<fw> at app-<fw>.pointer (see Caddyfile).
mkdir -p ~/pointer-api/dashboard
rm -rf ~/pointer-api/dashboard/angular && cp -r ~/pointer-dashboard/angular/dist/admin-web/browser ~/pointer-api/dashboard/angular
rm -rf ~/pointer-api/dashboard/react   && cp -r ~/pointer-dashboard/react/dist   ~/pointer-api/dashboard/react
rm -rf ~/pointer-api/dashboard/vue     && cp -r ~/pointer-dashboard/vue/dist     ~/pointer-api/dashboard/vue
ls ~/pointer-api/dashboard/*/index.html

cd ~/pointer-api && docker compose --env-file .env.prod -f docker-compose.prod.yml restart caddy
for h in app-angular app-react app-vue app; do
  printf "%s " "$h"; curl -s -o /dev/null -w "%{http_code}\n" "https://$h.pointer.moamen.work/"
done
