using System.Security.Cryptography;
using Airwave.Core;

namespace Airwave.Relay;

public sealed record RelayOptions(string PublishToken, string ListenToken, int MaximumListeners = 64)
{
    public void Validate()
    {
        var publish = ConnectionPolicy.ValidateToken(PublishToken);
        var listen = ConnectionPolicy.ValidateToken(ListenToken);
        if (CryptographicOperations.FixedTimeEquals(publish, listen)) throw new ArgumentException("Publisher and listener keys must differ.");
        if (MaximumListeners is < 1 or > 64) throw new ArgumentException("Listener capacity must be between 1 and 64.");
    }

    public static RelayOptions FromEnvironment() => new(
        Environment.GetEnvironmentVariable("AIRWAVE_PUBLISH_TOKEN") ?? "",
        Environment.GetEnvironmentVariable("AIRWAVE_LISTEN_TOKEN") ?? "");
}
