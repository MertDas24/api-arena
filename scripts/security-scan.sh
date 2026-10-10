#!/usr/bin/env bash
# Dependency + secret scans used by CI. Fails on: vulnerable .NET packages, high/critical production npm advisories,
# or any gitleaks finding not covered by the reviewed allow-list in .gitleaks.toml.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

echo "==> .NET vulnerable packages"
out="$(dotnet list backend/APIArena.sln package --vulnerable --include-transitive 2>&1)"
echo "$out"
if echo "$out" | grep -q "has the following vulnerable packages"; then echo "Vulnerable .NET packages found" >&2; exit 1; fi

echo "==> npm audit (production dependencies, high+)"
(cd frontend && npm audit --omit=dev --audit-level=high)

echo "==> npm audit (all, informational — dev-only findings are triaged in docs/SECURITY_REVIEW.md)"
(cd frontend && npm audit --audit-level=critical) || true

echo "==> gitleaks"
version="8.30.1"
tmp="$(mktemp -d)"
curl -sSfL -o "$tmp/gitleaks.tar.gz" "https://github.com/gitleaks/gitleaks/releases/download/v${version}/gitleaks_${version}_linux_x64.tar.gz"
curl -sSfL -o "$tmp/checksums.txt" "https://github.com/gitleaks/gitleaks/releases/download/v${version}/gitleaks_${version}_checksums.txt"
(cd "$tmp" && grep "gitleaks_${version}_linux_x64.tar.gz" checksums.txt | sed "s/gitleaks_${version}_linux_x64.tar.gz/gitleaks.tar.gz/" | sha256sum -c -)
tar -xzf "$tmp/gitleaks.tar.gz" -C "$tmp" gitleaks
if git rev-parse --git-dir >/dev/null 2>&1 && [ "$(git rev-list --count HEAD 2>/dev/null || echo 0)" -gt 0 ]; then
  "$tmp/gitleaks" git . --config .gitleaks.toml --redact --no-banner
else
  "$tmp/gitleaks" dir . --config .gitleaks.toml --redact --no-banner --max-target-megabytes 5
fi
echo "Security scans passed."
