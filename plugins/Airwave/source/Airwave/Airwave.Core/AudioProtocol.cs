using System.Buffers.Binary;

namespace Airwave.Core;

public sealed record AudioPacket(ulong Sequence, long TimestampMilliseconds, byte[] Payload);

/// <summary>Fixed-format, 20 ms Opus messages. No file names or executable content are accepted.</summary>
public static class AudioProtocol
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int FrameSamples = 960;
    public const int FrameBytes = 3840;
    public const int MaxOpusBytes = 1275;
    public const int HeaderBytes = 24;
    public const int MaxMessageBytes = HeaderBytes + MaxOpusBytes;

    public static byte[] Encode(AudioPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(packet.Payload);
        if (packet.TimestampMilliseconds < 0) throw new InvalidDataException("Invalid audio timestamp.");
        ValidateOpus(packet.Payload);
        var bytes = new byte[HeaderBytes + packet.Payload.Length];
        "AWAV"u8.CopyTo(bytes);
        bytes[4] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6), (ushort)packet.Payload.Length);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(8), packet.Sequence);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(16), packet.TimestampMilliseconds);
        packet.Payload.CopyTo(bytes, HeaderBytes);
        return bytes;
    }

    public static AudioPacket Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderBytes || bytes.Length > MaxMessageBytes || !bytes[..4].SequenceEqual("AWAV"u8)
            || bytes[4] != 1 || bytes[5] != 0 || BinaryPrimitives.ReadUInt16BigEndian(bytes[6..]) != bytes.Length - HeaderBytes)
            throw new InvalidDataException("Invalid audio message.");
        var timestamp = BinaryPrimitives.ReadInt64BigEndian(bytes[16..]);
        if (timestamp < 0) throw new InvalidDataException("Invalid audio timestamp.");
        ValidateOpus(bytes[HeaderBytes..]);
        return new(BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]), timestamp, bytes[HeaderBytes..].ToArray());
    }

    // Opus packet framing and TOC duration, RFC 6716 section 3. Arbitrary compressed
    // payload still requires an isolated decoder; framing validation is not decoding.
    public static void ValidateOpus(ReadOnlySpan<byte> packet)
    {
        if (packet.Length is < 1 or > MaxOpusBytes) throw new InvalidDataException("Invalid Opus packet length.");
        int toc = packet[0];
        int samples = (toc & 0x80) != 0 ? (SampleRate << ((toc >> 3) & 3)) / 400
            : (toc & 0x60) == 0x60 ? ((toc & 8) != 0 ? SampleRate / 50 : SampleRate / 100)
            : ((toc >> 3) & 3) == 3 ? SampleRate * 60 / 1000 : (SampleRate << ((toc >> 3) & 3)) / 100;
        int code = toc & 3;
        int count = code == 0 ? 1 : code == 3 ? packet.Length >= 2 ? packet[1] & 63 : 0 : 2;
        if (count == 0 || count * samples != FrameSamples) throw new InvalidDataException("Audio packets must contain exactly 20 ms.");
        int offset = code == 3 ? 2 : 1;
        int end = packet.Length;
        if (code == 3 && (packet[1] & 64) != 0)
        {
            int padding = 0;
            int next;
            do
            {
                if (offset >= end) throw new InvalidDataException("Invalid Opus padding.");
                next = packet[offset++];
                padding += next == 255 ? 254 : next;
            } while (next == 255);
            end -= padding;
            if (end < offset) throw new InvalidDataException("Invalid Opus padding.");
        }
        bool vbr = code == 2 || code == 3 && (packet[1] & 128) != 0;
        if (vbr)
        {
            int known = 0;
            for (int index = 0; index < count - 1; ++index)
            {
                if (offset >= end) throw new InvalidDataException("Invalid Opus frame lengths.");
                int length = packet[offset++];
                if (length >= 252)
                {
                    if (offset >= end) throw new InvalidDataException("Invalid Opus frame lengths.");
                    length += 4 * packet[offset++];
                }
                known += length;
            }
            if (known > end - offset) throw new InvalidDataException("Invalid Opus frame lengths.");
        }
        else if ((end - offset) % count != 0) throw new InvalidDataException("Invalid Opus CBR frame lengths.");
    }
}
