using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Airwave;

internal sealed class MainWindow : Window
{
    private readonly Plugin plugin;
    public MainWindow(Plugin plugin) : base("Airwave###AirwaveMain")
    {
        this.plugin = plugin;
        IsOpen = false;
        Size = new Vector2(680, 680); SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(550, 440), MaximumSize = new Vector2(float.MaxValue) };
    }

    public override void Draw()
    {
        ImGui.TextUnformatted(plugin.LocalSoundTest ? "LOCAL SOUND TEST · THIS PC ONLY" : "AIRWAVE · LIVE AUDIO");
        ImGui.TextUnformatted($"Listen: {plugin.ListeningState}    Broadcast: {plugin.BroadcastState}");
        ImGui.TextWrapped(plugin.Message);
        ImGui.Spacing();
        if (ImGui.Button("Stop all audio")) plugin.StopAll();
        ImGui.SameLine();
        ImGui.TextDisabled("Stops capture, broadcasting and playback.");
        var volume = plugin.Volume * 100;
        if (ImGui.SliderFloat("Playback volume", ref volume, 0, 100, "%.0f%%")) plugin.ChangeVolume(volume / 100);
        if (plugin.Volume == 0) ImGui.TextWrapped("Playback is muted. Raise Playback volume to hear the broadcast.");
        ImGui.Separator();

        if (ImGui.BeginChild("AirwaveContent", Vector2.Zero, false, ImGuiWindowFlags.None))
        {
            if (ImGui.BeginTabBar("AirwaveTabs"))
            {
                if (ImGui.BeginTabItem("Listen")) { DrawListen(); ImGui.EndTabItem(); }
                if (ImGui.BeginTabItem("Broadcast")) { DrawBroadcast(); ImGui.EndTabItem(); }
                if (ImGui.BeginTabItem("Connection")) { DrawConnection(); ImGui.EndTabItem(); }
                ImGui.EndTabBar();
            }
        }
        ImGui.EndChild();
    }

    private void DrawListen()
    {
        ImGui.TextWrapped("1. Get a listener invite from the broadcaster.");
        ImGui.TextWrapped("2. Paste the complete invite below and choose Join broadcast.");
        ImGui.BeginDisabled(plugin.SessionActive);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##ListenerInvite", "Paste your Airwave listener invite", ref plugin.Invite, 4096, ImGuiInputTextFlags.Password);
        if (ImGui.Button("Join broadcast")) plugin.JoinBroadcast();
        ImGui.EndDisabled();
        if (string.IsNullOrWhiteSpace(plugin.Invite) && plugin.ListenerSetupProblem is null)
            ImGui.TextWrapped("Your saved connection is ready. Join broadcast reconnects without pasting the invite again.");
        else if (string.IsNullOrWhiteSpace(plugin.Invite))
            ImGui.TextWrapped("If your host gave you an address and listener key separately, enter them in Connection first.");
        ImGui.Spacing();
        DrawSession(false);
        if (plugin.Listening && ImGui.Button("Stop listening")) plugin.StopListening();
        ImGui.Spacing();
        ImGui.TextWrapped("Playback uses your Windows default output device. If you hear nothing, check that device, the Windows volume mixer, and Playback volume above.");
        ImGui.TextWrapped("Invites grant access to the audio. Keep yours private. Joining connects to the host's relay, which receives your network address and can access the audio.");
        DrawDetails();
    }

    private void DrawBroadcast()
    {
        ImGui.TextWrapped("Share only rekordbox audio. Your microphone and other apps are not captured.");
        ImGui.Spacing();
        ImGui.TextUnformatted("Try your sound first");
        ImGui.TextWrapped("Open rekordbox and play a track. For an ASIO controller, enable PC MASTER OUT. Use headphones; this test plays a delayed copy through your Windows default output device.");
        ImGui.BeginDisabled(plugin.SessionActive);
        if (ImGui.Button("Start local sound test")) plugin.StartLocalSoundTest();
        ImGui.EndDisabled();
        ImGui.TextWrapped("One click starts the local relay, captures rekordbox, and plays the stream back. The test stays on this PC; no hosted relay or connection keys are needed. Stop all audio ends it.");
        ImGui.Separator();
        ImGui.TextUnformatted("Broadcast to other people");
        Step(plugin.CaptureSetupProblem is null, "1. Audio components", plugin.CaptureSetupProblem ?? "Installed and supported on this PC.");
        Step(plugin.RekordboxReady, "2. Audio source", plugin.RekordboxHelp);
        Step(plugin.BroadcastSetupProblem is null, "3. Hosted relay and broadcast key", plugin.BroadcastSetupProblem ?? "Connection details are ready. The relay is checked when you go on air.");
        ImGui.TextWrapped("Set up a hosted Airwave relay or get its wss:// address, listener key, and separate broadcast key from its operator. Enter these under Connection.");
        ImGui.BeginDisabled(plugin.Busy || plugin.Publishing || plugin.LocalTest);
        if (ImGui.Button("Go on air")) plugin.StartBroadcast();
        ImGui.EndDisabled();
        ImGui.TextWrapped("4. When the status is On air, copy the listener invite and send it to your listeners.");
        ImGui.BeginDisabled(plugin.LocalTest || plugin.ListenerSetupProblem is not null);
        if (ImGui.Button("Copy listener invite")) plugin.Guard(() =>
        { ImGui.SetClipboardText(plugin.MakeInvite()); plugin.InviteCopied(); });
        ImGui.EndDisabled();
        if (plugin.ListenerSetupProblem is not null) ImGui.TextWrapped("Add the separate listener key in Connection before copying an invite.");
        ImGui.TextWrapped("The invite contains only the listener key. Anyone holding it can listen. Keep the broadcast key private; it allows someone to go on air.");
        DrawSession(true);
        DrawDetails();
    }

    private void DrawConnection()
    {
        ImGui.TextWrapped("Listeners can paste an invite in Listen instead. Broadcasters need a hosted relay address and two different keys from the relay operator.");
        ImGui.BeginDisabled(plugin.SessionActive);
        ImGui.TextWrapped("If the relay operator gave you a listener invite, import it here first, then add your broadcast key below.");
        ImGui.InputText("Listener invite", ref plugin.Invite, 4096, ImGuiInputTextFlags.Password);
        if (ImGui.Button("Use invite without connecting")) plugin.ImportInvite();
        ImGui.Spacing();
        ImGui.InputText("Relay address", ref plugin.RelayUrl, 1024);
        ImGui.TextDisabled("Example: wss://radio.example.org");
        ImGui.InputText("Listener key", ref plugin.ListenKey, 172, ImGuiInputTextFlags.Password);
        ImGui.InputText("Broadcast key", ref plugin.PublishKey, 172, ImGuiInputTextFlags.Password);
        ImGui.TextDisabled("Broadcast key is needed only to go on air.");
        if (ImGui.Button("Save connection")) plugin.Save();
        ImGui.SameLine();
        if (ImGui.Button("Clear saved connection")) plugin.ClearConnection();
        ImGui.EndDisabled();
        if (plugin.SessionActive) ImGui.TextWrapped("Stop all audio before changing the connection.");
        ImGui.Spacing();
        ImGui.TextWrapped("Saving does not start audio. Saved keys are protected for your Windows account. The window and all audio sessions stay closed when Airwave loads.");
        ImGui.TextWrapped("The local sound test needs no hosted relay and cannot be joined from another PC. Internet broadcasts require a reachable relay with TLS. Hosting is configured separately; Airwave does not open firewall ports.");
        DrawDetails();
    }

    private void DrawSession(bool publishing)
    {
        var status = publishing ? plugin.PublisherStatus : plugin.ListenerStatus;
        if (UiState.Problem(status, publishing) is { } problem) ImGui.TextWrapped(problem);
        if (publishing && UiState.Number(status, "Peak") is { } peak)
        {
            ImGui.ProgressBar((float)Math.Clamp(peak, 0, 1), new Vector2(-1, 18), "rekordbox audio level");
            if (plugin.Publishing) ImGui.TextWrapped("If the meter stays empty while a track plays, check PC MASTER OUT and rekordbox output settings.");
        }
        if (UiState.AudioLoss(status))
            ImGui.TextWrapped("Audio interruptions were detected in this session. Check your network and reduce background load. Details below show the counts.");
    }

    private void DrawDetails()
    {
        ImGui.Spacing();
        if (!ImGui.CollapsingHeader("Details and troubleshooting")) return;
        ImGui.TextWrapped("The silent engine check tests codecs and buffering. Use the local sound test to check capture and playback.");
        if (ImGui.Button("Check audio engine")) plugin.RunTest();
        if (plugin.TestStatus is { } test)
        {
            var stage = test.TryGetProperty("Stage", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            ImGui.TextUnformatted(stage == "TestPassed" ? "Engine check passed." : stage is "Error" or "Stopped" ? "Engine check did not complete." : "Engine check is running…");
            if (UiState.Problem(test, false) is { } problem) ImGui.TextWrapped(problem);
        }
        if (plugin.PublisherStatus is { } publisher) DrawMetrics("Broadcast", publisher);
        if (plugin.ListenerStatus is { } listener) DrawMetrics("Listener", listener);
        ImGui.TextWrapped("Process audio capture requires Windows build 20348 or newer (Windows 11 or a supported Windows Server). Listening does not use process capture.");
        ImGui.TextWrapped("Connection failures stop the session. Use Join broadcast or Go on air to retry after correcting the problem. Audio does not reconnect automatically.");
    }

    private static void Step(bool ready, string title, string help)
    {
        ImGui.TextUnformatted($"{title}: {(ready ? "Ready" : "Action needed")}");
        ImGui.TextWrapped(help);
    }

    private static void DrawMetrics(string label, JsonElement state)
    {
        ImGui.TextUnformatted(label);
        foreach (var (key, title) in new[]
        {
            ("FramesSent", "Packets sent"), ("FramesReceived", "Packets received"),
            ("BufferedMilliseconds", "Audio buffered (ms)"), ("Underruns", "Playback interruptions"),
            ("DroppedFrames", "20 ms packets dropped"), ("FirstAudioMilliseconds", "Initial audio ready (ms)"),
        })
            if (UiState.Number(state, key) is { } value) ImGui.TextUnformatted($"{title}: {value:0.##}");
        if (label == "Broadcast")
            ImGui.TextUnformatted(UiState.Number(state, "NativeCaptureDroppedFrames") is { } dropped
                ? $"Audio lost in capture (ms): {dropped / 48:0.##}" : "Audio lost in capture: awaiting measurement");
    }
}
