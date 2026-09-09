# Architecture

```
                    Web / CLI clients
                           │ REST + SSE
                           ▼
   ┌───────────────────── Relay Server ──────────────────────┐
   │                                                         │
   │  CONTROL PLANE            ReliabilityLoop               │
   │  projects, resources,   observe → compare → act →       │
   │  observations, policies,  verify → remember             │
   │  incidents, actions                                     │
   │                                                         │
   │  EXECUTION PLANE          RecoverySweeper               │
   │  jobs, leases,          expired leases → interrupted    │
   │  checkpoints, events      → recovering → requeued       │
   └────────┬────────────────────────────────┬───────────────┘
            │                                │
      PostgreSQL                       Event streams
   (single source of truth)      (SSE hub, replay via Last-Event-ID)
                                             ▲
                                             │ claim / heartbeat / events
                                             │ (lease-token authorized)
                                         Workers ──▶ sandbox ──▶ coding agent
                                             │            │
                                       git worktree   test command
```

Relay is two planes in one server process, sharing one database and one discipline.

The **control plane** knows what should be happening and decides what to do about the gap.
The **execution plane** carries out one kind of intervention — running a coding agent in a
sandbox — durably enough to survive the machine it runs on. The control plane is the product;
the execution plane is machinery underneath it.

See [domain-model.md](domain-model.md) for the concepts and
[reliability-loop.md](reliability-loop.md) for the loop.

## The server is the single writer

Neither workers nor reporters touch the database. Every mutation is an HTTP call the server
validates:

* **Workers** authorize every write — claim, transition, checkpoint, heartbeat, complete,
  fail, and even append-only event writes — with the lease token issued at claim time. A
  worker that dies cannot corrupt state; at worst it goes silent and its lease expires. A
  worker that is merely slow discovers it lost the lease at its next heartbeat and stands
  down.
* **Reporters** push observations. The server stamps `received_at` from its own clock and
  clamps `observed_at` to it, so evidence can be late but never forged forward.
* **Humans** approve or reject proposed actions. That decision is recorded on the action, not
  inferred from a side effect.

Consequences: every transition is validated in one place; scaling workers needs no database
capacity planning; and the audit log is complete by construction rather than by convention.

## Concurrency: compare-and-swap everywhere

The same discipline holds at both layers, and it is what allows more than one server replica:

1. **Guarded updates.** Every state change is `UPDATE … WHERE id = @id AND status = @from`
   inside a transaction with a row lock. A caller whose view went stale writes nothing.
2. **Lease tokens.** Worker mutations carry the token from their claim; once the lease
   expires or is reassigned, they are rejected with 409.
3. **Database-enforced uniqueness.** One active incident per `(policy, resource)` is a
   partial unique index, not a read-then-write.
4. **Zombie stand-down.** A rejected heartbeat tells a worker it lost ownership; it aborts
   without writing.

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

No Redis, no Kafka. At current scale the lock-based queue is provably correct and
operationally free. When throughput justifies it, the claim endpoint is the seam.

## Recovery

`RecoverySweeper` runs every 5 s:

```
preparing|running|validating AND lease_expires_at <= now()
  → INTERRUPTED ("worker lost: lease expired")
  → RECOVERING
  → QUEUED (resume_from_checkpoint = last checkpoint seq, lease token cleared)
     or FAILED (attempt ≥ max_attempts) or CANCELLED (if requested)
```

Because jobs are guaranteed to reach a terminal state, an incident can never be stuck in
`acting` waiting on a job that will never answer. Recovery of the execution plane is what
makes the control plane's `acting → verifying` edge safe to depend on.

Checkpoints (`tool_call_N`, `workspace_ready`) are persisted through the server as the agent
progresses; on re-claim the adapter receives `RESUME_FROM`. How much an agent can truly resume
depends on the adapter — the bundled mock replays deterministically; a Claude Code-style CLI
would resume from its transcript.

## Sandbox

Agent commands run inside a Docker container built from `docker/sandbox.Dockerfile`:
worktree bind-mounted at `/workspace`, `--network none` by default,
`--memory 2g --cpus 2 --pids-limit 512`, non-root user. A `process` mode runs commands
directly on the host for development and CI — documented trade-off: no isolation, fast
startup.

## Observability

OpenTelemetry traces and meters in both processes. Control-plane counters
(`relay.incidents.*`, `relay.actions.dispatched`, `relay.verifications`) sit alongside the
execution counters (`relay.jobs.*`). Set `Otel:OtlpEndpoint`
(`OTEL_EXPORTER_OTLP_ENDPOINT`) to export; with no endpoint configured everything stays on
console logging.

## Data model (Postgres)

Control plane:

| Table | Purpose |
| --- | --- |
| `projects` | unit of ownership |
| `resources` | declared things with an operational identity; derived health |
| `observations` | append-only evidence; never updated or deleted by Relay |
| `policies` | desired state (`expectation`) plus authority (`remediation`) |
| `incidents` | divergence; one active per `(policy, resource)` |
| `actions` | auditable interventions, with approver and outcome |
| `incident_events` | append-only audit log; `(incident_id, seq)` unique; powers SSE replay |

Execution plane:

| Table | Purpose |
| --- | --- |
| `jobs` | one row per unit of execution: prompt, repo, budget, status, usage, lease, result |
| `job_events` | append-only log; `(job_id, seq)` unique |
| `checkpoints` | resume points |
| `workers` | registry + heartbeats |

Schema is created idempotently at startup from `src/Relay.Server/Data/schema.sql`, including
`ALTER TABLE … ADD COLUMN IF NOT EXISTS` for columns added after the first release.

## Store contracts

Both planes have an interface with two implementations: `PostgresJobStore` /
`InMemoryJobStore` and `PostgresControlPlaneStore` / `InMemoryControlPlaneStore`. The
in-memory stores hold **exactly** the same invariants — a single lock stands in for row
locks, and the state machines are re-checked identically — which is what makes them a
faithful stand-in for tests and for `Storage:Mode=memory` local runs.

Where the two ever drift, that is a bug in its own right: an invariant that holds only in
tests is not an invariant.
