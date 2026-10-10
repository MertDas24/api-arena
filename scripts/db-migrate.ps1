# Applies EF Core migrations to the development database.
# Uses ConnectionStrings__ApiArena when set (Docker path), else the native local default (Windows auth).
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
  if ($env:ConnectionStrings__ApiArena) { $env:APIARENA_DESIGN_CONNECTION = $env:ConnectionStrings__ApiArena }
  dotnet tool restore | Out-Null
  dotnet ef database update -p backend/src/APIArena.Infrastructure -s backend/src/APIArena.Infrastructure
  if ($LASTEXITCODE -ne 0) { throw "Migration failed (exit $LASTEXITCODE)" }
} finally { Pop-Location }
