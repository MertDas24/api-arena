#!/usr/bin/env bash
# Creates/updates the git-ignored .env with locally generated development secrets (values never printed).
#   --docker    SQL Server SA password + matching connection string (Docker SQL path)
#   --webhooks  enable the v1 webhook module locally: random AES-256 key + dev-only localhost receiver
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
env_file="$root/.env"
[ -f "$env_file" ] || cp "$root/.env.example" "$env_file"
chmod 600 "$env_file"

get() { grep -E "^$1=" "$env_file" | head -n1 | cut -d= -f2- || true; }
set_key() { # name value [only-if-empty]
  local name="$1" value="$2" only="${3:-}"
  if grep -qE "^$name=" "$env_file"; then
    if [ -n "$only" ] && [ -n "$(get "$name")" ]; then return; fi
    local tmp; tmp="$(mktemp)"
    awk -v n="$name" -v v="$value" 'BEGIN{FS=OFS="="} $1==n{print n"="v; next} {print}' "$env_file" > "$tmp" && mv "$tmp" "$env_file"
  else
    echo "$name=$value" >> "$env_file"
  fi
}

for arg in "$@"; do
  case "$arg" in
    --docker)
      set_key MSSQL_SA_PASSWORD "Aa1!$(head -c 24 /dev/urandom | base64 | tr '+/=' 'xxx')" only
      pw="$(get MSSQL_SA_PASSWORD)"
      set_key ConnectionStrings__ApiArena "Server=localhost,1433;Database=ApiArena_Dev;User Id=sa;Password=$pw;Encrypt=true;TrustServerCertificate=true" only
      ;;
    --webhooks)
      set_key ApiArena__Webhooks__Enabled true
      set_key ApiArena__Webhooks__EncryptionKeys__Current "$(head -c 32 /dev/urandom | base64)" only
      set_key ApiArena__Webhooks__DevLocalReceiver__Enabled true
      ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done
chmod 600 "$env_file"
echo "Updated .env (git-ignored). Secret values were generated locally and not printed."
