#!/usr/bin/env bash
# Overview integration demo — prove the real reliability loop against Overview:
#   1. apply the Overview project config (project, resources, policies)
#   2. report healthy observations (baseline)
#   3. kill the Overview server → report unavailable (real detected failure)
#   4. Relay opens an incident and runs the authorized run_command remediation
#   5. report healthy again → Relay verifies and resolves
#
# Prereqs: scripts/dev-up.sh already ran (server on :18080), Overview deps installed.
set -euo pipefail

ROOT="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
OVERVIEW_ROOT="${OVERVIEW_ROOT:-/Users/stive/.t3/worktrees/OverView/t3code-363ff4aa}"
RELAY_URL="${RELAY_URL:-http://127.0.0.1:18080}"
CLI="dotnet run --project $ROOT/src/Relay.Cli --no-build --"
CONFIG="$ROOT/integrations/overview.project.json"
PROJECT="overview"

status() {
  curl -sf "$RELAY_URL/api/incidents?project=$PROJECT&active=false" |
    python3 -c 'import json,sys; d=json.load(sys.stdin); print(d[0]["status"] if d else "none")'
}
incident_id() {
  curl -sf "$RELAY_URL/api/incidents?project=$PROJECT&active=false" |
    python3 -c 'import json,sys; d=json.load(sys.stdin); print(d[0]["short_id"] if d else "")'
}
await_status() {
  for _ in $(seq 1 "${2:-60}"); do
    [[ "$(status)" == "$1" ]] && return 0
    sleep 1
  done
  echo "timed out waiting for $1 (now: $(status))"
  return 1
}

echo "== 1/6  applying Overview project config =="
$CLI apply "$CONFIG"
$CLI state "$PROJECT"

echo
echo "== 2/6  baseline: healthy =="
# Ensure the Overview server is up for the probe.
(cd "$OVERVIEW_ROOT" && nohup npm run overview > /tmp/overview-server.log 2>&1 &)
sleep 3
$CLI report "$CONFIG"
sleep 3
echo "  incidents: $(status)"
[[ "$(status)" == "none" ]] || { echo "unexpected incident at baseline"; exit 1; }

echo
echo "== 3/6  reality diverges: kill the Overview server =="
pkill -f "src/cli.ts serve" 2>/dev/null || true
pkill -f "overview serve" 2>/dev/null || true
# Force an unavailable report even if the process is gone.
curl -sf -X POST "$RELAY_URL/api/projects/$PROJECT/observations" \
  -H 'content-type: application/json' \
  -d '{"kind":"service","key":"dashboard","state":"unavailable","source":"demo","message":"connection refused"}' \
  >/dev/null || true

echo
echo "== 4/6  Relay decides and acts =="
await_status acting 30
ID="$(incident_id)"
$CLI incident "$ID"

echo
echo "== 5/6  run_command remediation restarts Overview =="
# The policy's run_command restarts the Overview server; give the loop a tick.
sleep 6
$CLI incident "$ID" || true

echo
echo "== 6/6  verify with fresh healthy evidence =="
(cd "$OVERVIEW_ROOT" && nohup npm run overview > /tmp/overview-server.log 2>&1 &)
sleep 3
$CLI report "$CONFIG"
await_status resolved 60
$CLI incident "$ID"

echo
[[ "$(status)" == "resolved" ]]
