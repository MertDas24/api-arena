# Local SQL Server only; creates uniquely named disposable databases, never restores over user data.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$suffix = [guid]::NewGuid().ToString('N')
$sourceDb = "ApiArena_Drill_$suffix"
$restoredDb = "ApiArena_Restore_$suffix"
foreach ($target in @($sourceDb, $restoredDb)) {
  if ($target -notmatch '^ApiArena_(Drill|Restore)_[a-f0-9]{32}$') { throw 'Unsafe database identifier' }
}
function Sql([string]$query) {
  & sqlcmd -S localhost -E -C -b -r 1 -Q $query
  if ($LASTEXITCODE -ne 0) { throw "SQL restore drill failed (exit $LASTEXITCODE)" }
}
$oldConnection = $env:APIARENA_DESIGN_CONNECTION
Push-Location $root
try {
  Sql "CREATE DATABASE [$sourceDb];"
  $env:APIARENA_DESIGN_CONNECTION = "Server=localhost;Database=$sourceDb;Integrated Security=true;Encrypt=true;TrustServerCertificate=true"
  dotnet tool restore
  if ($LASTEXITCODE -ne 0) { throw 'Tool restore failed' }
  dotnet ef database update -p backend/src/APIArena.Infrastructure -s backend/src/APIArena.Infrastructure
  if ($LASTEXITCODE -ne 0) { throw 'Fresh migration failed' }
  $query = @"
SET NOCOUNT ON;
USE [$sourceDb];
CREATE TABLE dbo.ReleaseDrillMarker (Id int PRIMARY KEY, Value nvarchar(100) NOT NULL);
INSERT dbo.ReleaseDrillMarker VALUES (1, N'synthetic-restore-evidence');
USE master;
DECLARE @backup nvarchar(4000) = RTRIM(CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultBackupPath')), N'\') + N'\$sourceDb.bak';
DECLARE @data nvarchar(4000) = RTRIM(CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultDataPath')), N'\') + N'\$restoredDb.mdf';
DECLARE @log nvarchar(4000) = RTRIM(CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultLogPath')), N'\') + N'\${restoredDb}_log.ldf';
BACKUP DATABASE [$sourceDb] TO DISK = @backup WITH COPY_ONLY, CHECKSUM;
RESTORE VERIFYONLY FROM DISK = @backup WITH CHECKSUM;
RESTORE DATABASE [$restoredDb] FROM DISK = @backup
WITH MOVE N'$sourceDb' TO @data, MOVE N'${sourceDb}_log' TO @log, CHECKSUM, RECOVERY;
DBCC CHECKDB (N'$restoredDb') WITH NO_INFOMSGS;
IF (SELECT COUNT(*) FROM [$restoredDb].dbo.__EFMigrationsHistory) <> (SELECT COUNT(*) FROM [$sourceDb].dbo.__EFMigrationsHistory)
  THROW 51000, 'Migration history mismatch after restore', 1;
IF NOT EXISTS (SELECT 1 FROM [$restoredDb].dbo.ReleaseDrillMarker WHERE Id=1 AND Value=N'synthetic-restore-evidence')
  THROW 51001, 'Data marker missing after restore', 1;
SELECT 'RESTORE_DRILL_PASSED' AS Result, COUNT(*) AS RestoredMigrations FROM [$restoredDb].dbo.__EFMigrationsHistory;
PRINT 'Synthetic backup retained on SQL server: $sourceDb.bak (no user production data).';
"@
  Sql $query
} finally {
  $env:APIARENA_DESIGN_CONNECTION = $oldConnection
  # Only the two verified identifiers generated above may be dropped.
  foreach ($target in @($restoredDb, $sourceDb)) {
    Sql "IF DB_ID(N'$target') IS NOT NULL BEGIN ALTER DATABASE [$target] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$target]; END"
  }
  Pop-Location
}
