using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

[assembly: InternalsVisibleTo("Airwave.Transport.Tests")]

namespace Airwave.Relay;

internal sealed record QueuedAudio(byte[] Bytes, long CreatedAt);

internal sealed class ListenerSubscription : IDisposable
{
    private readonly Channel<QueuedAudio> queue = Channel.CreateBounded<QueuedAudio>(new BoundedChannelOptions(12)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    private readonly CancellationTokenSource stopped = new();
    public CancellationToken Stopped => stopped.Token;
    public ChannelReader<QueuedAudio> Reader => queue.Reader;

    public bool Enqueue(byte[] bytes)
    {
        if (queue.Writer.TryWrite(new(bytes, Stopwatch.GetTimestamp()))) return true;
        Stop();
        return false;
    }

    public void Stop() { queue.Writer.TryComplete(); stopped.Cancel(); }
    public void Dispose() { stopped.Dispose(); }
}

internal sealed class RelayRoom(int capacity)
{
    private readonly object gate = new();
    private readonly HashSet<ListenerSubscription> listeners = [];
    private bool publisher;
    private bool stopping;
    internal int ListenerCount { get { lock (gate) return listeners.Count; } }

    public bool TryStartPublisher()
    {
        lock (gate)
        {
            if (publisher || stopping) return false;
            publisher = true;
            return true;
        }
    }

    public ListenerSubscription? TrySubscribe(out int failureStatus)
    {
        lock (gate)
        {
            failureStatus = publisher && !stopping ? StatusCodes.Status429TooManyRequests : StatusCodes.Status503ServiceUnavailable;
            if (!publisher || stopping || listeners.Count >= capacity) return null;
            var subscription = new ListenerSubscription();
            listeners.Add(subscription);
            return subscription;
        }
    }

    public void Unsubscribe(ListenerSubscription subscription)
    {
        lock (gate) { listeners.Remove(subscription); subscription.Stop(); }
    }

    public void Forward(byte[] bytes)
    {
        lock (gate)
        {
            foreach (var listener in listeners.ToArray())
                if (!listener.Enqueue(bytes)) listeners.Remove(listener);
        }
    }

    public void EndPublisher()
    {
        lock (gate)
        {
            publisher = false;
            foreach (var listener in listeners) listener.Stop();
            listeners.Clear();
        }
    }

    public void Stop() { lock (gate) { stopping = true; EndPublisher(); } }
}
