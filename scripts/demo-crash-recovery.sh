#!/usr/bin/env bash
# Crash-recovery demo:
#   1. submit a job
#   2. kill -9 the worker while the agent is mid-flight
#   3. Relay detects the lost lease: RUNNING -> INTERRUPTED -> RECOVERING -> QUEUED
#   4. a fresh worker picks it up, resumes from the last checkpoint, finishes
#
# Prereqs: scripts/dev-up.sh already ran (server on :18080, postgres up),
#          RELAY_URL points at the server, and $ROOT/src builds exist.
set -euo pipefail

ROOT="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
RELAY_URL="${RELAY_URL:-http://127.0.0.1:18080}"
CLI="dotnet run --project $ROOT/src/Relay.Cli --no-build --"
REPO="${2:-/tmp/opencode/fixture}"

WORKER_PID="$(pgrep -f 'dotnet exec Relay.Worker.dll' | head -1)"
if [[ -z "$WORKER_PID" ]]; then echo "worker not running; start via scripts/dev-up.sh"; exit 1; fi

echo "== submitting job =="
OUT=$($CLI run "Fix issue BOS-123 style bug in this repo" --repo "$REPO" --budget tokens=120000,cost=2.00,runtime=30m --no-watch)
SHORT=$(echo "$OUT" | grep -oE '[0-9A-Z]{4}' | head -1)
echo "job: $SHORT"
sleep 4   # let the agent get mid-flight

echo
echo "== kill -9 worker (pid $WORKER_PID) =="
kill -9 "$WORKER_PID"

STATUS=""
for i in $(seq 1 40); do
  STATUS=$(curl -s "$RELAY_URL/api/jobs/$SHORT" | python3 -c 'import json,sys; print(json.load(sys.stdin)["status"])')
  printf "\r[%02ds] %s  " "$i" "$STATUS"
  sleep 1
done
echo

echo "== starting a fresh worker =="
(
  cd "$ROOT/src/Relay.Worker/bin/Debug/net10.0"
  RELAY_SERVER_URL="$RELAY_URL" RELAY_WORKER_NAME=recovered-worker \
  SANDBOX_MODE="${SANDBOX_MODE:-process}" WORKSPACE_ROOT=/tmp/relay-workspaces \
  nohup dotnet exec Relay.Worker.dll > /tmp/relay-logs/worker.log 2>&1 &
)
sleep 2

$CLI logs "$SHORT" -f || true

FINAL=$(curl -s "$RELAY_URL/api/jobs/$SHORT")
echo "$FINAL" | python3 -c '
import json,sys
j=json.load(sys.stdin)
r=j.get("result") or {}
u=j["usage"]
print()
print("== RESULT ==")
print("status     :", j["status"])
print("attempts   :", j["attempt"])
print("branch     :", r.get("branch"))
print("changed    :", r.get("changed_files"))
print("tests      :", r.get("tests_passed"))
print("tokens     : {:,} (cost ${:0.2f})".format(u["tokens_in"]+u["tokens_out"], float(u["cost_usd"])))
'
[[ "$(echo "$FINAL" | python3 -c 'import json,sys; print(json.load(sys.stdin)["status"])')" == "completed" ]]
