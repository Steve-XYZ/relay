# Relay

**Relay is a reliability control plane for software projects.** It knows what should be
happening in your project, what is actually happening, what requires intervention, what it
is allowed to do about it — and whether its intervention worked.

```
$ relay state checkout-api

RESOURCES
  ok   repository/checkout              (nothing reported)
  DOWN service/api                      health: unavailable per health-checker
  ok   scheduled_process/nightly-etl    last_run: healthy per cron-wrapper

DESIRED STATE
    api must be healthy            service/api            health is healthy -> run_agent_task
    nightly etl must run daily     all scheduled_process  last_run reported within 86400s -> notify

NEEDS INTERVENTION
  IBRW5  verifying  critical  service/api  'health' is unavailable per health-checker
```

```
$ relay incident IBRW5

IBRW5  resolved  (critical)
policy    : api must be healthy
resource  : service/api
observed  : verified: 'health' is healthy per health-checker
attempts  : 1/2
resolved  : verified at 2026-09-09 15:44:06Z

ACTIONS
  #1 run_agent_task   succeeded  authorized by policy:api must be healthy
     job      : e3e6f886-963c-4450-9fd9-a2c8c696af02
     verified : verified — verified: 'health' is healthy per health-checker

TIMELINE
  15:43:39 milestone    incident_opened
  15:43:39 observation  'health' is unavailable per health-checker: connect: connection refused
  15:43:39 state        open -> acting
  15:43:39 decision     acting: run_agent_task attempt 1/2, authorized by policy:api must be healthy
  15:43:39 milestone    run_agent_task_started
  15:43:50 state        acting -> verifying
  15:43:50 verification awaiting evidence received after 2026-09-09T15:43:50Z (deadline …15:45:50Z)
  15:44:06 verification verified: 'health' is healthy per health-checker
  15:44:06 state        verifying -> resolved
  15:44:06 milestone    incident_resolved
```

## The loop

```
Observe → Understand → Compare with desired state → Act → Verify → Remember → Repeat
```

You declare **resources** and push **observations** about them. You state **policies**:
what should be true, and what Relay may do when it isn't. Relay compares the two, opens an
**incident** when reality diverges, takes an **action** it has been authorized to take,
executes it as a **job**, and then — the part that matters — refuses to close the incident
until fresh evidence shows the expectation is satisfied again.

## The part most systems get wrong

An action reporting success is a claim about itself, not a fact about the resource. A job can
exit zero and change nothing. A health check can look green for minutes after a real outage.

So Relay records the instant an action finished and will only resolve an incident on evidence
it received **after** that instant, judged against the same expectation that opened the
incident. If no such evidence arrives within the policy's verification window, Relay says it
could not confirm and escalates — rather than claiming a fix it cannot demonstrate.

Freshness is measured on Relay's own clock, not the reporter's, so a reporter with a fast
clock cannot close an incident that is still broken.

## What is actually engineered here

* **Explicit domain model** — projects, resources, observations, policies, incidents,
  actions, jobs. Five closed expectation predicates instead of an expression language; two
  action kinds instead of a plugin host. [docs/domain-model.md](docs/domain-model.md)
* **Three state machines** — incident, action, job — each an explicit transition table,
  validated on every write and re-checked in the store. [docs/state-machine.md](docs/state-machine.md)
* **Verification as a first-class concept** — `ActionStatus.Succeeded` means it ran;
  `ActionOutcome.Verified` means it worked. Two different columns because they are two
  different facts.
* **Authorization that is auditable** — every action records an approver, even under blanket
  policy authority (`policy:<name>`). `requires_approval` makes Relay propose and wait.
* **Bounded autonomy** — per-policy attempt budgets, cool-downs, verification windows, and
  token/cost/runtime caps on the jobs actions spawn.
* **Concurrency-safe by construction** — compare-and-swap transitions, lease tokens, and a
  partial unique index enforcing one active incident per (policy, resource). Several server
  replicas can run the loop at once.
* **Crash-durable execution** — leases expire server-side, jobs requeue from their last
  checkpoint, and a zombie worker cannot write anything, not even an event.
  [docs/reliability-loop.md](docs/reliability-loop.md)

## What Relay deliberately is not

Relay **coordinates and reasons about** external systems. It does not replace them.

* Not a metrics store. Observations are latest-known-state facts, not time series. Relay does
  not scrape; reporters push.
* Not an orchestrator. It does not restart containers, run deploys, or scale anything. Where
  an intervention belongs to another system, Relay's job is to ask that system and verify the
  result.
* Not a monitoring product, a CI system, or an agent framework.
* It knows about resources somebody declared. There is no discovery, and a typo in a reporter
  gets a 404 rather than a phantom resource nobody owns.

## Quick start

```bash
docker compose up --build -d
export RELAY_URL=http://localhost:8080
```

Declare what exists:

```bash
curl -X POST $RELAY_URL/api/projects -H 'content-type: application/json' \
  -d '{"slug":"checkout-api","name":"Checkout API"}'

curl -X POST $RELAY_URL/api/projects/checkout-api/resources -H 'content-type: application/json' \
  -d '{"kind":"repository","key":"checkout","attributes":{"repo_url":"https://github.com/you/checkout"}}'

curl -X POST $RELAY_URL/api/projects/checkout-api/resources -H 'content-type: application/json' \
  -d '{"kind":"service","key":"api"}'
```

State what should be true, and what Relay may do about it:

```bash
curl -X POST $RELAY_URL/api/projects/checkout-api/policies -H 'content-type: application/json' -d '{
  "name": "api must be healthy",
  "target": {"kind": "service", "key": "api"},
  "expectation": {"kind": "healthy"},
  "severity": "critical",
  "remediation": {
    "action": "run_agent_task",
    "params": {"prompt": "The API health check is failing. Diagnose and fix.", "max_cost_usd": "2.00"},
    "requires_approval": true,
    "max_attempts": 2,
    "verify_within_seconds": 300
  }
}'
```

Report reality, from wherever already knows:

```bash
relay observe checkout-api service/api unavailable --source health-checker \
      --message "connect: connection refused"

# or straight from a cron wrapper:
curl -X POST $RELAY_URL/api/projects/checkout-api/observations -H 'content-type: application/json' \
  -d '{"kind":"scheduled_process","key":"nightly-etl","signal":"last_run","state":"healthy","source":"cron"}'
```

Then watch:

```bash
relay state checkout-api          # desired vs actual, incidents, pending approvals
relay incidents                   # what requires intervention
relay incident IBRW5              # evidence, decisions, verification
relay approve <action-id>          # authorize a proposed intervention
```

## Demos

```bash
scripts/dev-up.sh                      # postgres + server :18080 + one worker
scripts/demo-reliability-loop.sh       # the whole loop, including the refusal to
                                       # resolve on a successful job alone
scripts/demo-crash-recovery.sh         # kill -9 a worker mid-run; the job survives
```

## Local development

```bash
scripts/dev-up.sh
export RELAY_URL=http://127.0.0.1:18080
```

`Storage:Mode=memory` runs the server with no Postgres at all — the in-memory stores hold the
same invariants, so it is a real server, just not a durable one. `SANDBOX_MODE=process` runs
agent commands directly on the host: fast for dev, no isolation.

## API

Control plane:

| Method | Path | Purpose |
| --- | --- | --- |
| `POST` | `/api/projects` | declare a project (idempotent by slug) |
| `POST` | `/api/projects/{p}/resources` | declare a resource (attributes merge) |
| `POST` | `/api/projects/{p}/observations` | report what is actually happening |
| `POST` | `/api/projects/{p}/policies` | state desired state + authority |
| `GET` | `/api/projects/{p}/state` | the whole picture in one read |
| `GET` | `/api/incidents` | what requires intervention |
| `GET` | `/api/incidents/{id}` | one incident with actions and audit log |
| `GET` | `/api/incidents/{id}/events` | live incident timeline (SSE, replayable) |
| `POST` | `/api/actions/{id}/approve` \| `/reject` | authorize or refuse a proposal |

Execution plane: `POST /api/jobs`, `GET /api/jobs`, `GET /api/jobs/{id}`,
`POST /api/jobs/{id}/cancel`, `GET /api/jobs/{id}/events` (SSE).

## Repo layout

```
src/
  Relay.Core      domain: projects, resources, observations, policies, incidents,
                  actions, jobs; the evaluator, the verifier, three state machines
  Relay.Server    control plane (reliability loop, dispatcher) + execution plane
                  (leases, recovery sweeper); REST + SSE; Postgres and in-memory stores
  Relay.Worker    disposable executor: claims jobs, drives sandboxes, heartbeats
  Relay.Cli       `relay` command-line client for both planes
tests/
  Relay.Core.Tests    expectation evaluation, verification freshness, state machines,
                      policy validation
  Relay.Server.Tests  the loop end to end, control-plane invariants, lease/recovery
web/              Next.js dashboard for job timelines
docker/           server, worker, sandbox and web images
scripts/          dev-up.sh, demo-reliability-loop.sh, demo-crash-recovery.sh
```

## Agents

The default adapter (`--agent mock`) is a deterministic scripted agent, so demos and CI need
no API keys. Any shell command works as an adapter via `--agent "<command> {PROMPT}"`;
adapters communicate through line markers:

```
[relay:progress] tool_call 37
[relay:usage] tokens_in=21000 tokens_out=9000 cost_usd=0.31
```

and receive `PROMPT` and `RESUME_FROM` as environment variables. Policy-driven agent tasks
get the incident's evidence appended to the policy author's prompt, clearly delimited.

## What is deliberately not here yet

* Real LLM-agent adapters beyond the shell template (mock only today).
* Action kinds beyond `notify`, `run_agent_task` and `run_command`. Restarts and deploys
  belong to systems Relay should call, and each needs its own verification story before it
  earns an enum member.
* Observation ingest is push-only: no pollers, no scrapers, no integrations.
* The web dashboard still shows jobs only; the control plane is CLI and API today.
* No authentication between clients and the server.
