using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace GearsetOrganizer;

/// <summary>Owns native gearset operations. UI and optional IPC share this same guarded engine.</summary>
internal sealed partial class GearsetEngine : IDisposable
{
    private sealed record GearsetRequest(string Operation, string? Command, DateTimeOffset CreatedUtc, int ProcessId);

    private readonly IDalamudPluginInterface pi;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IObjectTable objects;
    private readonly ICondition condition;
    private readonly IDataManager data;
    private readonly IGameGui gameGui;
    private readonly string configDirectory;
    private bool disposed;
    private bool requestInProgress;

    public GearsetEngine(IDalamudPluginInterface pi, IFramework framework, IClientState clientState,
        IPlayerState playerState, IObjectTable objects, ICondition condition, IDataManager data, IGameGui gameGui)
    {
        this.pi = pi;
        this.framework = framework;
        this.clientState = clientState;
        this.playerState = playerState;
        this.objects = objects;
        this.condition = condition;
        this.data = data;
        this.gameGui = gameGui;
        configDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pi.GetPluginConfigDirectory()));
    }

    public string Request(string operation, string? token = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!framework.IsInFrameworkUpdateThread)
            throw new InvalidOperationException("Gearset requests must run on the game framework thread.");
        if (requestInProgress)
            throw new InvalidOperationException("Another gearset operation is already running.");
        if (operation is not ("gearset-inspect" or "gearset-status" or "gearset-apply" or "gearset-undo"))
            throw new InvalidOperationException("Unsupported gearset operation.");

        requestInProgress = true;
        try
        {
            return OrganizeGearsets(new(operation, token, DateTimeOffset.UtcNow, Environment.ProcessId));
        }
        finally
        {
            requestInProgress = false;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        preparedGearsets = null;
        gearsetUndoRecord = null;
    }
}
