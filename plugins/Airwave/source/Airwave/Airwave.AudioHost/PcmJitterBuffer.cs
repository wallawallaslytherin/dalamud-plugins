using Airwave.Core;
using NAudio.Wave;

namespace Airwave.AudioHost;

public sealed record BufferStatistics(double BufferedMilliseconds, long Underruns, long DroppedFrames,
    long PlayedFrames, long NonSilentFrames, float Peak, bool Primed, bool HasPlayed);

/// <summary>
/// One bounded PCM source for the entire listening session. It primes 160 ms,
/// returns silence on starvation, and sheds old audio when the 500 ms cap is exceeded.
/// </summary>
public sealed class PcmJitterBuffer : ISampleProvider
{
    public const int TargetMilliseconds = 160;
    public const int MaximumMilliseconds = 500;
    private const int SamplesPerMillisecond = AudioProtocol.SampleRate * AudioProtocol.Channels / 1000;
    private const int PacketSamples = AudioProtocol.FrameSamples * AudioProtocol.Channels;
    private readonly object gate = new();
    private readonly float[] samples = new float[MaximumMilliseconds * SamplesPerMillisecond];
    private readonly Action? firstPlayback;
    private int head;
    private int count;
    private bool primed;
    private bool hasPlayed;
    private bool underrunActive;
    private long underruns;
    private long droppedFrames;
    private long playedSamples;
    private long nonSilentSamples;
    private float peak;
    private float volume = 1;

    public PcmJitterBuffer(Action? firstPlayback = null) => this.firstPlayback = firstPlayback;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(AudioProtocol.SampleRate, AudioProtocol.Channels);

    public void SetVolume(float value)
    {
        if (!float.IsFinite(value) || value is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(value));
        lock (gate) volume = value;
    }

    public void Enqueue(ReadOnlySpan<float> frame)
    {
        if (frame.Length != PacketSamples) throw new ArgumentException("Expected one 20 ms frame.", nameof(frame));
        foreach (float value in frame)
            if (!float.IsFinite(value) || value is < -1 or > 1)
                throw new InvalidDataException("Invalid PCM samples.");
        lock (gate)
        {
            if (count + frame.Length > samples.Length)
            {
                int excess = count + frame.Length - TargetMilliseconds * SamplesPerMillisecond;
                int discard = Math.Min(count, ((excess + PacketSamples - 1) / PacketSamples) * PacketSamples);
                head = (head + discard) % samples.Length;
                count -= discard;
                droppedFrames += (discard + PacketSamples - 1) / PacketSamples;
            }
            int tail = (head + count) % samples.Length;
            int first = Math.Min(frame.Length, samples.Length - tail);
            frame[..first].CopyTo(samples.AsSpan(tail));
            frame[first..].CopyTo(samples);
            count += frame.Length;
            if (count >= TargetMilliseconds * SamplesPerMillisecond) primed = true;
        }
    }

    public int Read(Span<float> output)
    {
        if (output.Length % AudioProtocol.Channels != 0)
            throw new ArgumentException("Stereo reads must contain whole sample frames.", nameof(output));
        bool notify = false;
        lock (gate)
        {
            output.Clear();
            peak = 0;
            if (!primed || output.Length == 0) return output.Length;
            int reading = Math.Min(output.Length, count);
            for (int index = 0; index < reading; ++index)
            {
                float value = samples[(head + index) % samples.Length] * volume;
                output[index] = value;
                peak = Math.Max(peak, Math.Abs(value));
            }
            head = (head + reading) % samples.Length;
            count -= reading;
            playedSamples += reading;
            if (peak > 0.0001f) nonSilentSamples += reading;
            if (reading > 0 && !hasPlayed) { hasPlayed = true; notify = true; }
            if (reading < output.Length)
            {
                if (!underrunActive) ++underruns;
                underrunActive = true;
                primed = false;
            }
            else underrunActive = false;
        }
        if (notify) firstPlayback?.Invoke();
        return output.Length;
    }

    public BufferStatistics Statistics
    {
        get
        {
            lock (gate) return new((double)count / SamplesPerMillisecond, underruns, droppedFrames,
                playedSamples / PacketSamples, nonSilentSamples / PacketSamples, peak, primed, hasPlayed);
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            Array.Clear(samples);
            head = count = 0;
            primed = false;
            peak = 0;
        }
    }
}
