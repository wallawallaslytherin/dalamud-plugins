using System.Net;
using System.Security.Cryptography;

namespace Airwave.Core;

public static class ConnectionPolicy
{
    public static Uri Endpoint(string relayUrl, bool publishing)
    {
        if (!Uri.TryCreate(relayUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss")
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Use a ws:// or wss:// relay address without credentials, query or fragment.", nameof(relayUrl));
        if (uri.Scheme == "ws" && !IsLiteralLoopback(uri.Host))
            throw new ArgumentException("A remote relay requires encrypted wss:// transport.", nameof(relayUrl));
        if (uri.AbsolutePath != "/") throw new ArgumentException("Use the relay base address without a path.", nameof(relayUrl));
        return new UriBuilder(uri) { Path = publishing ? "/v1/publish" : "/v1/listen" }.Uri;
    }

    public static bool IsLiteralLoopback(string host) => IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);

    public static byte[] ValidateToken(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length is < 43 or > 172 || token.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            throw new ArgumentException("Use a random base64url access key of at least 32 bytes.", nameof(token));
        try
        {
            var normalized = token.Replace('-', '+').Replace('_', '/');
            var bytes = Convert.FromBase64String(normalized.PadRight((normalized.Length + 3) / 4 * 4, '='));
            if (bytes.Length is < 32 or > 128 || Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') != token)
                throw new FormatException();
            return bytes;
        }
        catch (FormatException) { throw new ArgumentException("Use a random base64url access key of at least 32 bytes.", nameof(token)); }
    }

    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
