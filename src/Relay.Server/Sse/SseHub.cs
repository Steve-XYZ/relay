using System.Collections.Concurrent;
using System.Threading.Channels;
using Relay.Core;

namespace Relay.Server.Sse;

/// <summary>
/// In-process fan-out of server-sent event frames, keyed by topic — a job id or an incident
/// id. Frames are also persisted by callers, so this hub is best-effort live delivery only;
/// clients replay history on reconnect using Last-Event-ID.
/// </summary>
public sealed class SseHub
{
    private sealed class Subscriber : IDisposable
    {
        private readonly SseHub _hub;
        private readonly Guid _topic;
        private readonly Guid _id;

        public Subscriber(SseHub hub, Guid topic, Guid id)
        {
            _hub = hub;
            _topic = topic;
            _id = id;
            Channel = System.Threading.Channels.Channel.CreateUnbounded<string>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        }

        public Channel<string> Channel { get; }

        public void Dispose()
        {
            Channel.Writer.TryComplete();
            _hub.Unsubscribe(_topic, _id);
        }
    }

    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Subscriber>> _subscribers = new();

    public IDisposable Subscribe(Guid topic, out ChannelReader<string> reader)
    {
        var id = Guid.NewGuid();
        var subscriber = new Subscriber(this, topic, id);
        _subscribers.GetOrAdd(topic, _ => new ConcurrentDictionary<Guid, Subscriber>())[id] = subscriber;
        reader = subscriber.Channel.Reader;
        return subscriber;
    }

    /// <summary>
    /// Drops the subscriber and the topic bucket once it empties. Without this the hub
    /// accumulates one dead entry per stream for the process lifetime.
    /// </summary>
    private void Unsubscribe(Guid topic, Guid id)
    {
        if (!_subscribers.TryGetValue(topic, out var subs)) return;
        subs.TryRemove(id, out _);
        if (subs.IsEmpty) _subscribers.TryRemove(topic, out _);
    }

    public void Publish(Guid topic, string sseFrame)
    {
        if (!_subscribers.TryGetValue(topic, out var subs)) return;
        foreach (var sub in subs.Values)
            sub.Channel.Writer.TryWrite(sseFrame);
    }
}

public static class SseFormat
{
    public static string Frame(JobEvent e) => Frame(e.Seq, e.Kind, e.Message, e.Data, e.CreatedAt);

    public static string Frame(IncidentEvent e) => Frame(e.Seq, e.Kind, e.Message, e.Data, e.CreatedAt);

    private static string Frame(
        long seq, string kind, string message, Dictionary<string, string>? data, DateTimeOffset createdAt)
    {
        var payload = Json.Serialize(new Dictionary<string, object?>
        {
            ["seq"] = seq,
            ["kind"] = kind,
            ["message"] = message,
            ["data"] = data,
            ["created_at"] = createdAt,
        });
        return $"id: {seq}\nevent: {kind}\ndata: {payload}\n\n";
    }
}
