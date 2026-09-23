using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Airwave.Core;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Airwave;

public sealed class Plugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface pi;
    private readonly ICommandManager commands;
    private readonly IFramework framework;
    private readonly IChatGui chat;
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly WindowSystem windows = new("Airwave");
    private readonly MainWindow window;
    private readonly ConcurrentQueue<Action> completions = new();
    private readonly string runtime;
    private OwnedProcess? publisher, listener, relay, test;
    private CancellationTokenSource? starting;
    private int generation;
    private string localRelay = "", localPublishKey = "", localListenKey = "";
    private bool disposed;
    private string testStage = "";
    private object? lastHelperFailure;
    private long nextCheck;
    private bool localSoundTest, localListenerPending, reportedPublisherExit, reportedListenerExit;
    private long localSoundDeadline;
    private int rekordboxCount;

    internal string RelayUrl = "", PublishKey = "", ListenKey = "", Invite = "";
    internal float Volume;
    internal string Message { get; private set; } = "Paste an invite to listen, or open Broadcast to share rekordbox audio.";
    internal bool Busy => starting is not null;
    internal bool Publishing => publisher?.Running == true;
    internal bool Listening => listener?.Running == true;
    internal bool LocalTest => relay?.Running == true;
    internal string PublisherStage => publisher?.Stage ?? "Off air";
    internal string ListenerStage => listener?.Stage ?? "Not listening";
    internal bool SessionActive => Busy || Publishing || Listening || LocalTest;
    internal bool LocalSoundTest => localSoundTest;
    internal bool AudioAvailable => File.Exists(Path.Combine(runtime, "audio", "Airwave.AudioHost.exe"));
    internal bool CaptureAvailable => File.Exists(Path.Combine(runtime, "capture", "Airwave.Capture.exe"));
    internal bool RelayAvailable => File.Exists(Path.Combine(runtime, "relay", "Airwave.Relay.exe"));
    internal string? CaptureSetupProblem => UiState.CaptureReadiness(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348), AudioAvailable, CaptureAvailable);
    internal bool RekordboxReady => rekordboxCount == 1;
    internal string RekordboxHelp => rekordboxCount switch
    { 0 => "Open rekordbox and start a track.", 1 => "rekordbox is open. Start a track and check PC MASTER OUT if using ASIO.", _ => "Close extra rekordbox instances so only one remains open." };
    internal string? BroadcastSetupProblem => ConnectionProblem(true);
    internal string? ListenerSetupProblem => ConnectionProblem(false);
    internal string BroadcastState => UiState.Session(true, Publishing, PublisherStage, PublisherStatus);
    internal string ListeningState => UiState.Session(false, Listening, ListenerStage, ListenerStatus);

    public Plugin(IDalamudPluginInterface pi, ICommandManager commands, IFramework framework,
        IChatGui chat, IPluginLog log)
    {
        this.pi = pi; this.commands = commands; this.framework = framework; this.chat = chat; this.log = log;
        runtime = Path.GetDirectoryName(pi.AssemblyLocation.FullName)!;
        config = pi.GetPluginConfig() as Configuration ?? new Configuration();
        RelayUrl = config.RelayUrl;
        Volume = float.IsFinite(config.Volume) ? Math.Clamp(config.Volume, 0, 1) : .35f;
        try { PublishKey = Configuration.Unprotect(config.ProtectedPublishKey); ListenKey = Configuration.Unprotect(config.ProtectedListenKey); }
        catch { Message = "Saved access keys could not be opened by this Windows account. Enter them again."; }
        window = new MainWindow(this);
        RefreshRekordbox();
        windows.AddWindow(window);
        if (!commands.AddHandler("/airwave", new CommandInfo(OnCommand)
        { HelpMessage = "Open Airwave. status: inspect; test: silent engine check; local: local sound test; listen: listen; on: broadcast; off: stop all audio." }))
            throw new InvalidOperationException("/airwave is already registered.");
        pi.UiBuilder.Draw += Draw;
        pi.UiBuilder.OpenMainUi += Open;
        pi.UiBuilder.OpenConfigUi += Open;
        framework.Update += Update;
        log.Information("Airwave 0.1.0.0 loaded; /airwave registered. Capture, listening and broadcasting are stopped.");
    }

    private void Open() => window.IsOpen = true;
    private void Draw() => windows.Draw();
    private void OnCommand(string command, string arguments)
    {
        switch (arguments.Trim().ToLowerInvariant())
        {
            case "": Open(); break;
            case "status": chat.Print($"[Airwave] Broadcast: {PublisherStage}. Listener: {ListenerStage}. {Message}"); log.Information("Airwave status: {State}", InspectState()); break;
            case "test": RunTest(); break;
            case "local": StartLocalSoundTest(); break;
            case "on": StartBroadcast(); break;
            case "listen": StartListening(); break;
            case "off": StopAll(); break;
            default: chat.Print("[Airwave] Use /airwave, or add status, test, local, on, listen or off."); break;
        }
    }

    private void Update(IFramework _)
    {
        if (disposed) return;
        while (completions.TryDequeue(out var action)) Guard(action);
        if (Stopwatch.GetTimestamp() < nextCheck) return;
        nextCheck = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2;
        if (test is not null && test.Stage != testStage)
        {
            testStage = test.Stage;
            if (test.Status is not null) log.Information("Airwave codec test: {State}", test.Status.Value.GetRawText());
            if (testStage == "TestPassed") Message = "Audio engine check passed. This silent check does not test rekordbox capture or your speakers.";
            else if (testStage == "Error" || !test.Running) Message = UiState.Problem(test.Status, false) ?? "The audio engine check could not finish. Reinstall the complete plugin and try again.";
        }
        if (window.IsOpen) RefreshRekordbox();
        if (relay is not null && !relay.Running && (Publishing || Listening))
        { FailLocal("RelayStopped", "The local test relay stopped. Start the local sound test again; reinstall Airwave if it keeps failing."); return; }
        if (localSoundTest && !Busy)
        {
            if (publisher is not null && (!Publishing || PublisherStage == "Error"))
            { FailLocal("CaptureStopped", UiState.Problem(PublisherStatus, true) ?? "The local capture stopped. Check rekordbox and PC MASTER OUT, then start the test again."); return; }
            if (listener is not null && (!Listening || ListenerStage == "Error"))
            { FailLocal("PlaybackStopped", UiState.Problem(ListenerStatus, false) ?? "Local playback stopped. Check your Windows output device, then start the test again."); return; }
            if (localListenerPending && UiState.PublisherReadyForLocalListener(Publishing, PublisherStage))
            {
                try
                {
                    listener?.Dispose(); listener = StartHost("listen", localRelay, localListenKey);
                    reportedListenerExit = false; localListenerPending = false;
                    Message = "Local test connected. Preparing playback on your Windows default output device…";
                }
                catch { FailLocal("PlaybackStartFailed", "Local playback could not start. Check your Windows output device and reinstall Airwave if needed."); return; }
            }
            if (localSoundDeadline > 0 && UiState.Number(ListenerStatus, "FirstAudioMilliseconds") is not null)
            { localSoundDeadline = 0; Message = "Local sound test connected. Use headphones to avoid hearing the source and delayed test together. Stop all audio ends the test."; }
            else if (localSoundDeadline > 0 && Stopwatch.GetTimestamp() > localSoundDeadline)
            { FailLocal("LocalSoundTimeout", "The sound test received no audio. Start a track in rekordbox, enable PC MASTER OUT if using ASIO, and try again."); return; }
        }
        if (publisher is not null && !Publishing && !reportedPublisherExit)
        { reportedPublisherExit = true; Message = UiState.Problem(PublisherStatus, true) ?? "Broadcast stopped. Check rekordbox and the relay, then go on air again."; }
        if (listener is not null && !Listening && !reportedListenerExit)
        { reportedListenerExit = true; Message = UiState.Problem(ListenerStatus, false) ?? "The broadcast ended. Wait for the broadcaster to return, then join again."; }
    }

    internal void Save() => Guard(() =>
    {
        RequireStoppedConnection();
        SaveConnection(ConnectionDraft.Validate(RelayUrl, ListenKey, PublishKey, savedBroadcastKeys: [SavedBroadcastKey()]));
        Message = "Connection saved. Access keys are protected by your Windows account.";
    });

    internal void ImportInvite() => Guard(() =>
    {
        RequireStoppedConnection();
        SaveConnection(ConnectionDraft.FromInvite(Invite, PublishKey, SavedBroadcastKey()));
        Invite = "";
        Message = "Listener invite saved. Choose Join broadcast to connect.";
    });

    internal void JoinBroadcast() => Guard(() =>
    {
        RequireStoppedConnection();
        EnsureRuntime();
        var draft = string.IsNullOrWhiteSpace(Invite)
            ? ConnectionDraft.Validate(RelayUrl, ListenKey, PublishKey, requireListener: true, savedBroadcastKeys: [SavedBroadcastKey()])
            : ConnectionDraft.FromInvite(Invite, PublishKey, SavedBroadcastKey());
        SaveConnection(draft);
        Invite = "";
        StartListeningCore();
    });

    private void RequireStoppedConnection()
    { if (SessionActive) throw new AirwaveUserException("Stop all audio before changing or joining a connection."); }

    private string SavedBroadcastKey()
    {
        try { return Configuration.Unprotect(config.ProtectedPublishKey); }
        catch { throw new AirwaveUserException("The saved broadcast key could not be opened. Clear the saved connection before entering new keys."); }
    }

    internal void ClearConnection() => Guard(() =>
    {
        RequireStoppedConnection();
        SaveConnection(new("", "", "")); Invite = "";
        Message = "Saved connection cleared. Paste an invite or enter new connection details.";
    });

    private void SaveConnection(ConnectionDraft draft)
    {
        var saved = new Configuration { Version = config.Version, RelayUrl = draft.RelayUrl,
            ProtectedPublishKey = Configuration.Protect(draft.PublishKey), ProtectedListenKey = Configuration.Protect(draft.ListenKey), Volume = Volume };
        pi.SavePluginConfig(saved);
        config.RelayUrl = RelayUrl = draft.RelayUrl; PublishKey = draft.PublishKey; ListenKey = draft.ListenKey;
        config.ProtectedPublishKey = saved.ProtectedPublishKey; config.ProtectedListenKey = saved.ProtectedListenKey; config.Volume = Volume;
    }

    private string? ConnectionProblem(bool publishing)
    {
        try { _ = ConnectionDraft.Validate(RelayUrl, ListenKey, PublishKey, requireListener: !publishing, requirePublisher: publishing); return null; }
        catch (AirwaveUserException error) { return error.Message; }
    }

    internal string MakeInvite()
    {
        var draft = ConnectionDraft.Validate(RelayUrl, ListenKey, PublishKey, requireListener: true, savedBroadcastKeys: [SavedBroadcastKey()]);
        if (ConnectionPolicy.IsLiteralLoopback(new Uri(draft.RelayUrl).Host)) throw new AirwaveUserException("A local address can only be used on this PC. Enter your hosted relay address before sharing an invite.");
        return ListenerInvite.Create(draft.RelayUrl, draft.ListenKey, draft.PublishKey, SavedBroadcastKey());
    }

    internal void InviteCopied() => Message = "Listener invite copied. Share it only with people you want to admit to this broadcast.";

    internal void StartBroadcast() => Guard(() =>
    {
        if (Busy || Publishing || LocalTest) throw new AirwaveUserException("Stop the current broadcast or local test first.");
        EnsureRuntime(capture: true);
        var draft = ConnectionDraft.Validate(RelayUrl, ListenKey, PublishKey, requirePublisher: true, savedBroadcastKeys: [SavedBroadcastKey()]);
        var capturePid = FindRekordbox();
        SaveConnection(draft);
        publisher?.Dispose();
        publisher = StartHost("publish", RelayUrl, PublishKey, capturePid); reportedPublisherExit = false;
        Message = "Connecting the broadcast. The audio meter shows when rekordbox is being captured.";
    });

    internal void StartListening() => Guard(() =>
    {
        StartListeningCore();
    });

    private void StartListeningCore()
    {
        if (Busy || Listening) throw new AirwaveUserException("Stop the current listener first.");
        if (localSoundTest && localListenerPending) throw new AirwaveUserException("The local sound test will start playback when capture is ready. Wait for the test to connect.");
        EnsureRuntime();
        var address = LocalTest ? localRelay : RelayUrl;
        var key = LocalTest ? localListenKey : ListenKey;
        var draft = ConnectionDraft.Validate(address, key, "", requireListener: true);
        listener?.Dispose();
        listener = StartHost("listen", draft.RelayUrl, draft.ListenKey); reportedListenerExit = false;
        Message = LocalTest ? "Connecting playback to your local rekordbox stream." : "Connecting to the live broadcast.";
    }

    internal void StartLocalSoundTest() => Guard(StartLocalCore);

    private void StartLocalCore()
    {
        if (SessionActive) throw new AirwaveUserException("Stop all audio before starting a local sound test.");
        EnsureRuntime(capture: true, localRelay: true);
        var capturePid = FindRekordbox();
        publisher?.Dispose(); publisher = null;
        listener?.Dispose(); listener = null;
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        localRelay = $"ws://127.0.0.1:{port}";
        localPublishKey = ConnectionPolicy.NewToken(); localListenKey = ConnectionPolicy.NewToken();
        relay?.Dispose();
        relay = new OwnedProcess(Path.Combine(runtime, "relay", "Airwave.Relay.exe"),
            ["--urls", $"http://127.0.0.1:{port}"], environment: new Dictionary<string, string>
            { ["AIRWAVE_PUBLISH_TOKEN"] = localPublishKey, ["AIRWAVE_LISTEN_TOKEN"] = localListenKey });
        var cancel = starting = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var cancelToken = cancel.Token;
        var current = ++generation;
        localSoundTest = true; localListenerPending = true;
        localSoundDeadline = 0;
        Message = "Starting the local relay. Audio stays on this PC.";
        lastHelperFailure = null;
        _ = Task.Run(async () =>
        {
            try
            {
                using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false })
                { Timeout = TimeSpan.FromSeconds(1) };
                while (true)
                {
                    cancelToken.ThrowIfCancellationRequested();
                    if (relay?.Running != true) throw new InvalidOperationException("The local relay exited during startup.");
                    try
                    {
                        using var response = await client.GetAsync($"http://127.0.0.1:{port}/health", cancelToken).ConfigureAwait(false);
                        if (response.IsSuccessStatusCode) break;
                    }
                    catch (HttpRequestException) { }
                    catch (TaskCanceledException) when (!cancelToken.IsCancellationRequested) { }
                    await Task.Delay(100, cancelToken).ConfigureAwait(false);
                }
                completions.Enqueue(() =>
                {
                    if (disposed || current != generation) return;
                    starting?.Dispose(); starting = null;
                    try
                    {
                        if (relay?.Running != true) throw new AirwaveUserException("The local relay could not start.");
                        publisher?.Dispose();
                        publisher = StartHost("publish", localRelay, localPublishKey, capturePid); reportedPublisherExit = false;
                        localSoundDeadline = Stopwatch.GetTimestamp() + 20 * Stopwatch.Frequency;
                        Message = "Local test is starting rekordbox capture, then playback. Start a track in rekordbox.";
                    }
                    catch { FailLocal("CaptureStartFailed", "The local capture could not start. Keep rekordbox open and reinstall Airwave if it keeps failing."); }
                });
            }
            catch
            {
                completions.Enqueue(() =>
                {
                    if (disposed || current != generation) return;
                    FailLocal("RelayStartFailed", "The local relay could not start. Start the test again; reinstall the complete plugin if it keeps failing.");
                });
            }
        });
    }

    private void FailLocal(string code, string message)
    {
        lastHelperFailure = new { Code = code, RelayExitCode = relay?.ExitCode, PublisherExitCode = publisher?.ExitCode, ListenerExitCode = listener?.ExitCode };
        StopAll(); Message = message;
    }

    internal void RunTest() => Guard(() =>
    {
        if (test?.Running == true) return;
        EnsureRuntime();
        test?.Dispose();
        test = StartHost("test", "", ""); testStage = "";
        Message = "Running the local codec and audio buffer check. No audio is captured or played.";
    });

    private OwnedProcess StartHost(string mode, string address, string key, int? capturePid = null)
    {
        var args = new List<string> { "--mode", mode, "--parent-pid", Environment.ProcessId.ToString() };
        if (capturePid.HasValue)
        {
            args.AddRange(["--capture-pid", capturePid.Value.ToString(), "--capture-exe", Path.Combine(runtime, "capture", "Airwave.Capture.exe")]);
        }
        return new OwnedProcess(Path.Combine(runtime, "audio", "Airwave.AudioHost.exe"), args,
            new { RelayUrl = address, Token = key, Volume });
    }

    private void EnsureRuntime(bool capture = false, bool localRelay = false)
    {
        if (capture && CaptureSetupProblem is { } problem) throw new AirwaveUserException(problem);
        if (!AudioAvailable || capture && !CaptureAvailable || localRelay && !RelayAvailable)
            throw new AirwaveUserException("An Airwave audio component is missing. Reinstall the complete plugin, then try again.");
    }

    private void RefreshRekordbox()
    {
        try
        {
            var processes = Process.GetProcessesByName("rekordbox");
            rekordboxCount = processes.Length;
            foreach (var process in processes) process.Dispose();
        }
        catch { rekordboxCount = 0; }
    }

    private static int FindRekordbox()
    {
        var processes = Process.GetProcessesByName("rekordbox");
        try
        {
            if (processes.Length != 1) throw new AirwaveUserException(processes.Length == 0
                ? "Open rekordbox before starting a broadcast."
                : "More than one rekordbox process is running. Close the extra instance before broadcasting.");
            return processes[0].Id;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    internal void ChangeVolume(float value) => Guard(() =>
    {
        Volume = float.IsFinite(value) ? Math.Clamp(value, 0, 1) : .35f;
        listener?.Send(new { Volume });
        config.Volume = Volume; pi.SavePluginConfig(config);
    });

    internal void StopListening()
    { if (localSoundTest) { StopAll(); return; } listener?.Dispose(); listener = null; Message = "Listening stopped."; }

    internal void StopAll()
    {
        ++generation;
        starting?.Cancel(); starting?.Dispose(); starting = null;
        publisher?.Dispose(); publisher = null;
        listener?.Dispose(); listener = null;
        relay?.Dispose(); relay = null;
        test?.Dispose(); test = null;
        localPublishKey = localListenKey = localRelay = "";
        localSoundTest = localListenerPending = false; localSoundDeadline = 0;
        Message = "Stopped. Capture, broadcast and listening are off.";
    }

    internal void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            Message = ex is AirwaveUserException ? ex.Message : "The audio operation failed. Stop all audio and retry; reinstall the complete plugin if it keeps failing.";
            // Only controlled user input errors are displayed; arbitrary subprocess/network exceptions are not logged.
            chat.PrintError("[Airwave] " + Message);
        }
    }

    public string InspectState() => JsonSerializer.Serialize(new
    {
        Version = "0.1.0.0", ProcessId = Environment.ProcessId, Loaded = !disposed,
        WindowOpen = window.IsOpen, CommandRegistered = commands.Commands.ContainsKey("/airwave"),
        Busy, Publishing, Listening, LocalTest, LocalSoundTest = localSoundTest,
        Publisher = publisher?.Status, Listener = listener?.Status, Test = test?.Status,
        PublisherProcessId = publisher?.Running == true ? publisher.Id : (int?)null,
        ListenerProcessId = listener?.Running == true ? listener.Id : (int?)null,
        RelayProcessId = relay?.Running == true ? relay.Id : (int?)null,
        CaptureAvailable = File.Exists(Path.Combine(runtime, "capture", "Airwave.Capture.exe")),
        AudioHostAvailable = File.Exists(Path.Combine(runtime, "audio", "Airwave.AudioHost.exe")),
        RelayAvailable = File.Exists(Path.Combine(runtime, "relay", "Airwave.Relay.exe")),
        ConnectionConfigured = !string.IsNullOrEmpty(RelayUrl), Volume, LastHelperFailure = lastHelperFailure,
        RuntimeOverrides = new[] { "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROLL_FORWARD", "DOTNET_MULTILEVEL_LOOKUP" }
            .Where(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))).ToArray(),
    });

    internal JsonElement? PublisherStatus => publisher?.Status;
    internal JsonElement? ListenerStatus => listener?.Status;
    internal JsonElement? TestStatus => test?.Status;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        framework.Update -= Update;
        pi.UiBuilder.Draw -= Draw; pi.UiBuilder.OpenMainUi -= Open; pi.UiBuilder.OpenConfigUi -= Open;
        commands.RemoveHandler("/airwave");
        windows.RemoveAllWindows(); StopAll();
        PublishKey = ListenKey = Invite = "";
        log.Information("Airwave unloaded; audio processes stopped.");
    }
}
