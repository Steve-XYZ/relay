using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Relay.Core;

// relay — client for the Relay reliability control plane.
//
// Control plane — what should be happening and what needs intervention:
//   relay state <project>                    resources, policies, incidents, approvals
//   relay observe <project> service/api down report what is actually happening
//   relay incidents                          what requires intervention
//   relay incident I7F2A                     one incident and its audit trail
//   relay approve <action-id>                authorize a proposed intervention
//
// Execution plane — jobs as the machinery underneath:
//   relay run "Fix issue BOS-123"            create a job and follow it live
//   relay list / get / logs / cancel
//
var server = Environment.GetEnvironmentVariable("RELAY_URL") ?? "http://localhost:8080";
var argsList = new List<string>(args);
string? Take() { if (argsList.Count == 0) return null; var v = argsList[0]; argsList.RemoveAt(0); return v; }

if (argsList.Count == 0)
{
    PrintUsage();
    return 2;
}

var command = Take()!;
switch (command)
{
    case "run": return await RunCommand();
    case "list": return await ListCommand();
    case "get": return await GetCommand(Take() ?? throw Usage("`get <id>` requires a job id"));
    case "logs": return await LogsCommand(Take() ?? throw Usage("`logs <id>` requires a job id"));
    case "cancel": return await CancelCommand(Take() ?? throw Usage("`cancel <id>` requires a job id"));
    case "projects": return await ProjectsCommand();
    case "state": return await StateCommand(Take() ?? throw Usage("`state <project>` requires a project"));
    case "observe": return await ObserveCommand();
    case "apply": return await ApplyCommand();
    case "report": return await ReportCommand();
    case "incidents": return await IncidentsCommand();
    case "incident": return await IncidentCommand(Take() ?? throw Usage("`incident <id>` requires an incident id"));
    case "approve": return await DecideCommand(Take() ?? throw Usage("`approve <action-id>` requires an action id"), approve: true);
    case "reject": return await DecideCommand(Take() ?? throw Usage("`reject <action-id>` requires an action id"), approve: false);
    case "--version" or "-v" or "version":
        Console.WriteLine("relay 0.1.0");
        return 0;
    case "-h" or "--help" or "help":
        PrintUsage();
        return 0;
    default:
        // `relay run "prompt"` shorthand
        if (File.Exists(command) || Uri.IsWellFormedUriString(command, UriKind.Absolute))
        {
            argsList.Insert(0, command);
            return await RunCommand();
        }
        Console.Error.WriteLine($"Unknown command '{command}'");
        PrintUsage();
        return 2;
}

InvalidOperationException Usage(string message)
{
    Console.Error.WriteLine(message);
    PrintUsage();
    return new InvalidOperationException();
}

void PrintUsage()
{
    Console.WriteLine("""
        relay — reliability control plane for software projects

        PROJECT STATE
          relay projects                          list projects
          relay state <project>                   what should be happening vs what is
          relay incidents [--project P] [--all]   what requires intervention
          relay incident <short-id>               one incident and its audit trail

        OBSERVE
          relay observe <project> <kind>/<key> healthy|degraded|unavailable
                        [--signal S] [--fact k=v] [--source S] [--message M]

        CONFIG
          relay apply <config.json>     declare project, resources, policies (idempotent)
          relay report <config.json>    probe resources and push observations

        DECIDE
          relay approve <action-id> [--as NAME]
          relay reject  <action-id> [--as NAME] [--reason TEXT]

        JOBS (execution machinery)
          relay run [options] "<prompt>"          create a job and follow it live
          relay list [--status queued|running|…]  list recent jobs
          relay get <short-id>                    fetch one job (JSON)
          relay logs <short-id> [-f]              show/stream job events
          relay cancel <short-id>                 request cancellation

        RUN OPTIONS
          --repo <url|path>      repository to work on (default: current dir)
          --title <text>         display title
          --agent mock|<cmd>     agent adapter (default: mock)
          --test-cmd <command>   validation command inside the sandbox
          --base-ref <ref>       base ref for the worktree/diff (default: HEAD)
          --budget tokens=N,cost=N.usd,runtime=30m   budget-aware execution limits
          --no-watch             exit after enqueueing

        ENVIRONMENT
          RELAY_URL              relay server URL (default http://localhost:8080)
        """);
}

static HttpClient Http(string server)
{
    var client = new HttpClient { BaseAddress = new Uri(server) };
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    return client;
}

async Task<Job> CreateJobAsync(HttpClient http)
{
    string? repo = null, title = null, agent = null, testCmd = null, baseRef = null;
    string? prompt = null;
    bool noWatch = false;
    long? maxTokens = null; decimal? maxCost = null; TimeSpan? maxRuntime = null;

    while (argsList.Count > 0)
    {
        var arg = Take()!;
        switch (arg)
        {
            case "--repo": repo = Take(); break;
            case "--title": title = Take(); break;
            case "--agent": agent = Take(); break;
            case "--test-cmd": testCmd = Take(); break;
            case "--base-ref": baseRef = Take(); break;
            case "-p" or "--prompt": prompt = Take(); break;
            case "--no-watch": noWatch = true; break;
            case "--budget":
                foreach (var part in (Take() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = part.Split('=', 2);
                    if (kv.Length != 2) continue;
                    switch (kv[0].Trim().ToLowerInvariant())
                    {
                        case "tokens" when long.TryParse(kv[1], out var t): maxTokens = t; break;
                        case "cost" or "usd" when decimal.TryParse(kv[1], out var c): maxCost = c; break;
                        case "runtime" or "max_runtime":
                            maxRuntime = ParseDuration(kv[1]);
                            break;
                    }
                }
                break;
            default:
                if (prompt is null && !arg.StartsWith('-')) { prompt = arg; }
                else Console.Error.WriteLine($"Ignoring unknown option '{arg}'");
                break;
        }
    }

    prompt ??= InteractivelyPrompt();
    repo ??= Directory.GetCurrentDirectory();

    var response = await http.PostAsJsonAsync("/api/jobs", new CreateJobRequest
    {
        RepoUrl = repo,
        Prompt = prompt,
        Title = title,
        Agent = agent,
        TestCommand = testCmd,
        BaseRef = baseRef,
        Budget = maxTokens is null && maxCost is null && maxRuntime is null
            ? null
            : new Budget
            {
                MaxTokens = maxTokens,
                MaxCostUsd = maxCost,
                MaxRuntimeSeconds = maxRuntime is null ? null : (long)maxRuntime.Value.TotalSeconds,
            },
    }, Json.Default);

    if (!response.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"failed to create job ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
        Environment.Exit(1);
    }

    var job = (await response.Content.ReadFromJsonAsync<Job>(Json.Default))!;
    Console.WriteLine($"job created: {job.ShortId}  ({repo})");
    if (job.Budget is { } b)
    {
        var parts = new List<string>();
        if (b.MaxTokens is { } mt) parts.Add($"tokens {mt:N0}");
        if (b.MaxCostUsd is { } mc) parts.Add($"${mc:0.00}");
        if (b.MaxRuntimeSeconds is { } mr)
        {
            var rt = TimeSpan.FromSeconds(mr);
            parts.Add(rt.TotalMinutes >= 1 ? $"{(int)rt.TotalMinutes}m" : $"{rt.TotalSeconds:F0}s");
        }
        Console.WriteLine($"budget: {string.Join(", ", parts)}");
    }
    if (noWatch) Environment.Exit(0);
    return job;
}

string InteractivelyPrompt()
{
    if (argsList.Count > 0) return Take()!;
    Console.Error.WriteLine("missing prompt text");
    Environment.Exit(2);
    return "";
}

static TimeSpan? ParseDuration(string value)
{
    value = value.Trim().ToLowerInvariant();
    if (value.EndsWith('m') && double.TryParse(value[..^1], out var minutes)) return TimeSpan.FromMinutes(minutes);
    if (value.EndsWith('s') && double.TryParse(value[..^1], out var seconds)) return TimeSpan.FromSeconds(seconds);
    if (value.EndsWith('h') && double.TryParse(value[..^1], out var hours)) return TimeSpan.FromHours(hours);
    return double.TryParse(value, out var raw) ? TimeSpan.FromSeconds(raw) : null;
}

async Task<int> RunCommand()
{
    using var http = Http(server);
    var job = await CreateJobAsync(http);
    Console.WriteLine();
    return await FollowAsync(http, job.ShortId);
}

async Task<int> ListCommand()
{
    string? status = null;
    if (argsList.Count > 0 && argsList[0] == "--status") { Take(); status = Take(); }

    using var http = Http(server);
    var jobs = await http.GetFromJsonAsync<List<Job>>($"/api/jobs?limit=25{(status is null ? "" : $"&status={status}")}", Json.Default) ?? [];

    Console.WriteLine($"{"ID",-6} {"STATUS",-12} {"ATTEMPT",7} {"TITLE"}");
    Console.WriteLine(new string('-', 72));
    foreach (var j in jobs)
    {
        var usage = $"{j.Usage.TotalTokens / 1000.0:0.#}k tok";
        Console.WriteLine($"{j.ShortId,-6} {j.Status,-12} {j.Attempt,7} {j.Title} [{usage}]");
    }
    if (jobs.Count == 0) Console.WriteLine("(no jobs yet)");
    return 0;
}

async Task<int> GetCommand(string id)
{
    using var http = Http(server);
    var response = await http.GetAsync($"/api/jobs/{id}");
    if (!response.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"job {id} not found");
        return 1;
    }
    var json = await response.Content.ReadAsStringAsync();
    Console.WriteLine(json);
    return 0;
}

async Task<int> LogsCommand(string id)
{
    var follow = argsList.RemoveAll(a => a is "-f" or "--follow") > 0;
    using var http = Http(server);
    if (!follow)
    {
        var events = await http.GetFromJsonAsync<List<JobEvent>>($"/api/jobs/{id}/events?afterSeq=0", Json.Default) ?? [];
        foreach (var e in events) Render(e, quietMilestones: false);
        return 0;
    }
    return await FollowAsync(http, id);
}

async Task<int> CancelCommand(string id)
{
    using var http = Http(server);
    var response = await http.PostAsync($"/api/jobs/{id}/cancel", content: null);
    if (response.IsSuccessStatusCode)
    {
        Console.WriteLine("cancellation requested");
        return 0;
    }
    Console.Error.WriteLine($"cancel failed ({(int)response.StatusCode})");
    return 1;
}

// ---- control plane ----

async Task<int> ProjectsCommand()
{
    using var http = Http(server);
    var projects = await http.GetFromJsonAsync<List<Project>>("/api/projects", Json.Default) ?? [];
    if (projects.Count == 0)
    {
        Console.WriteLine("(no projects yet)  create one: POST /api/projects {\"slug\":\"my-project\"}");
        return 0;
    }
    foreach (var p in projects) Console.WriteLine($"{p.Slug,-24} {p.Name}");
    return 0;
}

/// <summary>The whole picture for one project: desired state, reality, and open work.</summary>
async Task<int> StateCommand(string project)
{
    using var http = Http(server);
    var response = await http.GetAsync($"/api/projects/{project}/state");
    if (!response.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"project '{project}' not found");
        return 1;
    }
    var state = (await response.Content.ReadFromJsonAsync<ProjectState>(Json.Default))!;

    Console.WriteLine($"project  {state.Project.Slug}  ({state.Project.Name})");
    Console.WriteLine();

    Console.WriteLine("RESOURCES");
    foreach (var r in state.Resources)
        Console.WriteLine($"  {Health(r.Health)} {$"{r.Kind.ToWire()}/{r.Key}",-32} {r.HealthReason ?? "(nothing reported)"}");
    if (state.Resources.Count == 0) Console.WriteLine("  (none declared)");

    Console.WriteLine();
    Console.WriteLine("DESIRED STATE");
    foreach (var p in state.Policies)
        Console.WriteLine($"  {(p.Enabled ? " " : "~")} {p.Name,-32} {p.Target.Describe(),-22} " +
                          $"{p.Expectation.Describe()} -> {p.Remediation.Action.ToWire()}");
    if (state.Policies.Count == 0) Console.WriteLine("  (no policies)");

    Console.WriteLine();
    Console.WriteLine("NEEDS INTERVENTION");
    foreach (var i in state.ActiveIncidents) RenderIncidentLine(i);
    if (state.ActiveIncidents.Count == 0) Console.WriteLine("  (nothing diverging)");

    if (state.PendingApprovals.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("AWAITING YOUR APPROVAL");
        foreach (var a in state.PendingApprovals)
            Console.WriteLine($"  {a.Kind.ToWire(),-16} {a.Id}  {a.Reason}");
        Console.WriteLine("  approve with: relay approve <action-id>");
    }
    return 0;
}

async Task<int> ObserveCommand()
{
    var project = Take() ?? throw Usage("`observe <project> <kind>/<key> <state>` requires a project");
    var target = Take() ?? throw Usage("`observe` requires a <kind>/<key> target, e.g. service/api");
    var rawState = Take() ?? throw Usage("`observe` requires a state: healthy | degraded | unavailable");

    var slash = target.IndexOf('/');
    if (slash <= 0) throw Usage($"target '{target}' must look like service/api");

    var facts = new Dictionary<string, string>();
    string signal = Observation.DefaultSignal, source = "cli", message = "";
    DateTimeOffset? observedAt = null;
    while (argsList.Count > 0)
    {
        var arg = Take()!;
        switch (arg)
        {
            case "--signal": signal = Take() ?? signal; break;
            case "--source": source = Take() ?? source; break;
            case "--message": message = Take() ?? message; break;
            case "--observed-at":
                observedAt = DateTimeOffset.TryParse(Take(), out var at) ? at : null;
                break;
            case "--fact":
                var kv = (Take() ?? "").Split('=', 2);
                if (kv.Length == 2) facts[kv[0]] = kv[1];
                break;
            default: Console.Error.WriteLine($"Ignoring unknown option '{arg}'"); break;
        }
    }

    using var http = Http(server);
    var response = await http.PostAsJsonAsync($"/api/projects/{project}/observations", new ReportObservationRequest
    {
        Kind = EnumWire.ParseResourceKind(target[..slash]),
        Key = target[(slash + 1)..],
        Signal = signal,
        State = ObservedStateWire.Parse(rawState),
        Facts = facts.Count == 0 ? null : facts,
        Source = source,
        Message = string.IsNullOrEmpty(message) ? null : message,
        ObservedAt = observedAt,
    }, Json.Default);

    if (!response.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"observation refused ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
        return 1;
    }
    Console.WriteLine($"recorded: {target} {signal}={rawState}");
    return 0;
}

async Task<int> ApplyCommand()
{
    var path = Take() ?? throw Usage("`apply <config.json>` requires a config file");
    if (!File.Exists(path)) { Console.Error.WriteLine($"config not found: {path}"); return 1; }

    var config = Json.Deserialize<ProjectConfig>(await File.ReadAllTextAsync(path));
    if (config is null) { Console.Error.WriteLine("empty or invalid config"); return 1; }

    using var http = Http(server);

    var project = await http.PostAsJsonAsync("/api/projects", new UpsertProjectRequest
    {
        Slug = config.Project.Slug,
        Name = config.Project.Name,
    }, Json.Default);
    if (!project.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"project upsert failed: {await project.Content.ReadAsStringAsync()}");
        return 1;
    }

    // The upsert normalizes slugs ("Widget API" -> "widget-api"): use the canonical
    // slug for every follow-up request so apply works with any valid input.
    var created = await project.Content.ReadFromJsonAsync<Project>(Json.Default);
    var slug = created?.Slug ?? config.Project.Slug;

    var resources = config.Resources ?? [];
    foreach (var r in resources)
    {
        var response = await http.PostAsJsonAsync($"/api/projects/{slug}/resources",
            new UpsertResourceRequest
            {
                Kind = r.Kind,
                Key = r.Key,
                Name = r.Name,
                Attributes = r.Attributes,
            }, Json.Default);
        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"resource {r.Kind.ToWire()}/{r.Key} failed: {await response.Content.ReadAsStringAsync()}");
            return 1;
        }
    }

    var policies = config.Policies ?? [];
    foreach (var p in policies)
    {
        var response = await http.PostAsJsonAsync($"/api/projects/{slug}/policies", p, Json.Default);
        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"policy '{p.Name}' failed: {await response.Content.ReadAsStringAsync()}");
            return 1;
        }
    }

    Console.WriteLine($"applied {slug}: {resources.Count} resource(s), {policies.Count} polic(ies)");
    return 0;
}

async Task<int> ReportCommand()
{
    var path = Take() ?? throw Usage("`report <config.json>` requires a config file");
    if (!File.Exists(path)) { Console.Error.WriteLine($"config not found: {path}"); return 1; }

    var config = Json.Deserialize<ProjectConfig>(await File.ReadAllTextAsync(path));
    if (config is null) { Console.Error.WriteLine("empty or invalid config"); return 1; }

    using var http = Http(server);
    using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

    // Normalize locally so report targets the same slug the upsert created.
    string slug;
    try
    {
        slug = Project.NormalizeSlug(config.Project.Slug);
    }
    catch (DomainException ex)
    {
        Console.Error.WriteLine($"invalid project slug: {ex.Message}");
        return 1;
    }

    var reported = 0;
    var failed = 0;
    foreach (var r in config.Resources ?? [])
    {
        ObservedState state;
        string? message = null;
        string source = "report";

        // The dashboard URL may be declared as a top-level `url` or inside
        // `attributes.url` (as in integrations/overview.project.json): probe either.
        var probeUrl = !string.IsNullOrWhiteSpace(r.Url)
            ? r.Url
            : r.Attributes is not null && r.Attributes.TryGetValue("url", out var attrUrl)
                && !string.IsNullOrWhiteSpace(attrUrl) ? attrUrl : null;

        if (!string.IsNullOrWhiteSpace(probeUrl))
        {
            try
            {
                var resp = await probe.GetAsync(probeUrl);
                state = resp.IsSuccessStatusCode ? ObservedState.Healthy : ObservedState.Degraded;
                if (!resp.IsSuccessStatusCode) message = $"HTTP {(int)resp.StatusCode}";
            }
            catch (Exception ex)
            {
                state = ObservedState.Unavailable;
                message = ex.Message;
            }
        }
        else if (r.Kind == ResourceKind.Service)
        {
            // Never report a service as healthy without probing it: that would
            // conceal an outage and could satisfy verification incorrectly.
            Console.Error.WriteLine($"skip {r.Kind.ToWire()}/{r.Key}: no url to probe");
            continue;
        }
        else
        {
            // No probe target: report as healthy with no message (config-only resources).
            state = ObservedState.Healthy;
        }

        var response = await http.PostAsJsonAsync($"/api/projects/{slug}/observations",
            new ReportObservationRequest
            {
                Kind = r.Kind,
                Key = r.Key,
                Signal = r.Signal ?? Observation.DefaultSignal,
                State = state,
                Source = source,
                Message = message,
            }, Json.Default);
        if (response.IsSuccessStatusCode) reported++;
        else
        {
            failed++;
            Console.Error.WriteLine($"observation {r.Kind.ToWire()}/{r.Key} refused: {await response.Content.ReadAsStringAsync()}");
        }
    }

    Console.WriteLine($"reported {reported} observation(s) for {slug}");
    return failed > 0 ? 1 : 0;
}

async Task<int> IncidentsCommand()
{
    string? project = null;
    var all = false;
    while (argsList.Count > 0)
    {
        var arg = Take()!;
        if (arg == "--project") project = Take();
        else if (arg is "--all" or "-a") all = true;
        else Console.Error.WriteLine($"Ignoring unknown option '{arg}'");
    }

    using var http = Http(server);
    var query = $"/api/incidents?active={(all ? "false" : "true")}" + (project is null ? "" : $"&project={project}");
    var incidents = await http.GetFromJsonAsync<List<Incident>>(query, Json.Default) ?? [];

    foreach (var i in incidents) RenderIncidentLine(i);
    if (incidents.Count == 0) Console.WriteLine(all ? "(no incidents)" : "(nothing requires intervention)");
    return 0;
}

/// <summary>One incident with the evidence, the decisions and the verification behind it.</summary>
async Task<int> IncidentCommand(string id)
{
    using var http = Http(server);
    var response = await http.GetAsync($"/api/incidents/{id}");
    if (!response.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"incident {id} not found");
        return 1;
    }
    var detail = (await response.Content.ReadFromJsonAsync<IncidentDetail>(Json.Default))!;
    var incident = detail.Incident;

    Console.WriteLine($"{incident.ShortId}  {incident.Status.ToWire()}  ({incident.Severity.ToWire()})");
    Console.WriteLine($"policy    : {incident.PolicyName}");
    Console.WriteLine($"resource  : {incident.ResourceLabel}");
    Console.WriteLine($"opened    : {incident.OpenedAt:u}");
    Console.WriteLine($"observed  : {incident.Detail ?? incident.Summary}");
    Console.WriteLine($"attempts  : {incident.Attempt}/{incident.MaxAttempts}");
    if (incident.EscalationReason is { } esc) Console.WriteLine($"escalated : {esc}");
    if (incident.Resolution is { } res) Console.WriteLine($"resolved  : {res} at {incident.ResolvedAt:u}");

    if (detail.Actions.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("ACTIONS");
        foreach (var a in detail.Actions)
        {
            Console.WriteLine($"  #{a.Attempt} {a.Kind.ToWire(),-16} {a.Status.ToWire(),-10} " +
                              $"authorized by {a.ApprovedBy ?? "(pending)"}");
            if (a.ExecutionJobId is { } jobId) Console.WriteLine($"     job      : {jobId}");
            if (a.FailureReason is { } fr) Console.WriteLine($"     failure  : {fr}");
            if (a.Outcome is { } outcome) Console.WriteLine($"     verified : {outcome.ToWire()} — {a.OutcomeDetail}");
        }
    }

    Console.WriteLine();
    Console.WriteLine("TIMELINE");
    foreach (var e in detail.Events)
        Console.WriteLine($"  {e.CreatedAt:HH:mm:ss} {e.Kind,-12} {e.Message}");
    return 0;
}

async Task<int> DecideCommand(string actionId, bool approve)
{
    string who = Environment.UserName, reason = "";
    while (argsList.Count > 0)
    {
        var arg = Take()!;
        if (arg is "--as" or "--by") who = Take() ?? who;
        else if (arg == "--reason") reason = Take() ?? reason;
        else Console.Error.WriteLine($"Ignoring unknown option '{arg}'");
    }

    using var http = Http(server);
    var response = approve
        ? await http.PostAsJsonAsync($"/api/actions/{actionId}/approve",
            new ApproveActionRequest { ApprovedBy = who }, Json.Default)
        : await http.PostAsJsonAsync($"/api/actions/{actionId}/reject",
            new RejectActionRequest { RejectedBy = who, Reason = string.IsNullOrEmpty(reason) ? null : reason },
            Json.Default);

    if (!response.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"{(approve ? "approve" : "reject")} failed ({(int)response.StatusCode}): " +
                                await response.Content.ReadAsStringAsync());
        return 1;
    }
    Console.WriteLine(approve
        ? $"approved; Relay will act on the next loop tick (as {who})"
        : $"rejected; the incident is escalated to a human (as {who})");
    return 0;
}

static void RenderIncidentLine(Incident i) =>
    Console.WriteLine($"  {i.ShortId,-6} {i.Status.ToWire(),-18} {i.Severity.ToWire(),-8} " +
                      $"{i.ResourceLabel,-24} {i.Detail ?? i.Summary}");

static string Health(ResourceHealth health) => health switch
{
    ResourceHealth.Healthy => "ok  ",
    ResourceHealth.Degraded => "warn",
    ResourceHealth.Unavailable => "DOWN",
    _ => "?   ",
};

// ---- execution plane ----

/// <summary>Subscribes to the SSE stream until the job reaches a terminal state.</summary>
async Task<int> FollowAsync(HttpClient http, string id)
{
    var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

    using var stream = await http.GetStreamAsync($"/api/jobs/{id}/events?afterSeq=0");
    using var reader = new StreamReader(stream, Encoding.UTF8);

    _ = Task.Run(async () =>
    {
        Job? latest = null;
        try
        {
            latest = await http.GetFromJsonAsync<Job>($"/api/jobs/{id}", Json.Default);
        }
        catch { /* detail view is best-effort */ }

        var sawTerminal = false;
        while (await reader.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var payload = line["data: ".Length..];
            var e = Json.Deserialize<SseEventDto>(payload);
            if (e is null) continue;

            if (e.Kind == "state")
            {
                RenderState(e);
                if (e.Data?.TryGetValue("to", out var to) == true &&
                    to is "completed" or "failed" or "cancelled")
                {
                    sawTerminal = true;
                    await PrintSummaryAsync(http, id, to);
                    done.TrySetResult(to == "completed" ? 0 : 1);
                }
            }
            else
            {
                Render(e.ToEvent(), quietMilestones: sawTerminal);
            }
        }
        if (!sawTerminal) done.TrySetResult(1);
    });

    await done.Task;
    return await done.Task;

    static void RenderState(SseEventDto e)
    {
        if (e.Data is null) return;
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine($"[{e.Data.GetValueOrDefault("from")}] -> [{e.Data.GetValueOrDefault("to")}]" +
                          (e.Data.TryGetValue("reason", out var r) && r.Length > 0 ? $" {r}" : ""));
        Console.ResetColor();
    }
}


static void Render(JobEvent e, bool quietMilestones)
{
    switch (e.Kind)
    {
        case "state":
            return; // rendered separately by the follower
        case "milestone":
            if (quietMilestones) return;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("+ ");
            Console.ResetColor();
            Console.Write(e.Message);
            if (e.Data?.TryGetValue("detail", out var d) == true && d.Length > 0)
                Console.Write($"  ({d})");
            Console.WriteLine();
            return;
        case "progress":
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("* ");
            Console.ResetColor();
            Console.WriteLine(e.Message);
            return;
        case "usage":
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.Write("$ ");
            Console.ResetColor();
            Console.WriteLine(e.Message);
            return;
        case "error":
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("! " + e.Message);
            Console.ResetColor();
            return;
        default:
            Console.WriteLine("  " + e.Message);
            return;
    }
}

async Task PrintSummaryAsync(HttpClient http, string id, string terminalStatus)
{
    var job = await http.GetFromJsonAsync<Job>($"/api/jobs/{id}", Json.Default);
    if (job is null) return;

    Console.WriteLine();
    Console.WriteLine(terminalStatus == "completed" ? "== COMPLETED ==" : $"== {terminalStatus.ToUpperInvariant()} ==");
    if (terminalStatus == "completed" && job.Result is { } r)
    {
        Console.WriteLine($"branch     : {r.Branch}");
        Console.WriteLine($"changed    : {(r.ChangedFiles is { Count: > 0 } f ? string.Join(", ", f) : "(none)")}");
        if (!string.IsNullOrEmpty(r.PrUrl)) Console.WriteLine($"pull req   : {r.PrUrl}");
    }
    if (terminalStatus != "completed" && job.FailureReason is not null)
        Console.WriteLine($"failure    : {job.FailureReason}");

    Console.WriteLine($"tokens     : {job.Usage.TokensIn + job.Usage.TokensOut:N0} (in {job.Usage.TokensIn:N0} / out {job.Usage.TokensOut:N0})");
    Console.WriteLine($"cost est.  : ${job.Usage.CostUsd:0.00}" + (job.Budget?.MaxCostUsd is { } mc ? $" / ${mc:0.00}" : ""));
    Console.WriteLine($"tool calls : {job.Usage.ToolCalls}");
    Console.WriteLine($"retries    : {job.Usage.Retries}");
}

sealed file class SseEventDto
{
    public long Seq { get; set; }
    public string Kind { get; set; } = "log";
    public string Message { get; set; } = "";
    public Dictionary<string, string>? Data { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public JobEvent ToEvent() => new()
    {
        Seq = Seq, Kind = Kind, Message = Message, Data = Data, CreatedAt = CreatedAt,
    };
}

/// <summary>Declared project + resources + policies for `relay apply` / `relay report`.</summary>
sealed record ProjectConfig(
    [property: System.Text.Json.Serialization.JsonPropertyName("project")] ProjectConfigBody Project,
    [property: System.Text.Json.Serialization.JsonPropertyName("resources")] List<ResourceConfig>? Resources,
    [property: System.Text.Json.Serialization.JsonPropertyName("policies")] List<CreatePolicyRequest>? Policies);

sealed record ProjectConfigBody(
    [property: System.Text.Json.Serialization.JsonPropertyName("slug")] string Slug,
    [property: System.Text.Json.Serialization.JsonPropertyName("name")] string? Name);

sealed record ResourceConfig(
    [property: System.Text.Json.Serialization.JsonPropertyName("kind")] ResourceKind Kind,
    [property: System.Text.Json.Serialization.JsonPropertyName("key")] string Key,
    [property: System.Text.Json.Serialization.JsonPropertyName("name")] string? Name,
    [property: System.Text.Json.Serialization.JsonPropertyName("attributes")] Dictionary<string, string>? Attributes,
    [property: System.Text.Json.Serialization.JsonPropertyName("url")] string? Url,
    [property: System.Text.Json.Serialization.JsonPropertyName("signal")] string? Signal);
