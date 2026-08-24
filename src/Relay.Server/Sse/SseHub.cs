using System.Collections.Concurrent;
using System.Threading.Channels;
using Relay.Core;

namespace Relay.Server.Sse;

/// <summary>
/// In-process fan-out of server-sent event frames. Frames are also persisted by callers,
/// so this hub is best-effort live delivery only; clients replay history on reconnect
/// using Last-Event-ID.
/// </summary>
public sealed class SseHub
{
    private sealed class Subscriber(Channel<string> channel) : IDisposable
    {
        public Channel<string> Channel { get; } = channel;

        public void Dispose() => Channel.Writer.TryComplete();
    }

    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Subscriber>> _subscribers = new();

    public IDisposable Subscribe(Guid jobId, out ChannelReader<string> reader)
    {
        var subscriber = new Subscriber(Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        }));
        var subs = _subscribers.GetOrAdd(jobId, _ => new ConcurrentDictionary<Guid, Subscriber>());
        subs[Guid.NewGuid()] = subscriber;
        reader = subscriber.Channel.Reader;
        return subscriber;
    }

    public void Publish(Guid jobId, string sseFrame)
    {
        if (!_subscribers.TryGetValue(jobId, out var subs)) return;
        foreach (var sub in subs.Values)
            sub.Channel.Writer.TryWrite(sseFrame);
    }

    public void RemoveJob(Guid jobId)
    {
        if (_subscribers.TryRemove(jobId, out var subs))
            foreach (var sub in subs.Values)
                sub.Dispose();
    }
}

public static class SseFormat
{
    public static string Frame(JobEvent e)
    {
        var payload = Json.Serialize(new Dictionary<string, object?>
        {
            ["seq"] = e.Seq,
            ["kind"] = e.Kind,
            ["message"] = e.Message,
            ["data"] = e.Data,
            ["created_at"] = e.CreatedAt,
        });
        return $"id: {e.Seq}\nevent: {e.Kind}\ndata: {payload}\n\n";
    }
}
