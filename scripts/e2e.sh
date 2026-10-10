#!/usr/bin/env bash
# Builds backend + frontend, migrates the E2E database and runs Playwright against the real stack.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
: "${E2E_SQL_CONNECTION:?Set E2E_SQL_CONNECTION (SQL Server connection string for the E2E database)}"
dotnet build backend/APIArena.sln -nologo -v q
dotnet tool restore >/dev/null
APIARENA_DESIGN_CONNECTION="$E2E_SQL_CONNECTION" dotnet ef database update -p backend/src/APIArena.Infrastructure -s backend/src/APIArena.Infrastructure
cd frontend
npm run build
npx playwright test "$@"
