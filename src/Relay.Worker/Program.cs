using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Relay.Worker;
using Relay.Worker.Sandbox;

var builder = Host.CreateApplicationBuilder(args);

// Environment overrides (compose-friendly names). Explicit env wins over appsettings.
void SetIfPresent(string key, string? value)
{
    if (!string.IsNullOrEmpty(value)) builder.Configuration[key] = value;
}

SetIfPresent("Relay:ServerUrl", Environment.GetEnvironmentVariable("RELAY_SERVER_URL"));
SetIfPresent("Relay:WorkerName", Environment.GetEnvironmentVariable("RELAY_WORKER_NAME"));
SetIfPresent("Relay:WorkspaceRoot", Environment.GetEnvironmentVariable("WORKSPACE_ROOT"));
SetIfPresent("Relay:SandboxMode", Environment.GetEnvironmentVariable("SANDBOX_MODE"));
SetIfPresent("Relay:SandboxImage", Environment.GetEnvironmentVariable("SANDBOX_IMAGE"));
SetIfPresent("Relay:CreatePr", Environment.GetEnvironmentVariable("RELAY_CREATE_PR"));

builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection("Relay"));
builder.Services.AddHttpClient("relay-worker", (sp, client) =>
{
    var opts = sp.GetRequiredService<IOptions<WorkerOptions>>().Value;
    client.BaseAddress = new Uri(opts.ServerUrl.TrimEnd('/') + "/");
});
builder.Services.AddSingleton<GitWorkspace>();
builder.Services.AddHostedService<WorkerLoop>();

SandboxAssets.EnsureExtracted();

var otlpEndpoint = builder.Configuration["Otel:OtlpEndpoint"]
    ?? Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");

if (!string.IsNullOrEmpty(otlpEndpoint))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("relay-worker", serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString()))
        .WithTracing(t => t
            .AddSource(WorkerTelemetry.SourceName)
            .AddHttpClientInstrumentation())
        .WithMetrics(m => m.AddRuntimeInstrumentation());
}

var host = builder.Build();

var git = host.Services.GetRequiredService<GitWorkspace>();
git.MilestoneEmitted += (_, m) => _ = Task.Run(async () =>
{
    try
    {
        await host.Services.GetRequiredService<WorkerLoop>().EmitMilestoneAsync(
            m.JobId, m.Name, m.Detail, CancellationToken.None);
    }
    catch { /* event delivery is best-effort */ }
});

var opts = host.Services.GetRequiredService<IOptions<WorkerOptions>>().Value;
Console.WriteLine($"relay-worker -> server {opts.ServerUrl}, sandbox mode {opts.SandboxMode}");
host.Run();
