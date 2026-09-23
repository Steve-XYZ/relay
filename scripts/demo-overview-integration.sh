#!/usr/bin/env bash
# Overview integration demo — prove the real reliability loop against Overview:
#   1. apply the Overview project config (project, resources, policies)
#   2. report healthy observations (baseline)
#   3. stop the Overview server → report the observed failure via the configured probe
#   4. Relay opens an incident and runs the authorized run_command remediation
#   5. remediation restores Overview → report healthy → Relay verifies and resolves
#
# Prereqs: scripts/dev-up.sh already ran (server on :18080), Overview deps installed.
# The Relay *server* must run on the same machine with OVERVIEW_ROOT pointing at the
# Overview checkout and npm on PATH, since run_command executes there:
#   OVERVIEW_ROOT=/path/to/overview RELAY_URL=http://127.0.0.1:18080 ./scripts/demo-overview-integration.sh
set -euo pipefail

ROOT="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
if [ -z "${OVERVIEW_ROOT:-}" ]; then
  echo "OVERVIEW_ROOT must be set to the Overview checkout (e.g. OVERVIEW_ROOT=/path/to/overview $0)" >&2
  exit 2
fi
if [ ! -d "$OVERVIEW_ROOT" ]; then
  echo "OVERVIEW_ROOT is not a directory: $OVERVIEW_ROOT" >&2
  exit 2
fi
RELAY_URL="${RELAY_URL:-http://127.0.0.1:18080}"
CLI="dotnet run --project $ROOT/src/Relay.Cli --no-build --"
CONFIG="$ROOT/integrations/overview.project.json"
PROJECT="overview"
OVERVIEW_PID=""
OVERVIEW_PID_FILE="/tmp/overview-demo.pid"
OVERVIEW_LOG="/tmp/overview-server.log"

dashboard_url() {
  python3 -c 'import json,sys; c=json.load(open(sys.argv[1])); print(next((r.get("url") or (r.get("attributes") or {}).get("url") for r in c.get("resources", []) if r.get("kind") == "service" and r.get("key") == "dashboard"), "http://127.0.0.1:4317/api/health"))' "$CONFIG"
}

start_overview() {
  (cd "$OVERVIEW_ROOT" && nohup npm run overview > "$OVERVIEW_LOG" 2>&1 & echo $! > "$OVERVIEW_PID_FILE")
  OVERVIEW_PID="$(cat "$OVERVIEW_PID_FILE")"
}

stop_overview_pid() {
  # Stop only the instance this demo started — never a broad pkill -f.
  if [ -n "${OVERVIEW_PID:-}" ]; then
    pkill -P "$OVERVIEW_PID" 2>/dev/null || true
    kill "$OVERVIEW_PID" 2>/dev/null || true
    for _ in $(seq 1 10); do
      kill -0 "$OVERVIEW_PID" 2>/dev/null || break
      sleep 1
    done
    OVERVIEW_PID=""
    rm -f "$OVERVIEW_PID_FILE"
  fi
}

cleanup() {
  stop_overview_pid
}
trap cleanup EXIT

await_healthy() {
  local url="$1" timeout="${2:-30}"
  for _ in $(seq 1 "$timeout"); do
    curl -sf "$url" >/dev/null 2>&1 && return 0
    sleep 1
  done
  echo "timed out waiting for healthy $url" >&2
  return 1
}

await_unhealthy() {
  local url="$1" timeout="${2:-15}"
  for _ in $(seq 1 "$timeout"); do
    curl -sf "$url" >/dev/null 2>&1 || return 0
    sleep 1
  done
  echo "expected $url to stop responding" >&2
  return 1
}

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

DASHBOARD_URL="$(dashboard_url)"

echo "== 1/6  applying Overview project config =="
$CLI apply "$CONFIG"
$CLI state "$PROJECT"

echo
echo "== 2/6  baseline: healthy =="
# Ensure the Overview server is up for the probe.
start_overview
await_healthy "$DASHBOARD_URL" 30
$CLI report "$CONFIG"
sleep 3
echo "  incidents: $(status)"
[[ "$(status)" == "none" ]] || { echo "unexpected incident at baseline"; exit 1; }

echo
echo "== 3/6  reality diverges: stop the Overview server =="
stop_overview_pid
trap - EXIT
# Confirm the outage is real before reporting it, then report through the
# configured probe so the observation reflects what was observed.
await_unhealthy "$DASHBOARD_URL" 15
$CLI report "$CONFIG"

echo
echo "== 4/6  Relay decides and acts =="
await_status acting 30
ID="$(incident_id)"
$CLI incident "$ID"

echo
echo "== 5/6  run_command remediation restores Overview =="
# The policy's run_command restarts Overview on the server; wait for the probe
# to succeed rather than restarting it manually (which would bypass remediation).
await_healthy "$DASHBOARD_URL" 90
$CLI incident "$ID" || true

echo
echo "== 6/6  verify with fresh healthy evidence =="
$CLI report "$CONFIG"
await_status resolved 60
$CLI incident "$ID"

echo
[[ "$(status)" == "resolved" ]]
