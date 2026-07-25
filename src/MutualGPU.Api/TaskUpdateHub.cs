using System.Collections.Concurrent;
using System.Threading.Channels;
using MutualGPU.Domain;

namespace MutualGPU.Api;

/// <summary>
/// Process-local, requestor-scoped task-change fan-out for the single-process MVP.
/// A bounded channel deliberately coalesces bursts: a browser only needs to know that
/// it should fetch the latest task projection once, not every intermediate mutation.
/// </summary>
public sealed class TaskUpdateHub
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Channel<byte>>> subscribers = new();

    public TaskUpdateSubscription Subscribe(RequestorId requestorId)
    {
        var id = Guid.CreateVersion7();
        var channel = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
        subscribers.GetOrAdd(requestorId.Value, static _ => new()).TryAdd(id, channel);
        return new TaskUpdateSubscription(this, requestorId.Value, id, channel.Reader);
    }

    public void Publish(RequestorId requestorId)
    {
        if (!subscribers.TryGetValue(requestorId.Value, out var requestorSubscribers)) return;
        foreach (var subscriber in requestorSubscribers.Values) subscriber.Writer.TryWrite(0);
    }

    private void Unsubscribe(Guid requestorId, Guid subscriptionId)
    {
        if (!subscribers.TryGetValue(requestorId, out var requestorSubscribers)) return;
        if (requestorSubscribers.TryRemove(subscriptionId, out var subscriber)) subscriber.Writer.TryComplete();
        if (requestorSubscribers.IsEmpty) subscribers.TryRemove(new KeyValuePair<Guid, ConcurrentDictionary<Guid, Channel<byte>>>(requestorId, requestorSubscribers));
    }

    public sealed class TaskUpdateSubscription(TaskUpdateHub owner, Guid requestorId, Guid subscriptionId, ChannelReader<byte> reader) : IAsyncDisposable
    {
        public ChannelReader<byte> Reader { get; } = reader;

        public ValueTask DisposeAsync()
        {
            owner.Unsubscribe(requestorId, subscriptionId);
            return ValueTask.CompletedTask;
        }
    }
}
