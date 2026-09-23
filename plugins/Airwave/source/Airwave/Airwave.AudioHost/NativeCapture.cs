using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Airwave.Core;

namespace Airwave.AudioHost;

public sealed class CaptureFailureException : Exception;

/// <summary>Owns a capture child and both redirected pipes. No audio is written to disk.</summary>
public sealed class NativeCapture : IAsyncDisposable
{
    private readonly Process process;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task stderr;

    private NativeCapture(Process process, Action<long> nativeDroppedFrames)
    {
        this.process = process;
        stderr = ReadDiagnosticsAsync(process.StandardError, nativeDroppedFrames, lifetime.Token);
    }

    public int ProcessId => process.Id;

    public static NativeCapture Start(string executable, int captureProcessId, Action<long> nativeDroppedFrames)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable)
            || !string.Equals(Path.GetFileName(executable), "Airwave.Capture.exe", StringComparison.OrdinalIgnoreCase)
            || captureProcessId <= 0)
            throw new CaptureFailureException();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        start.ArgumentList.Add("--pid");
        start.ArgumentList.Add(captureProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--parent-pid");
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        try { return new(Process.Start(start) ?? throw new CaptureFailureException(), nativeDroppedFrames); }
        catch { throw new CaptureFailureException(); }
    }

    public async IAsyncEnumerable<short[]> Frames([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var bytes = new byte[AudioProtocol.FrameBytes];
        while (!linked.IsCancellationRequested)
        {
            await ReadFrameAsync(process.StandardOutput.BaseStream, bytes, TimeSpan.FromSeconds(2), linked.Token)
                .ConfigureAwait(false);
            yield return OpusAudioEncoder.DecodePcm16(bytes);
        }
    }

    public static async Task ReadFrameAsync(Stream input, Memory<byte> frame, TimeSpan maximumWait,
        CancellationToken cancellationToken)
    {
        if (frame.Length != AudioProtocol.FrameBytes || maximumWait <= TimeSpan.Zero || maximumWait > TimeSpan.FromSeconds(2))
            throw new ArgumentException("Invalid capture read boundary.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(maximumWait);
        try
        {
            int read = 0;
            while (read < frame.Length)
            {
                int current = await input.ReadAsync(frame[read..], deadline.Token).ConfigureAwait(false);
                if (current == 0) throw new CaptureFailureException();
                read += current;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CaptureFailureException();
        }
    }

    // Raw stdout stays PCM-only. Stderr reports cumulative sample-frame loss,
    // without enough ordering information to fabricate packet sequence gaps.
    public static async Task ReadDiagnosticsAsync(TextReader reader, Action<long> nativeDroppedFrames,
        CancellationToken cancellationToken)
    {
        var buffer = new char[1024];
        var line = new char[512];
        int length = 0;
        bool oversized = false;
        long previous = -1;
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
            {
                for (int index = 0; index < count; ++index)
                {
                    char value = buffer[index];
                    if (value == '\n')
                    {
                        if (!oversized && TryParseDiagnostic(new string(line, 0, length), out long dropped)
                            && dropped >= previous)
                        {
                            previous = dropped;
                            nativeDroppedFrames(dropped);
                        }
                        length = 0;
                        oversized = false;
                    }
                    else if (!oversized)
                    {
                        if (length == line.Length) oversized = true;
                        else line[length++] = value;
                    }
                }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    private static bool TryParseDiagnostic(string line, out long droppedFrames)
    {
        droppedFrames = 0;
        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("event", out var kind) && kind.ValueKind == JsonValueKind.String
                && kind.GetString() == "capture-stats"
                && root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number
                && version.TryGetInt32(out int number) && number == 1
                && root.TryGetProperty("droppedFrames", out var dropped) && dropped.ValueKind == JsonValueKind.Number
                && dropped.TryGetInt64(out droppedFrames) && droppedFrames >= 0
                && droppedFrames <= (long)AudioProtocol.SampleRate * 60 * 60 * 8;
        }
        catch (JsonException) { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        try { await stderr.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        process.Dispose();
        lifetime.Dispose();
    }
}
