using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace Airwave;

internal sealed class OwnedProcess : IDisposable
{
    private readonly Process process;
    private readonly SafeFileHandle job;
    private readonly object gate = new();
    private readonly Channel<string> controls = Channel.CreateBounded<string>(new BoundedChannelOptions(1)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource stopped = new();
    private JsonElement? status;
    private string diagnostic = "";
    private volatile bool disposed;
    public int Id { get; }
    public bool Running { get { try { return !disposed && !process.HasExited; } catch { return false; } } }
    public int? ExitCode { get { try { return process.HasExited ? process.ExitCode : null; } catch { return null; } } }
    public string Diagnostic { get { lock (gate) return diagnostic; } }
    public JsonElement? Status
    {
        get
        {
            lock (gate)
            {
                if (!Running && status is { } state && state.TryGetProperty("Stage", out var stage) &&
                    stage.GetString() is not ("Error" or "Stopped" or "TestPassed"))
                {
                    var stoppedState = System.Text.Json.Nodes.JsonNode.Parse(state.GetRawText())!.AsObject();
                    stoppedState["Stage"] = "Stopped";
                    status = JsonSerializer.SerializeToElement(stoppedState);
                }
                return status?.Clone();
            }
        }
    }
    public string Stage => Status is { } state && state.TryGetProperty("Stage", out var stage) ? stage.GetString() ?? "" : Running ? "Starting" : "Stopped";

    public OwnedProcess(string exe, IEnumerable<string> arguments, object? initialConfig = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        if (!File.Exists(exe)) throw new InvalidOperationException("An Airwave runtime component is missing. Reinstall the complete plugin.");
        job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) throw new InvalidOperationException("Could not create an audio process lifetime guard.");
        var limits = new JobExtendedLimits();
        limits.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobExtendedLimits>()))
        { job.Dispose(); throw new InvalidOperationException("Could not enforce audio process cleanup."); }
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        ChildRuntimeEnvironment.Apply(info);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (environment is not null) foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;
        process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("The audio process could not start.");
            Id = process.Id;
            if (!AssignProcessToJobObject(job, process.Handle)) throw new InvalidOperationException("Could not attach the audio process lifetime guard.");
            _ = DrainAsync(process.StandardOutput, true);
            _ = DrainAsync(process.StandardError, false);
            _ = PumpControlsAsync(initialConfig is null ? null : JsonSerializer.Serialize(initialConfig));
        }
        catch { try { if (!process.HasExited) process.Kill(true); } catch { } process.Dispose(); job.Dispose(); throw; }
    }

    public void Send(object value)
    {
        if (!Running) return;
        var message = JsonSerializer.Serialize(value);
        if (message.Length > 8192) throw new ArgumentException("The audio control message is too large.");
        controls.Writer.TryWrite(message);
    }

    private async Task PumpControlsAsync(string? initial)
    {
        try
        {
            if (initial is not null) await WriteControlAsync(initial).ConfigureAwait(false);
            await foreach (var message in controls.Reader.ReadAllAsync(stopped.Token).ConfigureAwait(false))
                await WriteControlAsync(message).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            if (!disposed)
            {
                lock (gate) status = JsonSerializer.SerializeToElement(new { Stage = "Error", Error = "The audio process stopped responding. Audio has been stopped." });
                job.Dispose();
            }
        }
    }

    private async Task WriteControlAsync(string message)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token);
        timeout.CancelAfter(750);
        await process.StandardInput.WriteLineAsync(message.AsMemory(), timeout.Token).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
    }

    private async Task DrainAsync(StreamReader reader, bool parse)
    {
        try
        {
            var chars = new char[512];
            var line = new StringBuilder();
            var oversized = false;
            while (true)
            {
                var count = await reader.ReadAsync(chars).ConfigureAwait(false);
                if (count == 0) break;
                for (var i = 0; i < count; i++)
                {
                    var c = chars[i];
                    if (c == '\n')
                    {
                        if (parse && !oversized && line.Length > 0)
                        {
                            try { using var doc = JsonDocument.Parse(line.ToString()); lock (gate) status = doc.RootElement.Clone(); }
                            catch (JsonException) { }
                        }
                        else if (!parse && !oversized && line.Length > 0)
                        { lock (gate) diagnostic = line.ToString(); }
                        line.Clear(); oversized = false;
                    }
                    else if (c != '\r' && !oversized)
                    {
                        if (line.Length >= 8192) { oversized = true; line.Clear(); }
                        else line.Append(c);
                    }
                }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            // Closing the job is immediate and terminates every descendant, including a blocked capture writer.
            job.Dispose();
            controls.Writer.TryComplete();
            stopped.Cancel();
            // Process.Close disposes managed pipe writers, so cleanup also stays off the UI thread.
            _ = Task.Run(() => { try { process.Dispose(); } catch (InvalidOperationException) { } });
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct JobBasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct JobExtendedLimits
    {
        public JobBasicLimits BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JobExtendedLimits info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr processHandle);
}
