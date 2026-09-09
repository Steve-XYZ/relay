namespace Relay.Server.Sse;

/// <summary>
/// The server-sent-events response loop shared by the job and incident timelines: replay the
/// durable log from the client's cursor, then follow live, with periodic comment frames so
/// idle streams survive proxies. Live delivery is best-effort by design — the durable log is
/// the source of truth and clients reconcile with Last-Event-ID on reconnect.
/// </summary>
public static class SseStream
{
    private static readonly TimeSpan IdlePing = TimeSpan.FromSeconds(15);

    public static long? ParseLastEventId(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Last-Event-ID", out var values)) return null;
        return long.TryParse(values.LastOrDefault(), out var id) ? id : null;
    }

    public static async Task RunAsync(
        HttpContext http, SseHub hub, Guid topic, IReadOnlyList<string> history, CancellationToken ct)
    {
        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";

        // Subscribe before flushing history so nothing emitted in between is lost.
        using var subscription = hub.Subscribe(topic, out var reader);

        foreach (var frame in history)
            await http.Response.WriteAsync(frame, ct);
        await http.Response.Body.FlushAsync(ct);

        try
        {
            while (true)
            {
                var readTask = reader.WaitToReadAsync(ct).AsTask();
                var idleTask = Task.Delay(IdlePing, ct);

                if (await Task.WhenAny(readTask, idleTask) == readTask)
                {
                    if (!await readTask) break;
                    while (reader.TryRead(out var frame))
                        await http.Response.WriteAsync(frame, ct);
                }
                else
                {
                    await http.Response.WriteAsync(": ping\n\n", ct);
                }
                await http.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected or the server is shutting down; both are normal endings.
        }
    }
}
