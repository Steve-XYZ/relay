# Relay

**Relay keeps AI coding agents running when your laptop, network, or SSH session doesn't.**

Submit a task from your phone → Relay creates a git worktree and an isolated
Docker sandbox → an agent works on it → you lose internet, the worker crashes,
you `kill -9` it on purpose — the job survives. A healthy worker resumes from
the last checkpoint and you get a diff, test results, and a branch (or PR).

```
$ relay run "Fix issue BOS-123 style bug in this repo"

+ job_created        (K2MS)
+ workspace_created  (worktree:/tmp/relay-workspaces/K2MS/tree)
+ agent_started
* [relay:progress] tool_call 1 … 8
$ [relay:usage] tokens_in=21000 tokens_out=9000 cost_usd=0.31
[running] -> [validating]
+ tests_passed  (tests: 42 passed, 0 failed)
+ commit_created  (relay/K2MS)
+ diff_ready  (1 file(s))
+ job_completed

== COMPLETED ==
branch     : relay/K2MS
changed    : src/feature.txt
tokens     : 30,000 (cost $0.31)
```

## Why

Remote agent execution is fragile the moment the machine controlling the work
is not the machine doing the work: SSH drops, laptops sleep, workers get
rescheduled. Relay treats that as the normal case. Jobs are durable rows in
Postgres, agents run in sandboxes owned by ephemeral workers, and every state
change is a persisted event you can replay.

What is actually engineered here — not calling an LLM API:

* **Explicit state machine** with validated, persisted transitions
  (`QUEUED → PREPARING → RUNNING → VALIDATING → COMPLETED`, plus
  `INTERRUPTED / RECOVERING` recovery paths).
* **Exactly-once-ish execution**: lease tokens + guarded updates + zombie
  stand-down. A killed worker cannot double-apply or corrupt state.
* **Crash recovery**: leases expire server-side; jobs requeue from their last
  checkpoint automatically.
* **Budget-aware execution**: token/cost/runtime limits enforced mid-run.
* **Isolation**: agents run in resource-limited Docker containers.
* **Observability**: durable event log replayed over SSE; OpenTelemetry wired.

## Quick start (Docker Compose)

```bash
docker compose up --build -d
# dashboard  → http://localhost:3000
# API        → http://localhost:8080
export RELAY_URL=http://localhost:8080
dotnet run --project src/Relay.Cli -- run "Fix issue BOS-123 style bug in this repo" \
    --repo https://github.com/you/some-repo
```

## Local development (process sandbox)

```bash
scripts/dev-up.sh                 # postgres (docker) + server :18080 + one worker
export RELAY_URL=http://127.0.0.1:18080

dotnet run --project src/Relay.Cli -- run "Fix issue BOS-123" --repo /path/to/repo
dotnet run --project src/Relay.Cli -- list
dotnet run --project src/Relay.Cli -- logs <short-id> -f
dotnet run --project src/Relay.Cli -- cancel <short-id>
```

`SANDBOX_MODE=process` runs agent commands directly on the host — fast for dev,
no isolation. Set `SANDBOX_MODE=docker` to use the `relay-sandbox` image.

## The crash-recovery demo

```bash
scripts/demo-crash-recovery.sh
```

The script submits a job, waits until the agent is mid-flight, then `kill -9`s
the worker. You watch the timeline go `running → interrupted → recovering →
queued`, a fresh worker claims it with `resume_from_checkpoint = N`, and the
job completes.

## Budget-aware execution

```bash
relay run "Refactor auth module" --repo . \
    --budget tokens=120000,cost=2.00,runtime=30m
```

Relay meters tokens, cost and tool calls as the agent reports them (adapters
emit `[relay:usage]` markers; anything unreported still counts against wall
clock). Crossing a limit aborts the run with the reason attached to the job.

## Architecture

```
             Web UI (Next.js)
                    │ REST + SSE
                    ▼
              Relay Server ──── control plane: state machine, leases, recovery
              │           │
       PostgreSQL       Event stream
                    ▲
                    │ claim / heartbeat / events (lease-token authorized)
                Workers ──▶ Docker sandbox ──▶ coding agent
                    │              │
               git worktree    test command
```

Details in [docs/architecture.md](docs/architecture.md); the full transition
table and recovery rules in [docs/state-machine.md](docs/state-machine.md).

## Repo layout

```
src/
  Relay.Core      domain: state machine, budgets, contracts (no dependencies)
  Relay.Server    control plane: REST + SSE APIs, Postgres store, sweeper
  Relay.Worker    executor: claims jobs, drives sandboxes, heartbeats, checkpoints
  Relay.Cli       `relay` command-line client
tests/
  Relay.Core.Tests         state machine transitions, budget metering
  Relay.Server.Tests       lease/recovery/cancel semantics (same contract as Postgres store)
web/              Next.js dashboard: live timelines, budget meters, SSE log
docker/           Dockerfiles: server, worker, sandbox image, web
scripts/          dev-up.sh, demo-crash-recovery.sh
```

## Agents

The default adapter (`--agent mock`) is a deterministic scripted agent so demos
and CI need no API keys. Any shell command works as an adapter via
`--agent "<command> {PROMPT}"`; adapters communicate through line markers:

```
[relay:progress] tool_call 37
[relay:usage] tokens_in=21000 tokens_out=9000 cost_usd=0.31
```

and receive `PROMPT` and `RESUME_FROM` as environment variables. A Claude Code
adapter would invoke `claude --resume/-p` inside the same sandbox.

## What is deliberately not here yet

* Real LLM-agent adapters beyond the shell template (mock only today).
* PR creation is wired but off by default (`RELAY_CREATE_PR=true` + `gh`).
* Multi-node workers share nothing, but there is no per-worker capability
  routing yet.
* Authentication between CLI/web and the server.
