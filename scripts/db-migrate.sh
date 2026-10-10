#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
if [ -n "${ConnectionStrings__ApiArena:-}" ]; then export APIARENA_DESIGN_CONNECTION="$ConnectionStrings__ApiArena"; fi
dotnet tool restore >/dev/null
dotnet ef database update -p backend/src/APIArena.Infrastructure -s backend/src/APIArena.Infrastructure
