# State machines

Relay has three, layered like the domain: incidents decide *what to do*, actions record
*doing it*, jobs *do it*. Each is an explicit transition table in `Relay.Core`, validated on
every write and re-checked by the store under a row lock, so no component can move a record
by writing a status directly.

| Machine | Type | Governs |
| --- | --- | --- |
| Incident | `IncidentStateMachine` | how Relay responds to divergence |
| Action | `ActionStateMachine` | one auditable intervention |
| Job | `JobStateMachine` | one unit of sandboxed execution |

---

## Incident

```
                    ┌──────────────────────────────┐
                    ▼                              │ retry within budget
   OPEN ──▶ AWAITING_APPROVAL ──▶ ACTING ──▶ VERIFYING ──▶ RESOLVED
     │                    ▲          │              │
     └────────────────────┘          └──────────────┴──▶ ESCALATED ──▶ OPEN
```

| From | To |
| --- | --- |
| `open` | `awaiting_approval`, `acting`, `resolved`, `escalated` |
| `awaiting_approval` | `acting`, `resolved`, `escalated` |
| `acting` | `verifying`, `open`, `escalated` |
| `verifying` | `resolved`, `open`, `escalated` |
| `escalated` | `open`, `resolved` |
| `resolved` | — |

Four deliberate absences:

* **No `acting` → `resolved`.** An action in flight must land first. Resolving around it
  would leave an intervention running against a closed incident, which no audit log could
  explain afterwards.
* **No `verifying` → `acting` and no `escalated` → `acting`.** Retries and human re-arms
  route through `open`, so approval, cool-down and the attempt budget are applied in exactly
  one place.
* **`escalated` is not terminal.** It is unresolved work that belongs to a human. Pass 1 of
  the loop resolves it as `recovered_externally` if reality recovers on its own.
* **`resolved` is terminal.** A new divergence opens a new incident with its own evidence,
  rather than reanimating an old one.

### Invariants

| Rule | Enforced by |
| --- | --- |
| At most one active incident per `(policy, resource)` | partial unique index `incidents_one_active_idx` |
| A transition applies only from the status the caller expected | guarded `UPDATE … WHERE status = @from` |
| Every transition appends a durable state event and fans out over SSE | `ProjectService` |
| Only evidence received after the action finished can resolve an incident | `verify_evidence_after` + `Verifier` |
| Verification cannot hang forever | `verify_deadline_at` |
| Disabling a policy resolves the incidents it caused | loop reconciliation |

---

## Action

```
   PROPOSED ──▶ APPROVED ──▶ EXECUTING ──▶ SUCCEEDED | FAILED
        └─────▶ REJECTED
```

Every action passes through `approved`, even under blanket policy authority — the approver is
recorded as `policy:<name>`. There is no path from `proposed` straight to `executing`, so the
audit log always answers *who allowed this*.

`succeeded` means the intervention **ran**. Whether it **worked** is `ActionOutcome`
(`verified`, `verification_failed`, `verification_timed_out`, `not_applicable`), written when
verification concludes. Settled actions are immutable.

---

## Job

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

### Invariants

| Rule | Enforced by |
| --- | --- |
| Only valid transitions are persisted | `JobStateMachine`, re-checked in the store |
| Every transition appends a durable state event, including completion | `JobService` |
| Every worker write presents the live lease token — including events and checkpoints, which change no status | `IJobStore.HasValidLeaseAsync`, `InternalApi` |
| Completion and failure require an unexpired lease, not merely a matching token | `CompleteAsync` / `FailAsync` guards |
| A worker whose heartbeat is rejected (409) stops immediately without writing | `WorkerLoop.HeartbeatLoopAsync` |
| A heartbeat is refused once the job is no longer executing | status guard in `HeartbeatAsync` |
| Recovery requeues with `resume_from_checkpoint` = last persisted checkpoint, and clears the lease token | `SweepExpiredLeasesAsync` |
| Usage accumulates across attempts, so budgets span recoveries | `Usage` totals + worker seeds its meter from `Job.Usage` |
| After `max_attempts` recoveries the job is FAILED | sweep chain |
| Cancel on an idle job cancels in place; on a running job it is a sticky flag honored at the next heartbeat | `RequestCancelAsync` |

The lease token gates **append-only** writes too. A checkpoint is the thing a recovered
worker resumes from, so a zombie able to write one could corrupt the run that replaced it —
which is why `/internal/jobs/{id}/events` and `/checkpoints` return 409 on a stale lease
rather than accepting anonymous writes.

---

## How the layers meet

```
incident  ACTING ──────────────────────────────▶ VERIFYING
              │                                     ▲
              │ action.execution_job_id              │ action settled
              ▼                                     │
job    QUEUED → PREPARING → RUNNING → VALIDATING → COMPLETED | FAILED
```

The loop polls the job and settles the action; the action's terminal state advances the
incident. The link is one-directional (`actions.execution_job_id`): actions know about jobs,
jobs know nothing about incidents.

## Timing defaults

| Knob | Default |
| --- | --- |
| Lease TTL | 15 s |
| Worker heartbeat | 5 s |
| Recovery sweeper interval | 5 s |
| Reliability loop interval | 5 s |
| Job max attempts | 3 |
| Remediation max attempts | 1 (per policy) |
| Remediation cool-down | 300 s (per policy) |
| Verification window | 120 s (per policy) |

All are configuration. Nothing depends on clock skew between machines: lease expiry,
observation receipt and verification freshness are all evaluated on the server's clock.
