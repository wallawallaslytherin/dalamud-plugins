using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace GearsetOrganizer;

internal sealed record GearsetItemSnapshot(int Slot, uint ItemId, uint GlamourId,
    byte Stain0Id, byte Stain1Id, ushort[] Materia, byte[] MateriaGrades, byte Flags);

internal sealed record GearsetEntrySnapshot(int Index, byte Id, string Name, byte ClassJob,
    string ClassJobName, short ItemLevel, byte Flags, byte GlamourSetLink, byte BannerIndex,
    ushort[] GlassesIds, GearsetItemSnapshot[] Items, string Fingerprint, string RawBase64);

internal sealed record GearsetHotbarSlotSnapshot(string Storage, int Group, int Hotbar, int Slot,
    byte CommandType, string CommandTypeName, uint CommandId);

internal sealed record GearsetMacroSnapshot(int Set, int Index, string Name, uint IconId,
    uint MacroIconRowId, string[] Lines, string[] LineBytesBase64, string Fingerprint, string RawNameBase64);

internal sealed record GearsetEquippedItemSnapshot(int Slot, uint ItemId, int Quantity,
    byte Flags, uint GlamourId, byte[] Stains, ushort[] Materia, byte[] MateriaGrades,
    ulong CrafterContentId, ushort Condition, ushort SpiritbondOrCollectability);

internal sealed record GearsetSaveState(bool HasChanges, bool IsSavePending, bool IsVirtual,
    ulong CharacterContentId);

internal sealed record GearsetSnapshot(DateTimeOffset CapturedUtc, int ProcessId, ulong ContentId,
    string CharacterName, bool IsLoggedIn, bool LocalPlayerPresent, uint TerritoryType,
    int CurrentGearsetIndex, byte NativeGearsetCount, GearsetEntrySnapshot[] Entries,
    GearsetHotbarSlotSnapshot[] HotbarSlots, GearsetMacroSnapshot[] Macros,
    GearsetEquippedItemSnapshot[] EquippedItems, string[] ActiveConditions, string[] VisibleAddons,
    bool HotbarModuleReady, bool HotbarDatFileLoaded, byte ActiveHotbarClassJobId,
    bool PvPHotbarsActive, byte[] HotbarShareStateBitmask, string SavedHotbarCommandsBase64,
    string ActiveHotbarCommandsBase64, string AllGearsetEntriesBase64,
    string OrderedGearsetsFingerprint, string HotbarFingerprint, string MacroFingerprint,
    string EquippedFingerprint, string RuntimeStructsVersion, int GearsetEntrySize,
    bool GearsetAgentAvailable, bool NativeReassignAddressResolved,
    GearsetSaveState GearsetSaveState, GearsetSaveState HotbarSaveState, GearsetSaveState MacroSaveState,
    string QuickPanelCommandsBase64 = "", string QuickPanelSettingsFingerprint = "");

/// <summary>
/// Read-only, bounded snapshots taken on Dalamud's framework thread. No native methods which
/// save, refresh, resolve icons, update gearsets, or load character files are called here.
/// </summary>
internal static unsafe class GearsetSnapshotReader
{
    internal static GearsetSnapshot Capture(IClientState clientState, IPlayerState playerState,
        IObjectTable objectTable, ICondition condition, IDataManager dataManager, IGameGui gameGui)
    {
        if (!clientState.IsLoggedIn || !playerState.IsLoaded || objectTable.LocalPlayer == null)
            throw new InvalidOperationException("A logged-in character and live local player are required for a gearset snapshot.");

        var gearsets = RaptureGearsetModule.Instance();
        var hotbars = RaptureHotbarModule.Instance();
        var macros = RaptureMacroModule.Instance();
        var quickPanels = QuickPanelModule.Instance();
        var inventory = InventoryManager.Instance();
        if (gearsets == null || hotbars == null || macros == null || quickPanels == null || inventory == null)
            throw new InvalidOperationException("A native gearset, hotbar, macro, quick-panel, or inventory module is unavailable.");
        if (gearsets->Entries.Length != 100 || hotbars->SavedHotbars.Length != 70 || hotbars->Hotbars.Length != 18 ||
            macros->Individual.Length != 100 || macros->Shared.Length != 100 || sizeof(RaptureGearsetModule.GearsetEntry) != 0x1C4)
            throw new InvalidOperationException("Unexpected native gearset/hotbar/macro layout; no snapshot was taken.");

        var jobSheet = dataManager.GetExcelSheet<ClassJob>(ClientLanguage.English);
        var entries = new List<GearsetEntrySnapshot>();
        for (var index = 0; index < gearsets->Entries.Length; index++)
        {
            ref var entry = ref gearsets->Entries[index];
            if ((entry.Flags & RaptureGearsetModule.GearsetFlag.Exists) == 0)
                continue;
            var itemSnapshots = new GearsetItemSnapshot[entry.Items.Length];
            for (var itemIndex = 0; itemIndex < entry.Items.Length; itemIndex++)
            {
                ref var item = ref entry.Items[itemIndex];
                itemSnapshots[itemIndex] = new(itemIndex, item.ItemId, item.GlamourId, item.Stain0Id, item.Stain1Id,
                    item.Materia.ToArray(), item.MateriaGrades.ToArray(), (byte)item.Flags);
            }
            var raw = new ReadOnlySpan<byte>(Unsafe.AsPointer(ref entry), sizeof(RaptureGearsetModule.GearsetEntry));
            var nameBytes = entry.Name;
            var nameEnd = nameBytes.IndexOf((byte)0);
            var name = Encoding.UTF8.GetString(nameEnd < 0 ? nameBytes : nameBytes[..nameEnd]);
            // Position and warning caches are intentionally excluded from identity. Preserve all
            // other entry flags, visibility bits, links, glasses, and saved item appearance data.
            var fingerprint = Hash(new
            {
                NameBytes = Convert.ToBase64String(nameEnd < 0 ? nameBytes : nameBytes[..nameEnd]),
                entry.ClassJob, entry.ItemLevel, entry.GlamourSetLink, entry.BannerIndex,
                Flags = (byte)(entry.Flags & ~RaptureGearsetModule.GearsetFlag.MainHandMissing),
                GlassesIds = entry.GlassesIds.ToArray(),
                Items = itemSnapshots.Select(item => new
                {
                    item.Slot, item.ItemId, item.GlamourId, item.Stain0Id, item.Stain1Id,
                    item.Materia, item.MateriaGrades,
                }).ToArray(),
            });
            entries.Add(new(index, entry.Id, name, entry.ClassJob, jobSheet.GetRow(entry.ClassJob).Name.ToString(),
                entry.ItemLevel, (byte)entry.Flags, entry.GlamourSetLink, entry.BannerIndex,
                entry.GlassesIds.ToArray(), itemSnapshots, fingerprint, Convert.ToBase64String(raw)));
        }

        var hotbarSlots = new List<GearsetHotbarSlotSnapshot>();
        using var savedCommands = new MemoryStream();
        using var activeCommands = new MemoryStream();
        using var quickPanelCommands = new MemoryStream();
        using (var savedWriter = new BinaryWriter(savedCommands, Encoding.UTF8, true))
        {
            for (var group = 0; group < hotbars->SavedHotbars.Length; group++)
            for (var bar = 0; bar < hotbars->SavedHotbars[group].Hotbars.Length; bar++)
            for (var slotIndex = 0; slotIndex < hotbars->SavedHotbars[group].Hotbars[bar].Slots.Length; slotIndex++)
            {
                ref var slot = ref hotbars->SavedHotbars[group].Hotbars[bar].Slots[slotIndex];
                savedWriter.Write((byte)slot.CommandType);
                savedWriter.Write(slot.CommandId);
                if (slot.CommandType != RaptureHotbarModule.HotbarSlotType.Empty)
                    hotbarSlots.Add(new("Saved", group, bar, slotIndex, (byte)slot.CommandType,
                        slot.CommandType.ToString(), slot.CommandId));
            }
        }
        using (var activeWriter = new BinaryWriter(activeCommands, Encoding.UTF8, true))
        {
            for (var bar = 0; bar < hotbars->Hotbars.Length; bar++)
            for (var slotIndex = 0; slotIndex < hotbars->Hotbars[bar].Slots.Length; slotIndex++)
            {
                ref var slot = ref hotbars->Hotbars[bar].Slots[slotIndex];
                activeWriter.Write((byte)slot.CommandType);
                activeWriter.Write(slot.CommandId);
                if (slot.CommandType != RaptureHotbarModule.HotbarSlotType.Empty)
                    hotbarSlots.Add(new("Active", hotbars->ActiveHotbarClassJobId, bar, slotIndex,
                        (byte)slot.CommandType, slot.CommandType.ToString(), slot.CommandId));
            }
        }
        using (var quickPanelWriter = new BinaryWriter(quickPanelCommands, Encoding.UTF8, true))
        {
            CaptureQuickPanel(0, quickPanels->Panel0CommandTypes, quickPanels->Panel0CommandIds, quickPanelWriter, hotbarSlots);
            CaptureQuickPanel(1, quickPanels->Panel1CommandTypes, quickPanels->Panel1CommandIds, quickPanelWriter, hotbarSlots);
            CaptureQuickPanel(2, quickPanels->Panel2CommandTypes, quickPanels->Panel2CommandIds, quickPanelWriter, hotbarSlots);
            CaptureQuickPanel(3, quickPanels->Panel3CommandTypes, quickPanels->Panel3CommandIds, quickPanelWriter, hotbarSlots);
        }
        var quickPanelSettingsFingerprint = Hash(new
        {
            Settings = (byte)quickPanels->Settings, Flags = (byte)quickPanels->Flags,
            Tint = quickPanels->Tint, PanelOpenIndex = quickPanels->PanelOpenIndex,
        });

        var macroSnapshots = new List<GearsetMacroSnapshot>(200);
        for (var set = 0; set < 2; set++)
        for (var index = 0; index < 100; index++)
        {
            ref var macro = ref (set == 0 ? ref macros->Individual[index] : ref macros->Shared[index]);
            var lines = new string[macro.Lines.Length];
            var encodedLines = new string[macro.Lines.Length];
            for (var lineIndex = 0; lineIndex < macro.Lines.Length; lineIndex++)
            {
                var line = ReadStringBytes(in macro.Lines[lineIndex]);
                lines[lineIndex] = Encoding.UTF8.GetString(line);
                encodedLines[lineIndex] = Convert.ToBase64String(line);
            }
            var nameBytes = ReadStringBytes(in macro.Name);
            var name = Encoding.UTF8.GetString(nameBytes);
            var fingerprint = Hash(new { NameBytes = Convert.ToBase64String(nameBytes), macro.IconId,
                macro.MacroIconRowId, Lines = encodedLines });
            macroSnapshots.Add(new(set, index, name, macro.IconId, macro.MacroIconRowId, lines, encodedLines, fingerprint, Convert.ToBase64String(nameBytes)));
        }

        var equipped = inventory->GetInventoryContainer(InventoryType.EquippedItems);
        if (equipped == null || !equipped->IsLoaded || equipped->Items == null || equipped->Size != 14)
            throw new InvalidOperationException("The native 14-slot equipped inventory is not fully loaded.");
        var equippedItems = new GearsetEquippedItemSnapshot[equipped->Size];
        for (var index = 0; index < equipped->Size; index++)
        {
            var item = &equipped->Items[index];
            if (item->IsSymbolic)
                throw new InvalidOperationException("Unexpected symbolic item in equipped inventory.");
            equippedItems[index] = new(index, item->ItemId, item->Quantity, (byte)item->Flags,
                item->GlamourId, item->Stains.ToArray(), item->Materia.ToArray(), item->MateriaGrades.ToArray(),
                item->CrafterContentId, item->Condition, item->SpiritbondOrCollectability);
        }

        string[] relevantAddons = ["GearSetList", "Character", "BannerEditor", "BannerGearsetLink",
            "GearSetPreview", "GearSetView", "SelectYesno", "SelectString", "SelectIconString",
            "InputString", "Macro", "Synthesis", "SynthesisSimple", "Gathering", "Fishing",
            "MateriaAttach", "MateriaAttachDialog", "MateriaRetrieve", "RetainerList", "RetainerSellList"];
        var visibleAddons = relevantAddons.Where(name =>
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(name);
            return addon != null && addon->IsVisible;
        }).ToArray();
        var savedBytes = savedCommands.ToArray();
        var activeBytes = activeCommands.ToArray();
        var quickPanelBytes = quickPanelCommands.ToArray();
        var shares = hotbars->HotbarShareStateBitmask.ToArray();
        var rawEntries = new ReadOnlySpan<byte>(Unsafe.AsPointer(ref gearsets->Entries[0]),
            gearsets->Entries.Length * sizeof(RaptureGearsetModule.GearsetEntry));
        return new(DateTimeOffset.UtcNow, Environment.ProcessId, playerState.ContentId, playerState.CharacterName,
            clientState.IsLoggedIn, objectTable.LocalPlayer != null, clientState.TerritoryType,
            gearsets->CurrentGearsetIndex, gearsets->NumGearsets, entries.ToArray(), hotbarSlots.ToArray(),
            macroSnapshots.ToArray(), equippedItems, condition.AsReadOnlySet().Select(flag => flag.ToString()).Order().ToArray(),
            visibleAddons, hotbars->ModuleReady, hotbars->DatFileLoadedSuccessfully, hotbars->ActiveHotbarClassJobId,
            hotbars->PvPHotbarsActive, shares, Convert.ToBase64String(savedBytes), Convert.ToBase64String(activeBytes),
            Convert.ToBase64String(rawEntries), Hash(entries.Select(entry => new { entry.Index, entry.Id, entry.Fingerprint }).ToArray()),
            Hash(new { Saved = Convert.ToBase64String(savedBytes), Active = Convert.ToBase64String(activeBytes), Shares = shares,
                QuickPanel = Convert.ToBase64String(quickPanelBytes), QuickPanelSettings = quickPanelSettingsFingerprint }),
            Hash(macroSnapshots.Select(macro => new { macro.Set, macro.Index, macro.Fingerprint }).ToArray()),
            Hash(equippedItems), typeof(RaptureGearsetModule).Assembly.GetName().Version?.ToString() ?? "unknown",
            sizeof(RaptureGearsetModule.GearsetEntry), AgentGearSet.Instance() != null,
            AgentGearSet.Addresses.ReassignGearsetId.Value != 0,
            SaveState((UserFileManager.UserFileEvent*)gearsets), SaveState((UserFileManager.UserFileEvent*)hotbars),
            SaveState((UserFileManager.UserFileEvent*)macros), Convert.ToBase64String(quickPanelBytes), quickPanelSettingsFingerprint);
    }

    private static void CaptureQuickPanel(int panel, ReadOnlySpan<RaptureHotbarModule.HotbarSlotType> types,
        ReadOnlySpan<uint> ids, BinaryWriter writer, List<GearsetHotbarSlotSnapshot> slots)
    {
        if (types.Length != 25 || ids.Length != 25)
            throw new InvalidOperationException("Unexpected native quick-panel layout; no snapshot was taken.");
        for (var index = 0; index < types.Length; index++)
        {
            writer.Write((byte)types[index]);
            writer.Write(ids[index]);
            if (types[index] != RaptureHotbarModule.HotbarSlotType.Empty)
                slots.Add(new("QuickPanel", panel, 0, index, (byte)types[index], types[index].ToString(), ids[index]));
        }
    }

    private static byte[] ReadStringBytes(in Utf8String value)
    {
        if (value.BufUsed < 0 || value.BufUsed > 4096 || value.StringLength < 0 || value.StringLength > 4095)
            throw new InvalidOperationException("A native macro string has an unexpected length.");
        if (value.Length == 0) return [];
        if ((byte*)value.StringPtr == null)
            throw new InvalidOperationException("A nonempty native macro string has no data pointer.");
        return value.AsSpan().ToArray();
    }

    private static GearsetSaveState SaveState(UserFileManager.UserFileEvent* value) =>
        new(value->HasChanges, value->IsSavePending, value->IsVirtual, value->CharacterContentId);

    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
