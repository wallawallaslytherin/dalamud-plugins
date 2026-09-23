using System.Security.Cryptography;
using System.Text;
using Dalamud.Configuration;

namespace Airwave;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public string RelayUrl { get; set; } = "";
    public string ProtectedPublishKey { get; set; } = "";
    public string ProtectedListenKey { get; set; } = "";
    public float Volume { get; set; } = 0.35f;

    public static string Protect(string secret)
    {
        if (string.IsNullOrEmpty(secret)) return "";
        var bytes = Encoding.UTF8.GetBytes(secret);
        try { return Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static string Unprotect(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return "";
        var clear = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), null, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(clear); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
}
