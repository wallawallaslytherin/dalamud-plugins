using System.Diagnostics;
using System.Threading.Channels;
using Airwave.Core;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Airwave.AudioHost;

public sealed class AudioOutputFailureException : Exception;

public static class AudioSession
{
    private sealed record PcmFrame(ulong Sequence, short[] Samples);

    public static async Task PublishAsync(HostConfiguration configuration, SessionMetrics metrics,
        int captureProcessId, string? captureExecutable, bool synthetic, CancellationToken cancellationToken)
    {
        using var encoder = new OpusAudioEncoder();
        // Pay the codec's initialization cost before opening a relay session or capturing sound.
        _ = encoder.Encode(new short[AudioProtocol.FrameSamples * AudioProtocol.Channels]);
        encoder.Reset();
        metrics.CodecsAvailable = true;
        metrics.Stage("Connecting");
        await using var connection = await StreamConnection.ConnectAsync(configuration.RelayUrl, configuration.Token,
            true, cancellationToken).ConfigureAwait(false);
        metrics.Connected();
        using var workers = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var queue = Channel.CreateBounded<PcmFrame>(new BoundedChannelOptions(10)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest,
        }, _ => metrics.Dropped());
        await using var capture = synthetic ? null : NativeCapture.Start(captureExecutable ?? "", captureProcessId,
            metrics.NativeCaptureDropped);
        metrics.Stage("Capturing");
        var producer = synthetic
            ? ProduceSynthetic(queue.Writer, workers.Token)
            : ProduceCapture(capture!, queue.Writer, workers.Token);
        try
        {
            ulong? previous = null;
            await foreach (var frame in queue.Reader.ReadAllAsync(workers.Token).ConfigureAwait(false))
            {
                if (previous is { } last && frame.Sequence != last + 1) encoder.Reset();
                var packet = new AudioPacket(frame.Sequence, checked((long)frame.Sequence * 20), encoder.Encode(frame.Samples));
                using var sendDeadline = CancellationTokenSource.CreateLinkedTokenSource(workers.Token);
                sendDeadline.CancelAfter(TimeSpan.FromMilliseconds(500));
                await connection.SendAsync(packet, sendDeadline.Token).ConfigureAwait(false);
                float peak = 0;
                foreach (short sample in frame.Samples) peak = Math.Max(peak, Math.Abs(sample / 32768f));
                metrics.Sent(peak);
                metrics.Stage("OnAir");
                previous = frame.Sequence;
            }
            await producer.ConfigureAwait(false);
        }
        finally
        {
            await workers.CancelAsync().ConfigureAwait(false);
            try { await producer.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException or CaptureFailureException) { }
        }
    }

    private static async Task ProduceCapture(NativeCapture capture, ChannelWriter<PcmFrame> queue, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            ulong sequence = 0;
            // Sequence numbers locate drops in this managed queue only. Native
            // cumulative diagnostics cannot locate gaps within the raw PCM pipe.
            await foreach (var samples in capture.Frames(cancellationToken).ConfigureAwait(false))
                queue.TryWrite(new(sequence++, samples));
        }
        catch (Exception error) { failure = error; throw; }
        finally { queue.TryComplete(failure); }
    }

    private static async Task ProduceSynthetic(ChannelWriter<PcmFrame> queue, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
            ulong sequence = 0;
            do
            {
                queue.TryWrite(new(sequence, SyntheticFrame(sequence++)));
            } while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        finally { queue.TryComplete(); }
    }

    public static short[] SyntheticFrame(ulong sequence)
    {
        var samples = new short[AudioProtocol.FrameSamples * AudioProtocol.Channels];
        for (int index = 0; index < AudioProtocol.FrameSamples; ++index)
        {
            double seconds = ((double)sequence * AudioProtocol.FrameSamples + index) / AudioProtocol.SampleRate;
            samples[index * 2] = (short)(Math.Sin(2 * Math.PI * 440 * seconds) * 8000);
            samples[index * 2 + 1] = (short)(Math.Sin(2 * Math.PI * 660 * seconds) * 8000);
        }
        return samples;
    }

    public static async Task ListenAsync(HostConfiguration configuration, SessionMetrics metrics, bool noOutput,
        CancellationToken cancellationToken)
    {
        using var decoder = new OpusAudioDecoder();
        metrics.CodecsAvailable = true;
        metrics.Stage("Connecting");
        await using var connection = await StreamConnection.ConnectAsync(configuration.RelayUrl, configuration.Token,
            false, cancellationToken).ConfigureAwait(false);
        metrics.Connected();
        using var workers = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var buffer = metrics.Buffer ?? throw new InvalidOperationException("Listening buffer was not initialized.");
        buffer.SetVolume(configuration.Volume);
        metrics.Stage("Buffering");
        Task output = noOutput ? DrainHeadlessAsync(buffer, workers.Token) : PlayWasapiAsync(buffer, workers.Token);
        Task receive = ReceiveFrames(connection, decoder, buffer, metrics, workers.Token);
        try
        {
            Task finished = await Task.WhenAny(output, receive).ConfigureAwait(false);
            await finished.ConfigureAwait(false);
        }
        finally
        {
            await workers.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(output, receive).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException or IOException
                or AudioOutputFailureException or System.Net.WebSockets.WebSocketException) { }
            buffer.Clear();
        }
    }

    private static async Task ReceiveFrames(StreamConnection connection, OpusAudioDecoder decoder, PcmJitterBuffer buffer,
        SessionMetrics metrics, CancellationToken cancellationToken)
    {
        var rate = new ReceiveRateLimit();
        var clock = Stopwatch.StartNew();
        while (!cancellationToken.IsCancellationRequested)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var packet = await connection.ReceiveAsync(timeout.Token).ConfigureAwait(false);
            if (packet is null) return;
            if (!rate.TryAccept(clock.Elapsed.TotalMilliseconds))
                throw new InvalidDataException("The audio relay exceeded the receive limit.");
            long missing = decoder.MissingFrames;
            float[] samples = decoder.Decode(packet);
            if (decoder.MissingFrames > missing) metrics.Dropped(decoder.MissingFrames - missing);
            buffer.Enqueue(samples);
            metrics.Received();
        }
    }

    public static async Task DrainHeadlessAsync(PcmJitterBuffer buffer, CancellationToken cancellationToken)
    {
        var samples = new float[AudioProtocol.FrameSamples * AudioProtocol.Channels];
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) buffer.Read(samples);
    }

    private static async Task PlayWasapiAsync(PcmJitterBuffer buffer, CancellationToken cancellationToken)
    {
        WasapiPlayer? player = null;
        try
        {
            player = new WasapiPlayerBuilder().WithSharedMode().WithEventSync().WithLatency(40).Build();
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            player.PlaybackStopped += (_, _) => stopped.TrySetException(new AudioOutputFailureException());
            player.Init(new SampleToWaveProvider(buffer));
            player.Play();
            await stopped.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch { throw new AudioOutputFailureException(); }
        finally { if (player is not null) await player.DisposeAsync().ConfigureAwait(false); }
    }
}

/// <summary>Bounds decoding work from an untrusted relay, including a short catch-up burst.</summary>
public sealed class ReceiveRateLimit
{
    private double tokens = 50;
    private double previousMilliseconds;
    public bool TryAccept(double nowMilliseconds)
    {
        if (!double.IsFinite(nowMilliseconds) || nowMilliseconds < previousMilliseconds) return false;
        tokens = Math.Min(50, tokens + (nowMilliseconds - previousMilliseconds) * 0.060);
        previousMilliseconds = nowMilliseconds;
        if (tokens < 1) return false;
        --tokens;
        return true;
    }
}
