using System.Buffers.Binary;
using Airwave.Core;
using Concentus;
using Concentus.Enums;
using Concentus.Structs;

namespace Airwave.AudioHost;

/// <summary>A single continuous managed Opus encoder. Instances are used by one thread only.</summary>
public sealed class OpusAudioEncoder : IDisposable
{
    private readonly IOpusEncoder encoder = ManagedCodecs.CreateEncoder();

    public byte[] Encode(ReadOnlySpan<short> pcm)
    {
        if (pcm.Length != AudioProtocol.FrameSamples * AudioProtocol.Channels)
            throw new ArgumentException("Audio input must contain exactly 20 ms of stereo samples.", nameof(pcm));
        Span<byte> output = stackalloc byte[AudioProtocol.MaxOpusBytes];
        int length = encoder.Encode(pcm, AudioProtocol.FrameSamples, output, output.Length);
        var result = output[..length].ToArray();
        AudioProtocol.ValidateOpus(result);
        return result;
    }

    public void Reset() => encoder.ResetState();
    public void Dispose() => encoder.Dispose();

    public static short[] DecodePcm16(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != AudioProtocol.FrameBytes)
            throw new InvalidDataException("Capture frame has the wrong length.");
        var samples = new short[AudioProtocol.FrameSamples * AudioProtocol.Channels];
        for (int index = 0; index < samples.Length; ++index)
            samples[index] = BinaryPrimitives.ReadInt16LittleEndian(bytes[(index * 2)..]);
        return samples;
    }
}

/// <summary>Checks the bounded wire format before decoding in this isolated audio process.</summary>
public sealed class OpusAudioDecoder : IDisposable
{
    private readonly IOpusDecoder decoder = ManagedCodecs.CreateDecoder();
    private ulong? sequence;
    private long timestamp;
    public long MissingFrames { get; private set; }
    public void Dispose() => decoder.Dispose();

    public float[] Decode(AudioPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Payload is null || packet.TimestampMilliseconds < 0)
            throw new InvalidDataException("Invalid audio packet.");
        AudioProtocol.ValidateOpus(packet.Payload);
        if (OpusPacketInfo.GetNumSamples(packet.Payload, AudioProtocol.SampleRate) != AudioProtocol.FrameSamples)
            throw new InvalidDataException("Invalid Opus duration.");

        if (sequence is { } previous)
        {
            if (packet.Sequence <= previous || packet.TimestampMilliseconds <= timestamp)
                throw new InvalidDataException("Audio packets arrived out of order.");
            ulong difference = packet.Sequence - previous;
            if (difference > 250 || packet.TimestampMilliseconds - timestamp != (long)difference * 20)
                throw new InvalidDataException("Invalid audio clock.");
            if (difference > 1)
            {
                MissingFrames = Math.Min(long.MaxValue - (long)difference + 1, MissingFrames) + (long)difference - 1;
                decoder.ResetState();
            }
        }

        var samples = new float[AudioProtocol.FrameSamples * AudioProtocol.Channels];
        try
        {
            int decoded = decoder.Decode(packet.Payload, samples, AudioProtocol.FrameSamples, false);
            if (decoded != AudioProtocol.FrameSamples)
                throw new InvalidDataException("Unexpected decoded audio duration.");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw new InvalidDataException("The audio packet could not be decoded.");
        }
        for (int index = 0; index < samples.Length; ++index)
        {
            if (!float.IsFinite(samples[index])) throw new InvalidDataException("Invalid decoded samples.");
            samples[index] = Math.Clamp(samples[index], -1f, 1f);
        }
        sequence = packet.Sequence;
        timestamp = packet.TimestampMilliseconds;
        return samples;
    }
}

internal static class ManagedCodecs
{
    // Keep compressed audio in managed code; do not discover native DLLs from the host machine.
    static ManagedCodecs() => OpusCodecFactory.AttemptToUseNativeLibrary = false;

    public static IOpusEncoder CreateEncoder()
    {
        var encoder = OpusCodecFactory.CreateEncoder(AudioProtocol.SampleRate, AudioProtocol.Channels,
            OpusApplication.OPUS_APPLICATION_AUDIO);
        encoder.Bitrate = 192_000;
        encoder.Complexity = 8;
        encoder.UseVBR = true;
        encoder.UseConstrainedVBR = true;
        encoder.SignalType = OpusSignal.OPUS_SIGNAL_MUSIC;
        encoder.UseDTX = false;
        return encoder;
    }
    public static IOpusDecoder CreateDecoder() => OpusCodecFactory.CreateDecoder(AudioProtocol.SampleRate, AudioProtocol.Channels);
}
