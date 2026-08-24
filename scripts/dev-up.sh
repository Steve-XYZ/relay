#!/usr/bin/env bash
# Local development helper: starts Postgres (docker), the Relay server and one worker
# with a process sandbox. Usage: scripts/dev-up.sh [repo-root]
set -euo pipefail

ROOT="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
SERVER_PORT="${SERVER_PORT:-18080}"
PG_CONTAINER="${PG_CONTAINER:-relay-pg}"
PG_PORT="${PG_PORT:-15432}"
export RELAY_URL="http://127.0.0.1:${SERVER_PORT}"

# 1. Postgres
if ! docker ps --format '{{.Names}}' | grep -qx "$PG_CONTAINER"; then
  docker rm -f "$PG_CONTAINER" >/dev/null 2>&1 || true
  docker run -d --name "$PG_CONTAINER" \
    -e POSTGRES_USER=relay -e POSTGRES_PASSWORD=relay -e POSTGRES_DB=relay \
    -p "${PG_PORT}:5432" postgres:16-alpine >/dev/null
  sleep 4
fi

echo "building..."
dotnet build "$ROOT" 2>&1 | grep -E " error " && exit 1 || true

pkill -f "dotnet exec Relay.Server.dll" 2>/dev/null || true
pkill -f "dotnet exec Relay.Worker.dll" 2>/dev/null || true
sleep 1

mkdir -p /tmp/relay-logs

# 2. Server
(
  cd "$ROOT/src/Relay.Server/bin/Debug/net10.0"
  ConnectionStrings__relay="Host=localhost;Port=${PG_PORT};Database=relay;Username=relay;Password=relay" \
  ASPNETCORE_URLS="http://127.0.0.1:${SERVER_PORT}" \
  nohup dotnet exec Relay.Server.dll > /tmp/relay-logs/server.log 2>&1 &
)
# 3. Worker (process sandbox for local dev; build relay-sandbox image + SANDBOX_MODE=docker for containers)
(
  cd "$ROOT/src/Relay.Worker/bin/Debug/net10.0"
  RELAY_SERVER_URL="$RELAY_URL" RELAY_WORKER_NAME=dev-worker \
  SANDBOX_MODE="${SANDBOX_MODE:-process}" WORKSPACE_ROOT=/tmp/relay-workspaces \
  nohup dotnet exec Relay.Worker.dll > /tmp/relay-logs/worker.log 2>&1 &
)

sleep 3
curl -fsS "$RELAY_URL/healthz" >/dev/null && echo "server: $RELAY_URL (healthy)"
echo "worker: started ($(cat /tmp/relay-logs/worker.log | head -1))"
echo "logs:   tail -f /tmp/relay-logs/{server,worker}.log"
