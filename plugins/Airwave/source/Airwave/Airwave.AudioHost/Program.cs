using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Airwave.Core;

namespace Airwave.AudioHost;

public sealed record HostOptions(string Mode, int ParentProcessId, int CaptureProcessId, string? CaptureExecutable, bool NoOutput)
{
    public static HostOptions Parse(string[] arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        bool noOutput = false;
        for (int index = 0; index < arguments.Length; ++index)
        {
            string key = arguments[index];
            if (key == "--no-output")
            {
                if (noOutput) throw new InvalidDataException("Duplicate option.");
                noOutput = true;
                continue;
            }
            if (key is not ("--mode" or "--parent-pid" or "--capture-pid" or "--capture-exe")
                || ++index == arguments.Length || !values.TryAdd(key, arguments[index]))
                throw new InvalidDataException("Invalid audio host options.");
        }
        string mode = values.GetValueOrDefault("--mode") ?? "";
        if (mode is not ("publish" or "listen" or "test" or "synthetic-publish"))
            throw new InvalidDataException("Invalid audio mode.");
        int parent = ParseId(values.GetValueOrDefault("--parent-pid"), mode == "test");
        int capture = ParseId(values.GetValueOrDefault("--capture-pid"), mode != "publish");
        string? executable = values.GetValueOrDefault("--capture-exe");
        if (mode == "publish" && (string.IsNullOrEmpty(executable) || !Path.IsPathFullyQualified(executable)))
            throw new InvalidDataException("An absolute capture helper path is required.");
        if (noOutput && mode != "listen") throw new InvalidDataException("Headless mode is listener-only.");
        return new(mode, parent, capture, executable, noOutput);
    }

    private static int ParseId(string? value, bool optional)
    {
        if (value is null && optional) return 0;
        return int.TryParse(value, out int id) && id > 0 ? id : throw new InvalidDataException("Invalid process identifier.");
    }
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true), false, 1024, leaveOpen: true);
        var metrics = new SessionMetrics();
        // Every status line is flushed explicitly. The process owns stdout;
        // teardown must not flush an output pipe that has already stopped responding.
        var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), 1024, leaveOpen: true);
        var writer = new StatusWriter(output);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromHours(8));
        Task? work = null;
        Task? commands = null;
        Task? report = null;
        Task? parent = null;
        string? errorMessage = null;
        try
        {
            await writer.WriteAsync(metrics.Snapshot()).ConfigureAwait(false);
            var options = HostOptions.Parse(args);
            if (options.Mode == "test")
            {
                RunCodecTest(metrics);
                await writer.WriteAsync(metrics.Snapshot("TestPassed")).ConfigureAwait(false);
                return 0;
            }
            using var owner = Process.GetProcessById(options.ParentProcessId);
            if (owner.HasExited || owner.Id == Environment.ProcessId) throw new InvalidDataException("Invalid parent process.");
            parent = MonitorParentAsync(owner, lifetime);
            metrics.Buffer = options.Mode == "listen" ? new PcmJitterBuffer(metrics.FirstAudio) : null;

            string? configurationLine;
            using (var initialDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                initialDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                configurationLine = await HostControl.ReadBoundedLineAsync(input, 4096, initialDeadline.Token)
                    .ConfigureAwait(false);
            }
            if (configurationLine is null)
            {
                await lifetime.CancelAsync().ConfigureAwait(false);
                throw new OperationCanceledException(lifetime.Token);
            }
            var configuration = HostControl.ParseConfiguration(configurationLine);
            // Credentials are read from the inherited private pipe, never command-line arguments.
            configurationLine = null;
            metrics.Buffer?.SetVolume(configuration.Volume);
            commands = HostControl.ConsumeCommandsAsync(input, value => metrics.Buffer?.SetVolume(value), lifetime);
            report = writer.RunAsync(metrics, lifetime.Token);
            work = options.Mode == "listen"
                ? AudioSession.ListenAsync(configuration, metrics, options.NoOutput, lifetime.Token)
                : AudioSession.PublishAsync(configuration, metrics, options.CaptureProcessId,
                    options.CaptureExecutable, options.Mode == "synthetic-publish", lifetime.Token);
            Task finished = await Task.WhenAny(work, commands, report, parent).ConfigureAwait(false);
            await finished.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            errorMessage = SafeError(error);
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            foreach (Task? pending in new[] { work, commands, report, parent })
            {
                if (pending is null) continue;
                try { await pending.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch { }
            }
            metrics.Buffer?.Clear();
        }
        try { await writer.WriteAsync(metrics.Snapshot(errorMessage is null ? "Stopped" : "Error", errorMessage)).ConfigureAwait(false); }
        catch { return 1; }
        return errorMessage is null ? 0 : 1;
    }

    public static string SafeError(Exception error) => error switch
    {
        RelayConnectionException connection => RelayConnectionException.MessageFor(connection.Failure),
        StatusOutputFailureException => "The status output stopped responding.",
        CaptureFailureException => "Rekordbox capture stopped or could not start.",
        AudioOutputFailureException => "The playback device could not be opened or stopped unexpectedly.",
        InvalidDataException or JsonException or ArgumentException => "Invalid audio, connection settings, or control data.",
        WebSocketException or HttpRequestException or IOException => "The relay connection failed or ended.",
        OperationCanceledException or TimeoutException => "The audio connection timed out.",
        UnauthorizedAccessException or System.ComponentModel.Win32Exception => "The audio process could not access a required device or process.",
        _ => "The audio session stopped unexpectedly.",
    };

    private static async Task MonitorParentAsync(Process owner, CancellationTokenSource lifetime)
    {
        try
        {
            await owner.WaitForExitAsync(lifetime.Token).ConfigureAwait(false);
            await lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    public static void RunCodecTest(SessionMetrics metrics)
    {
        using var encoder = new OpusAudioEncoder();
        using var decoder = new OpusAudioDecoder();
        metrics.Connected();
        var buffer = new PcmJitterBuffer(metrics.FirstAudio);
        metrics.Buffer = buffer;
        var output = new float[AudioProtocol.FrameSamples * AudioProtocol.Channels];
        for (ulong sequence = 0; sequence < 24; ++sequence)
        {
            byte[] opus = encoder.Encode(AudioSession.SyntheticFrame(sequence));
            var decoded = decoder.Decode(AudioProtocol.Decode(AudioProtocol.Encode(new(sequence, (long)sequence * 20, opus))));
            buffer.Enqueue(decoded);
            buffer.Read(output);
            metrics.Received();
        }
        var stats = buffer.Statistics;
        if (stats.NonSilentFrames < 10 || stats.DroppedFrames != 0 || stats.Underruns != 0)
            throw new InvalidDataException("The audio self-test failed.");
        metrics.CodecsAvailable = true;
    }
}
