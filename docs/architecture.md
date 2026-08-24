# Architecture

```
             Web / Mobile UI (Next.js)
                    │ REST + SSE
                    ▼
              Relay Server  ──── control plane: state machine, leases, recovery
              │           │
       PostgreSQL        Event stream
   (jobs, events,      (SSE hub, replay
    checkpoints,        via Last-Event-ID)
    workers, queue)
                    ▲
                    │ HTTP claim / heartbeat / events
                    │ (lease-token authorized)
                Workers ──▶ Docker sandbox ──▶ coding agent
                    │              │
                 git worktree   test command
```

## Control plane vs data plane

**The server is the single writer over Postgres.** Workers never touch the
database. Every mutation a worker performs — claim, transition, checkpoint,
heartbeat, complete, fail — is an HTTP call authorized by a lease token issued
at claim time. Consequences:

* The server can validate every transition against the state machine in one
  place (`Relay.Core.StateMachine`, enforced again inside the store).
* A worker that dies mid-flight cannot corrupt durable state; at worst it goes
  silent and its lease expires.
* Scaling workers out requires no database capacity planning — they are stateless.

## Postgres as the queue

Job claiming uses `SELECT … FOR UPDATE SKIP LOCKED`:

```sql
WITH picked AS (
    SELECT id FROM jobs
    WHERE status IN ('queued', 'recovering') AND cancel_requested = FALSE
    ORDER BY created_at
    FOR UPDATE SKIP LOCKED
    LIMIT 1
)
UPDATE jobs … RETURNING …
```

No Redis, no Kafka. At current scale (single-digit jobs/minute) the lock-based
queue is provably correct and operationally free. When throughput justifies it,
the claim endpoint is the seam to swap in a real broker behind.

Exactly-once-ish semantics come from three layers:

1. **Lease tokens**: every worker mutation carries the token from its claim;
   once the lease expires or is reassigned, mutations are rejected (409).
2. **Guarded updates**: transitions are `UPDATE … WHERE id = @id AND status =
   @current` inside a transaction with a row lock, so concurrent owners cannot
   double-apply.
3. **Zombie stand-down**: a heartbeat rejection tells the worker it lost
   ownership; it aborts without writing anything.

## Recovery

The sweeper (`RecoverySweeper`) runs every 5 s:

```
preparing|running|validating AND lease_expires_at <= now()
  → INTERRUPTED ("worker lost: lease expired")
  → RECOVERING
  → QUEUED (resume_from_checkpoint = last checkpoint seq)
     or FAILED (attempt ≥ max_attempts) or CANCELLED (if requested)
```

Workers persist checkpoints (e.g., `tool_call_N`) through the server as the
agent progresses; on re-claim the adapter receives `RESUME_FROM` so it can skip
already-completed steps. How much an agent can truly resume depends on the
adapter: the bundled mock agent replays deterministically; a Claude Code-style
CLI would resume from its transcript.

## Sandbox

Workers run agent commands inside a Docker container built from
`docker/sandbox.Dockerfile`:

* worktree bind-mounted read-write at `/workspace`
* `--network none` by default (mock agents need no network; flip per job for
  networked adapters)
* `--memory 2g --cpus 2 --pids-limit 512`
* non-root user mapped to the invoking host user

A `process` mode runs commands directly on the host for development/CI —
documented trade-off: no isolation, fast startup.

## Observability

OpenTelemetry traces/meters are wired in both processes
(`ActivitySource("Relay.Server")`, `"Relay.Worker"`, counters
`relay.jobs.created/finished`). Set `Otel:OtlpEndpoint`
(`OTEL_EXPORTER_OTLP_ENDPOINT`) plus a Jaeger/OTLP collector to export; with no
endpoint configured everything stays on console logging.

## Data model (Postgres)

| Table | Purpose |
| --- | --- |
| `jobs` | one row per task: prompt, repo, budget, status, usage counters, lease, result |
| `job_events` | durable append-only log; `(job_id, seq)` unique; powers SSE replay and timelines |
| `checkpoints` | resume points (`tool_call_N`, `workspace_ready`, …) |
| `workers` | registry + heartbeats |

Schema is created idempotently at startup from `src/Relay.Server/Data/schema.sql`.
