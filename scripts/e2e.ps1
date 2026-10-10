# Builds backend + frontend, migrates the E2E database and runs Playwright against the real stack.
# Uses E2E_SQL_CONNECTION or the local default (database ApiArena_E2E, Windows auth).
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not $env:E2E_SQL_CONNECTION) {
  $env:E2E_SQL_CONNECTION = "Server=localhost;Database=ApiArena_E2E;Integrated Security=true;Encrypt=true;TrustServerCertificate=true"
}
Push-Location $root
try {
  dotnet build backend/APIArena.sln -nologo -v q
  if ($LASTEXITCODE -ne 0) { throw "backend build failed" }
  dotnet tool restore | Out-Null
  $env:APIARENA_DESIGN_CONNECTION = $env:E2E_SQL_CONNECTION
  dotnet ef database update -p backend/src/APIArena.Infrastructure -s backend/src/APIArena.Infrastructure
  if ($LASTEXITCODE -ne 0) { throw "migration failed" }
  Push-Location frontend
  try {
    npm run build
    if ($LASTEXITCODE -ne 0) { throw "frontend build failed" }
    npx playwright test @args
    if ($LASTEXITCODE -ne 0) { throw "e2e failed" }
  } finally { Pop-Location }
} finally { Pop-Location }
