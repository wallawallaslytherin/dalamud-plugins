using System.Security.Cryptography;
using System.Text.Json;
using Airwave.Relay;
using Xunit;

namespace Airwave.Transport.Tests;

public sealed class RelayHostTests
{
    [Fact]
    public void ListenerInviteContainsOnlyListenerConnection()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var value = RelayHost.CreateInvite("wss://radio.example.org", key);
        Assert.StartsWith("airwave:", value);
        var encoded = value[8..].Replace('-', '+').Replace('_', '/');
        using var json = JsonDocument.Parse(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '=')));
        Assert.Equal(3, json.RootElement.EnumerateObject().Count());
        Assert.Equal(1, json.RootElement.GetProperty("Version").GetInt32());
        Assert.Equal("wss://radio.example.org", json.RootElement.GetProperty("RelayUrl").GetString());
        Assert.Equal(key, json.RootElement.GetProperty("ListenKey").GetString());
    }

    [Theory]
    [InlineData("ws://radio.example.org")]
    [InlineData("wss://radio.example.org/v1/listen")]
    [InlineData("wss://radio.example.org?token=invalid")]
    public void HostInviteRejectsUnsafeAddress(string address)
    {
        var key = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Throws<ArgumentException>(() => RelayHost.CreateInvite(address, key));
    }
}
