using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace GearsetOrganizer;

/// <summary>Optional external control; the window calls the native engine directly.</summary>
internal sealed class OrganizerIpc : IDisposable
{
    private readonly ICallGateProvider<string, string> endpoint;
    private readonly IFramework framework;
    private readonly GearsetEngine engine;
    private bool disposed;

    public OrganizerIpc(IDalamudPluginInterface pi, IFramework framework, GearsetEngine engine)
    {
        this.framework = framework;
        this.engine = engine;
        endpoint = pi.GetIpcProvider<string, string>("GearsetOrganizer.Request");
        endpoint.RegisterFunc(Handle);
    }

    private string Handle(string json)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!framework.IsInFrameworkUpdateThread)
            throw new InvalidOperationException("Gearset requests must run on the game framework thread.");
        if (json.Length > 8192) throw new InvalidDataException("Gearset request exceeds the size limit.");
        var request = JsonSerializer.Deserialize<ExternalRequest>(json)
            ?? throw new InvalidDataException("Gearset request is empty.");
        if (!Guid.TryParse(request.RequestId, out _))
            throw new InvalidDataException("Gearset request requires a valid request ID.");
        if (request.Operation is "gearset-apply" or "gearset-undo")
        {
            var age = DateTimeOffset.UtcNow - request.CreatedUtc;
            if (request.ProcessId != Environment.ProcessId || age < TimeSpan.FromSeconds(-5) || age > TimeSpan.FromSeconds(30))
                throw new InvalidOperationException("The gearset request is stale or belongs to another game process.");
        }
        return engine.Request(request.Operation, request.Command);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        endpoint.UnregisterFunc();
    }

    private sealed record ExternalRequest(string RequestId, string Operation, DateTimeOffset CreatedUtc,
        string? Command = null, int? ProcessId = null);
}
