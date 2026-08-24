# State machine

Every job moves through an explicit, persisted state machine. The server is the
single writer: workers propose transitions through leased internal API calls and
the sweeper advances recovery chains. Each arrow below is a durable `job_events`
row of kind `state`, visible in the CLI (`relay logs <id> -f`), the SSE stream,
and the web timeline.

```
QUEUED ──claim──▶ PREPARING ──▶ RUNNING ──▶ VALIDATING ──▶ COMPLETED

   PREPARING/RUNNING/VALIDATING
        │ lease expires (worker crashed, network died, kill -9 …)
        ▼
   INTERRUPTED ──▶ RECOVERING ──▶ QUEUED          (requeue with resume_from_checkpoint)
                       │
                       ├──▶ CANCELLED             (cancel was requested)
                       └──▶ FAILED                (attempt ≥ max_attempts)

any non-terminal state ──▶ CANCELLED | FAILED     (budget exceeded, tests failed, agent crash…)
```

## Rules

| Rule | Owner |
| --- | --- |
| Only valid transitions are persisted (validated against this table) | `Relay.Core.StateMachine` |
| Every transition appends a durable state event and fans it out over SSE | `JobService` |
| Workers must present the live lease token for every mutation | `IJobStore.TransitionWithLeaseAsync`, heartbeat guard |
| A worker whose heartbeat is rejected (409) stops immediately without writing anything — it is a zombie by definition | `WorkerLoop.HeartbeatLoopAsync` |
| Recovery requeues with `resume_from_checkpoint` = last persisted checkpoint seq | `SweepExpiredLeasesAsync` |
| After `max_attempts` recoveries the job is FAILED with `recovery attempts exhausted` | sweep chain |
| Cancel requested on an idle job cancels in place; on a running job it is a sticky flag honored at the next heartbeat/checkpoint | `RequestCancelAsync` |

## Timing defaults

| Knob | Default |
| --- | --- |
| Lease TTL | 15 s |
| Worker heartbeat | 5 s |
| Sweeper interval | 5 s |
| Max attempts | 3 |

All are configuration; nothing depends on wall-clock skew between machines
beyond normal clock drift because expiry is evaluated server-side (`now()`).
