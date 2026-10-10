<#
.SYNOPSIS
  Starts API Arena locally on Windows: migrates the database, then opens one window each for
  control API (:5150), simulator (:5151), worker and frontend (:3000).
.PARAMETER SkipMigrate  Do not apply EF migrations first.
.PARAMETER WithReceiver Also start the sample webhook receiver (:5160, v1 local demo).
#>
param([switch]$SkipMigrate, [switch]$WithReceiver)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

# Optional .env (Docker SQL path). Values are loaded into this process only, never printed.
$envFile = Join-Path $root ".env"
if (Test-Path $envFile) {
  Get-Content $envFile | Where-Object { $_ -match '^\s*([A-Za-z_][A-Za-z0-9_]*)=(.*)$' } | ForEach-Object {
    $name = $Matches[1]; $value = $Matches[2]
    if ($value -ne "") { [Environment]::SetEnvironmentVariable($name, $value, "Process") }
  }
}
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:DOTNET_ENVIRONMENT = "Development"

if (-not $SkipMigrate) { & (Join-Path $PSScriptRoot "db-migrate.ps1") }

if (-not (Test-Path (Join-Path $root "frontend/node_modules"))) {
  Push-Location (Join-Path $root "frontend"); npm ci; Pop-Location
}

function Start-Window($title, $dir, $cmd) {
  $full = "`$host.UI.RawUI.WindowTitle='$title'; `$env:ASPNETCORE_ENVIRONMENT='Development'; `$env:DOTNET_ENVIRONMENT='Development'; Set-Location '$dir'; $cmd"
  Start-Process powershell -ArgumentList "-NoExit", "-Command", $full | Out-Null
}

Start-Window "APIArena control API :5150" (Join-Path $root "backend/src/APIArena.Api") "dotnet run --no-launch-profile --urls http://localhost:5150"
Start-Window "APIArena simulator :5151"   (Join-Path $root "backend/src/APIArena.Simulator") "dotnet run --no-launch-profile --urls http://localhost:5151"
Start-Window "APIArena worker"            (Join-Path $root "backend/src/APIArena.Worker") "dotnet run --no-launch-profile"
if ($WithReceiver) {
  Start-Window "APIArena sample webhook receiver :5160" (Join-Path $root "examples/webhook-receiver") "dotnet run --no-launch-profile --urls http://localhost:5160"
}
Start-Window "APIArena frontend :3000"    (Join-Path $root "frontend") "npm run dev"

Write-Host "Started. Open http://localhost:3000 (first compile can take ~30 s). Close the windows to stop."
