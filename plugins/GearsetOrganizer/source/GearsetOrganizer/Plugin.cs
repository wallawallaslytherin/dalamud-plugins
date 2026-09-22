using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System.Text.Json;

namespace GearsetOrganizer;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/gearsets";
    private readonly IDalamudPluginInterface pi;
    private readonly ICommandManager commands;
    private readonly IFramework framework;
    private readonly IChatGui chat;
    private readonly IPluginLog log;
    private readonly GearsetEngine engine;
    private readonly OrganizerIpc? ipc;
    private readonly WindowSystem windows = new("GearsetOrganizer");
    private readonly OrganizerWindow window;
    private bool registered;
    private volatile bool disposed;
    private int busy;
    private int statusCommands;
    private int testCommands;

    internal OrganizerView View { get; private set; } = OrganizerView.Empty;
    internal bool Busy => Volatile.Read(ref busy) != 0;
    internal bool EngineAvailable => !disposed;
    internal string BusyMessage { get; private set; } = "";
    internal string LastResult { get; private set; } = "";
    internal string LastError { get; private set; } = "";

    public object RuntimeStatus => new
    {
        Version = "0.1.0.0", ProcessId = Environment.ProcessId, Loaded = !disposed,
        WindowOpen = window.IsOpen, CommandRegistered = registered, EngineAvailable,
        Standalone = true, RequiredPlugins = Array.Empty<string>(),
        ExternalControlAvailable = ipc != null,
        CurrentPreview = View.SnapshotId, View.ContentId, View.CharacterName, View.SetCount, View.MoveCount,
        View.CanApply, View.AlreadySorted, View.UndoAvailable, View.UndoReason,
        Busy, BusyMessage, LastResult, LastError, StatusCommands = statusCommands, TestCommands = testCommands,
    };

    public Plugin(IDalamudPluginInterface pi, ICommandManager commands, IFramework framework, IChatGui chat, IPluginLog log,
        IClientState clientState, IPlayerState playerState, IObjectTable objects, ICondition condition, IDataManager data, IGameGui gameGui)
    {
        this.pi = pi; this.commands = commands; this.framework = framework; this.chat = chat; this.log = log;
        engine = new GearsetEngine(pi, framework, clientState, playerState, objects, condition, data, gameGui);
        window = new OrganizerWindow(this) { IsOpen = false };
        windows.AddWindow(window);
        try
        {
            if (commands.Commands.ContainsKey(Command))
                throw new InvalidOperationException("/gearsets is already registered by another plugin.");
            registered = commands.AddHandler(Command, new CommandInfo(OnCommand)
            {
                HelpMessage = "Open Gearset Organizer. /gearsets status or /gearsets test checks the plugin without sorting.",
                ShowInHelp = true,
            });
            if (!registered) throw new InvalidOperationException("/gearsets could not be registered.");
            pi.UiBuilder.Draw += windows.Draw;
            pi.UiBuilder.OpenMainUi += Open;
            pi.UiBuilder.OpenConfigUi += Open;
            try { ipc = new OrganizerIpc(pi, framework, engine); }
            catch (Exception ex) { log.Warning(ex, "Optional Gearset Organizer external control could not be registered; the window remains available."); }
            log.Information("Gearset Organizer 0.1.0.0 loaded with standalone native engine; /gearsets registered; window closed on startup.");
        }
        catch { Dispose(); throw; }
    }

    private void Open() { if (!disposed) window.IsOpen = true; }

    private void OnCommand(string _, string arguments)
    {
        switch (arguments.Trim().ToLowerInvariant())
        {
            case "": window.IsOpen = !window.IsOpen; break;
            case "status": Interlocked.Increment(ref statusCommands); Begin("gearset-status", null, true); break;
            case "test": Interlocked.Increment(ref testCommands); Begin("gearset-status", null, true, true); break;
            default: chat.Print("[Gearset Organizer] Use /gearsets, /gearsets status, or /gearsets test."); break;
        }
    }

    internal void RefreshPreview() => Begin("gearset-inspect");

    internal void Sort()
    {
        var view = View;
        if (!view.HasPreview || view.PreviewExpired || !view.CanApply || view.MoveCount == 0) return;
        Begin("gearset-apply", view.SnapshotId);
    }

    internal void Undo()
    {
        var view = View;
        if (!view.UndoAvailable || !Guid.TryParse(view.UndoSnapshotId, out _)) return;
        Begin("gearset-undo", view.UndoSnapshotId);
    }

    private void Begin(string operation, string? token = null, bool print = false, bool test = false)
    {
        if (disposed || Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
        BusyMessage = operation switch
        {
            "gearset-inspect" => "Reading the current gearsets and references…",
            "gearset-apply" => "Sorting and verifying gearsets…",
            "gearset-undo" => "Restoring and verifying the previous order…",
            _ => "Checking Gearset Organizer…",
        };
        LastError = "";
        if (operation is "gearset-apply" or "gearset-undo")
            View = View with { CanApply = false, UndoAvailable = false };
        _ = RunAsync(operation, token, print, test);
    }

    private async Task RunAsync(string operation, string? token, bool print, bool test)
    {
        var mutationCompleted = false;
        try
        {
            var response = await RequestAsync(operation, token);
            if (disposed) return;
            switch (operation)
            {
                case "gearset-inspect":
                    View = OrganizerView.Preview(response);
                    LastResult = $"Preview refreshed: {View.SetCount} saved gearsets.";
                    break;
                case "gearset-status":
                    View = View.WithStatus(response);
                    LastResult = $"{(test ? "Read-only test passed" : "Organizer ready")}: {View.SetCount} gearsets on {View.CharacterName}.";
                    if (View.Blockers.Length > 0) LastResult += " Sorting is currently blocked.";
                    if (print) await framework.RunOnFrameworkThread(() => { if (!disposed) chat.Print("[Gearset Organizer] " + LastResult); });
                    log.Information("Gearset Organizer {Check}: {Result}", test ? "test" : "status", LastResult);
                    break;
                default:
                    mutationCompleted = true;
                    LastResult = operation == "gearset-undo"
                        ? $"Previous order restored and verified for {JsonValue.Number(response, "SetCount")} gearsets."
                        : $"Sorted and verified {JsonValue.Number(response, "SetCount")} gearsets. Equipped gear is unchanged.";
                    var actionResult = LastResult;
                    View = OrganizerView.Empty;
                    // One explicit follow-up read updates the preview and undo token after this
                    // action. There is no polling loop and no automatic mutation retry.
                    var fresh = await RequestAsync("gearset-inspect");
                    if (disposed) return;
                    View = OrganizerView.Preview(fresh);
                    LastResult = actionResult;
                    log.Information("Gearset Organizer action completed: {Result}", actionResult);
                    break;
            }
        }
        catch (Exception ex)
        {
            if (disposed) return;
            View = View with { CanApply = false, UndoAvailable = false };
            LastError = mutationCompleted
                ? "The action completed, but refreshing its preview failed: " + ex.Message
                : ex.Message;
            log.Warning(ex, "Gearset Organizer request {Operation} stopped", operation);
            if (print)
                await framework.RunOnFrameworkThread(() => { if (!disposed) chat.PrintError("[Gearset Organizer] " + LastError); });
        }
        finally
        {
            BusyMessage = "";
            Interlocked.Exchange(ref busy, 0);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ipc?.Dispose();
        engine.Dispose();
        pi.UiBuilder.Draw -= windows.Draw;
        pi.UiBuilder.OpenMainUi -= Open;
        pi.UiBuilder.OpenConfigUi -= Open;
        if (registered) { commands.RemoveHandler(Command); registered = false; }
        windows.RemoveAllWindows();
    }

    private async Task<JsonElement> RequestAsync(string operation, string? token = null)
    {
        var json = await framework.RunOnFrameworkThread(() => engine.Request(operation, token));
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
