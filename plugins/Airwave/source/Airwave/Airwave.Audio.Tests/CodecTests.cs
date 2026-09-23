using Airwave.AudioHost;
using Airwave.Core;
using Xunit;

namespace Airwave.Audio.Tests;

public sealed class CodecTests
{
    [Fact]
    public void LiveFramesRoundTripAt20MillisecondsAndPreserveStereoToneSeparation()
    {
        using var encoder = new OpusAudioEncoder();
        using var decoder = new OpusAudioDecoder();
        var left = new List<float>();
        var right = new List<float>();
        int totalBytes = 0;
        for (ulong sequence = 0; sequence < 100; ++sequence)
        {
            byte[] payload = encoder.Encode(AudioSession.SyntheticFrame(sequence));
            totalBytes += payload.Length;
            Assert.InRange(payload.Length, 1, AudioProtocol.MaxOpusBytes);
            AudioPacket packet = AudioProtocol.Decode(AudioProtocol.Encode(new(sequence, (long)sequence * 20, payload)));
            var pcm = decoder.Decode(packet);
            Assert.Equal(1920, pcm.Length);
            if (sequence < 50) continue; // Ignore initial codec lookahead, then examine a full second.
            for (int index = 0; index < pcm.Length; index += 2)
            {
                left.Add(pcm[index]);
                right.Add(pcm[index + 1]);
            }
        }
        Assert.InRange(totalBytes * 4, 80_000, 240_000); // Two seconds, expressed as bits per second.
        Assert.True(Amplitude(left, 440) > Amplitude(left, 660) * 30);
        Assert.True(Amplitude(right, 660) > Amplitude(right, 440) * 30);
        Assert.InRange(Amplitude(left, 440), 0.20, 0.28);
        Assert.Equal(0, decoder.MissingFrames);
    }

    [Fact]
    public void ReplayAndFalseClocksAreRejectedBeforeDecode()
    {
        using var encoder = new OpusAudioEncoder();
        byte[] opus = encoder.Encode(AudioSession.SyntheticFrame(0));
        using var decoder = new OpusAudioDecoder();
        _ = decoder.Decode(new(20, 400, opus)); // A late join may begin at any valid sequence.
        Assert.Throws<InvalidDataException>(() => decoder.Decode(new(20, 400, opus)));
        Assert.Throws<InvalidDataException>(() => decoder.Decode(new(21, 450, opus)));
        Assert.Throws<InvalidDataException>(() => decoder.Decode(new(500, 10_000, opus)));
        _ = decoder.Decode(new(23, 460, opus));
        Assert.Equal(2, decoder.MissingFrames);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1276)]
    [InlineData(10_000)]
    public void OversizedAndEmptyPacketsAreRejected(int size)
    {
        using var decoder = new OpusAudioDecoder();
        Assert.Throws<InvalidDataException>(() => decoder.Decode(new(0, 0, new byte[size])));
    }

    [Fact]
    public void BoundedHostilePacketsEitherDecodeFinite20msOrFailWithControlledError()
    {
        var random = new Random(715_947);
        using var decoder = new OpusAudioDecoder();
        for (ulong sequence = 0; sequence < 250; ++sequence)
        {
            var bytes = new byte[random.Next(1, AudioProtocol.MaxOpusBytes + 1)];
            random.NextBytes(bytes);
            if (sequence % 2 == 0) bytes[0] = 0xFC; // Valid 20 ms stereo CELT framing with hostile compressed contents.
            try
            {
                float[] result = decoder.Decode(new(sequence, (long)sequence * 20, bytes));
                Assert.Equal(1920, result.Length);
                Assert.All(result, value => Assert.True(float.IsFinite(value) && value is >= -1 and <= 1));
            }
            catch (InvalidDataException) { }
        }
    }

    [Fact]
    public void CaptureBytesAreDecodedAsLittleEndianStereo()
    {
        byte[] bytes = new byte[AudioProtocol.FrameBytes];
        bytes[0] = 0x34; bytes[1] = 0x12; bytes[2] = 0xFF; bytes[3] = 0xFF;
        short[] decoded = OpusAudioEncoder.DecodePcm16(bytes);
        Assert.Equal((short)0x1234, decoded[0]);
        Assert.Equal((short)-1, decoded[1]);
        Assert.Throws<InvalidDataException>(() => OpusAudioEncoder.DecodePcm16(new byte[5]));
    }

    private static double Amplitude(IReadOnlyList<float> samples, double frequency)
    {
        double real = 0, imaginary = 0;
        for (int index = 0; index < samples.Count; ++index)
        {
            double phase = 2 * Math.PI * frequency * index / AudioProtocol.SampleRate;
            real += samples[index] * Math.Cos(phase);
            imaginary += samples[index] * Math.Sin(phase);
        }
        return 2 * Math.Sqrt(real * real + imaginary * imaginary) / samples.Count;
    }
}
