#!/bin/sh
set -eu
if [ -n "${APIARENA_WORKER_HEARTBEAT:-}" ]; then
  test -n "$(find "$APIARENA_WORKER_HEARTBEAT" -mmin -1 -print 2>/dev/null)"
else
  curl --fail --silent --max-time 4 http://127.0.0.1:8080/health/live >/dev/null
fi
