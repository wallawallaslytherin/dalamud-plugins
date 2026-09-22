using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace GearsetOrganizer;

internal sealed class OrganizerWindow(Plugin plugin) : Window("Gearset Organizer###GearsetOrganizerMain")
{
    public override void OnOpen()
    {
        Size = new Vector2(830, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        plugin.RefreshPreview();
    }

    public override void Draw()
    {
        var view = plugin.View;
        var available = plugin.EngineAvailable;
        ImGui.TextWrapped("Group your saved gearsets by role and job. Alternate sets stay together, with names and saved equipment preserved.");
        ImGui.Spacing();
        if (!available)
            ImGui.TextColored(new Vector4(1f, .65f, .4f, 1f), "Gearset Organizer is unavailable.");
        ImGui.BeginDisabled(plugin.Busy || !available);
        if (ImGui.Button("Refresh preview")) plugin.RefreshPreview();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(plugin.Busy || !available || !view.HasPreview || view.PreviewExpired || !view.CanApply || view.MoveCount == 0);
        if (ImGui.Button("Sort gearsets")) plugin.Sort();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(plugin.Busy || !available || !view.UndoAvailable || view.UndoSnapshotId.Length == 0);
        if (ImGui.Button("Undo last sort")) plugin.Undo();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !view.UndoAvailable)
            ImGui.SetTooltip(view.UndoReason.Length == 0 ? "No verified sort is available to undo." : view.UndoReason);

        if (plugin.Busy) ImGui.TextWrapped(plugin.BusyMessage);
        if (plugin.LastResult.Length > 0) ImGui.TextWrapped(plugin.LastResult);
        if (plugin.LastError.Length > 0)
            ImGui.TextColored(new Vector4(1f, .55f, .45f, 1f), "Request stopped:");
        if (plugin.LastError.Length > 0) ImGui.TextWrapped(plugin.LastError);
        if (view.PreviewExpired) ImGui.TextWrapped("This preview has expired. Refresh it before sorting.");
        foreach (var blocker in view.Blockers) ImGui.TextWrapped("Blocked: " + blocker);
        ImGui.Separator();

        if (!view.HasPreview)
        {
            ImGui.TextWrapped("Read a preview to see each set's current and proposed number.");
            return;
        }
        ImGui.TextUnformatted($"{view.CharacterName}  |  {view.SetCount} sets  |  {view.MoveCount} swaps");
        if (view.AlreadySorted) ImGui.TextWrapped("Your gearsets are already in the planned order.");
        else ImGui.TextWrapped("Preview order: tanks, healers, melee, ranged, casters, limited jobs, crafters, gatherers.");
        if (view.MacroEditCount > 0)
            ImGui.TextWrapped($"{view.MacroEditCount} numbered macro line(s) will follow their original gearsets.");
        ImGui.TextWrapped("Sorting keeps your equipped gear unchanged. Existing gear warnings are preserved.");
        ImGui.Spacing();
        if (ImGui.BeginTable("GearsetOrder", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
                new Vector2(0, Math.Max(140, ImGui.GetContentRegionAvail().Y))))
        {
            ImGui.TableSetupColumn("Role", ImGuiTableColumnFlags.WidthStretch, 1.1f);
            ImGui.TableSetupColumn("Current", ImGuiTableColumnFlags.WidthFixed, 58);
            ImGui.TableSetupColumn("After", ImGuiTableColumnFlags.WidthFixed, 52);
            ImGui.TableSetupColumn("Gearset", ImGuiTableColumnFlags.WidthStretch, 1.6f);
            ImGui.TableSetupColumn("Job", ImGuiTableColumnFlags.WidthStretch, 1.1f);
            ImGui.TableSetupColumn("Item level", ImGuiTableColumnFlags.WidthFixed, 68);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();
            string? lastRole = null;
            foreach (var row in view.Rows)
            {
                ImGui.TableNextRow(); ImGui.TableNextColumn();
                if (lastRole != row.Role) ImGui.TextWrapped(row.Role);
                lastRole = row.Role;
                ImGui.TableNextColumn(); ImGui.TextUnformatted(row.OldNumber.ToString());
                ImGui.TableNextColumn(); ImGui.TextUnformatted(row.NewNumber.ToString());
                ImGui.TableNextColumn(); ImGui.TextWrapped(row.Name + (row.Equipped ? " (equipped)" : ""));
                ImGui.TableNextColumn(); ImGui.TextWrapped(row.Job);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(row.ItemLevel.ToString());
            }
            ImGui.EndTable();
        }
    }
}
