#!/usr/bin/env bash
# Runs the same checks as CI (except Playwright E2E). Integration tests need APIARENA_TEST_SQL.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
echo "==> dotnet build"; dotnet build backend/APIArena.sln -c Release -nologo
echo "==> dotnet test";  dotnet test backend/APIArena.sln -c Release --no-build -nologo
cd frontend
echo "==> npm ci";       npm ci --no-audit --no-fund
echo "==> lint";         npm run lint
echo "==> typecheck";    npm run typecheck
echo "==> unit tests";   npm test
echo "==> build";        npm run build
echo "All checks passed."
