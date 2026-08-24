using System.Data;
using Npgsql;
using NpgsqlTypes;
using Relay.Core;
using Relay.Server.Data;
using StateMachine = Relay.Core.StateMachine;

namespace Relay.Server.Stores;

/// <summary>
/// Postgres-backed store. The server is the single writer: workers never touch the database.
/// Job claiming uses SELECT ... FOR UPDATE SKIP LOCKED so server replicas can run safely.
/// </summary>
public sealed class PostgresJobStore : IJobStore
{
    public const string JobColumns = """
        id, short_id, title, repo_url, base_ref, prompt, agent, test_command, status,
        budget, usage_tokens_in, usage_tokens_out, usage_cost_usd, usage_tool_calls, usage_retries,
        attempt, max_attempts, cancel_requested, failure_reason, result, resume_from_checkpoint,
        lease_expires_at, created_at, started_at, finished_at
        """;

    private readonly IDbConnectionFactory _db;

    private static readonly string JobColumnsQualified = string.Join(", ",
        JobColumns.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(line => line.Split(',', StringSplitOptions.TrimEntries))
            .Where(c => c.Length > 0)
            .Select(c => "j." + c));

    public PostgresJobStore(IDbConnectionFactory db) => _db = db;

    private static DateTimeOffset ToDto(DateTime value) =>
        value.Kind == DateTimeKind.Utc
            ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))
            : new DateTimeOffset(value.ToUniversalTime());

    internal static Job MapJob(NpgsqlDataReader r)
    {
        var usage = new Usage
        {
            TokensIn = r.GetInt64(10),
            TokensOut = r.GetInt64(11),
            CostUsd = r.GetDecimal(12),
            ToolCalls = r.GetInt64(13),
            Retries = r.GetInt64(14),
        };
        return new Job
        {
            Id = r.GetGuid(0),
            ShortId = r.GetString(1),
            Title = r.GetString(2),
            RepoUrl = r.GetString(3),
            BaseRef = r.GetString(4),
            Prompt = r.GetString(5),
            Agent = r.GetString(6),
            TestCommand = r.IsDBNull(7) ? null : r.GetString(7),
            Status = r.GetString(8),
            Budget = r.IsDBNull(9) ? null : Json.Deserialize<Budget>(r.GetString(9)),
            Usage = usage,
            Attempt = r.GetInt32(15),
            MaxAttempts = r.GetInt32(16),
            CancelRequested = r.GetBoolean(17),
            FailureReason = r.IsDBNull(18) ? null : r.GetString(18),
            Result = r.IsDBNull(19) ? null : Json.Deserialize<JobResult>(r.GetString(19)),
            ResumeFromCheckpoint = r.GetInt64(20),
            LeaseExpiresAt = r.IsDBNull(21) ? null : ToDto(r.GetDateTime(21)),
            CreatedAt = ToDto(r.GetDateTime(22)),
            StartedAt = r.IsDBNull(23) ? null : ToDto(r.GetDateTime(23)),
            FinishedAt = r.IsDBNull(24) ? null : ToDto(r.GetDateTime(24)),
        };
    }

    internal static JobEvent MapEvent(NpgsqlDataReader r)
    {
        var i = -1;
        return new JobEvent
        {
            Seq = r.GetInt64(++i),
            Kind = r.GetString(++i),
            Message = r.GetString(++i),
            Data = r.IsDBNull(++i) ? null : Json.Deserialize<Dictionary<string, string>>(r.GetString(i)),
            CreatedAt = ToDto(r.GetDateTime(++i)),
        };
    }

    private static NpgsqlCommand Cmd(NpgsqlConnection conn, string sql) => new(sql, conn);

    private static void AddJson<T>(NpgsqlCommand cmd, string name, T? value)
    {
        var p = cmd.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Jsonb));
        p.Value = value is null ? DBNull.Value : Json.Serialize(value);
    }

    public async Task<Job> CreateJobAsync(CreateJobRequest request, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        string sql = $"""
            INSERT INTO jobs (id, short_id, title, repo_url, base_ref, prompt, agent, test_command, budget, status)
            VALUES (@id, @shortId, @title, @repoUrl, @baseRef, @prompt, @agent, @testCommand, @budget, 'queued')
            RETURNING {JobColumns}
            """;
        await using var cmd = Cmd(conn, sql);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("shortId", await GenerateShortId(conn, ct));
        cmd.Parameters.AddWithValue("title", request.Title ?? (request.Prompt.Length > 63 ? request.Prompt[..60] + "..." : request.Prompt));
        cmd.Parameters.AddWithValue("repoUrl", request.RepoUrl);
        cmd.Parameters.AddWithValue("baseRef", string.IsNullOrWhiteSpace(request.BaseRef) ? "HEAD" : request.BaseRef!);
        cmd.Parameters.AddWithValue("prompt", request.Prompt);
        cmd.Parameters.AddWithValue("agent", string.IsNullOrWhiteSpace(request.Agent) ? "mock" : request.Agent!);
        cmd.Parameters.AddWithValue("testCommand", (object?)request.TestCommand ?? DBNull.Value);
        AddJson(cmd, "budget", request.Budget);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return MapJob(reader);
    }

    private static async Task<string> GenerateShortId(NpgsqlConnection conn, CancellationToken ct)
    {
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        var chars = new char[4];
        for (var i = 0; i < 16; i++)
        {
            for (var j = 0; j < chars.Length; j++)
                chars[j] = alphabet[Random.Shared.Next(alphabet.Length)];
            var candidate = new string(chars);

            using var check = Cmd(conn, "SELECT 1 FROM jobs WHERE short_id = @s");
            check.Parameters.AddWithValue("s", candidate);
            if (await check.ExecuteScalarAsync(ct) is null)
                return candidate;
        }
        throw new InvalidOperationException("Could not generate a unique short id");
    }

    public async Task<Job?> ResolveAsync(string idOrShortId, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        if (Guid.TryParse(idOrShortId, out var id))
        {
            var byId = await ReadById(conn, id, ct);
            if (byId is not null) return byId;
        }
        await using var cmd = Cmd(conn, $"SELECT {JobColumns} FROM jobs WHERE lower(short_id) = lower(@s) LIMIT 1");
        cmd.Parameters.AddWithValue("s", idOrShortId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapJob(reader) : null;
    }

    private static async Task<Job?> ReadById(NpgsqlConnection conn, Guid id, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, $"SELECT {JobColumns} FROM jobs WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapJob(reader) : null;
    }

    public async Task<IReadOnlyList<Job>> ListAsync(JobStatus? status, int limit, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        var sql = $"SELECT {JobColumns} FROM jobs";
        if (status is not null) sql += " WHERE status = @status";
        sql += " ORDER BY created_at DESC LIMIT @limit";

        await using var cmd = Cmd(conn, sql);
        if (status is not null) cmd.Parameters.AddWithValue("status", status.Value.ToWire());
        cmd.Parameters.AddWithValue("limit", limit);

        var jobs = new List<Job>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) jobs.Add(MapJob(reader));
        return jobs;
    }

    public async Task EnsureWorkerAsync(Guid workerId, string name, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = Cmd(conn, """
            INSERT INTO workers (id, name) VALUES (@id, @name)
            ON CONFLICT (name) DO UPDATE SET id = EXCLUDED.id, last_heartbeat = now()
            """);
        cmd.Parameters.AddWithValue("id", workerId);
        cmd.Parameters.AddWithValue("name", name);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<(Job Job, Guid LeaseToken, IReadOnlyList<Checkpoint>)?> TryClaimAsync(
        Guid workerId, string workerName, TimeSpan leaseTtl, CancellationToken ct)
    {
        await EnsureWorkerAsync(workerId, workerName, ct);
        var token = Guid.NewGuid();

        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        string claimSql = $"""
            WITH picked AS (
                SELECT id FROM jobs
                WHERE status IN ('queued', 'recovering')
                  AND cancel_requested = FALSE
                ORDER BY created_at
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            UPDATE jobs j SET
                status = 'preparing',
                attempt = j.attempt + 1,
                started_at = COALESCE(j.started_at, now()),
                lease_worker = @workerId,
                lease_token = @token,
                lease_expires_at = now() + make_interval(secs => @leaseSeconds),
                updated_at = now()
            FROM picked
            WHERE j.id = picked.id
            RETURNING {JobColumnsQualified}
            """;

        Job? claimed;
        Guid jobId;
        await using (var cmd = Cmd(conn, claimSql))
        {
            cmd.Transaction = tx;
            cmd.Parameters.AddWithValue("workerId", workerId);
            cmd.Parameters.AddWithValue("token", token);
            cmd.Parameters.AddWithValue("leaseSeconds", leaseTtl.TotalSeconds);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            claimed = MapJob(reader);
            jobId = claimed.Id;
        }

        var checkpoints = new List<Checkpoint>();
        await using (var cpCmd = Cmd(conn, """
            SELECT seq, label, data, created_at FROM checkpoints WHERE job_id = @id ORDER BY seq
            """))
        {
            cpCmd.Transaction = tx;
            cpCmd.Parameters.AddWithValue("id", jobId);
            await using var reader = await cpCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var i = -1;
                checkpoints.Add(new Checkpoint
                {
                    Seq = reader.GetInt64(++i),
                    Label = reader.GetString(++i),
                    Data = reader.IsDBNull(++i) ? null : Json.Deserialize<Dictionary<string, string>>(reader.GetString(i)),
                    CreatedAt = ToDto(reader.GetDateTime(++i)),
                });
            }
        }

        await tx.CommitAsync(ct);
        return (claimed, token, checkpoints);
    }

    public async Task<bool> HasValidLeaseAsync(Guid jobId, Guid leaseToken, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = Cmd(conn, """
            SELECT 1 FROM jobs
            WHERE id = @id AND lease_token = @token AND lease_expires_at > now()
            """);
        cmd.Parameters.AddWithValue("id", jobId);
        cmd.Parameters.AddWithValue("token", leaseToken);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<HeartbeatOutcome?> HeartbeatAsync(
        Guid jobId, Guid leaseToken, TimeSpan extendBy, Usage? usageDelta, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);

        const string sql = """
            UPDATE jobs SET
                usage_tokens_in   = usage_tokens_in   + @tin,
                usage_tokens_out  = usage_tokens_out  + @tout,
                usage_cost_usd    = usage_cost_usd    + @cost,
                usage_tool_calls  = usage_tool_calls  + @toolCalls,
                usage_retries     = usage_retries     + @retries,
                lease_expires_at  = GREATEST(lease_expires_at, now()) + make_interval(secs => @ext),
                updated_at = now()
            WHERE id = @id AND lease_token = @token
              AND lease_expires_at > now()
              AND status IN ('preparing', 'running', 'validating')
            RETURNING cancel_requested, lease_expires_at
            """;
        await using var cmd = Cmd(conn, sql);
        cmd.Parameters.AddWithValue("id", jobId);
        cmd.Parameters.AddWithValue("token", leaseToken);
        cmd.Parameters.AddWithValue("tin", usageDelta?.TokensIn ?? 0);
        cmd.Parameters.AddWithValue("tout", usageDelta?.TokensOut ?? 0);
        cmd.Parameters.AddWithValue("cost", usageDelta?.CostUsd ?? 0m);
        cmd.Parameters.AddWithValue("toolCalls", usageDelta?.ToolCalls ?? 0L);
        cmd.Parameters.AddWithValue("retries", usageDelta?.Retries ?? 0L);
        cmd.Parameters.AddWithValue("ext", extendBy.TotalSeconds);

        bool cancelRequested;
        DateTimeOffset expiresAt;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            cancelRequested = reader.GetBoolean(0);
            expiresAt = ToDto(reader.GetDateTime(1));
        }

        var job = await ReadById(conn, jobId, CancellationToken.None);
        return job is null ? null : new HeartbeatOutcome(cancelRequested, expiresAt, job);
    }

    /// <summary>
    /// Shared transition core. Reads the current row under lock, validates against the state
    /// machine, then performs a guarded UPDATE so concurrent transitions cannot double-apply.
    /// When requireLease is set, the caller must present the live lease token.
    /// </summary>
    private async Task<(Job Job, JobStatus From)?> TransitionCoreAsync(
        Guid jobId, Guid? leaseToken, bool requireLease, JobStatus to, string? failureReason,
        bool finish, bool clearLease, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        string current;
        await using (var sel = Cmd(conn,
            requireLease
                ? "SELECT status FROM jobs WHERE id = @id AND lease_token = @token AND lease_expires_at > now() FOR UPDATE"
                : "SELECT status FROM jobs WHERE id = @id FOR UPDATE"))
        {
            sel.Transaction = tx;
            sel.Parameters.AddWithValue("id", jobId);
            if (requireLease) sel.Parameters.AddWithValue("token", leaseToken!.Value);
            var found = await sel.ExecuteScalarAsync(ct);
            if (found is null) return null;
            current = (string)found;
        }

        if (!StateMachine.CanTransition(Wire.From(current), to)) return null;

        var sql = $"UPDATE jobs SET status = @to{(finish ? ", finished_at = now()" : "")}" +
                  (failureReason is null ? "" : ", failure_reason = @reason") +
                  (clearLease ? ", lease_worker = NULL, lease_token = NULL, lease_expires_at = NULL" : "") +
                  ", updated_at = now() WHERE id = @id AND status = @current RETURNING " + JobColumns;

        Job? updated;
        await using (var upd = Cmd(conn, sql))
        {
            upd.Transaction = tx;
            upd.Parameters.AddWithValue("to", to.ToWire());
            upd.Parameters.AddWithValue("id", jobId);
            upd.Parameters.AddWithValue("current", current);
            if (failureReason is not null) upd.Parameters.AddWithValue("reason", failureReason);
            await using var reader = await upd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            updated = MapJob(reader);
        }

        await tx.CommitAsync(ct);
        return (updated, Wire.From(current));
    }

    public Task<(Job Job, JobStatus From)?> TransitionWithLeaseAsync(Guid jobId, Guid leaseToken, JobStatus to, string? reason, CancellationToken ct) =>
        TransitionCoreAsync(jobId, leaseToken, requireLease: true, to, to == JobStatus.Failed ? reason : null,
            finish: to.IsTerminal(), clearLease: to.IsTerminal(), ct);

    public Task<(Job Job, JobStatus From)?> TransitionAsync(Guid jobId, JobStatus to, string? reason, CancellationToken ct) =>
        TransitionCoreAsync(jobId, null, requireLease: false, to, to == JobStatus.Failed ? reason : null,
            finish: to.IsTerminal(), clearLease: to.IsTerminal(), ct);

    public async Task<IReadOnlyList<JobEvent>> AppendEventsAsync(Guid jobId, IReadOnlyList<JobEvent> events, CancellationToken ct)
    {
        if (events.Count == 0) return Array.Empty<JobEvent>();

        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var persisted = new List<JobEvent>(events.Count);

        // Serialize appends per job so concurrent emitters cannot compute the same seq.
        await using (var gate = Cmd(conn, "SELECT id FROM jobs WHERE id = @id FOR UPDATE"))
        {
            gate.Transaction = tx;
            gate.Parameters.AddWithValue("id", jobId);
            await gate.ExecuteNonQueryAsync(ct);
        }

        foreach (var e in events)
        {
            await using var cmd = Cmd(conn, """
                INSERT INTO job_events (job_id, seq, kind, message, data)
                VALUES (@jobId, (SELECT COALESCE(MAX(seq), 0) + 1 FROM job_events WHERE job_id = @jobId), @kind, @message, @data)
                RETURNING seq, kind, message, data, created_at
                """);
            cmd.Transaction = tx;
            cmd.Parameters.AddWithValue("jobId", jobId);
            cmd.Parameters.AddWithValue("kind", e.Kind);
            cmd.Parameters.AddWithValue("message", e.Message);
            AddJson(cmd, "data", e.Data);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            persisted.Add(MapEvent(reader));
        }

        await tx.CommitAsync(ct);
        return persisted;
    }

    public async Task AppendCheckpointAsync(Guid jobId, Checkpoint checkpoint, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = Cmd(conn, """
            INSERT INTO checkpoints (job_id, seq, label, data)
            VALUES (@jobId, (SELECT COALESCE(MAX(seq), 0) + 1 FROM checkpoints WHERE job_id = @jobId), @label, @data)
            ON CONFLICT (job_id, seq) DO NOTHING
            """);
        cmd.Parameters.AddWithValue("jobId", jobId);
        cmd.Parameters.AddWithValue("label", checkpoint.Label);
        AddJson(cmd, "data", checkpoint.Data);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<Checkpoint>> GetCheckpointsAsync(Guid jobId, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = Cmd(conn, "SELECT seq, label, data, created_at FROM checkpoints WHERE job_id = @id ORDER BY seq");
        cmd.Parameters.AddWithValue("id", jobId);
        var cps = new List<Checkpoint>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var i = -1;
            cps.Add(new Checkpoint
            {
                Seq = reader.GetInt64(++i),
                Label = reader.GetString(++i),
                Data = reader.IsDBNull(++i) ? null : Json.Deserialize<Dictionary<string, string>>(reader.GetString(i)),
                CreatedAt = ToDto(reader.GetDateTime(++i)),
            });
        }
        return cps;
    }

    public async Task<IReadOnlyList<JobEvent>> GetEventsAsync(Guid jobId, long afterSeq, int limit, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = Cmd(conn, """
            SELECT seq, kind, message, data, created_at FROM job_events
            WHERE job_id = @id AND seq > @afterSeq
            ORDER BY seq LIMIT @limit
            """);
        cmd.Parameters.AddWithValue("id", jobId);
        cmd.Parameters.AddWithValue("afterSeq", afterSeq);
        cmd.Parameters.AddWithValue("limit", limit);
        var events = new List<JobEvent>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) events.Add(MapEvent(reader));
        return events;
    }

    public async Task<Job?> CompleteAsync(Guid jobId, Guid leaseToken, JobResult result, Usage? usageFinal, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        string sql = $"""
            UPDATE jobs SET
                status = 'completed',
                result = @result,
                usage_tokens_in  = CASE WHEN @hasFinal THEN @tin    ELSE usage_tokens_in  END,
                usage_tokens_out = CASE WHEN @hasFinal THEN @tout   ELSE usage_tokens_out END,
                usage_cost_usd   = CASE WHEN @hasFinal THEN @cost   ELSE usage_cost_usd   END,
                usage_tool_calls = CASE WHEN @hasFinal THEN @toolCalls ELSE usage_tool_calls END,
                usage_retries    = CASE WHEN @hasFinal THEN @retries   ELSE usage_retries END,
                finished_at = now(),
                lease_worker = NULL, lease_token = NULL, lease_expires_at = NULL,
                updated_at = now()
            WHERE id = @id AND lease_token = @token AND status = 'validating'
            RETURNING {JobColumns}
            """;
        await using var cmd = Cmd(conn, sql);
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("id", jobId);
        cmd.Parameters.AddWithValue("token", leaseToken);
        AddJson(cmd, "result", result);
        cmd.Parameters.AddWithValue("hasFinal", usageFinal is not null);
        cmd.Parameters.AddWithValue("tin", usageFinal?.TokensIn ?? 0);
        cmd.Parameters.AddWithValue("tout", usageFinal?.TokensOut ?? 0);
        cmd.Parameters.AddWithValue("cost", usageFinal?.CostUsd ?? 0m);
        cmd.Parameters.AddWithValue("toolCalls", usageFinal?.ToolCalls ?? 0L);
        cmd.Parameters.AddWithValue("retries", usageFinal?.Retries ?? 0L);

        Job? completed;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            completed = MapJob(reader);
        }
        await tx.CommitAsync(ct);
        return completed;
    }

    public async Task<(Job Job, JobStatus From)?> FailAsync(Guid jobId, Guid leaseToken, string reason, Usage? usageFinal, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Single guarded statement: the FROM-list locks the row and validates both
        // lease and source status, so no separate read can race the update.
        string sql = $"""
            WITH cur AS (
                SELECT id, status FROM jobs
                WHERE id = @id AND lease_token = @token
                  AND status IN ('preparing', 'running', 'validating')
                FOR UPDATE
            )
            UPDATE jobs AS j SET
                status = 'failed',
                failure_reason = @reason,
                usage_tokens_in  = CASE WHEN @hasFinal THEN @tin    ELSE j.usage_tokens_in  END,
                usage_tokens_out = CASE WHEN @hasFinal THEN @tout   ELSE j.usage_tokens_out END,
                usage_cost_usd   = CASE WHEN @hasFinal THEN @cost   ELSE j.usage_cost_usd   END,
                usage_tool_calls = CASE WHEN @hasFinal THEN @toolCalls ELSE j.usage_tool_calls END,
                usage_retries    = CASE WHEN @hasFinal THEN @retries   ELSE j.usage_retries END,
                finished_at = now(),
                lease_worker = NULL, lease_token = NULL, lease_expires_at = NULL,
                updated_at = now()
            FROM cur
            WHERE j.id = cur.id AND j.status = cur.status
            RETURNING {JobColumnsQualified}, cur.status AS _from_status
            """;
        await using var cmd = Cmd(conn, sql);
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("id", jobId);
        cmd.Parameters.AddWithValue("token", leaseToken);
        cmd.Parameters.AddWithValue("reason", reason);
        cmd.Parameters.AddWithValue("hasFinal", usageFinal is not null);
        cmd.Parameters.AddWithValue("tin", usageFinal?.TokensIn ?? 0);
        cmd.Parameters.AddWithValue("tout", usageFinal?.TokensOut ?? 0);
        cmd.Parameters.AddWithValue("cost", usageFinal?.CostUsd ?? 0m);
        cmd.Parameters.AddWithValue("toolCalls", usageFinal?.ToolCalls ?? 0L);
        cmd.Parameters.AddWithValue("retries", usageFinal?.Retries ?? 0L);

        (Job, JobStatus)? outcome = null;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
                outcome = (MapJob(reader), Wire.From(reader.GetString(25)));
        }
        if (outcome is null) return null;
        await tx.CommitAsync(ct);
        return outcome;
    }

    public async Task<CancelResult> RequestCancelAsync(Guid jobId, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);

        string status;
        bool hasActiveLease;
        await using (var sel = Cmd(conn, """
            SELECT status, (lease_token IS NOT NULL AND lease_expires_at > now())
            FROM jobs WHERE id = @id
            """))
        {
            sel.Parameters.AddWithValue("id", jobId);
            await using var reader = await sel.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return CancelResult.NotFound;
            status = reader.GetString(0);
            hasActiveLease = reader.GetBoolean(1);
        }

        if (status is "completed" or "failed" or "cancelled") return CancelResult.AlreadyTerminal;

        // Idle states can be cancelled in place; leased work gets a sticky flag the worker honors.
        if (status is "queued" or "interrupted" or "recovering" && !hasActiveLease)
        {
            await using var cmd = Cmd(conn, """
                UPDATE jobs SET status = 'cancelled', finished_at = now(),
                    lease_worker = NULL, lease_token = NULL, lease_expires_at = NULL, updated_at = now()
                WHERE id = @id AND status = @status
                  AND (lease_token IS NULL OR lease_expires_at <= now())
                """);
            cmd.Parameters.AddWithValue("id", jobId);
            cmd.Parameters.AddWithValue("status", status);
            return await cmd.ExecuteNonQueryAsync(ct) == 1
                ? CancelResult.CancelledDirectly
                : CancelResult.MarkRequested;
        }

        await using var mark = Cmd(conn, """
            UPDATE jobs SET cancel_requested = TRUE, updated_at = now()
            WHERE id = @id AND status NOT IN ('completed', 'failed', 'cancelled')
            """);
        mark.Parameters.AddWithValue("id", jobId);
        await mark.ExecuteNonQueryAsync(ct);
        return CancelResult.MarkRequested;
    }

    public async Task<IReadOnlyList<SweepTransition>> SweepExpiredLeasesAsync(CancellationToken ct)
    {
        var transitions = new List<SweepTransition>();

        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var expiredIds = new List<Guid>();
        await using (var sel = Cmd(conn, """
            SELECT id FROM jobs
            WHERE status IN ('preparing', 'running', 'validating')
              AND lease_expires_at IS NOT NULL AND lease_expires_at <= now()
            ORDER BY created_at
            FOR UPDATE SKIP LOCKED
            """))
        {
            sel.Transaction = tx;
            await using var reader = await sel.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) expiredIds.Add(reader.GetGuid(0));
        }

        foreach (var jobId in expiredIds)
        {
            string status;
            int attempt, maxAttempts;
            bool cancelRequested;
            await using (var info = Cmd(conn, "SELECT status, attempt, max_attempts, cancel_requested FROM jobs WHERE id = @id"))
            {
                info.Transaction = tx;
                info.Parameters.AddWithValue("id", jobId);
                await using var reader = await info.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct)) continue;
                status = reader.GetString(0);
                attempt = reader.GetInt32(1);
                maxAttempts = reader.GetInt32(2);
                cancelRequested = reader.GetBoolean(3);
            }
            var from = Wire.From(status);

            if (!await Step(tx, jobId, from, JobStatus.Interrupted, "worker lost: lease expired")) continue;
            transitions.Add(new SweepTransition(jobId, "", from, JobStatus.Interrupted, "worker lost: lease expired"));

            if (cancelRequested)
            {
                if (await Step(tx, jobId, JobStatus.Interrupted, JobStatus.Recovering, null))
                    transitions.Add(new SweepTransition(jobId, "", JobStatus.Interrupted, JobStatus.Recovering, "recovery started"));
                if (await Step(tx, jobId, JobStatus.Recovering, JobStatus.Cancelled, null))
                    transitions.Add(new SweepTransition(jobId, "", JobStatus.Recovering, JobStatus.Cancelled, "cancelled while worker lost"));
                continue;
            }

            if (attempt >= maxAttempts)
            {
                var reason = $"recovery attempts exhausted ({maxAttempts})";
                if (await Step(tx, jobId, JobStatus.Interrupted, JobStatus.Failed, reason))
                    transitions.Add(new SweepTransition(jobId, "", JobStatus.Interrupted, JobStatus.Failed, reason));
                continue;
            }

            if (!await Step(tx, jobId, JobStatus.Interrupted, JobStatus.Recovering, null)) continue;
            transitions.Add(new SweepTransition(jobId, "", JobStatus.Interrupted, JobStatus.Recovering, "recovery started"));

            await using (var requeue = Cmd(conn, """
                UPDATE jobs SET status = 'queued',
                    resume_from_checkpoint = COALESCE((SELECT MAX(seq) FROM checkpoints WHERE job_id = @id), 0),
                    lease_worker = NULL, lease_token = NULL, lease_expires_at = NULL,
                    updated_at = now()
                WHERE id = @id AND status = 'recovering'
                """))
            {
                requeue.Transaction = tx;
                requeue.Parameters.AddWithValue("id", jobId);
                if (await requeue.ExecuteNonQueryAsync(ct) == 1)
                    transitions.Add(new SweepTransition(jobId, "", JobStatus.Recovering, JobStatus.Queued, "requeued for recovery"));
            }
        }

        await tx.CommitAsync(ct);

        // Short ids are display-only; fill them in bulk so callers can log readable lines.
        if (transitions.Count > 0)
        {
            var shorts = await LoadShortIdsAsync(conn, transitions.Select(t => t.JobId).Distinct(), CancellationToken.None);
            transitions = transitions
                .Select(t => t with { ShortId = shorts.GetValueOrDefault(t.JobId, "?") })
                .ToList();
        }
        return transitions;
    }

    private async Task<bool> Step(
        NpgsqlTransaction tx, Guid jobId, JobStatus from, JobStatus to, string? failureReason)
    {
        var conn = tx.Connection!;
        var sql = "UPDATE jobs SET status = @to" +
                  (failureReason is not null ? ", failure_reason = @reason" : "") +
                  (to.IsTerminal() ? ", finished_at = now(), lease_worker = NULL, lease_token = NULL, lease_expires_at = NULL" : "") +
                  ", updated_at = now() WHERE id = @id AND status = @from";
        await using var cmd = Cmd(conn, sql);
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("to", to.ToWire());
        cmd.Parameters.AddWithValue("id", jobId);
        cmd.Parameters.AddWithValue("from", from.ToWire());
        if (failureReason is not null) cmd.Parameters.AddWithValue("reason", failureReason);
        return await cmd.ExecuteNonQueryAsync(CancellationToken.None) == 1;
    }

    private static async Task<Dictionary<Guid, string>> LoadShortIdsAsync(
        NpgsqlConnection conn, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var map = new Dictionary<Guid, string>();
        foreach (var id in ids)
        {
            await using var cmd = Cmd(conn, "SELECT short_id FROM jobs WHERE id = @id");
            cmd.Parameters.AddWithValue("id", id);
            var shortId = await cmd.ExecuteScalarAsync(ct) as string;
            if (shortId is not null) map[id] = shortId;
        }
        return map;
    }
}
