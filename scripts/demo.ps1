<#
.SYNOPSIS
  Real end-to-end demo against a locally running API Arena (scripts/dev.ps1 first):
  creates two runs of one scenario, drives them with the example client in naive and resilient mode,
  finishes them and prints the report and comparison links.
.PARAMETER Scenario  retry-503 | rate-limit | timeout
.NOTES
  Needs APIARENA_DEMO_PASSWORD (same value used for `--seed-demo`). The run key is passed to the client via
  the process environment only; it is never printed.
#>
param([ValidateSet("retry-503", "rate-limit", "timeout")][string]$Scenario = "retry-503",
      [string]$Frontend = "http://localhost:3000")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not $env:APIARENA_DEMO_PASSWORD) { throw "APIARENA_DEMO_PASSWORD is not set. Run: dotnet run --project backend/src/APIArena.Api -- --seed-demo" }

$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
function Csrf { (Invoke-RestMethod "$Frontend/api/auth/csrf" -WebSession $session).token }
function Post($path, $body) {
  $h = @{ "X-CSRF-TOKEN" = (Csrf); "Origin" = $Frontend }
  Invoke-RestMethod "$Frontend$path" -Method Post -WebSession $session -Headers $h -ContentType "application/json" -Body ($body | ConvertTo-Json -Compress)
}

Post "/api/auth/login" @{ email = "demo@apiarena.local"; password = $env:APIARENA_DEMO_PASSWORD } | Out-Null
$project = (Invoke-RestMethod "$Frontend/api/projects?search=%5BDEMO%5D" -WebSession $session).items | Select-Object -First 1
if (-not $project) { throw "Demo project not found. Run --seed-demo first." }

Push-Location $root
try {
  dotnet build examples/dotnet-client -nologo -v q | Out-Null
  $runs = @{}
  foreach ($mode in "naive", "resilient") {
    $run = Post "/api/projects/$($project.id)/runs" @{ scenario = "$Scenario-v1"; label = "demo $mode" }
    $key = Post "/api/runs/$($run.id)/access-keys" @{}
    Post "/api/runs/$($run.id)/start" @{} | Out-Null
    $env:SIM_BASE_URL = $key.simulatorBaseUrl
    $env:RUN_KEY = $key.key
    Write-Host "`n=== $Scenario · $mode ==="
    dotnet run --project examples/dotnet-client --no-build -- $Scenario $mode
    Remove-Item Env:RUN_KEY
    Post "/api/runs/$($run.id)/finish" @{} | Out-Null
    $runs[$mode] = $run.id
  }
} finally { Pop-Location }

Write-Host "`nRaporlar:"
Write-Host "  naive:     $Frontend/runs/$($runs['naive'])/report"
Write-Host "  resilient: $Frontend/runs/$($runs['resilient'])/report"
Write-Host "  karşılaştır: $Frontend/compare?left=$($runs['naive'])&right=$($runs['resilient'])"
