$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$smokeDb = 'ApiArena_MigrationSmoke_' + [guid]::NewGuid().ToString('N')
if ($smokeDb -notmatch '^ApiArena_MigrationSmoke_[a-f0-9]{32}$') { throw 'Unsafe DB name' }
$previousEnvironment = $env:ASPNETCORE_ENVIRONMENT
$previousConnection = $env:ConnectionStrings__ApiArena
Push-Location $root
try {
  $env:ASPNETCORE_ENVIRONMENT = 'Development'
  $env:ConnectionStrings__ApiArena = "Server=localhost;Database=$smokeDb;Integrated Security=true;Encrypt=true;TrustServerCertificate=true"
  dotnet run --project backend/src/APIArena.Api -c Release --no-build --no-launch-profile -- --migrate
  if ($LASTEXITCODE -ne 0) { throw 'Migration job failed' }
  # Second run must be idempotent.
  dotnet run --project backend/src/APIArena.Api -c Release --no-build --no-launch-profile -- --migrate
  if ($LASTEXITCODE -ne 0) { throw 'Repeated migration job failed' }
  sqlcmd -S localhost -E -C -b -d $smokeDb -Q "IF (SELECT COUNT(*) FROM dbo.__EFMigrationsHistory) <> 3 THROW 51000, 'Missing migrations', 1; IF (SELECT COUNT(*) FROM dbo.ScenarioTemplates WHERE OwnerId IS NULL AND Slug IN ('retry-503-v1','rate-limit-v1','timeout-v1')) <> 3 THROW 51001, 'Reference seed missing', 1; SELECT 'MIGRATION_JOB_PASSED' AS Result;"
  if ($LASTEXITCODE -ne 0) { throw 'Migration verification failed' }
} finally {
  $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment
  $env:ConnectionStrings__ApiArena = $previousConnection
  sqlcmd -S localhost -E -C -b -Q "IF DB_ID(N'$smokeDb') IS NOT NULL BEGIN ALTER DATABASE [$smokeDb] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$smokeDb]; END"
  Pop-Location
}
