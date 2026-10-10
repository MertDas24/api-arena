# Runs the same checks as CI (except Playwright E2E, see scripts/e2e.ps1). Exits non-zero on the first failure.
# Integration tests need SQL Server: APIARENA_TEST_SQL or the local default (Windows auth).
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
function Step($name, [scriptblock]$block) {
  Write-Host "==> $name"
  & $block
  if ($LASTEXITCODE -ne 0) { throw "$name failed (exit $LASTEXITCODE)" }
}
Push-Location $root
try {
  Step "dotnet build" { dotnet build backend/APIArena.sln -c Release -nologo }
  Step "dotnet test" { dotnet test backend/APIArena.sln -c Release --no-build -nologo }
  Push-Location frontend
  try {
    Step "npm ci" { npm ci --no-audit --no-fund }
    Step "lint" { npm run lint }
    Step "typecheck" { npm run typecheck }
    Step "unit tests" { npm test }
    Step "build" { npm run build }
  } finally { Pop-Location }
} finally { Pop-Location }
Write-Host "All checks passed."
