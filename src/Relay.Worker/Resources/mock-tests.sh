#!/usr/bin/env bash
# Default validation step for mock-agent jobs: the change must exist.
set -u

if grep -rq "BOS-123 fixed" src/ 2>/dev/null; then
  echo "tests: 42 passed, 0 failed"
  exit 0
fi

echo "tests: expected fix for BOS-123 not found in src/"
exit 1
