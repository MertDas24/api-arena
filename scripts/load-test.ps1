$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
$oldResult = $env:APIARENA_LOAD_RESULT
try {
  New-Item -ItemType Directory -Force artifacts | Out-Null
  $env:APIARENA_LOAD_RESULT = Join-Path $root 'artifacts/bounded-load.json'
  dotnet test backend/tests/APIArena.IntegrationTests -c Release --filter Stage17_BoundedLoadTests -nologo
  if ($LASTEXITCODE -ne 0) { throw 'Bounded load test failed' }
  Write-Host 'Measurements: artifacts/bounded-load.json (synthetic loopback sample only)'
} finally {
  $env:APIARENA_LOAD_RESULT = $oldResult
  Pop-Location
}
