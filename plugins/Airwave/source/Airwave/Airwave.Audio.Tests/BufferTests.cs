using Airwave.AudioHost;
using Xunit;

namespace Airwave.Audio.Tests;

public sealed class BufferTests
{
    private static float[] Frame(float value = 0.5f) => Enumerable.Repeat(value, 1920).ToArray();

    [Fact]
    public void Primes160MillisecondsBeforePlayingAndRetainsOneContinuousProvider()
    {
        int starts = 0;
        var buffer = new PcmJitterBuffer(() => ++starts);
        float[] output = Frame();
        for (int index = 0; index < 7; ++index) buffer.Enqueue(Frame());
        buffer.Read(output);
        Assert.All(output, value => Assert.Equal(0, value));
        Assert.Equal(0, buffer.Statistics.Underruns);
        buffer.Enqueue(Frame());
        buffer.Read(output);
        Assert.All(output, value => Assert.Equal(0.5f, value));
        Assert.Equal(1, starts);
        Assert.Equal(140, buffer.Statistics.BufferedMilliseconds);
        buffer.Read(output);
        Assert.Equal(1, starts);
    }

    [Fact]
    public void OverflowDropsOldAudioDownToTargetInsteadOfAccumulatingDelay()
    {
        var buffer = new PcmJitterBuffer();
        for (int index = 0; index < 26; ++index) buffer.Enqueue(Frame(index / 100f));
        Assert.Equal(160, buffer.Statistics.BufferedMilliseconds);
        Assert.Equal(18, buffer.Statistics.DroppedFrames);
        var output = new float[1920];
        buffer.Read(output);
        Assert.All(output, value => Assert.Equal(0.18f, value));
    }

    [Fact]
    public void UnderrunOutputsSilenceAndReprimesOncePerEpisode()
    {
        var buffer = new PcmJitterBuffer();
        for (int index = 0; index < 8; ++index) buffer.Enqueue(Frame());
        var output = new float[1920 * 9];
        buffer.Read(output);
        Assert.Equal(1, buffer.Statistics.Underruns);
        Assert.All(output.AsSpan(1920 * 8).ToArray(), value => Assert.Equal(0, value));
        for (int index = 0; index < 5; ++index) buffer.Read(output);
        Assert.Equal(1, buffer.Statistics.Underruns);
        for (int index = 0; index < 8; ++index) buffer.Enqueue(Frame());
        buffer.Read(new float[1920]);
        Assert.True(buffer.Statistics.Primed);
        Assert.Equal(1, buffer.Statistics.Underruns);
    }

    [Fact]
    public void VolumeIsLocalAndStatisticsWorkForSubPacketDeviceReads()
    {
        var buffer = new PcmJitterBuffer();
        for (int index = 0; index < 8; ++index) buffer.Enqueue(Frame());
        buffer.SetVolume(0.25f);
        var output = new float[480];
        for (int index = 0; index < 4; ++index) buffer.Read(output);
        Assert.All(output, value => Assert.Equal(0.125f, value));
        Assert.Equal(1, buffer.Statistics.NonSilentFrames);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.SetVolume(float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.SetVolume(2));
        buffer.Clear();
        Assert.Equal(0, buffer.Statistics.BufferedMilliseconds);
        buffer.Read(output);
        Assert.All(output, value => Assert.Equal(0, value));
    }

    [Fact]
    public void NonfiniteAndWrongSizePcmNeverReachTheOutput()
    {
        var buffer = new PcmJitterBuffer();
        var malformed = Frame(); malformed[12] = float.PositiveInfinity;
        Assert.Throws<InvalidDataException>(() => buffer.Enqueue(malformed));
        Assert.Throws<ArgumentException>(() => buffer.Enqueue(new float[1919]));
        Assert.Equal(0, buffer.Statistics.BufferedMilliseconds);
    }

    [Fact]
    public void ConcurrentNetworkAndPlaybackAccessRemainBounded()
    {
        var buffer = new PcmJitterBuffer();
        float[] frame = Frame();
        Parallel.Invoke(
            () => { for (int index = 0; index < 10_000; ++index) buffer.Enqueue(frame); },
            () =>
            {
                var output = new float[480];
                for (int index = 0; index < 10_000; ++index)
                {
                    buffer.Read(output);
                    Assert.InRange(buffer.Statistics.BufferedMilliseconds, 0, 500);
                }
            });
        Assert.InRange(buffer.Statistics.BufferedMilliseconds, 0, 500);
    }

    [Fact]
    public void ReceiveRateLimitAllowsLivePacingAndRefusesDecodeFloods()
    {
        var rate = new ReceiveRateLimit();
        for (int index = 0; index < 1000; ++index) Assert.True(rate.TryAccept(index * 20));
        int allowed = 0;
        while (rate.TryAccept(20_000)) ++allowed;
        Assert.InRange(allowed, 1, 50);
        Assert.False(rate.TryAccept(19_000));
        Assert.False(rate.TryAccept(double.NaN));
    }
}
