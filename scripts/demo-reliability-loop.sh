#!/usr/bin/env bash
# Reliability-loop demo — the whole product in one script:
#   1. declare a project and its resources          (what exists)
#   2. state a policy                               (what should be happening)
#   3. report the service healthy                   (what is actually happening)
#   4. report it unavailable                        (reality diverges)
#   5. Relay opens an incident, authorizes an agent task, and runs it as a job
#   6. the job succeeds -- and the incident does NOT resolve, because nothing has
#      reported on the resource since the fix ran
#   7. the health checker reports back healthy      (fresh evidence)
#   8. Relay verifies and resolves the incident
#
# Prereqs: a server on $RELAY_URL and at least one worker. scripts/dev-up.sh does both.
set -euo pipefail

ROOT="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
RELAY_URL="${RELAY_URL:-http://127.0.0.1:18080}"
CLI="dotnet run --project $ROOT/src/Relay.Cli --no-build --"
PROJECT="${PROJECT:-demo}"
REPO="${REPO:-/tmp/relay-demo-repo}"

api() { curl -sf -X POST "$RELAY_URL$1" -H 'content-type: application/json' -d "$2" > /dev/null; }
status() {
  curl -sf "$RELAY_URL/api/incidents?project=$PROJECT&active=false" |
    python3 -c 'import json,sys; d=json.load(sys.stdin); print(d[0]["status"] if d else "none")'
}
incident_id() {
  curl -sf "$RELAY_URL/api/incidents?project=$PROJECT&active=false" |
    python3 -c 'import json,sys; d=json.load(sys.stdin); print(d[0]["short_id"] if d else "")'
}
await() { # await <expected-status> <seconds>
  for _ in $(seq 1 "$2"); do
    [[ "$(status)" == "$1" ]] && return 0
    printf '\r  waiting for %-18s (now: %-18s)' "$1" "$(status)"
    sleep 1
  done
  printf '\n  timed out waiting for %s\n' "$1"
  return 1
}

if [[ ! -d "$REPO/.git" ]]; then
  echo "== creating a repository for the agent to work in: $REPO =="
  mkdir -p "$REPO"
  git -C "$REPO" init -q -b main
  echo healthy > "$REPO/status.txt"
  git -C "$REPO" add -A
  git -C "$REPO" -c user.email=demo@local -c user.name=demo commit -qm "init"
fi

echo "== 1/8  declaring what exists =="
api /api/projects "{\"slug\":\"$PROJECT\",\"name\":\"Reliability demo\"}"
api "/api/projects/$PROJECT/resources" "{\"kind\":\"repository\",\"key\":\"app\",\"attributes\":{\"repo_url\":\"$REPO\"}}"
api "/api/projects/$PROJECT/resources" '{"kind":"service","key":"api","name":"API service"}'

echo "== 2/8  stating what should be happening =="
api "/api/projects/$PROJECT/policies" '{
  "name": "api must be healthy",
  "target": {"kind": "service", "key": "api"},
  "expectation": {"kind": "healthy"},
  "severity": "critical",
  "remediation": {
    "action": "run_agent_task",
    "params": {"prompt": "The API health check is failing. Diagnose and fix."},
    "max_attempts": 2,
    "cooldown_seconds": 0,
    "verify_within_seconds": 300
  }
}'
$CLI state "$PROJECT"

echo
echo "== 3/8  reporting reality: healthy =="
$CLI observe "$PROJECT" service/api healthy --source health-checker
sleep 3
echo "  incidents: $(status)"

echo
echo "== 4/8  reality diverges =="
$CLI observe "$PROJECT" service/api unavailable --source health-checker --message "connect: connection refused"

echo
echo "== 5/8  Relay decides and acts =="
await acting 30 && echo
ID="$(incident_id)"
$CLI incident "$ID"

echo
echo "== 6/8  the job finishes -- and the incident stays open =="
await verifying 120 && echo
echo "  the agent task succeeded, but nothing has reported on service/api since it ran."
echo "  a job exiting zero is a claim about the job, not a fact about the resource."
sleep 5
echo "  incident is still: $(status)"

echo
echo "== 7/8  the health checker reports back =="
$CLI observe "$PROJECT" service/api healthy --source health-checker

echo
echo "== 8/8  Relay verifies its own intervention =="
await resolved 60 && echo
$CLI incident "$ID"

echo
[[ "$(status)" == "resolved" ]]
