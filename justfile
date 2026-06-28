set dotenv-load := true
up:        ; docker compose up
down:      ; docker compose down
build:     ; dotnet build
fmt:       ; dotnet csharpier .
migrate name: ; dotnet ef migrations add {{name}} -p Infrastructure -s API
db-update: ; dotnet ef database update -p Infrastructure -s API
test:      ; dotnet test
psql:      ; docker compose exec db psql -U pointer -d pointer
# Web component (<pointer-feedback>) — builds into API/wwwroot/pointer.{js,css}
widget:        ; cd web-component && npm run watch    # rebuild on change during local dev
widget-build:  ; cd web-component && npm run build    # one-shot build (run + commit before pushing)
# API client generation (Angular + React + Vue from Swagger spec)
gen-clients: ; npm run generate-clients
# One-command dev: start API + DB, wait for ready, generate clients
dev:
  docker compose up -d
  @echo "Waiting for API on :8090 ..."
  @until curl -sf http://localhost:8090/swagger/v1/swagger.json > /dev/null 2>&1; do sleep 2; done
  @echo "API ready."
  npm run generate-clients
  @echo "\n✅ All ready! Dashboard: cd ../pointer-dashboard && npm start"
