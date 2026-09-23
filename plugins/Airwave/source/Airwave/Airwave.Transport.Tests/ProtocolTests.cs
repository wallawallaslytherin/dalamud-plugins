using Airwave.Core;
using Airwave.Relay;
using Xunit;

namespace Airwave.Transport.Tests;

public sealed class ProtocolTests
{
    internal static AudioPacket Packet(ulong sequence = 0) => new(sequence, checked((long)sequence * 20), [0xFC, 0xFF, 0xFE]);

    [Fact]
    public void RoundTripKeepsSequenceTimestampAndBytes()
    {
        var input = Packet(12345);
        var output = AudioProtocol.Decode(AudioProtocol.Encode(input));
        Assert.Equal(input.Sequence, output.Sequence);
        Assert.Equal(input.TimestampMilliseconds, output.TimestampMilliseconds);
        Assert.Equal(input.Payload, output.Payload);
    }

    [Theory]
    [InlineData(0)] [InlineData(4)] [InlineData(5)] [InlineData(7)]
    public void HeaderTamperingIsRejected(int offset)
    {
        var bytes = AudioProtocol.Encode(Packet());
        bytes[offset] ^= 1;
        Assert.Throws<InvalidDataException>(() => AudioProtocol.Decode(bytes));
    }

    [Fact]
    public void OversizedTruncatedAndNegativeTimeAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => AudioProtocol.Decode(new byte[AudioProtocol.MaxMessageBytes + 1]));
        Assert.Throws<InvalidDataException>(() => AudioProtocol.Decode(AudioProtocol.Encode(Packet())[..^1]));
        Assert.Throws<InvalidDataException>(() => AudioProtocol.Encode(Packet() with { TimestampMilliseconds = -1 }));
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x80 })]
    [InlineData(new byte[] { 0xFC, 0x00, 0x00, 0x00, 0x00 }, false)]
    [InlineData(new byte[] { 0xFD, 0x00, 0x00 })]
    [InlineData(new byte[] { 0xF2, 0xFF })]
    [InlineData(new byte[] { 0xF3, 0xC2, 0xFF })]
    [InlineData(new byte[] { 0xF1, 0, 0 }, false)]
    [InlineData(new byte[] { 0xF3, 0x02, 0, 0 }, false)]
    public void OpusFramingEnforcesExactlyTwentyMilliseconds(byte[] bytes, bool invalid = true)
    {
        if (invalid) Assert.Throws<InvalidDataException>(() => AudioProtocol.ValidateOpus(bytes));
        else AudioProtocol.ValidateOpus(bytes);
    }

    [Fact]
    public void MalformedMessagesNeverEscapeAsBoundsErrors()
    {
        var random = new Random(1147);
        for (var iteration = 0; iteration < 5000; iteration++)
        {
            var bytes = new byte[random.Next(0, 1400)];
            random.NextBytes(bytes);
            var error = Record.Exception(() => AudioProtocol.Decode(bytes));
            Assert.True(error is null or InvalidDataException, error?.GetType().Name);
            error = Record.Exception(() => AudioProtocol.ValidateOpus(bytes));
            Assert.True(error is null or InvalidDataException, error?.GetType().Name);
        }
    }

    [Theory]
    [InlineData("ws://localhost:17855")]
    [InlineData("ws://example.com")]
    [InlineData("wss://user:pass@example.com")]
    [InlineData("wss://example.com/?key=secret")]
    [InlineData("wss://example.com/#key")]
    [InlineData("wss://example.com/path")]
    [InlineData("http://127.0.0.1")]
    public void UnsafeEndpointsAreRejected(string address) => Assert.Throws<ArgumentException>(() => ConnectionPolicy.Endpoint(address, false));

    [Theory]
    [InlineData("ws://127.0.0.1:17855", "ws://127.0.0.1:17855/v1/listen")]
    [InlineData("ws://[::1]:17855", "ws://[::1]:17855/v1/listen")]
    [InlineData("wss://example.com", "wss://example.com/v1/listen")]
    public void SecureOrLoopbackEndpointsAreAccepted(string input, string expected) => Assert.Equal(expected, ConnectionPolicy.Endpoint(input, false).AbsoluteUri);

    [Fact]
    public void KeysAreStrongCanonicalAndDistinct()
    {
        var key = ConnectionPolicy.NewToken();
        Assert.Equal(32, ConnectionPolicy.ValidateToken(key).Length);
        Assert.Throws<ArgumentException>(() => ConnectionPolicy.ValidateToken("password"));
        Assert.Throws<ArgumentException>(() => ConnectionPolicy.ValidateToken(key + "="));
        Assert.Throws<ArgumentException>(() => new RelayOptions(key, key).Validate());
        new RelayOptions(key, ConnectionPolicy.NewToken()).Validate();
    }

    [Fact]
    public void SlowListenerIsDisconnectedAtTwelveQueuedFrames()
    {
        var room = new RelayRoom(64);
        Assert.True(room.TryStartPublisher());
        using var slow = room.TrySubscribe(out _)!;
        for (int i = 0; i < 13; ++i) room.Forward(AudioProtocol.Encode(Packet((ulong)i)));
        Assert.True(slow.Stopped.IsCancellationRequested);
        int queued = 0;
        while (slow.Reader.TryRead(out _)) queued++;
        Assert.Equal(12, queued);
        using var late = room.TrySubscribe(out _)!;
        Assert.False(late.Reader.TryRead(out _));
        room.EndPublisher();
        Assert.True(late.Stopped.IsCancellationRequested);
    }
}
