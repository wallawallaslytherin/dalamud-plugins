using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Airwave.AudioHost;

public sealed record HostStatus(string Stage, string? Error, long FramesSent, long FramesReceived, long Underruns,
    long DroppedFrames, double BufferedMilliseconds, float Peak, double? FirstAudioMilliseconds,
    long NonSilentFrames, bool CodecsAvailable, long? NativeCaptureDroppedFrames = null);

public sealed class SessionMetrics
{
    private readonly Stopwatch stopwatch = new();
    private string stage = "Starting";
    private long framesSent;
    private long framesReceived;
    private long droppedFrames;
    private long nativeCaptureDroppedFrames = -1;
    private long nonSilentFrames;
    private float peak;
    private long firstAudioTicks = -1;
    public PcmJitterBuffer? Buffer { get; set; }
    public bool CodecsAvailable { get; set; }
    public void Connected() => stopwatch.Start();
    public void Stage(string value) => Volatile.Write(ref stage, value);
    public void Sent(float value)
    {
        Interlocked.Increment(ref framesSent);
        Volatile.Write(ref peak, value);
        if (value > 0.0001f) Interlocked.Increment(ref nonSilentFrames);
        FirstAudio();
    }
    public void Received() => Interlocked.Increment(ref framesReceived);
    public void Dropped(long value = 1) => Interlocked.Add(ref droppedFrames, value);
    public void NativeCaptureDropped(long value)
    {
        if (value < 0) return;
        long previous = Interlocked.Read(ref nativeCaptureDroppedFrames);
        while (value > previous)
        {
            long observed = Interlocked.CompareExchange(ref nativeCaptureDroppedFrames, value, previous);
            if (observed == previous) break;
            previous = observed;
        }
    }
    public void FirstAudio() => Interlocked.CompareExchange(ref firstAudioTicks, stopwatch.ElapsedTicks, -1);
    public HostStatus Snapshot(string? terminalStage = null, string? error = null)
    {
        var buffer = Buffer?.Statistics;
        long first = Interlocked.Read(ref firstAudioTicks);
        long nativeDropped = Interlocked.Read(ref nativeCaptureDroppedFrames);
        string currentStage = terminalStage ?? Volatile.Read(ref stage);
        if (currentStage == "Buffering" && buffer is { HasPlayed: true, Primed: true }) currentStage = "Listening";
        return new(currentStage, error, Interlocked.Read(ref framesSent), Interlocked.Read(ref framesReceived),
            buffer?.Underruns ?? 0, Interlocked.Read(ref droppedFrames) + (buffer?.DroppedFrames ?? 0),
            buffer?.BufferedMilliseconds ?? 0, buffer?.Peak ?? Volatile.Read(ref peak),
            first < 0 ? null : first * 1000.0 / Stopwatch.Frequency,
            buffer?.NonSilentFrames ?? Interlocked.Read(ref nonSilentFrames), CodecsAvailable,
            nativeDropped < 0 ? null : nativeDropped);
    }
}

public sealed class StatusOutputFailureException : Exception;

public sealed class StatusWriter
{
    private readonly TextWriter writer;
    private readonly SemaphoreSlim gate = new(1);
    private readonly TimeSpan writeTimeout;
    private readonly Action terminateBlockedWriter;
    private int faulted;
    private static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public StatusWriter(TextWriter writer, TimeSpan? writeTimeout = null, Action? terminateBlockedWriter = null)
    {
        this.writer = writer;
        this.writeTimeout = writeTimeout ?? TimeSpan.FromSeconds(2);
        if (this.writeTimeout <= TimeSpan.Zero || this.writeTimeout > TimeSpan.FromSeconds(2))
            throw new ArgumentOutOfRangeException(nameof(writeTimeout));
        // A synchronous inherited pipe can ignore cancellation. Terminating this
        // process also releases WASAPI and makes the capture child's parent watch stop it.
        this.terminateBlockedWriter = terminateBlockedWriter ?? (() => Environment.Exit(3));
    }

    public bool IsFaulted => Volatile.Read(ref faulted) != 0;

    public async Task WriteAsync(HostStatus status)
    {
        if (IsFaulted) throw new StatusOutputFailureException();
        if (!await gate.WaitAsync(writeTimeout).ConfigureAwait(false))
        {
            Interlocked.Exchange(ref faulted, 1);
            throw new StatusOutputFailureException();
        }
        try
        {
            if (IsFaulted) throw new StatusOutputFailureException();
            // All values are counters or fixed application messages, never connection configuration.
            string json = JsonSerializer.Serialize(status, Options);
            using var cancellation = new CancellationTokenSource(writeTimeout);
            CancellationToken token = cancellation.Token;
            // A TextWriter is permitted to block before returning its Task. Keep
            // that work off the watchdog's caller as well as passing cancellation.
            Task operation = Task.Run(async () =>
            {
                await writer.WriteLineAsync(json.AsMemory(), token).ConfigureAwait(false);
                await writer.FlushAsync(token).ConfigureAwait(false);
            });
            try
            {
                await operation.WaitAsync(writeTimeout + TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            }
            catch
            {
                Interlocked.Exchange(ref faulted, 1);
                if (!operation.IsCompleted)
                {
                    // Never attempt another write or teardown flush against a
                    // still-active operation whose pipe ignored cancellation.
                    _ = operation.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    terminateBlockedWriter();
                }
                throw new StatusOutputFailureException();
            }
        }
        finally { gate.Release(); }
    }

    public async Task RunAsync(SessionMetrics metrics, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await WriteAsync(metrics.Snapshot()).ConfigureAwait(false);
    }
}
