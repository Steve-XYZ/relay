using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Relay.Server.Stores;

namespace Relay.Server.Services;

/// <summary>
/// Detects workers whose leases expired mid-flight and drives the recovery chain:
/// INTERRUPTED -> RECOVERING -> QUEUED (or FAILED after max attempts / CANCELLED on request).
/// </summary>
public sealed class RecoverySweeper : BackgroundService
{
    private readonly IJobStore _store;
    private readonly JobService _jobs;
    private readonly ILogger<RecoverySweeper> _logger;

    public RecoverySweeper(IJobStore store, JobService jobs, ILogger<RecoverySweeper> logger)
    {
        _store = store;
        _jobs = jobs;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Recovery sweeper started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var transitions = await _store.SweepExpiredLeasesAsync(stoppingToken);
                if (transitions.Count > 0)
                    await _jobs.HandleSweepTransitionsAsync(transitions, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lease sweep failed; retrying next tick");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
