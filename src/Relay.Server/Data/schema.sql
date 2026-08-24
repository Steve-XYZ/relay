-- Relay schema. Executed idempotently at server startup.

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
