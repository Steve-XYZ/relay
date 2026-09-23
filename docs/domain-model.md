# Domain model

Relay's job is to make one sentence true:

> Relay continuously knows what should be happening in a project, what is actually
> happening, what requires intervention, what it is allowed to do about it, and whether
> its intervention worked.

Each clause in that sentence is a concept in the model, and nothing in the model exists
that does not serve one of them.

```
Project                     the unit of ownership
  └── Resource              a thing with an operational identity
        └── Observation     evidence about it  ─────────────┐  what is actually happening
  └── Policy                                                │
        ├── Expectation     what should be true  ────────────┤  what should be happening
        └── Remediation     what Relay may do about it  ─────┤  what it is allowed to do
              │                                              │
              ▼           compare ◀──────────────────────────┘
        Incident            reality diverged  ──────────────────  what requires intervention
              └── Action    one auditable intervention
                    └── Job execution machinery
              └── Verification  did it work?  ────────────────── whether it worked
```

Types live in `src/Relay.Core`, one file per concept.

## Project

`Project` — a slug, a name, and everything hanging off it. The scope of ownership, of
policy, and of every query. Nothing crosses project boundaries.

## Resource

`Resource` — something with an operational identity that a policy can target and a reporter
can report on. Identified by `(project, kind, key)`.

`ResourceKind` is an explicit enum: `Repository`, `Service`, `Worker`, `Deployment`,
`Database`, `ScheduledProcess`, `ExternalDependency`. The loop treats every kind
identically, so a new kind costs one enum member and a line of documentation — not a type
registry, a schema per kind, or a plugin. Kind-specific configuration goes in a flat
`attributes` map (`repo_url` on a repository, `url` on a service). When a kind's
configuration wants a schema, that is the signal it has earned explicit fields.

Resources are **declared**, never invented. `Resource.Health` is derived — the worst state
across the latest observation of every signal — so health is always evidence-backed and
always explainable via `health_reason`.

## Observation

`Observation` — one immutable, timestamped fact reported about a resource. Observations are
the only input to Relay's picture of reality. The loop never invents one and never mutates
one.

**Relay does not scrape.** Observations arrive by push from whatever already knows: a health
checker, CI, a cron wrapper, a deploy pipeline. That is the line between a control plane and
a monitoring system, and it is what keeps Relay from becoming a worse Prometheus.

A `signal` names which aspect an observation is about (`health`, `last_run`, `queue_depth`),
so one resource can be watched from several angles by several policies. `facts` carries
detail an expectation can assert on.

Two timestamps, and the difference matters:

| Field | Clock | Used for |
| --- | --- | --- |
| `observed_at` | the reporter's | staleness (`fresh` expectations) |
| `received_at` | Relay's | verification freshness |

`observed_at` is clamped to `received_at` at ingest, so an observation can be late but never
from the future. Verification trusts `received_at` only — a reporter with a fast clock must
not be able to close an incident that is still broken.

## Policy: desired state, and authority

`Policy` — a durable statement of what should be true plus the authority to restore it. The
only reason Relay ever acts on its own.

`Expectation` is a **closed set** of five predicates. An expression language would make
policies unreviewable and evaluation untestable:

| Kind | Asserts |
| --- | --- |
| `healthy` | the latest observation for the signal reports healthy |
| `fresh` | an observation exists and is younger than `max_age_seconds` |
| `fact_at_most` | a numeric fact is at or below `threshold` |
| `fact_at_least` | a numeric fact is at or above `threshold` |
| `fact_equals` | a fact equals `value` exactly |

`ResourceSelector` targets a kind, optionally narrowed to one key. No key means every
resource of that kind, so one policy covers a fleet.

`Remediation` is the entire authorization surface for autonomous action. If it does not
grant something, Relay escalates to a human instead of improvising:

| Field | Meaning |
| --- | --- |
| `action` | which `ActionKind` Relay may take |
| `params` | inputs for it (`prompt`, `repo_url`, budget caps) |
| `requires_approval` | propose and wait for a human, rather than act |
| `max_attempts` | how many times Relay may try before escalating |
| `cooldown_seconds` | minimum gap between attempts |
| `verify_within_seconds` | how long to wait for evidence the action worked |

Both records are validated at write time (`Expectation.Validated()`,
`Remediation.Validated()`), so a half-formed policy cannot reach the loop. An agent task
with no prompt is rejected when it is authored, not discovered during an outage. Same for
`run_command` with no `command` param.

## Incident

`Incident` — the durable record that reality diverged, and everything Relay did about it.

**At most one incident per `(policy, resource)` may be active.** Enforced by a partial
unique index in Postgres and re-checked on every open, so a flapping resource produces one
incident rather than a storm, and two server replicas cannot both open one.

`IncidentStatus` is the loop's own steps, so "what is Relay doing about this" is one column:
`open`, `awaiting_approval`, `acting`, `verifying`, `resolved`, `escalated`. `resolved` is
the only terminal status — an escalated incident is still Relay's problem to *track*, it is
just not Relay's to *fix*. See [state-machine.md](state-machine.md).

## Action

`RemediationAction` — one auditable intervention: what Relay decided to do, who allowed it,
what executed it, and whether it worked.

Every action passes through `approved` even when the policy grants blanket authority — the
approver is recorded as `policy:<name>`. The audit log therefore always answers *who allowed
this*.

`ActionKind` is deliberately tiny:

| Kind | What it does |
| --- | --- |
| `notify` | records that a human must intervene, with the incident's evidence attached |
| `run_agent_task` | runs a coding agent against a repository, through the job runtime |
| `run_command` | runs a short shell command synchronously (restart, requeue); exit 0 = it ran |

Relay does not restart your containers or run your deploys. Where an intervention belongs to
an external system, the honest design is to ask that system and then verify the result — so
a new kind is one enum member plus one branch in `ActionDispatcher`, reviewed like any other
code. There is no plugin host, because a plugin host would make the authorization surface
unauditable.

`ActionStatus.Succeeded` means the intervention **ran**. Whether it **worked** is
`ActionOutcome`, set later by verification.

## Job: execution machinery

`Job` is unchanged in substance and demoted in status. It is how one action gets carried
out: clone a repo, run an agent in a sandbox, validate, produce a diff — with leases,
checkpoints and crash recovery intact.

A job knows its `project_id` and its `origin` (`user` or `policy`) and deliberately knows
**nothing** about the incident that caused it. The link lives in one place,
`actions.execution_job_id`, pointing downward. Execution must stay replaceable without
touching the control-plane model.

## Verification

Not a table — a rule, in `Verifier`:

> Only evidence Relay received **after** the action finished may resolve an incident.

An action reporting success is a claim about itself, not a fact about the resource. A job can
exit zero and change nothing; a health check can look green for minutes after a real
outage. So an incident entering `verifying` records two facts — `verify_evidence_after` and
`verify_deadline_at` — and resolves only when a fresh observation satisfies the same
expectation that opened it.

| Verdict | Meaning | Consequence |
| --- | --- | --- |
| `passed` | fresh evidence, expectation satisfied | resolve as `verified` |
| `failed` | fresh evidence, still violated | retry within budget, else escalate |
| `pending` | no conclusive fresh evidence yet | wait |
| `timed_out` | deadline passed without it | retry within budget, else escalate |

Fresh-but-inconclusive evidence (the asserted fact is missing) waits rather than guessing,
and lets the deadline decide.

## What this model deliberately does not do

* **Discover resources.** Relay knows about what someone declared. A typo in a reporter gets
  a 404, not a phantom resource nobody owns.
* **Store metrics.** Observations are latest-known-state facts, not a time series. Ask
  Prometheus for percentiles.
* **Verify without a reporter.** Verification needs a signal that reports at least once
  within `verify_within_seconds`. A slowly-reported signal needs a longer window, or Relay
  will honestly say it could not confirm — which is the correct answer, not a false one.
* **Own the fix.** Relay coordinates external systems and, for code, runs agents. It does
  not implement restarts, deploys, or scaling.
