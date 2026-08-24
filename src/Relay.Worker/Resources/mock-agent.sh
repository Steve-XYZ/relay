#!/usr/bin/env bash
# Deterministic stand-in for a coding agent, used by demos and integration tests.
# Emits Relay marker lines so the worker can meter progress, checkpoints and usage:
#   [relay:progress] tool_call <n>
#   [relay:usage] tokens_in=<n> tokens_out=<n> cost_usd=<f>
# Env:
#   PROMPT       the task description
#   RESUME_FROM  last persisted checkpoint seq; earlier steps are skipped on recovery
set -u

RESUME_FROM="${RESUME_FROM:-0}"
STEPS=8

echo "[relay] mock agent starting: $PROMPT"
if [ "${RESUME_FROM}" -gt 0 ]; then
  echo "[relay] resuming from checkpoint ${RESUME_FROM}, skipping earlier steps"
fi

i=1
while [ "$i" -le "$STEPS" ]; do
  if [ "$i" -le "${RESUME_FROM}" ]; then
    i=$((i + 1))
    continue
  fi
  echo "[relay:progress] tool_call ${i}"
  echo "[relay] step ${i}/${STEPS}: scanning repository context"
  sleep 1
  i=$((i + 1))
done

TARGET="src/feature.txt"
mkdir -p "$(dirname "${TARGET}")"
cat >> "${TARGET}" <<EOF
BOS-123 fixed by Relay mock agent
prompt: ${PROMPT}
EOF

echo "[relay] applying edit to ${TARGET}"
echo "[relay:usage] tokens_in=21000 tokens_out=9000 cost_usd=0.31"
echo "[relay] done"
exit 0
