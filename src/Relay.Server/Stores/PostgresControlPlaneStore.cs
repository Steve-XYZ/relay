using Npgsql;
using NpgsqlTypes;
using Relay.Core;
using Relay.Server.Data;

namespace Relay.Server.Stores;

/// <summary>
/// Durable control-plane store. The server is the single writer here too: nothing outside
/// this class mutates projects, resources, observations, policies, incidents or actions.
///
/// Two guarantees are load-bearing and both are enforced by Postgres rather than by a prior
/// read, so concurrent server replicas cannot break them:
///   * one active incident per (policy, resource) — the partial unique index;
///   * transitions only apply from the status the caller expected — guarded UPDATEs.
/// </summary>
public sealed class PostgresControlPlaneStore : IControlPlaneStore
{
    private readonly IDbConnectionFactory _db;

    public PostgresControlPlaneStore(IDbConnectionFactory db) => _db = db;

    private const string ProjectColumns = "id, slug, name, created_at, updated_at";

    private const string ResourceColumns =
        "id, project_id, kind, key, name, attributes, health, health_reason, last_observed_at, created_at, updated_at";

    private const string ObservationColumns =
        "id, project_id, resource_id, signal, state, facts, source, message, observed_at, received_at";

    private const string PolicyColumns =
        "id, project_id, name, target, expectation, severity, remediation, enabled, created_at, updated_at";

    private const string IncidentColumns = """
        id, short_id, project_id, policy_id, policy_name, resource_id, resource_label, status,
        severity, summary, detail, attempt, max_attempts, current_action_id, last_attempt_at,
        verify_deadline_at, verify_evidence_after, escalation_reason, resolution,
        opened_at, updated_at, resolved_at
        """;

    private const string ActionColumns = """
        id, project_id, incident_id, kind, status, attempt, params, reason, requires_approval,
        approved_by, execution_job_id, failure_reason, outcome, outcome_detail,
        created_at, started_at, finished_at
        """;

    // ---- plumbing ----

    private static NpgsqlCommand Cmd(NpgsqlConnection conn, string sql) => new(sql, conn);

    private static void AddJson<T>(NpgsqlCommand cmd, string name, T? value)
    {
        var p = cmd.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Jsonb));
        p.Value = value is null ? DBNull.Value : Json.Serialize(value);
    }

    private static DateTimeOffset ToDto(DateTime value) =>
        new(DateTime.SpecifyKind(value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime(), DateTimeKind.Utc));

    private static DateTimeOffset? ToDtoOrNull(NpgsqlDataReader r, int i) =>
        r.IsDBNull(i) ? null : ToDto(r.GetDateTime(i));

    private static Dictionary<string, string> ReadMap(NpgsqlDataReader r, int i) =>
        r.IsDBNull(i) ? [] : Json.Deserialize<Dictionary<string, string>>(r.GetString(i)) ?? [];

    private static T ReadJson<T>(NpgsqlDataReader r, int i) =>
        Json.Deserialize<T>(r.GetString(i)) ?? throw new DomainException($"malformed json in column {i}");

    private async Task<TResult?> QueryOneAsync<TResult>(
        string sql, Action<NpgsqlCommand> bind, Func<NpgsqlDataReader, TResult> map, CancellationToken ct)
        where TResult : class
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = Cmd(conn, sql);
        bind(cmd);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? map(reader) : null;
    }

    private async Task<List<TResult>> QueryManyAsync<TResult>(
        string sql, Action<NpgsqlCommand> bind, Func<NpgsqlDataReader, TResult> map, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = Cmd(conn, sql);
        bind(cmd);
        var results = new List<TResult>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) results.Add(map(reader));
        return results;
    }

    // ---- mapping ----

    private static Project MapProject(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        Slug = r.GetString(1),
        Name = r.GetString(2),
        CreatedAt = ToDto(r.GetDateTime(3)),
        UpdatedAt = ToDto(r.GetDateTime(4)),
    };

    private static Resource MapResource(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        ProjectId = r.GetGuid(1),
        Kind = EnumWire.ParseResourceKind(r.GetString(2)),
        Key = r.GetString(3),
        Name = r.GetString(4),
        Attributes = ReadMap(r, 5),
        Health = EnumWire.ParseResourceHealth(r.GetString(6)),
        HealthReason = r.IsDBNull(7) ? null : r.GetString(7),
        LastObservedAt = ToDtoOrNull(r, 8),
        CreatedAt = ToDto(r.GetDateTime(9)),
        UpdatedAt = ToDto(r.GetDateTime(10)),
    };

    private static Observation MapObservation(NpgsqlDataReader r) => new()
    {
        Id = r.GetInt64(0),
        ProjectId = r.GetGuid(1),
        ResourceId = r.GetGuid(2),
        Signal = r.GetString(3),
        State = ObservedStateWire.Parse(r.GetString(4)),
        Facts = ReadMap(r, 5),
        Source = r.GetString(6),
        Message = r.IsDBNull(7) ? null : r.GetString(7),
        ObservedAt = ToDto(r.GetDateTime(8)),
        ReceivedAt = ToDto(r.GetDateTime(9)),
    };

    private static Policy MapPolicy(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        ProjectId = r.GetGuid(1),
        Name = r.GetString(2),
        Target = ReadJson<ResourceSelector>(r, 3),
        Expectation = ReadJson<Expectation>(r, 4),
        Severity = PolicyWire.ParseSeverity(r.GetString(5)),
        Remediation = ReadJson<Remediation>(r, 6),
        Enabled = r.GetBoolean(7),
        CreatedAt = ToDto(r.GetDateTime(8)),
        UpdatedAt = ToDto(r.GetDateTime(9)),
    };

    private static Incident MapIncident(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        ShortId = r.GetString(1),
        ProjectId = r.GetGuid(2),
        PolicyId = r.GetGuid(3),
        PolicyName = r.GetString(4),
        ResourceId = r.GetGuid(5),
        ResourceLabel = r.GetString(6),
        Status = IncidentWire.ParseIncidentStatus(r.GetString(7)),
        Severity = PolicyWire.ParseSeverity(r.GetString(8)),
        Summary = r.GetString(9),
        Detail = r.IsDBNull(10) ? null : r.GetString(10),
        Attempt = r.GetInt32(11),
        MaxAttempts = r.GetInt32(12),
        CurrentActionId = r.IsDBNull(13) ? null : r.GetGuid(13),
        LastAttemptAt = ToDtoOrNull(r, 14),
        VerifyDeadlineAt = ToDtoOrNull(r, 15),
        VerifyEvidenceAfter = ToDtoOrNull(r, 16),
        EscalationReason = r.IsDBNull(17) ? null : r.GetString(17),
        Resolution = r.IsDBNull(18) ? null : r.GetString(18),
        OpenedAt = ToDto(r.GetDateTime(19)),
        UpdatedAt = ToDto(r.GetDateTime(20)),
        ResolvedAt = ToDtoOrNull(r, 21),
    };

    private static RemediationAction MapAction(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        ProjectId = r.GetGuid(1),
        IncidentId = r.GetGuid(2),
        Kind = ActionWire.ParseActionKind(r.GetString(3)),
        Status = ActionWire.ParseActionStatus(r.GetString(4)),
        Attempt = r.GetInt32(5),
        Params = ReadMap(r, 6),
        Reason = r.GetString(7),
        RequiresApproval = r.GetBoolean(8),
        ApprovedBy = r.IsDBNull(9) ? null : r.GetString(9),
        ExecutionJobId = r.IsDBNull(10) ? null : r.GetGuid(10),
        FailureReason = r.IsDBNull(11) ? null : r.GetString(11),
        Outcome = r.IsDBNull(12) ? null : ActionWire.ParseActionOutcome(r.GetString(12)),
        OutcomeDetail = r.IsDBNull(13) ? null : r.GetString(13),
        CreatedAt = ToDto(r.GetDateTime(14)),
        StartedAt = ToDtoOrNull(r, 15),
        FinishedAt = ToDtoOrNull(r, 16),
    };

    private static IncidentEvent MapIncidentEvent(NpgsqlDataReader r) => new()
    {
        Seq = r.GetInt64(0),
        Kind = r.GetString(1),
        Message = r.GetString(2),
        Data = r.IsDBNull(3) ? null : Json.Deserialize<Dictionary<string, string>>(r.GetString(3)),
        CreatedAt = ToDto(r.GetDateTime(4)),
    };

    // ---- projects ----

    public async Task<Project> UpsertProjectAsync(UpsertProjectRequest request, CancellationToken ct)
    {
        var slug = Project.NormalizeSlug(request.Slug);
        var project = await QueryOneAsync($"""
            INSERT INTO projects (id, slug, name) VALUES (@id, @slug, @name)
            ON CONFLICT (slug) DO UPDATE
                SET name = COALESCE(NULLIF(EXCLUDED.name, ''), projects.name), updated_at = now()
            RETURNING {ProjectColumns}
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("id", Guid.NewGuid());
                cmd.Parameters.AddWithValue("slug", slug);
                cmd.Parameters.AddWithValue("name", request.Name ?? slug);
            }, MapProject, ct);
        return project ?? throw new InvalidOperationException("project upsert returned no row");
    }

    public Task<Project?> ResolveProjectAsync(string idOrSlug, CancellationToken ct) =>
        QueryOneAsync($"""
            SELECT {ProjectColumns} FROM projects
            WHERE (@isGuid AND id = @id) OR lower(slug) = lower(@slug) LIMIT 1
            """,
            cmd =>
            {
                var isGuid = Guid.TryParse(idOrSlug, out var id);
                cmd.Parameters.AddWithValue("isGuid", isGuid);
                cmd.Parameters.AddWithValue("id", isGuid ? id : Guid.Empty);
                cmd.Parameters.AddWithValue("slug", idOrSlug);
            }, MapProject, ct);

    public async Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct) =>
        await QueryManyAsync($"SELECT {ProjectColumns} FROM projects ORDER BY slug", _ => { }, MapProject, ct);

    // ---- resources ----

    public async Task<Resource> UpsertResourceAsync(Guid projectId, UpsertResourceRequest request, CancellationToken ct)
    {
        var key = Resource.NormalizeKey(request.Key);
        // Attributes merge rather than replace: a reporter that knows one field must not wipe
        // configuration another reporter supplied.
        var resource = await QueryOneAsync($"""
            INSERT INTO resources (id, project_id, kind, key, name, attributes)
            VALUES (@id, @projectId, @kind, @key, @name, @attributes)
            ON CONFLICT (project_id, kind, key) DO UPDATE SET
                name = COALESCE(NULLIF(EXCLUDED.name, ''), resources.name),
                attributes = resources.attributes || EXCLUDED.attributes,
                updated_at = now()
            RETURNING {ResourceColumns}
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("id", Guid.NewGuid());
                cmd.Parameters.AddWithValue("projectId", projectId);
                cmd.Parameters.AddWithValue("kind", request.Kind.ToWire());
                cmd.Parameters.AddWithValue("key", key);
                cmd.Parameters.AddWithValue("name", request.Name ?? key);
                AddJson(cmd, "attributes", request.Attributes ?? []);
            }, MapResource, ct);
        return resource ?? throw new InvalidOperationException("resource upsert returned no row");
    }

    public Task<Resource?> FindResourceAsync(Guid projectId, ResourceKind kind, string key, CancellationToken ct) =>
        QueryOneAsync($"""
            SELECT {ResourceColumns} FROM resources
            WHERE project_id = @projectId AND kind = @kind AND key = @key
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("projectId", projectId);
                cmd.Parameters.AddWithValue("kind", kind.ToWire());
                cmd.Parameters.AddWithValue("key", key);
            }, MapResource, ct);

    public Task<Resource?> GetResourceAsync(Guid resourceId, CancellationToken ct) =>
        QueryOneAsync($"SELECT {ResourceColumns} FROM resources WHERE id = @id",
            cmd => cmd.Parameters.AddWithValue("id", resourceId), MapResource, ct);

    public async Task<IReadOnlyList<Resource>> ListResourcesAsync(Guid projectId, CancellationToken ct) =>
        await QueryManyAsync($"SELECT {ResourceColumns} FROM resources WHERE project_id = @p ORDER BY kind, key",
            cmd => cmd.Parameters.AddWithValue("p", projectId), MapResource, ct);

    // ---- observations ----

    public async Task<Observation> RecordObservationAsync(
        Resource resource, ReportObservationRequest request, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        Observation observation;
        await using (var insert = Cmd(conn, $"""
            INSERT INTO observations (project_id, resource_id, signal, state, facts, source, message, observed_at, received_at)
            VALUES (@projectId, @resourceId, @signal, @state, @facts, @source, @message,
                    LEAST(COALESCE(@observedAt, now()), now()), now())
            RETURNING {ObservationColumns}
            """))
        {
            insert.Transaction = tx;
            insert.Parameters.AddWithValue("projectId", resource.ProjectId);
            insert.Parameters.AddWithValue("resourceId", resource.Id);
            insert.Parameters.AddWithValue("signal",
                string.IsNullOrWhiteSpace(request.Signal) ? Observation.DefaultSignal : request.Signal);
            insert.Parameters.AddWithValue("state", request.State.ToWire());
            AddJson(insert, "facts", request.Facts ?? []);
            insert.Parameters.AddWithValue("source",
                string.IsNullOrWhiteSpace(request.Source) ? "unknown" : request.Source);
            insert.Parameters.AddWithValue("message", (object?)request.Message ?? DBNull.Value);
            insert.Parameters.AddWithValue("observedAt", (object?)request.ObservedAt ?? DBNull.Value);

            await using var reader = await insert.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            observation = MapObservation(reader);
        }

        // Derived health is the worst state across the latest observation of every signal, so
        // a green health check cannot mask a red queue-depth signal.
        await using (var derive = Cmd(conn, """
            WITH latest AS (
                SELECT DISTINCT ON (signal) signal, state, source, received_at, observed_at
                FROM observations WHERE resource_id = @id
                ORDER BY signal, received_at DESC, id DESC
            ), worst AS (
                SELECT signal, state, source,
                       CASE state WHEN 'unavailable' THEN 3 WHEN 'degraded' THEN 2 ELSE 1 END AS rank
                FROM latest ORDER BY rank DESC LIMIT 1
            )
            UPDATE resources SET
                health = CASE (SELECT state FROM worst)
                             WHEN 'healthy' THEN 'healthy'
                             WHEN 'degraded' THEN 'degraded'
                             ELSE 'unavailable' END,
                health_reason = (SELECT signal || ': ' || state || ' per ' || source FROM worst),
                last_observed_at = (SELECT MAX(observed_at) FROM latest),
                updated_at = now()
            WHERE id = @id
            """))
        {
            derive.Transaction = tx;
            derive.Parameters.AddWithValue("id", resource.Id);
            await derive.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return observation;
    }

    public async Task<ObservationSnapshot> SnapshotAsync(Guid resourceId, CancellationToken ct)
    {
        var latest = await QueryManyAsync($"""
            SELECT DISTINCT ON (signal) {ObservationColumns} FROM observations
            WHERE resource_id = @id
            ORDER BY signal, received_at DESC, id DESC
            """,
            cmd => cmd.Parameters.AddWithValue("id", resourceId), MapObservation, ct);
        return new ObservationSnapshot(latest);
    }

    public async Task<IReadOnlyList<Observation>> ListObservationsAsync(Guid resourceId, int limit, CancellationToken ct) =>
        await QueryManyAsync($"""
            SELECT {ObservationColumns} FROM observations WHERE resource_id = @id
            ORDER BY id DESC LIMIT @limit
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("id", resourceId);
                cmd.Parameters.AddWithValue("limit", limit);
            }, MapObservation, ct);

    // ---- policies ----

    public async Task<Policy> CreatePolicyAsync(Guid projectId, CreatePolicyRequest request, CancellationToken ct)
    {
        var expectation = request.Expectation.Validated();
        var remediation = request.Remediation.Validated();

        // Idempotent by (project_id, name): re-applying a config updates in place so
        // `relay apply` can be run repeatedly without duplicating policies.
        var policy = await QueryOneAsync($"""
            INSERT INTO policies (id, project_id, name, target, expectation, severity, remediation, enabled)
            VALUES (@id, @projectId, @name, @target, @expectation, @severity, @remediation, @enabled)
            ON CONFLICT (project_id, name) DO UPDATE SET
                target = EXCLUDED.target,
                expectation = EXCLUDED.expectation,
                severity = EXCLUDED.severity,
                remediation = EXCLUDED.remediation,
                enabled = EXCLUDED.enabled,
                updated_at = now()
            RETURNING {PolicyColumns}
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("id", Guid.NewGuid());
                cmd.Parameters.AddWithValue("projectId", projectId);
                cmd.Parameters.AddWithValue("name", request.Name);
                AddJson(cmd, "target", request.Target);
                AddJson(cmd, "expectation", expectation);
                cmd.Parameters.AddWithValue("severity", request.Severity.ToWire());
                AddJson(cmd, "remediation", remediation);
                cmd.Parameters.AddWithValue("enabled", request.Enabled);
            }, MapPolicy, ct);
        return policy ?? throw new InvalidOperationException("policy upsert returned no row");
    }

    public Task<Policy?> GetPolicyAsync(Guid policyId, CancellationToken ct) =>
        QueryOneAsync($"SELECT {PolicyColumns} FROM policies WHERE id = @id",
            cmd => cmd.Parameters.AddWithValue("id", policyId), MapPolicy, ct);

    public async Task<IReadOnlyList<Policy>> ListPoliciesAsync(Guid projectId, bool enabledOnly, CancellationToken ct) =>
        await QueryManyAsync($"""
            SELECT {PolicyColumns} FROM policies
            WHERE project_id = @p AND (@allowDisabled OR enabled)
            ORDER BY name
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("p", projectId);
                cmd.Parameters.AddWithValue("allowDisabled", !enabledOnly);
            }, MapPolicy, ct);

    public Task<Policy?> SetPolicyEnabledAsync(Guid policyId, bool enabled, CancellationToken ct) =>
        QueryOneAsync($"""
            UPDATE policies SET enabled = @enabled, updated_at = now() WHERE id = @id
            RETURNING {PolicyColumns}
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("id", policyId);
                cmd.Parameters.AddWithValue("enabled", enabled);
            }, MapPolicy, ct);

    // ---- incidents ----

    public async Task<Incident?> OpenIncidentAsync(NewIncident spec, CancellationToken ct)
    {
        // The partial unique index is the arbiter: whoever loses the race to open an incident
        // for this (policy, resource) gets a unique violation and reports "already handled".
        const string activeIncidentIndex = "incidents_one_active_idx";

        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                return await QueryOneAsync($"""
                    INSERT INTO incidents (
                        id, short_id, project_id, policy_id, policy_name, resource_id, resource_label,
                        status, severity, summary, detail, max_attempts)
                    VALUES (@id, @shortId, @projectId, @policyId, @policyName, @resourceId, @resourceLabel,
                            'open', @severity, @summary, @summary, @maxAttempts)
                    RETURNING {IncidentColumns}
                    """,
                    cmd =>
                    {
                        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
                        cmd.Parameters.AddWithValue("shortId", "I" + ShortId.Generate());
                        cmd.Parameters.AddWithValue("projectId", spec.ProjectId);
                        cmd.Parameters.AddWithValue("policyId", spec.Policy.Id);
                        cmd.Parameters.AddWithValue("policyName", spec.Policy.Name);
                        cmd.Parameters.AddWithValue("resourceId", spec.Resource.Id);
                        cmd.Parameters.AddWithValue("resourceLabel", spec.Resource.Describe());
                        cmd.Parameters.AddWithValue("severity", spec.Policy.Severity.ToWire());
                        cmd.Parameters.AddWithValue("summary", spec.Summary);
                        cmd.Parameters.AddWithValue("maxAttempts", spec.Policy.Remediation.MaxAttempts);
                    }, MapIncident, ct);
            }
            catch (PostgresException ex)
                when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == activeIncidentIndex)
            {
                return null;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // Short-id collision: draw another one and retry.
            }
        }
        throw new InvalidOperationException("Could not generate a unique incident short id");
    }

    public Task<Incident?> FindActiveIncidentAsync(Guid policyId, Guid resourceId, CancellationToken ct) =>
        QueryOneAsync($"""
            SELECT {IncidentColumns} FROM incidents
            WHERE policy_id = @policyId AND resource_id = @resourceId AND status <> 'resolved'
            LIMIT 1
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("policyId", policyId);
                cmd.Parameters.AddWithValue("resourceId", resourceId);
            }, MapIncident, ct);

    public Task<Incident?> ResolveIncidentRefAsync(string idOrShortId, CancellationToken ct) =>
        QueryOneAsync($"""
            SELECT {IncidentColumns} FROM incidents
            WHERE (@isGuid AND id = @id) OR lower(short_id) = lower(@shortId) LIMIT 1
            """,
            cmd =>
            {
                var isGuid = Guid.TryParse(idOrShortId, out var id);
                cmd.Parameters.AddWithValue("isGuid", isGuid);
                cmd.Parameters.AddWithValue("id", isGuid ? id : Guid.Empty);
                cmd.Parameters.AddWithValue("shortId", idOrShortId);
            }, MapIncident, ct);

    public async Task<IReadOnlyList<Incident>> ListIncidentsAsync(
        Guid? projectId, bool activeOnly, int limit, CancellationToken ct) =>
        await QueryManyAsync($"""
            SELECT {IncidentColumns} FROM incidents
            WHERE (@anyProject OR project_id = @projectId)
              AND (@anyStatus OR status <> 'resolved')
            ORDER BY opened_at DESC LIMIT @limit
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("anyProject", projectId is null);
                cmd.Parameters.AddWithValue("projectId", projectId ?? Guid.Empty);
                cmd.Parameters.AddWithValue("anyStatus", !activeOnly);
                cmd.Parameters.AddWithValue("limit", limit);
            }, MapIncident, ct);

    public async Task<IReadOnlyList<Incident>> ListIncidentsToAdvanceAsync(int limit, CancellationToken ct) =>
        await QueryManyAsync($"""
            SELECT {IncidentColumns} FROM incidents
            WHERE status <> 'resolved'
            ORDER BY updated_at, opened_at LIMIT @limit
            """,
            cmd => cmd.Parameters.AddWithValue("limit", limit), MapIncident, ct);

    public Task<Incident?> RecordVerdictAsync(Guid incidentId, string detail, CancellationToken ct) =>
        QueryOneAsync($"""
            UPDATE incidents SET detail = @detail, updated_at = now() WHERE id = @id
            RETURNING {IncidentColumns}
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("id", incidentId);
                cmd.Parameters.AddWithValue("detail", detail);
            }, MapIncident, ct);

    /// <summary>
    /// Guarded transition: the UPDATE only matches while the row is still in the expected
    /// status, so a replica whose view went stale writes nothing and gets null back.
    /// </summary>
    private async Task<Incident?> TransitionIncidentAsync(
        Guid incidentId, IncidentStatus from, IncidentStatus to, string setClause,
        Action<NpgsqlCommand> bind, CancellationToken ct)
    {
        if (!IncidentStateMachine.CanTransition(from, to)) return null;

        return await QueryOneAsync($"""
            UPDATE incidents SET status = @to, updated_at = now(){(setClause.Length > 0 ? ", " + setClause : "")}
            WHERE id = @id AND status = @from
            RETURNING {IncidentColumns}
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("id", incidentId);
                cmd.Parameters.AddWithValue("from", from.ToWire());
                cmd.Parameters.AddWithValue("to", to.ToWire());
                bind(cmd);
            }, MapIncident, ct);
    }

    public Task<Incident?> AwaitApprovalAsync(Guid incidentId, IncidentStatus from, Guid actionId, CancellationToken ct) =>
        TransitionIncidentAsync(incidentId, from, IncidentStatus.AwaitingApproval,
            "current_action_id = @actionId",
            cmd => cmd.Parameters.AddWithValue("actionId", actionId), ct);

    public Task<Incident?> BeginActingAsync(
        Guid incidentId, IncidentStatus from, Guid actionId, int attempt, CancellationToken ct) =>
        TransitionIncidentAsync(incidentId, from, IncidentStatus.Acting,
            """
            current_action_id = @actionId, attempt = @attempt, last_attempt_at = now(),
            verify_deadline_at = NULL, verify_evidence_after = NULL
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("actionId", actionId);
                cmd.Parameters.AddWithValue("attempt", attempt);
            }, ct);

    public Task<Incident?> BeginVerifyingAsync(
        Guid incidentId, Guid actionId, DateTimeOffset evidenceAfter, DateTimeOffset deadline, CancellationToken ct) =>
        TransitionIncidentAsync(incidentId, IncidentStatus.Acting, IncidentStatus.Verifying,
            "current_action_id = @actionId, verify_evidence_after = @evidenceAfter, verify_deadline_at = @deadline",
            cmd =>
            {
                cmd.Parameters.AddWithValue("actionId", actionId);
                cmd.Parameters.AddWithValue("evidenceAfter", evidenceAfter);
                cmd.Parameters.AddWithValue("deadline", deadline);
            }, ct);

    public Task<Incident?> ReopenIncidentAsync(Guid incidentId, IncidentStatus from, string detail, CancellationToken ct) =>
        TransitionIncidentAsync(incidentId, from, IncidentStatus.Open,
            "detail = @detail, current_action_id = NULL, verify_deadline_at = NULL, verify_evidence_after = NULL",
            cmd => cmd.Parameters.AddWithValue("detail", detail), ct);

    public Task<Incident?> ResolveIncidentAsync(
        Guid incidentId, IncidentStatus from, string resolution, string detail, CancellationToken ct) =>
        TransitionIncidentAsync(incidentId, from, IncidentStatus.Resolved,
            """
            detail = @detail, resolution = @resolution, resolved_at = now(),
            verify_deadline_at = NULL, verify_evidence_after = NULL, escalation_reason = NULL
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("detail", detail);
                cmd.Parameters.AddWithValue("resolution", resolution);
            }, ct);

    public Task<Incident?> EscalateIncidentAsync(Guid incidentId, IncidentStatus from, string reason, CancellationToken ct) =>
        TransitionIncidentAsync(incidentId, from, IncidentStatus.Escalated,
            "escalation_reason = @reason, verify_deadline_at = NULL, verify_evidence_after = NULL",
            cmd => cmd.Parameters.AddWithValue("reason", reason), ct);

    // ---- incident audit log ----

    public async Task<IReadOnlyList<IncidentEvent>> AppendIncidentEventsAsync(
        Guid incidentId, IReadOnlyList<IncidentEvent> events, CancellationToken ct)
    {
        if (events.Count == 0) return [];

        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Serialize appends per incident so concurrent emitters cannot compute the same seq.
        await using (var gate = Cmd(conn, "SELECT id FROM incidents WHERE id = @id FOR UPDATE"))
        {
            gate.Transaction = tx;
            gate.Parameters.AddWithValue("id", incidentId);
            if (await gate.ExecuteScalarAsync(ct) is null) return [];
        }

        var persisted = new List<IncidentEvent>(events.Count);
        foreach (var e in events)
        {
            await using var cmd = Cmd(conn, """
                INSERT INTO incident_events (incident_id, seq, kind, message, data)
                VALUES (@id, (SELECT COALESCE(MAX(seq), 0) + 1 FROM incident_events WHERE incident_id = @id),
                        @kind, @message, @data)
                RETURNING seq, kind, message, data, created_at
                """);
            cmd.Transaction = tx;
            cmd.Parameters.AddWithValue("id", incidentId);
            cmd.Parameters.AddWithValue("kind", e.Kind);
            cmd.Parameters.AddWithValue("message", e.Message);
            AddJson(cmd, "data", e.Data);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            persisted.Add(MapIncidentEvent(reader));
        }

        await tx.CommitAsync(ct);
        return persisted;
    }

    public async Task<IReadOnlyList<IncidentEvent>> GetIncidentEventsAsync(
        Guid incidentId, long afterSeq, int limit, CancellationToken ct) =>
        await QueryManyAsync("""
            SELECT seq, kind, message, data, created_at FROM incident_events
            WHERE incident_id = @id AND seq > @afterSeq
            ORDER BY seq LIMIT @limit
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("id", incidentId);
                cmd.Parameters.AddWithValue("afterSeq", afterSeq);
                cmd.Parameters.AddWithValue("limit", limit);
            }, MapIncidentEvent, ct);

    // ---- actions ----

    public async Task<RemediationAction> CreateActionAsync(NewAction spec, CancellationToken ct)
    {
        var action = await QueryOneAsync($"""
            INSERT INTO actions (id, project_id, incident_id, kind, status, attempt, params, reason, requires_approval)
            VALUES (@id, @projectId, @incidentId, @kind, 'proposed', @attempt, @params, @reason, @requiresApproval)
            RETURNING {ActionColumns}
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("id", Guid.NewGuid());
                cmd.Parameters.AddWithValue("projectId", spec.ProjectId);
                cmd.Parameters.AddWithValue("incidentId", spec.IncidentId);
                cmd.Parameters.AddWithValue("kind", spec.Kind.ToWire());
                cmd.Parameters.AddWithValue("attempt", spec.Attempt);
                AddJson(cmd, "params", spec.Params);
                cmd.Parameters.AddWithValue("reason", spec.Reason);
                cmd.Parameters.AddWithValue("requiresApproval", spec.RequiresApproval);
            }, MapAction, ct);
        return action ?? throw new InvalidOperationException("action insert returned no row");
    }

    public Task<RemediationAction?> GetActionAsync(Guid actionId, CancellationToken ct) =>
        QueryOneAsync($"SELECT {ActionColumns} FROM actions WHERE id = @id",
            cmd => cmd.Parameters.AddWithValue("id", actionId), MapAction, ct);

    public async Task<IReadOnlyList<RemediationAction>> ListActionsAsync(Guid incidentId, CancellationToken ct) =>
        await QueryManyAsync($"""
            SELECT {ActionColumns} FROM actions WHERE incident_id = @id ORDER BY attempt, created_at
            """,
            cmd => cmd.Parameters.AddWithValue("id", incidentId), MapAction, ct);

    public async Task<IReadOnlyList<RemediationAction>> ListPendingApprovalsAsync(Guid projectId, CancellationToken ct) =>
        await QueryManyAsync($"""
            SELECT {ActionColumns} FROM actions
            WHERE project_id = @p AND status = 'proposed' AND requires_approval
            ORDER BY created_at
            """,
            cmd => cmd.Parameters.AddWithValue("p", projectId), MapAction, ct);

    private async Task<RemediationAction?> TransitionActionAsync(
        Guid actionId, ActionStatus from, ActionStatus to, string setClause,
        Action<NpgsqlCommand> bind, CancellationToken ct)
    {
        if (!ActionStateMachine.CanTransition(from, to)) return null;

        return await QueryOneAsync($"""
            UPDATE actions SET status = @to{(setClause.Length > 0 ? ", " + setClause : "")}
            WHERE id = @id AND status = @from
            RETURNING {ActionColumns}
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("id", actionId);
                cmd.Parameters.AddWithValue("from", from.ToWire());
                cmd.Parameters.AddWithValue("to", to.ToWire());
                bind(cmd);
            }, MapAction, ct);
    }

    public Task<RemediationAction?> ApproveActionAsync(Guid actionId, string approvedBy, CancellationToken ct) =>
        TransitionActionAsync(actionId, ActionStatus.Proposed, ActionStatus.Approved,
            "approved_by = @approvedBy",
            cmd => cmd.Parameters.AddWithValue("approvedBy", approvedBy), ct);

    public Task<RemediationAction?> RejectActionAsync(
        Guid actionId, string rejectedBy, string? reason, CancellationToken ct) =>
        TransitionActionAsync(actionId, ActionStatus.Proposed, ActionStatus.Rejected,
            "failure_reason = @reason, finished_at = now()",
            cmd => cmd.Parameters.AddWithValue("reason", reason ?? $"rejected by {rejectedBy}"), ct);

    public Task<RemediationAction?> SupersedeActionAsync(Guid actionId, string reason, CancellationToken ct) =>
        QueryOneAsync($"""
            UPDATE actions SET status = 'rejected', failure_reason = @reason, finished_at = now()
            WHERE id = @id AND status IN ('proposed', 'approved')
            RETURNING {ActionColumns}
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("id", actionId);
                cmd.Parameters.AddWithValue("reason", reason);
            }, MapAction, ct);

    public Task<RemediationAction?> BeginExecutionAsync(Guid actionId, Guid? executionJobId, CancellationToken ct) =>
        TransitionActionAsync(actionId, ActionStatus.Approved, ActionStatus.Executing,
            "execution_job_id = @jobId, started_at = now()",
            cmd => cmd.Parameters.AddWithValue("jobId", (object?)executionJobId ?? DBNull.Value), ct);

    public Task<RemediationAction?> SettleActionAsync(
        Guid actionId, bool succeeded, string? failureReason, CancellationToken ct) =>
        TransitionActionAsync(actionId, ActionStatus.Executing,
            succeeded ? ActionStatus.Succeeded : ActionStatus.Failed,
            "failure_reason = @reason, finished_at = now()",
            cmd => cmd.Parameters.AddWithValue("reason", (object?)failureReason ?? DBNull.Value), ct);

    public Task<RemediationAction?> RecordOutcomeAsync(
        Guid actionId, ActionOutcome outcome, string detail, CancellationToken ct) =>
        QueryOneAsync($"""
            UPDATE actions SET outcome = @outcome, outcome_detail = @detail WHERE id = @id
            RETURNING {ActionColumns}
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("id", actionId);
                cmd.Parameters.AddWithValue("outcome", outcome.ToWire());
                cmd.Parameters.AddWithValue("detail", detail);
            }, MapAction, ct);
}
