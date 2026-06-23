set dotenv-load := true
up:        ; docker compose up
down:      ; docker compose down
build:     ; dotnet build
fmt:       ; dotnet csharpier .
migrate name: ; dotnet ef migrations add {{name}} -p Infrastructure -s API
db-update: ; dotnet ef database update -p Infrastructure -s API
test:      ; dotnet test
psql:      ; docker compose exec db psql -U pointer -d pointer
