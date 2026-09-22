namespace GearsetOrganizer;

internal sealed record GearsetAutomationSignal(string Name, string State, bool Busy);
internal sealed record GearsetAutomationState(Dictionary<string, string> Status, string[] Blockers);

internal static class GearsetAutomationPolicy
{
    private static readonly string[] KnownPlugins =
    [
        "GatherCraftControl", "GatherBuddyReborn", "Artisan", "HunterV2",
        "AutoDuty", "Lifestream", "AutoRetainer",
    ];

    internal static GearsetAutomationState Capture(
        IEnumerable<string> loadedPluginNames,
        Func<string, IReadOnlyList<GearsetAutomationSignal>> capture)
    {
        var loaded = loadedPluginNames.ToHashSet(StringComparer.Ordinal);
        var status = new Dictionary<string, string>(StringComparer.Ordinal);
        var blockers = new List<string>();
        foreach (var name in KnownPlugins)
        {
            if (!loaded.Contains(name))
            {
                status[name] = "NotLoaded";
                continue;
            }

            status[name] = "Loaded";
            try
            {
                var signals = capture(name);
                if (signals.Count == 0)
                    throw new InvalidOperationException("No idle-state observations are available.");
                foreach (var signal in signals)
                {
                    status[signal.Name] = signal.State;
                    if (signal.Busy)
                        blockers.Add($"{signal.Name} is {signal.State}.");
                }
            }
            catch (Exception ex)
            {
                var reason = ex.GetBaseException().Message;
                status[name + ".Error"] = reason;
                blockers.Add($"{name} idle state is unknown: {reason}");
            }
        }

        return new GearsetAutomationState(status, blockers.ToArray());
    }
}
