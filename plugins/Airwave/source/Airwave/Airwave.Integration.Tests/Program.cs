using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Airwave.AudioHost;
using Airwave.Core;
using Airwave.Relay;

namespace Airwave.Integration.Tests;

internal sealed record Options(int Seconds, string AudioHost, int CapturePid, string? CaptureExe, bool Wasapi)
{
    public static Options Parse(string[] args)
    {
        int seconds = 30;
        int capturePid = 0;
        string? captureExe = null;
        string audioHost = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Airwave.AudioHost/bin/Release/net10.0-windows/Airwave.AudioHost.exe"));
        bool wasapi = false;
        for (int index = 0; index < args.Length; ++index)
        {
            if (args[index] == "--wasapi") { wasapi = true; continue; }
            if (index + 1 >= args.Length) throw new ArgumentException("An option is missing its value.");
            switch (args[index++])
            {
                case "--seconds": seconds = int.Parse(args[index]); break;
                case "--audio-host": audioHost = Path.GetFullPath(args[index]); break;
                case "--capture-pid": capturePid = int.Parse(args[index]); break;
                case "--capture-exe": captureExe = Path.GetFullPath(args[index]); break;
                default: throw new ArgumentException("Unknown integration option.");
            }
        }
        if (seconds is < 20 or > 7200 || capturePid < 0 || !File.Exists(audioHost)
            || capturePid != 0 && (captureExe is null || !File.Exists(captureExe))) throw new ArgumentException("Invalid integration options or missing runtime.");
        return new(seconds, audioHost, capturePid, captureExe, wasapi);
    }
}

internal sealed record ListenerResult(string Name, bool Passed, long FramesReceived, long NonSilentFrames,
    double? FirstAudioMilliseconds, long Underruns, long DroppedFrames, double MaximumBufferedMilliseconds,
    float MaximumPeak, double ContinuousObservedSeconds, string? ErrorBeforeStop, bool StoppedWithSource, int ExitCode, string? FinalStage);

internal sealed record TrialResult(bool Passed, string Source, int RequestedSeconds, bool Wasapi,
    long FramesSent, long SenderDroppedFrames, long? NativeCaptureDroppedFrames, long SenderNonSilentFrames, IReadOnlyList<ListenerResult> Listeners,
    int WorkingDirectoryFileEvents, int WorkingDirectoryFiles, bool AllProcessesStopped, string? Failure);

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Options options;
        try { options = Options.Parse(args); }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { Passed = false, Failure = "Invalid integration options or missing runtime." }));
            return 2;
        }
        var runtimeDirectory = Path.Combine(Path.GetTempPath(), "Airwave-Trial-" + Guid.NewGuid().ToString("N"));
        TrialResult result;
        try
        {
            try { result = await RunTrial(options, runtimeDirectory); }
            finally { CleanupTrial(runtimeDirectory); }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { Passed = false, Failure = "Trial setup, shutdown, or owned-directory cleanup failed.", ErrorType = error.GetType().Name }));
            return 1;
        }
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return result.Passed ? 0 : 1;
    }

    private static async Task<TrialResult> RunTrial(Options options, string runtimeDirectory)
    {
        var hostDirectory = Path.GetDirectoryName(options.AudioHost)!;
        var workDirectory = Path.Combine(runtimeDirectory, "work");
        var frozenDirectory = Path.Combine(runtimeDirectory, "runtime");
        Directory.CreateDirectory(workDirectory);
        Directory.CreateDirectory(frozenDirectory);
        CopyDirectory(hostDirectory, frozenDirectory);
        var frozenHost = Path.Combine(frozenDirectory, Path.GetFileName(options.AudioHost));
        int events = 0;
        using var watcher = new FileSystemWatcher(workDirectory) { IncludeSubdirectories = true, EnableRaisingEvents = true };
        watcher.Created += (_, _) => Interlocked.Increment(ref events);
        watcher.Changed += (_, _) => Interlocked.Increment(ref events);
        watcher.Deleted += (_, _) => Interlocked.Increment(ref events);
        watcher.Renamed += (_, _) => Interlocked.Increment(ref events);
        var relayOptions = new RelayOptions(ConnectionPolicy.NewToken(), ConnectionPolicy.NewToken());
        await using var relay = RelayApplication.Build(["--urls", "http://127.0.0.1:0"], relayOptions);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(options.Seconds + 35));
        HostProcess? publisher = null;
        var listeners = new List<HostProcess>();
        var results = new List<ListenerResult>();
        bool sourceStopped = false;
        string? failure = null;
        try
        {
            await relay.StartAsync(deadline.Token);
            string url = relay.Urls.Single().Replace("http://", "ws://", StringComparison.Ordinal);
            publisher = await HostProcess.Start(frozenHost, workDirectory, "publisher", options.CapturePid == 0 ? "synthetic-publish" : "publish",
                new(url, relayOptions.PublishToken), false, options.CapturePid, options.CaptureExe);
            await publisher.WaitFor(s => s.Stage == "OnAir", TimeSpan.FromSeconds(10), deadline.Token);
            var first = await HostProcess.Start(frozenHost, workDirectory, "initial", "listen", new(url, relayOptions.ListenToken), !options.Wasapi);
            listeners.Add(first);
            await first.WaitFor(s => s.Stage == "Listening", TimeSpan.FromSeconds(5), deadline.Token);
            var trial = Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromSeconds(5), deadline.Token);
            var late = await HostProcess.Start(frozenHost, workDirectory, "late", "listen", new(url, relayOptions.ListenToken), true);
            listeners.Add(late);
            await late.WaitFor(s => s.Stage == "Listening", TimeSpan.FromSeconds(5), deadline.Token);
            await Task.Delay(TimeSpan.FromSeconds(options.Seconds) - trial.Elapsed, deadline.Token);
            foreach (var listener in listeners)
                if (listener.HasExited || listener.Latest is { Stage: "Error" }) throw new InvalidDataException("A listener ended during the trial.");
            var runningSnapshots = listeners.Select(listener => listener.Samples.ToArray()).ToArray();
            await publisher.Stop();
            sourceStopped = true;
            for (int index = 0; index < listeners.Count; ++index)
            {
                var listener = listeners[index];
                bool stopped = await listener.WaitForExit(TimeSpan.FromSeconds(6));
                var snapshots = runningSnapshots[index];
                var playing = snapshots.Where(s => s.Status.Stage == "Listening").ToArray();
                var last = snapshots.Last().Status;
                var firstAudio = snapshots.Select(s => s.Status.FirstAudioMilliseconds).FirstOrDefault(v => v is not null);
                double continuous = playing.Length > 1 ? (playing[^1].Elapsed - playing[0].Elapsed).TotalSeconds : 0;
                float peak = snapshots.Max(s => s.Status.Peak);
                string? error = snapshots.Select(s => s.Status.Error).FirstOrDefault(e => e is not null);
                bool passed = stopped && firstAudio is > 0 and < 1000 && continuous >= 5 && last.NonSilentFrames >= 250
                    && last.Underruns == 0 && last.DroppedFrames == 0 && snapshots.Max(s => s.Status.BufferedMilliseconds) <= 500
                    && peak > 0.0001f && error is null && listener.ExitCode == 0 && listener.Latest is { Stage: "Stopped", Error: null };
                results.Add(new(listener.Name, passed, last.FramesReceived, last.NonSilentFrames, firstAudio, last.Underruns,
                    last.DroppedFrames, snapshots.Max(s => s.Status.BufferedMilliseconds), peak, continuous, error, stopped, listener.ExitCode, listener.Latest?.Stage));
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            failure = error is OperationCanceledException ? "The integration trial timed out." : "A stream or readiness check failed (" + error.GetType().Name + ").";
        }
        finally
        {
            foreach (var host in listeners.Prepend(publisher).OfType<HostProcess>())
                try { await host.DisposeAsync(); } catch { failure ??= "An audio helper failed to stop cleanly."; }
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await relay.StopAsync(stopping.Token); } catch { failure ??= "The relay failed to stop cleanly."; }
        }
        watcher.EnableRaisingEvents = false;
        int files = Directory.GetFiles(workDirectory, "*", SearchOption.AllDirectories).Length;
        var sender = publisher?.Latest;
        bool allStopped = (publisher?.HasExited ?? true) && listeners.All(l => l.HasExited);
        bool passedTrial = failure is null && sourceStopped && results.Count == 2 && results.All(l => l.Passed)
            && sender is { DroppedFrames: 0 } && (options.CapturePid == 0 || sender.NativeCaptureDroppedFrames == 0)
            && files == 0 && events == 0 && allStopped;
        var result = new TrialResult(passedTrial, options.CapturePid == 0 ? "Synthetic" : "Rekordbox", options.Seconds,
            options.Wasapi, sender?.FramesSent ?? 0, sender?.DroppedFrames ?? 0, sender?.NativeCaptureDroppedFrames, sender?.NonSilentFrames ?? 0, results,
            events, files, allStopped, failure);
        return result;
    }

    private static void CopyDirectory(string source, string destination)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("Runtime links are not supported.");
        foreach (var file in Directory.EnumerateFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Runtime file links are not supported.");
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var next = Path.Combine(destination, Path.GetFileName(directory));
            Directory.CreateDirectory(next);
            CopyDirectory(directory, next);
        }
    }

    private static void CleanupTrial(string owner)
    {
        if (!Directory.Exists(owner)) return;
        var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Path.GetFileName(owner)));
        if (!Path.GetFileName(owner).StartsWith("Airwave-Trial-", StringComparison.Ordinal)
            || expected != Path.GetFullPath(owner) || (File.GetAttributes(owner) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Unexpected trial directory.");
        foreach (var child in Directory.EnumerateFileSystemEntries(owner))
        {
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) throw new IOException("A trial path became a link.");
            if (Directory.Exists(child)) ValidateCleanupRoot(owner, child);
        }
        foreach (var file in Directory.EnumerateFiles(owner, "*", SearchOption.AllDirectories)) File.Delete(file);
        foreach (var directory in Directory.EnumerateDirectories(owner, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length)) Directory.Delete(directory);
        Directory.Delete(owner);
    }

    private static void ValidateCleanupRoot(string owner, string target)
    {
        string root = Path.GetFullPath(owner) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(target);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Cleanup target is outside the trial directory.");
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new IOException("Cleanup target became a link.");
        foreach (var child in Directory.EnumerateFileSystemEntries(full))
        {
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) throw new IOException("A trial path became a link.");
            if (Directory.Exists(child)) ValidateCleanupRoot(owner, child);
        }
    }
}

internal sealed record StatusSample(TimeSpan Elapsed, HostStatus Status);

internal sealed class HostProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Task output;
    private readonly Task errors;
    private bool disposed;
    private bool exited;
    private int exitCode = -1;
    private HostStatus? latest;
    public string Name { get; }
    public ConcurrentQueue<StatusSample> Samples { get; } = new();
    public HostStatus? Latest => Volatile.Read(ref latest);
    public bool HasExited => exited || !disposed && process.HasExited;
    public int ExitCode => exitCode;

    private HostProcess(Process process, string name)
    {
        this.process = process;
        Name = name;
        output = ReadOutput();
        errors = process.StandardError.ReadToEndAsync();
    }

    public static async Task<HostProcess> Start(string executable, string directory, string name, string mode,
        HostConfiguration configuration, bool noOutput, int capturePid = 0, string? captureExe = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory,
        };
        foreach (var argument in new[] { "--mode", mode, "--parent-pid", Environment.ProcessId.ToString() }) start.ArgumentList.Add(argument);
        if (noOutput) start.ArgumentList.Add("--no-output");
        if (capturePid != 0)
        {
            start.ArgumentList.Add("--capture-pid"); start.ArgumentList.Add(capturePid.ToString());
            start.ArgumentList.Add("--capture-exe"); start.ArgumentList.Add(captureExe!);
        }
        start.Environment["TEMP"] = directory;
        start.Environment["TMP"] = directory;
        var process = Process.Start(start) ?? throw new IOException("The audio host could not start.");
        var host = new HostProcess(process, name);
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(configuration));
            await process.StandardInput.FlushAsync();
            return host;
        }
        catch { await host.DisposeAsync(); throw; }
    }

    private async Task ReadOutput()
    {
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            if (line.Length > 4096) throw new InvalidDataException("Oversized audio status.");
            var status = JsonSerializer.Deserialize<HostStatus>(line) ?? throw new InvalidDataException("Invalid audio status.");
            Volatile.Write(ref latest, status);
            Samples.Enqueue(new(clock.Elapsed, status));
        }
    }

    public async Task WaitFor(Func<HostStatus, bool> predicate, TimeSpan timeout, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            if (Latest is { } status)
            {
                if (predicate(status)) return;
                if (status.Stage == "Error") throw new InvalidDataException(Name + ": " + status.Error);
            }
            if (process.HasExited) throw new IOException(Name + " exited before readiness.");
            await Task.Delay(25, token);
        }
        throw new TimeoutException(Name + " did not become ready.");
    }

    public async Task<bool> WaitForExit(TimeSpan timeout)
    {
        try { await process.WaitForExitAsync().WaitAsync(timeout); }
        catch (TimeoutException) { return false; }
        exited = true;
        exitCode = process.ExitCode;
        await Task.WhenAll(output, errors);
        return true;
    }

    public async Task Stop()
    {
        if (process.HasExited) { await WaitForExit(TimeSpan.FromSeconds(1)); return; }
        await process.StandardInput.WriteLineAsync("stop");
        await process.StandardInput.FlushAsync();
        if (!await WaitForExit(TimeSpan.FromSeconds(5))) throw new TimeoutException(Name + " did not stop.");
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        try { await Stop(); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await WaitForExit(TimeSpan.FromSeconds(3));
        }
        finally { disposed = true; process.Dispose(); }
    }
}
