using System.Diagnostics;
using System.Text.Json;
using Airwave.AudioHost;
using Airwave.Core;
using Xunit;

namespace Airwave.Audio.Tests;

public sealed class ControlTests
{
    [Fact]
    public async Task InputSizeIsBoundedAndEofIsAnExplicitStop()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            HostControl.ReadBoundedLineAsync(new StringReader(new string('x', 257)), 256, CancellationToken.None));
        using var lifetime = new CancellationTokenSource();
        float volume = 1;
        await HostControl.ConsumeCommandsAsync(new StringReader("{\"Volume\":0.25}\n"), value => volume = value, lifetime);
        Assert.Equal(0.25f, volume);
        Assert.True(lifetime.IsCancellationRequested);
    }

    [Fact]
    public async Task StopAndVolumeCommandsCannotIntroduceArbitraryOperations()
    {
        using var lifetime = new CancellationTokenSource();
        await HostControl.ConsumeCommandsAsync(new StringReader("stop\n{\"Volume\":1}\n"), _ => Assert.Fail("No command may follow stop."), lifetime);
        Assert.True(lifetime.IsCancellationRequested);
        using var second = new CancellationTokenSource();
        await Assert.ThrowsAsync<JsonException>(() => HostControl.ConsumeCommandsAsync(
            new StringReader("{\"Execute\":\"cmd.exe\"}\n"), _ => { }, second));
    }

    [Fact]
    public void ErrorStatusNeverEchoesCredentialsOrNetworkErrorText()
    {
        string secret = "NeverEchoThisSensitiveToken";
        Assert.DoesNotContain(secret, Program.SafeError(new HttpRequestException(secret)));
        Assert.DoesNotContain(secret, Program.SafeError(new InvalidDataException(secret)));
        var status = new SessionMetrics().Snapshot("Error", Program.SafeError(new IOException(secret)));
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(status));
    }

    [Theory]
    [InlineData(RelayConnectionFailure.Unauthorized, "access key")]
    [InlineData(RelayConnectionFailure.BroadcastOffline, "not on air")]
    [InlineData(RelayConnectionFailure.BroadcasterBusy, "already on air")]
    [InlineData(RelayConnectionFailure.Full, "full")]
    [InlineData(RelayConnectionFailure.SecureConnection, "certificate")]
    [InlineData(RelayConnectionFailure.Unavailable, "relay address")]
    public void ConnectionFailuresExplainTheNextAction(RelayConnectionFailure failure, string expected)
        => Assert.Contains(expected, Program.SafeError(new RelayConnectionException(failure)));

    [Theory]
    [InlineData("--mode", "publish")]
    [InlineData("--mode", "not-a-mode")]
    [InlineData("--mode", "test", "--token", "never-on-command-line")]
    [InlineData("--mode", "test", "--no-output")]
    public void InvalidCliIsRejected(params string[] args) => Assert.Throws<InvalidDataException>(() => HostOptions.Parse(args));

    [Fact]
    public async Task ExecutableSelfTestUsesNoConfigurationAndNoAudioDevice()
    {
        using var process = Start("test");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("", await stderr);
        var statuses = (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<HostStatus>(line)!).ToArray();
        Assert.Equal("Starting", statuses[0].Stage);
        Assert.Equal("TestPassed", statuses[^1].Stage);
        Assert.True(statuses[^1].CodecsAvailable);
        Assert.True(statuses[^1].NonSilentFrames >= 10);
    }

    [Fact]
    public async Task EofBeforeConfigurationTerminatesWithoutOpeningAConnection()
    {
        using var process = Start("listen", "--no-output");
        var stdout = process.StandardOutput.ReadToEndAsync();
        process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, process.ExitCode);
        Assert.Contains("\"Stage\":\"Stopped\"", await stdout);
    }

    private static Process Start(string mode, params string[] extra)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "audio-host", "Airwave.AudioHost.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add("--mode"); start.ArgumentList.Add(mode);
        start.ArgumentList.Add("--parent-pid"); start.ArgumentList.Add(Environment.ProcessId.ToString());
        foreach (string argument in extra) start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }
}
