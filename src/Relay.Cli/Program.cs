using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Relay.Core;

// relay — client for the Relay durable agent runtime.
//
//   relay run "Fix issue BOS-123"            create a job and follow it live
//   relay list                               recent jobs
//   relay get 7F2A                           one job as JSON
//   relay logs 7F2A -f                       stream events
//   relay cancel 7F2A                        request cancellation
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
        relay — durable runtime for AI coding agents

        USAGE
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
