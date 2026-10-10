<#
.SYNOPSIS
  Creates/updates the git-ignored .env with locally generated development secrets. Values are never printed.
.PARAMETER Docker    Add a generated SQL Server SA password and the matching connection string (Docker SQL path).
.PARAMETER Webhooks  Enable the v1 webhook module for LOCAL development: random AES-256 key + dev-only localhost receiver.
.NOTES
  Native Windows SQL Server with Integrated Security needs no database secret. Existing keys are kept as they are.
#>
param([switch]$Docker, [switch]$Webhooks)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $root ".env"

$lines = [System.Collections.Generic.List[string]]::new()
if (Test-Path $envFile) { $lines.AddRange([IO.File]::ReadAllLines($envFile)) } else { $lines.AddRange([IO.File]::ReadAllLines((Join-Path $root ".env.example"))) }

function Get-Random64([int]$bytes) {
  $b = New-Object byte[] $bytes
  [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
  [Convert]::ToBase64String($b)
}
function Set-Key([string]$name, [string]$value, [switch]$OnlyIfEmpty) {
  for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match "^$([regex]::Escape($name))=(.*)$") {
      if ($OnlyIfEmpty -and $Matches[1] -ne "") { return }
      $lines[$i] = "$name=$value"; return
    }
  }
  $lines.Add("$name=$value")
}

if ($Docker) {
  $password = "Aa1!" + ((Get-Random64 24) -replace '[+/=]', 'x')
  Set-Key "MSSQL_SA_PASSWORD" $password -OnlyIfEmpty
  $current = ($lines | Where-Object { $_ -match '^MSSQL_SA_PASSWORD=' }) -replace '^MSSQL_SA_PASSWORD=', ''
  Set-Key "ConnectionStrings__ApiArena" "Server=localhost,1433;Database=ApiArena_Dev;User Id=sa;Password=$current;Encrypt=true;TrustServerCertificate=true" -OnlyIfEmpty
}
if ($Webhooks) {
  Set-Key "ApiArena__Webhooks__Enabled" "true"
  Set-Key "ApiArena__Webhooks__EncryptionKeys__Current" (Get-Random64 32) -OnlyIfEmpty
  Set-Key "ApiArena__Webhooks__DevLocalReceiver__Enabled" "true"
}

[IO.File]::WriteAllLines($envFile, $lines)
Write-Host "Updated .env (git-ignored). Secret values were generated locally and not printed."
