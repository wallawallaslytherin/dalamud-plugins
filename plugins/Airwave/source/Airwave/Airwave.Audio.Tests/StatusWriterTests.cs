using Airwave.AudioHost;
using Xunit;

namespace Airwave.Audio.Tests;

public sealed class StatusWriterTests
{
    [Fact]
    public async Task CancellableWriteReceivesDeadlineAndCannotBeRetriedAfterFailure()
    {
        using var target = new CancellableWriter();
        int terminated = 0;
        var writer = new StatusWriter(target, TimeSpan.FromMilliseconds(30), () => ++terminated);
        await Assert.ThrowsAsync<StatusOutputFailureException>(() => writer.WriteAsync(new SessionMetrics().Snapshot()))
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(target.SawCancellationToken);
        Assert.True(writer.IsFaulted);
        Assert.Equal(0, terminated);
        await Assert.ThrowsAsync<StatusOutputFailureException>(() => writer.WriteAsync(new SessionMetrics().Snapshot()));
        Assert.Equal(1, target.WriteCalls);
    }

    [Fact]
    public async Task SynchronouslyBlockedWriterTriggersTerminationInsteadOfHangingCaller()
    {
        using var released = new ManualResetEventSlim();
        using var target = new SynchronousBlockedWriter(released);
        int terminated = 0;
        var writer = new StatusWriter(target, TimeSpan.FromMilliseconds(30), () =>
        {
            Interlocked.Increment(ref terminated);
            released.Set(); // The production callback terminates the process, releasing its inherited pipe.
        });
        await Assert.ThrowsAsync<StatusOutputFailureException>(() => writer.WriteAsync(new SessionMetrics().Snapshot()))
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, terminated);
        Assert.True(writer.IsFaulted);
        await Assert.ThrowsAsync<StatusOutputFailureException>(() => writer.WriteAsync(new SessionMetrics().Snapshot()));
        Assert.Equal(1, target.WriteCalls);
    }

    [Fact]
    public async Task FlushAlsoReceivesCancellationAndFailsClosed()
    {
        using var target = new CancellableFlushWriter();
        var writer = new StatusWriter(target, TimeSpan.FromMilliseconds(30), () => Assert.Fail("Cancellation should complete this writer."));
        await Assert.ThrowsAsync<StatusOutputFailureException>(() => writer.WriteAsync(new SessionMetrics().Snapshot()))
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(target.SawCancellationToken);
        Assert.True(writer.IsFaulted);
    }

    private sealed class CancellableWriter : StringWriter
    {
        public bool SawCancellationToken { get; private set; }
        public int WriteCalls { get; private set; }
        public override async Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            ++WriteCalls;
            SawCancellationToken = cancellationToken.CanBeCanceled;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class SynchronousBlockedWriter(ManualResetEventSlim released) : StringWriter
    {
        public int WriteCalls { get; private set; }
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            ++WriteCalls;
            released.Wait();
            return Task.CompletedTask;
        }
    }

    private sealed class CancellableFlushWriter : StringWriter
    {
        public bool SawCancellationToken { get; private set; }
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            SawCancellationToken = cancellationToken.CanBeCanceled;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
