using System.Text;
using System.Text.Json;
using Airwave.Core;

namespace Airwave;

public static class ListenerInvite
{
    public static string Create(string relayUrl, string listenKey, params string[] knownBroadcastKeys)
    {
        relayUrl = relayUrl.Trim();
        listenKey = listenKey.Trim();
        _ = ConnectionPolicy.Endpoint(relayUrl, false);
        _ = ConnectionPolicy.ValidateToken(listenKey);
        ValidateKeySeparation(listenKey, knownBroadcastKeys);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Invite(1, relayUrl, listenKey));
        return "airwave:" + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static void ValidateKeySeparation(string listenKey, params string[] knownBroadcastKeys)
    {
        var normalized = listenKey.Trim();
        if (normalized.Length > 0 && knownBroadcastKeys.Any(key => normalized == key.Trim()))
            throw new ArgumentException("Use different broadcast and listener keys before saving or sharing an invite.");
    }

    public static (string RelayUrl, string ListenKey) Parse(string value)
    {
        value = value.Trim();
        if (value.Length > 4096 || !value.StartsWith("airwave:", StringComparison.Ordinal))
            throw new FormatException("Paste a valid Airwave listener invite.");
        try
        {
            var encoded = value[8..].Replace('-', '+').Replace('_', '/');
            var bytes = Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='));
            var invite = JsonSerializer.Deserialize<Invite>(bytes);
            if (invite is null || invite.Version != 1 || string.IsNullOrWhiteSpace(invite.RelayUrl) ||
                string.IsNullOrWhiteSpace(invite.ListenKey)) throw new FormatException();
            _ = ConnectionPolicy.Endpoint(invite.RelayUrl, false);
            _ = ConnectionPolicy.ValidateToken(invite.ListenKey);
            return (invite.RelayUrl, invite.ListenKey);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        { throw new FormatException("The listener invite is invalid or uses an unsupported version."); }
    }

    private sealed record Invite(int Version, string RelayUrl, string ListenKey);
}
