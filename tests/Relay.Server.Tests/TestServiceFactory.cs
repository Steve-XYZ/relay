using Relay.Server.Stores;
using Microsoft.Extensions.Logging.Abstractions;
using Relay.Server.Services;
using Relay.Server.Sse;
using Xunit;

namespace Relay.Server.Tests;

public static class TestServiceFactory
{
    public static JobService CreateJobService(InMemoryJobStore store) =>
        new(store, new SseHub(), NullLogger<JobService>.Instance);
}
