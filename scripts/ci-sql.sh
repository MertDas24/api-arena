#!/usr/bin/env bash
# CI helper: starts an ephemeral SQL Server container with a password generated for this job only.
# The password is masked in GitHub logs and never written to the repository or artifacts.
set -euo pipefail
password="Aa1!$(openssl rand -base64 24 | tr -d '/+=' | head -c 28)"
if [ -n "${GITHUB_ACTIONS:-}" ]; then echo "::add-mask::$password"; fi
docker run -d --name apiarena-sql -e ACCEPT_EULA=Y -e MSSQL_PID=Developer -e "MSSQL_SA_PASSWORD=$password" \
  -p 127.0.0.1:1433:1433 mcr.microsoft.com/mssql/server:2022-latest >/dev/null

# Bounded readiness wait (max ~120 s).
for i in $(seq 1 60); do
  if docker exec apiarena-sql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$password" -Q "SELECT 1" -b -o /dev/null 2>/dev/null; then
    echo "SQL Server ready after $((i * 2)) s"
    break
  fi
  if [ "$i" -eq 60 ]; then echo "SQL Server did not become ready" >&2; docker logs apiarena-sql | tail -n 30; exit 1; fi
  sleep 2
done

base="Server=127.0.0.1,1433;User Id=sa;Password=$password;Encrypt=true;TrustServerCertificate=true"
if [ -n "${GITHUB_ENV:-}" ]; then
  {
    echo "APIARENA_TEST_SQL=$base"
    echo "E2E_SQL_CONNECTION=$base;Database=ApiArena_E2E"
  } >> "$GITHUB_ENV"
else
  echo "export APIARENA_TEST_SQL='$base'"
fi
