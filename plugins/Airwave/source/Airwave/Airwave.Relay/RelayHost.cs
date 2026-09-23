using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Airwave.Core;

namespace Airwave.Relay;

public static class RelayHost
{
    public static bool IsHostCommand(string[] args) => args.Length > 0 && args[0].StartsWith("--host-", StringComparison.Ordinal);

    public static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The host setup commands require Windows. Use environment-based configuration on other platforms.");
            return 2;
        }
        try { return RunWindows(args); }
        catch (HostSetupException error) { Console.Error.WriteLine(error.Message); return 2; }
        catch (Exception)
        {
            Console.Error.WriteLine("The relay action failed. Check the Windows account, package files, saved settings, and whether the selected port is already in use.");
            return 1;
        }
    }

    [SupportedOSPlatform("windows")]
    private static int RunWindows(string[] args)
    {
        var command = args[0];
        var settingsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Airwave", "RelayHost");
        string? publicUrl = null;
        int? port = null;
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length) throw new HostSetupException("A host command option is missing its value.");
            switch (args[index])
            {
                case "--settings-directory": settingsDirectory = Path.GetFullPath(args[index + 1]); break;
                case "--public-url": publicUrl = args[index + 1]; break;
                case "--port":
                    if (!int.TryParse(args[index + 1], out var parsed) || parsed is < 1024 or > 65535) throw new HostSetupException("Use a relay port between 1024 and 65535.");
                    port = parsed;
                    break;
                default: throw new HostSetupException("Unknown host command option. See the relay README.");
            }
        }
        var executable = Environment.ProcessPath ?? throw new HostSetupException("The relay executable could not be identified.");
        var configPath = Path.Combine(settingsDirectory, "settings.json");
        var processPath = Path.Combine(settingsDirectory, "process.json");
        RejectLinks(settingsDirectory);
        RejectLinks(configPath);
        RejectLinks(processPath);
        SecureDirectory(settingsDirectory);
        var lockPath = Path.Combine(settingsDirectory, "host.lock");
        RejectLinks(lockPath);
        using var commandLock = AcquireCommandLock(lockPath);
        using var running = FindOwnedProcess(processPath, executable);

        if (command == "--host-status")
        {
            Console.WriteLine(running is not null ? "Airwave relay is running." : File.Exists(configPath) ? "Airwave relay is stopped." : "Run Setup-Relay first.");
            return 0;
        }
        if (command == "--host-stop")
        {
            if (running is not null)
            {
                running.Kill();
                if (!running.WaitForExit(5000)) throw new HostSetupException("The relay has not stopped yet. Check its state before replacing files.");
            }
            File.Delete(processPath);
            Console.WriteLine("Airwave relay is stopped.");
            return 0;
        }
        if (command == "--host-setup")
        {
            if (running is not null) throw new HostSetupException("Stop this relay before changing its connection settings.");
            HostSettings settings;
            if (File.Exists(configPath))
            {
                settings = LoadSettings(configPath);
                if (publicUrl is null && port is null)
                {
                    Console.WriteLine("Setup already exists. Existing keys and connection settings were preserved.");
                    return 0;
                }
                if (port is not null && port != settings.Port && publicUrl is null)
                    throw new HostSetupException("Changing an existing relay port also requires --public-url. Include the new loopback port in a local address, or keep the hosted TLS address and update its proxy.");
                settings = settings with { PublicUrl = publicUrl ?? settings.PublicUrl, Port = port ?? settings.Port };
            }
            else
            {
                settings = new HostSettings(1, port ?? 17855, publicUrl ?? $"ws://127.0.0.1:{port ?? 17855}", "", "");
                settings = NewKeys(settings);
            }
            ValidateSettings(settings);
            Save(configPath, settings);
            Console.WriteLine("Relay setup saved. Start the relay, then use the copy shortcuts to configure the broadcaster and listeners. Keys are protected for this Windows account.");
            return 0;
        }

        var config = LoadSettings(configPath);
        switch (command)
        {
            case "--host-rotate-keys":
                if (running is not null) throw new HostSetupException("Stop this relay before replacing its access keys.");
                Save(configPath, NewKeys(config));
                Console.WriteLine("Both access keys were replaced. Copy the new broadcaster key and listener invite before the next broadcast.");
                return 0;
            case "--host-copy-address":
                CopyToClipboard(config.PublicUrl);
                Console.WriteLine("Relay address copied.");
                return 0;
            case "--host-copy-broadcast-key":
                CopyToClipboard(Unprotect(config.PublishKey));
                Console.WriteLine("Broadcast key copied. Paste it only into the broadcaster connection settings; do not share it with listeners.");
                return 0;
            case "--host-copy-invite":
                CopyToClipboard(CreateInvite(config.PublicUrl, Unprotect(config.ListenKey)));
                Console.WriteLine("Listener invite copied. Share it only with your intended listeners.");
                return 0;
            case "--host-start":
                if (running is not null) { Console.WriteLine("Airwave relay is already running."); return 0; }
                Start(config, executable, processPath);
                Console.WriteLine("Airwave relay started in the background on loopback. Use Stop-Relay to end it.");
                return 0;
            default: throw new HostSetupException("Unknown host command. See the relay README.");
        }
    }

    public static string CreateInvite(string publicUrl, string listenerKey)
    {
        _ = ConnectionPolicy.Endpoint(publicUrl, false);
        _ = ConnectionPolicy.ValidateToken(listenerKey);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, RelayUrl = publicUrl, ListenKey = listenerKey });
        return "airwave:" + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    [SupportedOSPlatform("windows")]
    private static void Start(HostSettings config, string executable, string processPath)
    {
        // Fail before starting if another application already owns this port.
        var reservation = new TcpListener(IPAddress.Loopback, config.Port);
        reservation.Server.ExclusiveAddressUse = true;
        try { reservation.Start(); }
        finally { reservation.Stop(); }
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory,
            // Detach the background relay from a caller's console or redirected
            // output handles. Startup output is discarded in fixed-size buffers.
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add($"http://127.0.0.1:{config.Port}");
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("AIRWAVE_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["AIRWAVE_PUBLISH_TOKEN"] = Unprotect(config.PublishKey);
        start.Environment["AIRWAVE_LISTEN_TOKEN"] = Unprotect(config.ListenKey);
        using var process = StartDetachedProcess(start);
        using var discardStop = new CancellationTokenSource();
        var discardOutput = DiscardOutput(process.StandardOutput.BaseStream, discardStop.Token);
        var discardError = DiscardOutput(process.StandardError.BaseStream, discardStop.Token);
        start.Environment.Remove("AIRWAVE_PUBLISH_TOKEN");
        start.Environment.Remove("AIRWAVE_LISTEN_TOKEN");
        try
        {
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(1) };
            var ready = false;
            for (var attempt = 0; attempt < 25; attempt++)
            {
                if (process.HasExited) break;
                if (!OwnsLoopbackListener(process.Id, config.Port)) { Thread.Sleep(150); continue; }
                try
                {
                    var json = client.GetStringAsync($"http://127.0.0.1:{config.Port}/health").GetAwaiter().GetResult();
                    using var health = JsonDocument.Parse(json);
                    if (health.RootElement.GetProperty("status").GetString() == "ok" && health.RootElement.GetProperty("protocol").GetInt32() == 1)
                    { ready = OwnsLoopbackListener(process.Id, config.Port); if (ready) break; }
                }
                catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException) { }
                Thread.Sleep(150);
            }
            if (!ready || process.HasExited) throw new HostSetupException("The relay did not become ready. Check its port and package files.");
            Save(processPath, new HostProcess(process.Id, process.StartTime.ToUniversalTime().Ticks));
        }
        catch
        {
            if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); }
            throw;
        }
        finally
        {
            discardStop.Cancel();
            process.StandardOutput.Dispose();
            process.StandardError.Dispose();
            try { Task.WhenAll(discardOutput, discardError).Wait(TimeSpan.FromSeconds(1)); }
            catch (AggregateException) { }
        }
    }

    private static async Task DiscardOutput(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        try { while (await stream.ReadAsync(buffer.AsMemory(), cancellationToken) != 0) { } }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or IOException) { }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    [SupportedOSPlatform("windows")]
    private static Process StartDetachedProcess(ProcessStartInfo start)
    {
        // Redirected child streams do not by themselves exclude additional
        // inheritable handles already held by the caller. Keep the caller's
        // original standard pipes out of the background relay as well.
        var changed = new List<IntPtr>();
        try
        {
            foreach (var identifier in new[] { -10, -11, -12 })
            {
                var handle = GetStdHandle(identifier);
                if (handle == IntPtr.Zero || handle == new IntPtr(-1)) continue;
                if (!GetHandleInformation(handle, out var flags)) continue;
                if ((flags & 1) == 0) continue;
                if (!SetHandleInformation(handle, 1, 0)) throw new HostSetupException("The relay could not detach from its caller's standard handles.");
                changed.Add(handle);
            }
            return Process.Start(start) ?? throw new HostSetupException("The relay process could not start.");
        }
        finally { foreach (var handle in changed) SetHandleInformation(handle, 1, 1); }
    }

    [SupportedOSPlatform("windows")]
    private static HostSettings NewKeys(HostSettings settings)
    {
        var publish = ConnectionPolicy.NewToken();
        string listen;
        do { listen = ConnectionPolicy.NewToken(); } while (listen == publish);
        return settings with { PublishKey = Protect(publish), ListenKey = Protect(listen) };
    }

    [SupportedOSPlatform("windows")]
    private static HostSettings LoadSettings(string path)
    {
        if (!File.Exists(path)) throw new HostSetupException("Run Setup-Relay first.");
        var settings = JsonSerializer.Deserialize<HostSettings>(File.ReadAllText(path)) ?? throw new HostSetupException("The saved relay settings are invalid.");
        ValidateSettings(settings);
        return settings;
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateSettings(HostSettings settings)
    {
        if (settings.Version != 1 || settings.Port is < 1024 or > 65535) throw new HostSetupException("The saved relay settings are invalid.");
        _ = ConnectionPolicy.Endpoint(settings.PublicUrl, false);
        new RelayOptions(Unprotect(settings.PublishKey), Unprotect(settings.ListenKey)).Validate();
    }

    [SupportedOSPlatform("windows")]
    private static string Protect(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try { return Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    [SupportedOSPlatform("windows")]
    private static string Unprotect(string value)
    {
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    [SupportedOSPlatform("windows")]
    private static void SecureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        RejectLinks(path);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new HostSetupException("The Windows account could not be identified.");
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new HostSetupException("Host settings must not be stored through a linked file or directory.");
    }

    private static void Save<T>(string path, T value)
    {
        RejectLinks(path);
        var temporary = path + ".new";
        RejectLinks(temporary);
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }

    private static Process? FindOwnedProcess(string path, string executable)
    {
        Process? process = null;
        try
        {
            if (!File.Exists(path)) return null;
            var identity = JsonSerializer.Deserialize<HostProcess>(File.ReadAllText(path));
            if (identity is null) throw new HostSetupException("The saved relay process could not be identified. Keep its process receipt and check the original relay folder.");
            try { process = Process.GetProcessById(identity.Id); }
            catch (ArgumentException) { return null; }
            if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == identity.StartTicks)
            {
                if (!string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                    throw new HostSetupException("This relay is running from another folder. Stop it with that folder's Stop-Relay shortcut before using this copy. Existing settings and the process receipt were preserved.");
                return process;
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException or IOException)
        {
            process?.Dispose();
            throw new HostSetupException("The saved relay process could not be verified. Keep its process receipt and stop the relay from its original folder before changing settings.");
        }
        catch { process?.Dispose(); throw; }
        process?.Dispose();
        return null;
    }

    private static FileStream AcquireCommandLock(string path)
    {
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new HostSetupException("Another relay setup command is running. Try again after it finishes."); }
    }

    [SupportedOSPlatform("windows")]
    private static bool OwnsLoopbackListener(int processId, int port)
    {
        uint size = 0;
        const int addressFamilyIpv4 = 2;
        const int ownerPidListenerTable = 3;
        var result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, addressFamilyIpv4, ownerPidListenerTable, 0);
        if (result is not (0 or 122) || size is < 4 or > 16777216) return false;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, addressFamilyIpv4, ownerPidListenerTable, 0) != 0) return false;
            var count = Marshal.ReadInt32(buffer);
            if (count < 0 || count > ((int)size - 4) / 24) return false;
            var loopbackAddress = BitConverter.ToInt32(IPAddress.Loopback.GetAddressBytes());
            for (var index = 0; index < count; index++)
            {
                var offset = 4 + index * 24;
                var localPort = Marshal.ReadInt32(buffer, offset + 8);
                var hostPort = ((localPort & 255) << 8) | ((localPort >> 8) & 255);
                if (Marshal.ReadInt32(buffer, offset + 4) == loopbackAddress && hostPort == port
                    && Marshal.ReadInt32(buffer, offset + 20) == processId) return true;
            }
            return false;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [SupportedOSPlatform("windows")]
    private static void CopyToClipboard(string value)
    {
        var owner = CreateWindowExW(0, "STATIC", "", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (owner == IntPtr.Zero) throw new HostSetupException("The clipboard could not create its message window.");
        if (!OpenClipboard(owner))
        {
            DestroyWindow(owner);
            throw new HostSetupException("The clipboard is busy. Try the copy action again.");
        }
        IntPtr memory = IntPtr.Zero;
        try
        {
            var bytes = Encoding.Unicode.GetBytes(value + '\0');
            memory = GlobalAlloc(0x0002, (nuint)bytes.Length);
            if (memory == IntPtr.Zero) throw new HostSetupException("The clipboard could not allocate memory.");
            var pointer = GlobalLock(memory);
            if (pointer == IntPtr.Zero) throw new HostSetupException("The clipboard could not be opened for writing.");
            try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
            finally { GlobalUnlock(memory); CryptographicOperations.ZeroMemory(bytes); }
            if (!EmptyClipboard() || SetClipboardData(13, memory) == IntPtr.Zero) throw new HostSetupException("The clipboard could not be updated.");
            memory = IntPtr.Zero;
        }
        finally { if (memory != IntPtr.Zero) GlobalFree(memory); CloseClipboard(); DestroyWindow(owner); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowExW(uint extendedStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenClipboard(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int identifier);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetHandleInformation(IntPtr handle, out uint flags);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
    [DllImport("iphlpapi.dll", SetLastError = true)] private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, int addressFamily, int tableClass, uint reserved);

    private sealed record HostSettings(int Version, int Port, string PublicUrl, string PublishKey, string ListenKey);
    private sealed record HostProcess(int Id, long StartTicks);
    private sealed class HostSetupException(string message) : Exception(message);
}
