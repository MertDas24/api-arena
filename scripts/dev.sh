#!/usr/bin/env bash
# Starts API Arena locally on macOS/Linux (Docker SQL Server path). Ctrl+C stops all processes.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
[ -f .env ] || { echo "Missing .env — run scripts/dev-secrets.sh and 'docker compose up -d' first."; exit 1; }
set -a; . ./.env; set +a
export ASPNETCORE_ENVIRONMENT=Development DOTNET_ENVIRONMENT=Development

if [ "${1:-}" != "--skip-migrate" ]; then ./scripts/db-migrate.sh; fi
[ -d frontend/node_modules ] || (cd frontend && npm ci)

pids=()
trap 'kill "${pids[@]}" 2>/dev/null || true' EXIT INT TERM
(cd backend/src/APIArena.Api && dotnet run --no-launch-profile --urls http://localhost:5150) & pids+=($!)
(cd backend/src/APIArena.Simulator && dotnet run --no-launch-profile --urls http://localhost:5151) & pids+=($!)
(cd backend/src/APIArena.Worker && dotnet run --no-launch-profile) & pids+=($!)
(cd frontend && npm run dev) & pids+=($!)
echo "Started. Open http://localhost:3000 (first compile can take ~30 s)."
wait
