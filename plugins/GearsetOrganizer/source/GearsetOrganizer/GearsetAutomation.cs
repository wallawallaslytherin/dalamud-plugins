using System.Collections;
using System.Reflection;
using Dalamud.Plugin;

namespace GearsetOrganizer;

internal sealed partial class GearsetEngine
{
    private GearsetAutomationState CaptureGearsetAutomation()
    {
        try
        {
            // Public metadata selects loaded copies, including developer plugins. No companion
            // is required: IPC is consulted only when its owning plugin is loaded.
            return GearsetAutomationPolicy.Capture(
                pi.InstalledPlugins.Where(plugin => plugin.IsLoaded).Select(plugin => plugin.InternalName),
                CaptureAutomationSignals);
        }
        catch (Exception ex)
        {
            return new GearsetAutomationState(
                new Dictionary<string, string> { ["Plugins.Error"] = ex.GetBaseException().Message },
                ["Installed automation state is unavailable: " + ex.GetBaseException().Message]);
        }
    }

    private IReadOnlyList<GearsetAutomationSignal> CaptureAutomationSignals(string name)
    {
        var signals = new List<GearsetAutomationSignal>();
        void Flag(string endpoint, bool idleValue = false)
        {
            var subscriber = pi.GetIpcSubscriber<bool>(endpoint);
            if (!subscriber.HasFunction)
                throw new InvalidOperationException($"The {endpoint} status provider is unavailable.");
            var value = subscriber.InvokeFunc();
            signals.Add(new GearsetAutomationSignal(endpoint, value.ToString(), value != idleValue));
        }

        switch (name)
        {
            case "GatherBuddyReborn":
                Flag("GatherBuddyReborn.IsAutoGatherEnabled");
                break;
            case "Artisan":
                Flag("Artisan.IsBusy");
                Flag("Artisan.GetEnduranceStatus");
                Flag("Artisan.IsListRunning");
                CaptureLegacyAutomation(name, signals);
                break;
            case "AutoDuty":
                Flag("AutoDuty.IsStopped", idleValue: true);
                Flag("AutoDuty.IsLooping");
                Flag("AutoDuty.IsNavigating");
                CaptureLegacyAutomation(name, signals);
                break;
            case "Lifestream":
                Flag("Lifestream.IsBusy");
                break;
            case "AutoRetainer":
                Flag("AutoRetainer.PluginState.IsBusy");
                Flag("AutoRetainer.PluginState.GetMultiModeStatus");
                CaptureLegacyAutomation(name, signals);
                break;
            case "GatherCraftControl":
            case "HunterV2":
                // These plugins do not expose idle IPC in the supported local versions.
                CaptureLegacyAutomation(name, signals);
                break;
            default:
                throw new InvalidOperationException($"No status adapter exists for {name}.");
        }

        return signals;
    }

    private static void CaptureLegacyAutomation(string name, List<GearsetAutomationSignal> signals)
    {
        // The legacy adapter also checks work queues omitted by otherwise public IPC.
        // Missing or changed bindings are errors, never an assumption of idle state.
        var instance = FindLegacyAutomationInstance(name);
        void Flag(string fieldName, object value)
        {
            if (value is not bool active)
                throw new InvalidOperationException($"{fieldName} did not return a boolean.");
            signals.Add(new GearsetAutomationSignal(fieldName, active.ToString(), active));
        }

        void Queue(string fieldName, object queue)
            => Flag(fieldName, RequireLegacyMember(queue, "IsBusy"));

        void State(string fieldName, object value, params string[] idleStates)
        {
            var state = value.ToString() ?? throw new InvalidOperationException($"{fieldName} is unavailable.");
            signals.Add(new GearsetAutomationSignal(fieldName, state, !idleStates.Contains(state, StringComparer.Ordinal)));
        }

        var assembly = instance.GetType().Assembly;
        if (name == "Artisan")
        {
            Queue("Artisan.Tasks", RequireLegacyMember(instance, "TM"));
            Queue("Artisan.CraftingTasks", RequireLegacyMember(instance, "CTM"));
            Queue("Artisan.RetainerTasks", RequireLegacyStatic(assembly, "Artisan.IPC.RetainerInfo", "TM"));
            State("Artisan.CraftingState", RequireLegacyStatic(assembly, "Artisan.GameInterop.Crafting", "CurState"), "IdleNormal", "IdleBetween");
            return;
        }

        if (name == "AutoDuty")
        {
            State("AutoDuty.Stage", RequireLegacyMember(instance, "Stage"), "Stopped", "None");
            Queue("AutoDuty.Tasks", RequireLegacyMember(instance, "taskManager"));
            return;
        }

        if (name == "AutoRetainer")
        {
            Queue("AutoRetainer.Tasks", RequireLegacyMember(instance, "TaskManager"));
            Queue("AutoRetainer.OfflineDataTasks", RequireLegacyMember(instance, "ODMTaskManager"));
            Flag("AutoRetainer.RetainerScheduler", RequireLegacyStatic(assembly, "AutoRetainer.Scheduler.SchedulerMain", "PluginEnabled"));
            Flag("AutoRetainer.VoyageScheduler", RequireLegacyStatic(assembly, "AutoRetainer.Modules.Voyage.VoyageScheduler", "Enabled"));
            return;
        }

        if (name == "GatherCraftControl")
        {
            var jobs = RequireLegacyMember(instance, "jobs");
            Flag("GatherCraftControl.CraftJobActive", RequireLegacyMember(jobs, "CraftJobActive"));
            Flag("GatherCraftControl.PurpleCraftingLoop", RequireLegacyMember(jobs, "PurpleScripLoopEnabled"));
            Flag("GatherCraftControl.OrangeCraftingLoop", RequireLegacyMember(jobs, "OrangeCraftingScripLoopEnabled"));
            foreach (var fieldName in new[] { "hunter", "storage", "elementalOverflow" })
                Flag($"GatherCraftControl.{fieldName}", RequireLegacyMember(RequireLegacyMember(jobs, fieldName), "IsRunning"));
            Flag("GatherCraftControl.GpRecoveryReduction", RequireLegacyMember(RequireLegacyMember(jobs, "gatherBuddy"), "gpRecoveryReductionActive"));
            // A present null request is idle. A missing field is an unknown binding.
            Flag("GatherCraftControl.ReductionRequest", ReadLegacyMember(jobs, "reductionRequest") != null);
            Flag("GatherCraftControl.MateriaExtraction", RequireLegacyMember(RequireLegacyMember(jobs, "materia"), "BlocksAutomation"));
            return;
        }

        if (name != "HunterV2")
            throw new InvalidOperationException($"No legacy status adapter exists for {name}.");
        Flag("HunterV2.AutoHunt", RequireLegacyMember(RequireLegacyMember(instance, "Configuration"), "AutoHuntEnabled"));
        var state = RequireLegacyMember(RequireLegacyMember(instance, "State"), "CurrentState").ToString()
                    ?? throw new InvalidOperationException("HunterV2 state is unavailable.");
        signals.Add(new GearsetAutomationSignal("HunterV2.State", state, state != "Idle"));
    }

    private static object FindLegacyAutomationInstance(string internalName)
    {
        // This lookup is restricted to the legacy adapters above and performs no
        // configuration writes, instance mutation, or loading/unloading operations.
        if (internalName is not ("GatherCraftControl" or "HunterV2" or "Artisan" or "AutoDuty" or "AutoRetainer"))
            throw new InvalidOperationException("Legacy plugin lookup is not allowed for this plugin.");
        var assembly = typeof(IDalamudPluginInterface).Assembly;
        var managerType = assembly.GetType("Dalamud.Plugin.Internal.PluginManager", throwOnError: true)!;
        var serviceType = assembly.GetType("Dalamud.Service`1", throwOnError: true)!.MakeGenericType(managerType);
        var getter = serviceType.GetMethod("Get", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            binder: null, Type.EmptyTypes, modifiers: null)
            ?? throw new MissingMethodException(serviceType.FullName, "Get");
        var manager = getter.Invoke(null, null)
                      ?? throw new InvalidOperationException("Dalamud plugin manager is unavailable.");
        var installed = RequireLegacyMember(manager, "InstalledPlugins") as IEnumerable
                        ?? throw new InvalidOperationException("Dalamud plugin entries are unavailable.");
        var candidates = installed.Cast<object>().Where(entry =>
            string.Equals(RequireLegacyMember(entry, "InternalName") as string, internalName, StringComparison.Ordinal) &&
            RequireLegacyMember(entry, "IsLoaded") is true).ToArray();
        if (candidates.Length != 1)
            throw new InvalidOperationException($"Expected one loaded {internalName} instance; found {candidates.Length}.");
        return RequireLegacyMember(candidates[0], "instance");
    }

    private static object RequireLegacyMember(object target, string name)
        => ReadLegacyMember(target, name)
           ?? throw new InvalidOperationException($"{target.GetType().FullName}.{name} is unavailable.");

    private static object RequireLegacyStatic(Assembly assembly, string typeName, string name)
    {
        var type = assembly.GetType(typeName, throwOnError: true)!;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        var field = type.GetField(name, flags);
        if (field != null)
            return field.GetValue(null) ?? throw new InvalidOperationException($"{typeName}.{name} is unavailable.");
        var property = type.GetProperty(name, flags) ?? throw new MissingMemberException(typeName, name);
        return property.GetValue(null) ?? throw new InvalidOperationException($"{typeName}.{name} is unavailable.");
    }

    private static object? ReadLegacyMember(object target, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                   BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (var type = target.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(name, flags);
            if (field != null)
                return field.GetValue(target);
            var property = type.GetProperty(name, flags);
            if (property != null)
                return property.GetValue(target);
        }
        throw new MissingMemberException(target.GetType().FullName, name);
    }
}
