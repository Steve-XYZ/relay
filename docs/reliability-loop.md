# The reliability loop

> Observe → Understand → Compare with desired state → Act → Verify → Remember → Repeat

`ReliabilityLoop` (`src/Relay.Server/Services/ReliabilityLoop.cs`) is a `BackgroundService`
that runs every `ReliabilityLoop:IntervalSeconds` (default 5). It owns no state. Everything
it decides is read from and written back to the durable store under compare-and-swap, so
several server replicas can run it concurrently and a restart mid-incident loses nothing but
time.

`RunOnceAsync` is public and side-effect-complete, so tests drive the loop tick by tick
against a controlled clock instead of waiting on timers
(`tests/Relay.Server.Tests/ReliabilityLoopTests.cs`).

## Pass 1 — observe, understand, compare

For every project, every enabled policy, and every resource the policy's selector matches:

```
snapshot = latest observation per signal for the resource
verdict  = Evaluator.Evaluate(policy.Expectation, snapshot, now)

violated  + no active incident   → open an incident, citing the verdict
violated  + active incident      → refresh its detail
satisfied + active incident      → resolve (self_healed | recovered_externally)
unknown                          → do nothing
```

Two rules make this pass safe:

* **`unknown` never opens or closes anything.** "Nobody has reported" and "reported healthy"
  are different facts. Conflating them is how monitoring goes quiet at the worst moment.
* **Incidents in `acting` or `verifying` are skipped entirely.** Those belong to pass 2,
  which applies a stricter freshness rule. Without this skip, a stale green observation
  could close an incident whose fix has not been confirmed — and the loop would narrate its
  own polling into the audit log on every tick.

## Pass 2 — act, verify, remember

Active incidents are advanced least-recently-touched first. That ordering is the fairness
property: with a per-tick ceiling (`MaxIncidentsPerTick`), newest-first would let a burst of
new incidents starve older ones indefinitely.

Before anything else, the incident is reconciled against its policy:

| Situation | Outcome |
| --- | --- |
| policy deleted | resolve `policy_deleted` |
| policy disabled | resolve `policy_disabled` — turning a policy off must stop the work it caused |
| resource removed | resolve `resource_removed` |

Then one step per status. An incident may take up to four steps in a tick, so settling an
action and entering verification do not need two ticks:

**`open` — decide.** Out of attempts → escalate. Cool-down not elapsed → wait. Otherwise
propose an action, record its authorization (`policy:<name>`, or wait for a human when
`requires_approval` is set), and dispatch.

**`awaiting_approval` — the authorization gate.** Approved → act. Rejected → escalate; a
human saying no is not a failed attempt to retry around. Still proposed → wait, however many
ticks that takes.

**`acting` — watch the execution.** For an agent task, poll the job; when it reaches a
terminal state, settle the action (`succeeded` for a completed job, `failed` otherwise). The
job machinery guarantees terminality — leases expire, recovery requeues, attempts run out —
so an incident can never be stuck here waiting on a job that will never answer.

For `run_command`, the command runs synchronously during dispatch: exit 0 settles the action
as succeeded with no job; non-zero exit or timeout settles it as failed (undispatchable).

Once the action is `succeeded`: a verifiable remediation enters `verifying`; `notify` records
`ActionOutcome.NotApplicable` and escalates, because telling a human has no observable effect
on the resource and pretending to verify it would be a lie.

**`verifying` — decide whether it worked.** `Verifier.Verify` against fresh observations:
`passed` resolves as `verified`; `failed`/`timed_out` retries within budget or escalates;
`pending` waits and writes nothing.

**`escalated` — stop.** Relay has said what it knows and what it cannot do. Pass 1 resolves
the incident if reality recovers; a human can re-arm it through `open`.

Every retry routes back through `open`, so approval, cool-down and the attempt budget are
applied by the one place that owns those rules.

## Remember

"Remember" is not a separate pass — it is `incident_events`, an append-only, densely
sequenced, SSE-replayable log written *before* the next decision is taken. A finished
incident reads as the story of itself:

```
milestone     incident_opened
observation   'health' is unavailable per health-checker: connect: connection refused
state         open -> acting
decision      acting: run_agent_task attempt 1/2, authorized by policy:api must be healthy
milestone     run_agent_task_started
state         acting -> verifying
verification  awaiting evidence received after 2026-09-09T15:43:50Z (deadline …15:45:50Z)
verification  verified: 'health' is healthy per health-checker
state         verifying -> resolved
milestone     incident_resolved
```

The log records events, not polling. Ten quiet ticks add ten rows to nothing — asserted by
`The_audit_log_records_events_not_polling`.

## Concurrency

The loop uses the same discipline as the lease machinery it sits above. Every state-changing
store method takes the status the caller believes the record is in and returns `null` when
that belief was wrong:

```csharp
await _store.BeginActingAsync(incident.Id, from: IncidentStatus.Open, action.Id, attempt, ct)
// UPDATE incidents SET status = 'acting', … WHERE id = @id AND status = 'open'
```

A replica that loses a race writes nothing and does nothing. Where losing a race would leave
an orphan action, the loser closes it out as `superseded`, so the audit log has no mystery
rows.

## Configuration

| Knob | Default |
| --- | --- |
| `ReliabilityLoop:Enabled` | `true` |
| `ReliabilityLoop:IntervalSeconds` | 5 |
| `ReliabilityLoop:MaxIncidentsPerTick` | 500 |

## Metrics

`relay.observations.recorded` (by state, signal), `relay.incidents.opened` /
`.resolved` (by resolution) / `.escalated`, `relay.actions.dispatched` (by kind, started),
and `relay.verifications` (by status).

The ratio of `passed` to `failed`/`timed_out` verifications is the honest measure of whether
Relay's interventions actually work. It is the one number that would embarrass the product if
it were bad, which is exactly why it is instrumented.
