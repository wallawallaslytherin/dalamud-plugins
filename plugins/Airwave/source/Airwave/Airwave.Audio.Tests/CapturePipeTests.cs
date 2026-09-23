using Airwave.AudioHost;
using Airwave.Core;
using Xunit;

namespace Airwave.Audio.Tests;

public sealed class CapturePipeTests
{
    [Fact]
    public async Task StalledCaptureFailsWithinTheProgressDeadline()
    {
        using var input = new StalledStream();
        await Assert.ThrowsAsync<CaptureFailureException>(() => NativeCapture.ReadFrameAsync(input,
            new byte[AudioProtocol.FrameBytes], TimeSpan.FromMilliseconds(50), CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task OwnerCancellationIsNotMistakenForCaptureFailure()
    {
        using var input = new StalledStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeCapture.ReadFrameAsync(input,
            new byte[AudioProtocol.FrameBytes], TimeSpan.FromSeconds(2), cancellation.Token));
    }

    [Fact]
    public async Task TruncatedPcmCannotBecomeAValidAudioFrame()
    {
        using var input = new MemoryStream(new byte[AudioProtocol.FrameBytes - 1]);
        await Assert.ThrowsAsync<CaptureFailureException>(() => NativeCapture.ReadFrameAsync(input,
            new byte[AudioProtocol.FrameBytes], TimeSpan.FromSeconds(2), CancellationToken.None));
    }

    [Fact]
    public async Task FullPcmIsReturnedWithoutChangingTheBytes()
    {
        byte[] source = Enumerable.Range(0, AudioProtocol.FrameBytes).Select(index => (byte)index).ToArray();
        using var input = new MemoryStream(source);
        byte[] output = new byte[AudioProtocol.FrameBytes];
        await NativeCapture.ReadFrameAsync(input, output, TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.Equal(source, output);
    }

    [Fact]
    public async Task NativeLossIsReportedBeforeDiagnosticStreamEnds()
    {
        var metrics = new SessionMetrics();
        Assert.Null(metrics.Snapshot().NativeCaptureDroppedFrames);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        using var input = new DiagnosticReader(Diagnostic(0) + Diagnostic(480), 7, stallAtEnd: true);
        Task reading = NativeCapture.ReadDiagnosticsAsync(input, value =>
        {
            metrics.NativeCaptureDropped(value);
            if (value == 480) observed.TrySetResult();
        }, cancellation.Token);
        try
        {
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(reading.IsCompleted);
            metrics.Dropped(3);
            Assert.Equal(480, metrics.Snapshot().NativeCaptureDroppedFrames);
            Assert.Equal(3, metrics.Snapshot().DroppedFrames);
        }
        finally { await cancellation.CancelAsync(); }
        await reading.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task DiagnosticCountsAreCumulativeAndMalformedOrOversizedLinesAreSkipped()
    {
        string inputText = Diagnostic(0) + Diagnostic(480) + Diagnostic(480) + Diagnostic(240)
            + Diagnostic(-1) + Diagnostic(long.MaxValue)
            + "{\"event\":\"capture-stats\",\"version\":2,\"droppedFrames\":960}\n"
            + "{\"event\":\"capture-stats\",\"version\":1,\"droppedFrames\":\"960\"}\n"
            + "{\"event\":\"error\",\"droppedFrames\":960}\nnull\n[1]\ninvalid json\n"
            + new string(' ', 16384) + Diagnostic(960)
            + Diagnostic(1440) + "{\"event\":\"capture-stats\",\"version\":1,\"droppedFrames\":1920}";
        using var input = new DiagnosticReader(inputText, 13);
        var counts = new List<long>();
        await NativeCapture.ReadDiagnosticsAsync(input, counts.Add, CancellationToken.None);
        Assert.Equal(new long[] { 0, 480, 480, 1440 }, counts);
        var metrics = new SessionMetrics();
        foreach (long value in counts) metrics.NativeCaptureDropped(value);
        metrics.NativeCaptureDropped(0);
        metrics.NativeCaptureDropped(-1);
        Assert.Equal(1440, metrics.Snapshot().NativeCaptureDroppedFrames);
        Assert.Equal(0, metrics.Snapshot().DroppedFrames);
    }

    private static string Diagnostic(long count) =>
        "{\"event\":\"capture-stats\",\"version\":1,\"droppedFrames\":"
        + count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}\n";

    private sealed class DiagnosticReader(string text, int maximumChunk, bool stallAtEnd = false) : TextReader
    {
        private int offset;
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(Math.Min(buffer.Length, maximumChunk), text.Length - offset);
            if (count == 0 && stallAtEnd) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            text.AsMemory(offset, count).CopyTo(buffer);
            offset += count;
            return count;
        }
    }

    private sealed class StalledStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
