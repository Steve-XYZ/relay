using Relay.Server.Api;
using Relay.Server.Data;
using Relay.Server.Sse;
using Relay.Server.Services;
using Relay.Server.Stores;
using Relay.Server.Telemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

var storageMode = builder.Configuration["Storage:Mode"] ?? "postgres";
var otlpEndpoint = builder.Configuration["Otel:OtlpEndpoint"];

if (!string.IsNullOrEmpty(otlpEndpoint))
{
    // OTLP takes over logging; otherwise console logging stays active for operators.
    builder.Logging.ClearProviders();
    builder.Logging.AddOpenTelemetry(o => o
        .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(RelayMetrics.SourceName))
        .AddOtlpExporter());
}

// Minimal APIs bind with their own serializer instance, so the shared contract has to be
// applied to it explicitly or request bodies and stored JSON drift apart.
builder.Services.ConfigureHttpJsonOptions(o => Relay.Core.Json.Configure(o.SerializerOptions));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SseHub>();

// Execution plane: jobs, leases, crash recovery.
builder.Services.AddSingleton<JobService>();
builder.Services.AddHostedService<RecoverySweeper>();

// Control plane: projects, resources, observations, policies, incidents, actions.
builder.Services.AddSingleton<ProjectService>();
builder.Services.AddSingleton<ActionDispatcher>();
builder.Services.Configure<ReliabilityLoopOptions>(builder.Configuration.GetSection("ReliabilityLoop"));
builder.Services.AddHostedService<ReliabilityLoop>();

if (storageMode == "memory")
{
    builder.Services.AddSingleton<IJobStore>(sp =>
        new InMemoryJobStore(sp.GetRequiredService<TimeProvider>()));
    builder.Services.AddSingleton<IControlPlaneStore>(sp =>
        new InMemoryControlPlaneStore(sp.GetRequiredService<TimeProvider>()));
}
else
{
    var connectionString = builder.Configuration.GetConnectionString("relay")
        ?? throw new InvalidOperationException("ConnectionStrings:relay is required for postgres storage");
    builder.Services.AddSingleton<IDbConnectionFactory>(new NpgsqlConnectionFactory(connectionString));
    builder.Services.AddSingleton<IJobStore, PostgresJobStore>();
    builder.Services.AddSingleton<IControlPlaneStore, PostgresControlPlaneStore>();
}

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("relay-server", serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString()))
    .WithTracing(t => t
        .AddSource(RelayMetrics.ActivitySource.Name)
        .AddHttpClientInstrumentation()
        .AddAspNetCoreInstrumentation())
    .WithMetrics(m => m
        .AddMeter(RelayMetrics.MeterName)
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation());

var app = builder.Build();

app.UseCors();

if (storageMode != "memory")
{
    await SchemaBootstrapper.ApplyAsync(
        app.Services.GetRequiredService<IDbConnectionFactory>(),
        app.Lifetime.ApplicationStopping);
}

app.MapPublicApi();
app.MapProjectsApi();
app.MapInternalApi();

app.Run();

public partial class Program;
