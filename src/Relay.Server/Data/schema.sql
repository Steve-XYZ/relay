-- Relay schema. Executed idempotently at server startup.
--
-- Layered exactly like the domain: projects own resources, resources accumulate
-- observations, policies state desired state, incidents record divergence, actions record
-- intervention, and jobs are how an action gets carried out.

-- ---------------------------------------------------------------- control plane

CREATE TABLE IF NOT EXISTS projects (
    id          UUID PRIMARY KEY,
    slug        TEXT NOT NULL UNIQUE,
    name        TEXT NOT NULL DEFAULT '',
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS resources (
    id               UUID PRIMARY KEY,
    project_id       UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    kind             TEXT NOT NULL,
    key              TEXT NOT NULL,
    name             TEXT NOT NULL DEFAULT '',
    attributes       JSONB NOT NULL DEFAULT '{}'::jsonb,
    health           TEXT NOT NULL DEFAULT 'unknown',
    health_reason    TEXT,
    last_observed_at TIMESTAMPTZ,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (project_id, kind, key)
);

-- Append-only evidence. Never updated, never deleted by Relay: an incident must always be
-- able to cite the exact observation it was opened on.
CREATE TABLE IF NOT EXISTS observations (
    id           BIGSERIAL PRIMARY KEY,
    project_id   UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    resource_id  UUID NOT NULL REFERENCES resources(id) ON DELETE CASCADE,
    signal       TEXT NOT NULL DEFAULT 'health',
    state        TEXT NOT NULL,
    facts        JSONB NOT NULL DEFAULT '{}'::jsonb,
    source       TEXT NOT NULL DEFAULT 'unknown',
    message      TEXT,
    observed_at  TIMESTAMPTZ NOT NULL,
    received_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS observations_latest_idx
    ON observations (resource_id, signal, received_at DESC, id DESC);

CREATE TABLE IF NOT EXISTS policies (
    id           UUID PRIMARY KEY,
    project_id   UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    name         TEXT NOT NULL,
    target       JSONB NOT NULL,
    expectation  JSONB NOT NULL,
    severity     TEXT NOT NULL DEFAULT 'warning',
    remediation  JSONB NOT NULL,
    enabled      BOOLEAN NOT NULL DEFAULT TRUE,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at   TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX IF NOT EXISTS policies_project_name_idx ON policies (project_id, name);
CREATE INDEX IF NOT EXISTS policies_project_idx ON policies (project_id) WHERE enabled;

CREATE TABLE IF NOT EXISTS incidents (
    id                     UUID PRIMARY KEY,
    short_id               TEXT NOT NULL UNIQUE,
    project_id             UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    policy_id              UUID NOT NULL REFERENCES policies(id) ON DELETE CASCADE,
    policy_name            TEXT NOT NULL DEFAULT '',
    resource_id            UUID NOT NULL REFERENCES resources(id) ON DELETE CASCADE,
    resource_label         TEXT NOT NULL DEFAULT '',
    status                 TEXT NOT NULL DEFAULT 'open',
    severity               TEXT NOT NULL DEFAULT 'warning',
    summary                TEXT NOT NULL DEFAULT '',
    detail                 TEXT,
    attempt                INT NOT NULL DEFAULT 0,
    max_attempts           INT NOT NULL DEFAULT 1,
    current_action_id      UUID,
    last_attempt_at        TIMESTAMPTZ,
    verify_deadline_at     TIMESTAMPTZ,
    verify_evidence_after  TIMESTAMPTZ,
    escalation_reason      TEXT,
    resolution             TEXT,
    opened_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
    resolved_at            TIMESTAMPTZ
);

-- The invariant that keeps a flapping resource from producing an incident storm, and that
-- stops two server replicas from both opening one. Enforced by the database, not by a read.
CREATE UNIQUE INDEX IF NOT EXISTS incidents_one_active_idx
    ON incidents (policy_id, resource_id) WHERE status <> 'resolved';

CREATE INDEX IF NOT EXISTS incidents_active_idx ON incidents (opened_at) WHERE status <> 'resolved';

CREATE TABLE IF NOT EXISTS actions (
    id                UUID PRIMARY KEY,
    project_id        UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    incident_id       UUID NOT NULL REFERENCES incidents(id) ON DELETE CASCADE,
    kind              TEXT NOT NULL,
    status            TEXT NOT NULL DEFAULT 'proposed',
    attempt           INT NOT NULL DEFAULT 1,
    params            JSONB NOT NULL DEFAULT '{}'::jsonb,
    reason            TEXT NOT NULL DEFAULT '',
    requires_approval BOOLEAN NOT NULL DEFAULT FALSE,
    approved_by       TEXT,
    execution_job_id  UUID,
    failure_reason    TEXT,
    outcome           TEXT,
    outcome_detail    TEXT,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    started_at        TIMESTAMPTZ,
    finished_at       TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS actions_incident_idx ON actions (incident_id, attempt);
CREATE INDEX IF NOT EXISTS actions_pending_idx ON actions (project_id)
    WHERE status = 'proposed' AND requires_approval;

CREATE TABLE IF NOT EXISTS incident_events (
    id          BIGSERIAL PRIMARY KEY,
    incident_id UUID NOT NULL REFERENCES incidents(id) ON DELETE CASCADE,
    seq         BIGINT NOT NULL,
    kind        TEXT NOT NULL,
    message     TEXT NOT NULL DEFAULT '',
    data        JSONB,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (incident_id, seq)
);

-- ---------------------------------------------------------------- execution plane

CREATE TABLE IF NOT EXISTS workers (
    id              UUID PRIMARY KEY,
    name            TEXT NOT NULL UNIQUE,
    capabilities    JSONB NOT NULL DEFAULT '{}'::jsonb,
    first_seen      TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_heartbeat  TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS jobs (
    id                      UUID PRIMARY KEY,
    short_id                TEXT NOT NULL UNIQUE,
    project_id              UUID REFERENCES projects(id) ON DELETE SET NULL,
    origin                  TEXT NOT NULL DEFAULT 'user',
    title                   TEXT NOT NULL DEFAULT '',
    repo_url                TEXT NOT NULL,
    base_ref                TEXT NOT NULL DEFAULT 'HEAD',
    prompt                  TEXT NOT NULL,
    agent                   TEXT NOT NULL DEFAULT 'mock',
    test_command            TEXT,
    status                  TEXT NOT NULL DEFAULT 'queued',
    budget                  JSONB,
    usage_tokens_in         BIGINT NOT NULL DEFAULT 0,
    usage_tokens_out        BIGINT NOT NULL DEFAULT 0,
    usage_cost_usd          NUMERIC(12,4) NOT NULL DEFAULT 0,
    usage_tool_calls        BIGINT NOT NULL DEFAULT 0,
    usage_retries           BIGINT NOT NULL DEFAULT 0,
    attempt                 INT NOT NULL DEFAULT 0,
    max_attempts            INT NOT NULL DEFAULT 3,
    cancel_requested        BOOLEAN NOT NULL DEFAULT FALSE,
    failure_reason          TEXT,
    result                  JSONB,
    resume_from_checkpoint  BIGINT NOT NULL DEFAULT 0,
    lease_worker            UUID REFERENCES workers(id),
    lease_token             UUID,
    lease_expires_at        TIMESTAMPTZ,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
    started_at              TIMESTAMPTZ,
    finished_at             TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS jobs_claim_idx ON jobs (created_at)
    WHERE status IN ('queued', 'recovering');
CREATE INDEX IF NOT EXISTS jobs_status_idx ON jobs (status);
CREATE INDEX IF NOT EXISTS jobs_lease_expiry_idx ON jobs (lease_expires_at)
    WHERE lease_expires_at IS NOT NULL;
CREATE INDEX IF NOT EXISTS jobs_project_idx ON jobs (project_id) WHERE project_id IS NOT NULL;

-- Columns added after the first release; CREATE TABLE IF NOT EXISTS would skip them on an
-- existing database, so they are stated explicitly.
ALTER TABLE jobs ADD COLUMN IF NOT EXISTS project_id UUID REFERENCES projects(id) ON DELETE SET NULL;
ALTER TABLE jobs ADD COLUMN IF NOT EXISTS origin TEXT NOT NULL DEFAULT 'user';

-- The single link between an action and the job that carried it out. Added after both
-- tables exist so bootstrap order stays acyclic.
ALTER TABLE actions DROP CONSTRAINT IF EXISTS actions_execution_job_fk;
ALTER TABLE actions ADD CONSTRAINT actions_execution_job_fk
    FOREIGN KEY (execution_job_id) REFERENCES jobs(id) ON DELETE SET NULL;

CREATE TABLE IF NOT EXISTS job_events (
    id          BIGSERIAL PRIMARY KEY,
    job_id      UUID NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
    seq         BIGINT NOT NULL,
    kind        TEXT NOT NULL,
    message     TEXT NOT NULL DEFAULT '',
    data        JSONB,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (job_id, seq)
);

CREATE TABLE IF NOT EXISTS checkpoints (
    id          BIGSERIAL PRIMARY KEY,
    job_id      UUID NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
    seq         BIGINT NOT NULL,
    label       TEXT NOT NULL DEFAULT '',
    data        JSONB,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (job_id, seq)
);
