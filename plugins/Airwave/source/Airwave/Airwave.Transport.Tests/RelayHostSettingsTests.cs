using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Airwave.Relay;
using Xunit;

namespace Airwave.Transport.Tests;

public sealed class RelayHostSettingsTests
{
    [Fact]
    public void SetupProtectsIndependentKeysAndPreservesThemOnRepeat()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "Airwave-host-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(0, RelayHost.Run(["--host-setup", "--settings-directory", directory, "--port", "27855"]));
            var path = Path.Combine(directory, "settings.json");
            var first = File.ReadAllText(path);
            using var config = JsonDocument.Parse(first);
            var publish = Decrypt(config.RootElement.GetProperty("PublishKey").GetString()!);
            var listen = Decrypt(config.RootElement.GetProperty("ListenKey").GetString()!);
            Assert.True(publish != listen);
            Assert.False(first.Contains(publish, StringComparison.Ordinal));
            Assert.False(first.Contains(listen, StringComparison.Ordinal));
            Assert.Equal("ws://127.0.0.1:27855", config.RootElement.GetProperty("PublicUrl").GetString());
            Assert.Equal(0, RelayHost.Run(["--host-setup", "--settings-directory", directory]));
            Assert.Equal(first, File.ReadAllText(path));
            Assert.Equal(0, RelayHost.Run(["--host-rotate-keys", "--settings-directory", directory]));
            using var rotated = JsonDocument.Parse(File.ReadAllText(path));
            Assert.True(publish != Decrypt(rotated.RootElement.GetProperty("PublishKey").GetString()!));
            Assert.True(listen != Decrypt(rotated.RootElement.GetProperty("ListenKey").GetString()!));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void ConcurrentCommandPreservesSettings()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "Airwave-host-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(0, RelayHost.Run(["--host-setup", "--settings-directory", directory]));
            var path = Path.Combine(directory, "settings.json");
            var before = File.ReadAllText(path);
            using (var commandLock = new FileStream(Path.Combine(directory, "host.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.Equal(2, RelayHost.Run(["--host-rotate-keys", "--settings-directory", directory]));
            Assert.Equal(before, File.ReadAllText(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void ExistingPortChangeRequiresAnExplicitInviteAddress()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "Airwave-host-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(0, RelayHost.Run(["--host-setup", "--settings-directory", directory, "--port", "27855"]));
            var path = Path.Combine(directory, "settings.json");
            var before = File.ReadAllText(path);
            Assert.Equal(2, RelayHost.Run(["--host-setup", "--settings-directory", directory, "--port", "27856"]));
            Assert.Equal(before, File.ReadAllText(path));
            Assert.Equal(0, RelayHost.Run(["--host-setup", "--settings-directory", directory, "--port", "27856", "--public-url", "ws://127.0.0.1:27856"]));
            using var config = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(27856, config.RootElement.GetProperty("Port").GetInt32());
            Assert.Equal("ws://127.0.0.1:27856", config.RootElement.GetProperty("PublicUrl").GetString());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string Decrypt(string value)
    {
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
