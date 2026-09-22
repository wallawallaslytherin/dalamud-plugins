using System.Text;
using System.Text.Json;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using GearsetOrganizer.Core;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.System.String;

namespace GearsetOrganizer;

internal sealed partial class GearsetEngine
{
    private sealed record GearsetMacroEdit(int Set, int Index, int Line, string Before, string After);
    private sealed record PreparedGearsets(Guid Id, DateTimeOffset CreatedUtc, GearsetSnapshot Snapshot,
        GearsetOrganizationPlan Plan, GearsetMacroEdit[] MacroEdits, string[] Blockers, string Directory);
    private PreparedGearsets? preparedGearsets;
    private static readonly JsonSerializerOptions GearsetJson = new() { WriteIndented = true };
    private sealed record GearsetUndoIndex(int SchemaVersion, Guid SnapshotId, int ProcessId, ulong ContentId,
        string RunDirectory, string State, DateTimeOffset UpdatedUtc);
    private sealed record GearsetUndoRecord(GearsetUndoIndex Index, GearsetSnapshot Before,
        GearsetSnapshot After, GearsetOrganizationPlan Plan, GearsetMacroEdit[] MacroEdits);
    private sealed record GearsetUndoStatus(bool UndoAvailable, string UndoReason, Guid? UndoSnapshotId);
    private GearsetUndoRecord? gearsetUndoRecord;
    private ulong loadedGearsetUndoCharacter;
    private bool gearsetUndoLoaded;
    private string gearsetUndoLoadReason = "No completed sort is available to undo.";

    private unsafe string OrganizeGearsets(GearsetRequest request)
    {
        if (request.Operation is not ("gearset-inspect" or "gearset-apply" or "gearset-undo" or "gearset-status"))
            throw new InvalidOperationException("Unsupported gearset operation.");
        GearsetSnapshot Capture() => GearsetSnapshotReader.Capture(clientState, playerState, objects, condition, data, gameGui);
        var snapshot = Capture();
        var automation = CaptureGearsetAutomation();
        var blockers = GearsetStateBlockers(snapshot).Concat(automation.Blockers).ToList();
        if (data.Language != ClientLanguage.English &&
            snapshot.Macros.Any(macro => macro.Lines.Any(line => !string.IsNullOrWhiteSpace(line))))
            blockers.Add("Sorting with saved macros currently requires an English game client; localized macro references have not been validated.");
        var undo = ReadGearsetUndoStatus(snapshot, blockers);

        if (request.Operation == "gearset-status")
        {
            var currentPlan = GearsetOrganization.CreatePlan(snapshot.Entries.Select(e => new GearsetIdentity(e.Index, e.ClassJob, e.Name, e.Fingerprint)));
            return JsonSerializer.Serialize(new { snapshot.ProcessId, snapshot.CharacterName, snapshot.ContentId,
                SetCount = snapshot.Entries.Length, AlreadySorted = currentPlan.Moves.Count == 0,
                Blockers = blockers, Automation = automation, undo.UndoAvailable, undo.UndoReason, undo.UndoSnapshotId });
        }
        if (request.Operation == "gearset-undo")
            return UndoGearsets(request, snapshot, blockers, Capture);

        if (request.Operation == "gearset-inspect")
        {
            var plan = GearsetOrganization.CreatePlan(snapshot.Entries.Select(e => new GearsetIdentity(e.Index, e.ClassJob, e.Name, e.Fingerprint)));
            var numberMap = plan.Assignments.ToDictionary(a => a.Gearset.OriginalIndex + 1, a => a.TargetIndex + 1);
            var edits = new List<GearsetMacroEdit>();
            foreach (var macro in snapshot.Macros)
            {
                for (var line = 0; line < macro.Lines.Length; line++)
                {
                    var result = GearsetMacroReferences.AnalyzeLine(macro.Lines[line], numberMap);
                    var context = $"Macro {macro.Set}/{macro.Index + 1} line {line + 1}";
                    if (result.Blocker != null) blockers.Add($"{context}: {result.Blocker}");
                    if (result.NamedReference != null)
                    {
                        var original = snapshot.Entries.OrderBy(e => e.Index).FirstOrDefault(e => e.Name.StartsWith(result.NamedReference, StringComparison.Ordinal));
                        var final = plan.Assignments.FirstOrDefault(a => a.Gearset.Name.StartsWith(result.NamedReference, StringComparison.Ordinal));
                        if (original == null || final == null || original.Index != final.Gearset.OriginalIndex)
                            blockers.Add($"{context}: name reference '{result.NamedReference}' does not retain a unique original target after sorting.");
                    }
                    if (result.Changed)
                    {
                        if (Convert.ToBase64String(Encoding.UTF8.GetBytes(macro.Lines[line])) != macro.LineBytesBase64[line])
                            blockers.Add($"{context}: encoded payload requires an explicit native-preserving edit.");
                        else edits.Add(new(macro.Set, macro.Index, line, macro.Lines[line], result.UpdatedLine));
                    }
                }
            }
            foreach (var group in edits.GroupBy(e => (e.Set, e.Index)))
            {
                var macro = snapshot.Macros.Single(m => m.Set == group.Key.Set && m.Index == group.Key.Index);
                var updatedBytes = macro.LineBytesBase64.Select((original, line) =>
                {
                    var edit = group.SingleOrDefault(e => e.Line == line);
                    return edit == null ? Convert.FromBase64String(original) : Encoding.UTF8.GetBytes(edit.After);
                }).ToArray();
                if (updatedBytes.Any(bytes => bytes.Length > 180 || bytes.Any(value => value < 32 && value != 9)))
                    blockers.Add($"Macro {macro.Set}/{macro.Index + 1} requires an opaque or oversized native macro rewrite.");
                if (updatedBytes.Any(bytes => bytes.Contains((byte)0) || bytes.Contains((byte)'\n') || bytes.Contains((byte)'\r')))
                    blockers.Add($"Macro {macro.Set}/{macro.Index + 1} contains embedded native delimiters.");
            }

            var id = Guid.NewGuid();
            var folder = ValidateGearsetRunDirectory(Path.Combine(GearsetUndoRoot, snapshot.ContentId.ToString("X16"), "runs", id.ToString()));
            Directory.CreateDirectory(folder);
            preparedGearsets = new(id, DateTimeOffset.UtcNow, snapshot, plan, edits.ToArray(), blockers.ToArray(), folder);
            File.WriteAllText(Path.Combine(folder, "before.json"), JsonSerializer.Serialize(snapshot, GearsetJson));
            File.WriteAllText(Path.Combine(folder, "plan.json"), JsonSerializer.Serialize(new { Plan = plan, MacroEdits = edits, Automation = automation, Blockers = blockers }, GearsetJson));
            return JsonSerializer.Serialize(new { SnapshotId = id, snapshot.ProcessId, snapshot.CharacterName,
                snapshot.ContentId, SetCount = snapshot.Entries.Length, CanApply = blockers.Count == 0,
                AlreadySorted = plan.Moves.Count == 0 && edits.Count == 0,
                undo.UndoAvailable, undo.UndoReason, undo.UndoSnapshotId,
                Blockers = blockers, Automation = automation, Plan = plan, MacroEdits = edits,
                Directory = folder, Snapshot = snapshot });
        }

        var prepared = preparedGearsets ?? throw new InvalidOperationException("Read a fresh gearset snapshot before applying.");
        var age = DateTimeOffset.UtcNow - request.CreatedUtc;
        if (!Guid.TryParse(request.Command, out var requestedId) || requestedId != prepared.Id || request.ProcessId != snapshot.ProcessId ||
            age < TimeSpan.FromSeconds(-5) || age > TimeSpan.FromSeconds(30) ||
            DateTimeOffset.UtcNow - prepared.CreatedUtc > TimeSpan.FromMinutes(10))
            throw new InvalidOperationException("The gearset apply request is stale or does not match this process and prepared snapshot.");
        if (prepared.Blockers.Length != 0 || blockers.Count != 0)
            throw new InvalidOperationException("Gearset application is blocked: " + string.Join("; ", prepared.Blockers.Concat(blockers)));
        RequireSameGearsetSnapshot(prepared.Snapshot, snapshot);
        if (prepared.Plan.Moves.Count == 0 && prepared.MacroEdits.Length == 0)
            return JsonSerializer.Serialize(new { Applied = false, AlreadySorted = true, SnapshotId = prepared.Id,
                snapshot.ProcessId, snapshot.CharacterName, snapshot.ContentId, SetCount = snapshot.Entries.Length,
                Moves = 0, MacroLinesUpdated = 0, undo.UndoAvailable, undo.UndoReason, undo.UndoSnapshotId,
                Directory = prepared.Directory, Snapshot = snapshot });
        if (AgentGearSet.Instance() == null || AgentGearSet.MemberFunctionPointers.ReassignGearsetId == null)
            throw new InvalidOperationException("Native gearset reassignment is unavailable.");
        if (prepared.MacroEdits.Length != 0 && (RaptureMacroModule.MemberFunctionPointers.SetMacroLines == null ||
            RaptureMacroModule.MemberFunctionPointers.SetSavePendingFlag == null))
            throw new InvalidOperationException("Native macro editing is unavailable.");
        ValidateGearsetMacroEdits(snapshot, prepared.MacroEdits);

        // Consume before any native call; a timeout or duplicate request cannot replay moves.
        preparedGearsets = null;
        var journal = new List<object>();
        var verifiedMoves = new List<GearsetMove>();
        var editedMacros = new List<GearsetMacroEdit>();
        var expectedSlots = new GearsetEntrySnapshot?[100];
        foreach (var entry in snapshot.Entries) expectedSlots[entry.Index] = entry;
        var positions = Enumerable.Range(0, 100).ToArray();
        var current = snapshot;
        void SaveJournal(string state, string? error = null) => File.WriteAllText(Path.Combine(prepared.Directory, "journal.json"),
            JsonSerializer.Serialize(new { State = state, Error = error, Entries = journal }, GearsetJson));
        SaveJournal("Started");
        InvalidateGearsetUndo(snapshot.ContentId, "Superseded");
        try
        {
            foreach (var move in prepared.Plan.Moves)
            {
                journal.Add(new { Operation = "Swap", move.SourceIndex, move.TargetIndex, StartedUtc = DateTimeOffset.UtcNow });
                SaveJournal("Applying");
                AgentGearSet.Instance()->ReassignGearsetId(move.SourceIndex, move.TargetIndex);
                (expectedSlots[move.SourceIndex], expectedSlots[move.TargetIndex]) = (expectedSlots[move.TargetIndex], expectedSlots[move.SourceIndex]);
                (positions[move.SourceIndex], positions[move.TargetIndex]) = (positions[move.TargetIndex], positions[move.SourceIndex]);
                current = Capture();
                VerifyGearsetContents(current, expectedSlots, snapshot);
                verifiedMoves.Add(move);
                VerifyGearsetHotbars(snapshot, current, positions);
                VerifyGearsetMacros(snapshot, current, []);
                journal.Add(new { Operation = "Verified", move.SourceIndex, move.TargetIndex, CurrentGearsetIndex = current.CurrentGearsetIndex });
                SaveJournal("Applying");
            }
            foreach (var group in prepared.MacroEdits.GroupBy(e => (e.Set, e.Index)))
            {
                var originalMacro = snapshot.Macros.Single(m => m.Set == group.Key.Set && m.Index == group.Key.Index);
                WriteGearsetMacro(originalMacro, group.ToArray());
                editedMacros.AddRange(group);
            }
            current = Capture();
            VerifyGearsetContents(current, expectedSlots, snapshot);
            VerifyGearsetHotbars(snapshot, current, positions);
            VerifyGearsetMacros(snapshot, current, prepared.MacroEdits);
            File.WriteAllText(Path.Combine(prepared.Directory, "after.json"), JsonSerializer.Serialize(current, GearsetJson));
            SaveJournal("Completed");
            RememberGearsetUndo(new(1, prepared.Id, snapshot.ProcessId, snapshot.ContentId,
                prepared.Directory, "Ready", DateTimeOffset.UtcNow), snapshot, current, prepared.Plan, prepared.MacroEdits);
            var completedUndo = ReadGearsetUndoStatus(current, []);
            AgentGearSet.Instance()->Show();
            return JsonSerializer.Serialize(new { Applied = true, SnapshotId = prepared.Id, snapshot.ProcessId,
                snapshot.CharacterName, SetCount = current.Entries.Length, Moves = verifiedMoves.Count,
                AlreadySorted = true, completedUndo.UndoAvailable, completedUndo.UndoReason, completedUndo.UndoSnapshotId,
                MacroLinesUpdated = editedMacros.Count, Directory = prepared.Directory, Snapshot = current });
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(prepared.Directory, "failure-snapshot.json"), JsonSerializer.Serialize(Capture(), GearsetJson)); }
            catch (Exception captureError) { journal.Add(new { Operation = "SnapshotFailure", Error = captureError.GetBaseException().Message }); }
            try { SaveJournal("Stopped", ex.GetBaseException().Message); } catch (IOException) { }
            throw new InvalidOperationException($"Gearset sorting stopped. Inspect {prepared.Directory} before recovery: {ex.GetBaseException().Message}", ex);
        }
    }

    private static string[] GearsetStateBlockers(GearsetSnapshot snapshot)
    {
        var blockers = new List<string>();
        if (!snapshot.IsLoggedIn || !snapshot.LocalPlayerPresent || snapshot.ContentId == 0) blockers.Add("A logged-in local player is required.");
        if (!snapshot.HotbarModuleReady) blockers.Add("The character hotbar module is not ready.");
        if (!snapshot.HotbarDatFileLoaded) blockers.Add("The character hotbar file is not loaded.");
        if (snapshot.NativeGearsetCount != snapshot.Entries.Length) blockers.Add("The native gearset count disagrees with existing entries.");
        if (snapshot.GearsetSaveState.CharacterContentId != snapshot.ContentId || snapshot.HotbarSaveState.CharacterContentId != snapshot.ContentId)
            blockers.Add("Native gearset or hotbar files belong to a different character.");
        foreach (var flag in snapshot.ActiveConditions)
            if (flag.Contains("Combat", StringComparison.Ordinal) || flag.Contains("Craft", StringComparison.Ordinal) ||
                flag.Contains("Gather", StringComparison.Ordinal) || flag.Contains("Casting", StringComparison.Ordinal) ||
                flag.Contains("Occupied", StringComparison.Ordinal) || flag.Contains("BetweenAreas", StringComparison.Ordinal) ||
                flag.Contains("BoundByDuty", StringComparison.Ordinal) || flag.Contains("LoggingOut", StringComparison.Ordinal))
                blockers.Add($"Game condition {flag} is active.");
        foreach (var addon in snapshot.VisibleAddons)
            if (addon.Contains("Banner", StringComparison.Ordinal) || addon.Contains("Macro", StringComparison.Ordinal))
                blockers.Add($"Close the {addon} editor before sorting.");
        return blockers.ToArray();
    }

    private static void RequireSameGearsetSnapshot(GearsetSnapshot before, GearsetSnapshot current)
    {
        if (before.ProcessId != current.ProcessId || before.ContentId != current.ContentId ||
            before.CurrentGearsetIndex != current.CurrentGearsetIndex || before.OrderedGearsetsFingerprint != current.OrderedGearsetsFingerprint ||
            before.HotbarFingerprint != current.HotbarFingerprint || before.MacroFingerprint != current.MacroFingerprint ||
            before.EquippedFingerprint != current.EquippedFingerprint ||
            before.QuickPanelSettingsFingerprint != current.QuickPanelSettingsFingerprint ||
            !before.HotbarShareStateBitmask.SequenceEqual(current.HotbarShareStateBitmask) ||
            before.Entries.Length != current.Entries.Length ||
            before.Entries.Zip(current.Entries).Any(pair => pair.First.Index != pair.Second.Index ||
                pair.First.Id != pair.Second.Id || GearsetEntryContent(pair.First) != GearsetEntryContent(pair.Second)) ||
            JsonSerializer.Serialize(before.HotbarSlots) != JsonSerializer.Serialize(current.HotbarSlots) ||
            JsonSerializer.Serialize(before.EquippedItems) != JsonSerializer.Serialize(current.EquippedItems) ||
            JsonSerializer.Serialize(before.Macros.Select(GearsetMacroContent)) != JsonSerializer.Serialize(current.Macros.Select(GearsetMacroContent)))
            throw new InvalidOperationException("Gearsets, equipment, hotbars or macros changed after inspection. Read a fresh snapshot.");
    }

    private static string GearsetEntryContent(GearsetEntrySnapshot entry) => JsonSerializer.Serialize(new
    {
        entry.Name, entry.ClassJob, entry.ItemLevel, entry.GlamourSetLink, entry.BannerIndex, entry.GlassesIds,
        Flags = entry.Flags & ~(byte)RaptureGearsetModule.GearsetFlag.MainHandMissing,
        Items = entry.Items.Select(item => new
        {
            item.Slot, item.ItemId, item.GlamourId, item.Stain0Id, item.Stain1Id, item.Materia, item.MateriaGrades,
            Flags = item.Flags & ~0x1D, // Only documented missing/materia/dye/appearance warning caches may refresh.
        }),
    });

    private static object GearsetMacroContent(GearsetMacroSnapshot macro) => new
    {
        macro.Set, macro.Index, macro.RawNameBase64, macro.IconId, macro.MacroIconRowId, macro.LineBytesBase64,
    };

    private static void VerifyGearsetContents(GearsetSnapshot current, GearsetEntrySnapshot?[] expected, GearsetSnapshot before)
    {
        if (current.ContentId != before.ContentId || current.ProcessId != before.ProcessId || current.Entries.Length != before.Entries.Length ||
            current.EquippedFingerprint != before.EquippedFingerprint)
            throw new InvalidOperationException("Character, set count or equipped gear changed during sorting.");
        foreach (var entry in current.Entries)
            if (expected[entry.Index] is not { } original || original.Fingerprint != entry.Fingerprint ||
                entry.Id != entry.Index || GearsetEntryContent(original) != GearsetEntryContent(entry))
                throw new InvalidOperationException($"Gearset contents differ at set {entry.Index + 1}.");
        if (before.CurrentGearsetIndex >= 0 && before.CurrentGearsetIndex < 100 &&
            (current.CurrentGearsetIndex < 0 || current.CurrentGearsetIndex >= 100 || expected[current.CurrentGearsetIndex]?.Index != before.CurrentGearsetIndex))
            throw new InvalidOperationException("The selected gearset no longer refers to the originally equipped set.");
    }

    private static void VerifyGearsetHotbars(GearsetSnapshot before, GearsetSnapshot current, int[] positions)
    {
        if (before.QuickPanelSettingsFingerprint != current.QuickPanelSettingsFingerprint ||
            !before.HotbarShareStateBitmask.SequenceEqual(current.HotbarShareStateBitmask))
            throw new InvalidOperationException("Quick Panel or shared-hotbar settings changed during sorting.");
        var remap = positions.Select((original, index) => (original, index)).ToDictionary(p => p.original, p => p.index);
        var expected = before.HotbarSlots.Select(slot => slot with { CommandId = slot.CommandType == 15 && slot.CommandId < 100
            ? (uint)remap[(int)slot.CommandId] : slot.CommandId }).ToArray();
        if (JsonSerializer.Serialize(expected) != JsonSerializer.Serialize(current.HotbarSlots))
            throw new InvalidOperationException("Native hotbar references did not follow the gearset permutation exactly.");
    }

    private static void VerifyGearsetMacros(GearsetSnapshot before, GearsetSnapshot current, GearsetMacroEdit[] edits)
    {
        foreach (var macro in before.Macros)
        {
            var after = current.Macros.Single(m => m.Set == macro.Set && m.Index == macro.Index);
            if (macro.RawNameBase64 != after.RawNameBase64 || macro.IconId != after.IconId || macro.MacroIconRowId != after.MacroIconRowId)
                throw new InvalidOperationException("A macro name or icon changed during sorting.");
            for (var line = 0; line < macro.Lines.Length; line++)
            {
                var edit = edits.SingleOrDefault(e => e.Set == macro.Set && e.Index == macro.Index && e.Line == line);
                var expectedBytes = edit == null ? macro.LineBytesBase64[line] : Convert.ToBase64String(Encoding.UTF8.GetBytes(edit.After));
                if (after.LineBytesBase64[line] != expectedBytes) throw new InvalidOperationException("A macro line failed preservation verification.");
            }
        }
    }

    private static unsafe void WriteGearsetMacro(GearsetMacroSnapshot before, GearsetMacroEdit[] edits)
    {
        var module = RaptureMacroModule.Instance();
        if (module == null) throw new InvalidOperationException("The macro module became unavailable.");
        var macro = module->GetMacro((uint)before.Set, (uint)before.Index);
        if (macro == null) throw new InvalidOperationException("A macro became unavailable.");
        using var fullText = new MemoryStream();
        for (var line = 0; line < 15; line++)
        {
            if (Convert.ToBase64String(macro->Lines[line].AsSpan()) != before.LineBytesBase64[line])
                throw new InvalidOperationException("A macro changed before its references could be remapped.");
            var edit = edits.SingleOrDefault(e => e.Line == line);
            var bytes = edit == null ? Convert.FromBase64String(before.LineBytesBase64[line]) : Encoding.UTF8.GetBytes(edit.After);
            if (bytes.Contains((byte)0) || bytes.Contains((byte)'\n') || bytes.Contains((byte)'\r'))
                throw new InvalidOperationException("A macro contains an embedded delimiter and cannot be safely rewritten.");
            if (line != 0) fullText.WriteByte((byte)'\n');
            fullText.Write(bytes);
        }
        // Native SetMacroLines clears every subsequent line; always supply all 15 lines.
        var text = new Utf8String(fullText.ToArray());
        try { module->SetMacroLines(macro, 0, &text); }
        finally { text.Dtor(); }
        module->SetSavePendingFlag(true, (uint)before.Set);
        module->SaveFile(false);
    }

    private string GearsetUndoRoot => Path.Combine(configDirectory, "gearsets");

    private string GearsetUndoIndexPath(ulong contentId) => Path.Combine(GearsetUndoRoot,
        contentId.ToString("X16"), "latest-undo.json");

    private string ValidateGearsetRunDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(GearsetUndoRoot);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The retained gearset run is outside this plugin's data directory.");
        for (var directory = new DirectoryInfo(full); directory != null && directory.FullName.Length >= configDirectory.Length; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Retained gearset evidence cannot follow a filesystem link.");
        return full;
    }

    private static string ReadGearsetEvidence(string path, long maximumBytes = 8 * 1024 * 1024)
    {
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > maximumBytes)
            throw new InvalidDataException("Retained gearset evidence is missing, linked, or exceeds the bounded size limit.");
        return File.ReadAllText(path);
    }

    private void WriteGearsetUndoIndex(GearsetUndoIndex index)
    {
        var path = GearsetUndoIndexPath(index.ContentId);
        ValidateGearsetRunDirectory(Path.GetDirectoryName(path)!);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        if (File.Exists(temporary) && (File.GetAttributes(temporary) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Undo metadata cannot be written through a filesystem link.");
        File.WriteAllText(temporary, JsonSerializer.Serialize(index, GearsetJson));
        File.Move(temporary, path, true);
    }

    private void RememberGearsetUndo(GearsetUndoIndex index, GearsetSnapshot before, GearsetSnapshot after,
        GearsetOrganizationPlan plan, GearsetMacroEdit[] edits)
    {
        var record = new GearsetUndoRecord(index, before, after, plan, edits);
        ValidateRetainedGearsetUndo(record);
        WriteGearsetUndoIndex(index);
        gearsetUndoRecord = record;
        gearsetUndoLoaded = true;
        loadedGearsetUndoCharacter = index.ContentId;
        gearsetUndoLoadReason = string.Empty;
    }

    private void InvalidateGearsetUndo(ulong contentId, string state)
    {
        var previous = gearsetUndoRecord?.Index;
        var index = previous != null && previous.ContentId == contentId
            ? previous with { State = state, UpdatedUtc = DateTimeOffset.UtcNow }
            : new GearsetUndoIndex(1, Guid.Empty, Environment.ProcessId, contentId, string.Empty, state, DateTimeOffset.UtcNow);
        WriteGearsetUndoIndex(index);
        if (gearsetUndoRecord != null && gearsetUndoRecord.Index.ContentId == contentId)
            gearsetUndoRecord = gearsetUndoRecord with { Index = index };
        gearsetUndoLoaded = true;
        loadedGearsetUndoCharacter = contentId;
        gearsetUndoLoadReason = $"The retained sort cannot be undone ({state}).";
    }

    private void LoadGearsetUndo(GearsetSnapshot current)
    {
        if (gearsetUndoLoaded && loadedGearsetUndoCharacter == current.ContentId) return;
        gearsetUndoLoaded = true;
        loadedGearsetUndoCharacter = current.ContentId;
        gearsetUndoRecord = null;
        gearsetUndoLoadReason = "No completed sort is available to undo.";
        try
        {
            var indexPath = GearsetUndoIndexPath(current.ContentId);
            GearsetUndoIndex index;
            if (File.Exists(indexPath))
            {
                ValidateGearsetRunDirectory(Path.GetDirectoryName(indexPath)!);
                index = JsonSerializer.Deserialize<GearsetUndoIndex>(ReadGearsetEvidence(indexPath, 32768))
                    ?? throw new InvalidDataException("Retained undo metadata is empty.");
                if (index.SchemaVersion != 1 || index.ContentId != current.ContentId)
                    throw new InvalidDataException("Retained undo metadata belongs to a different character or schema.");
                if (index.State != "Ready")
                {
                    gearsetUndoLoadReason = $"The retained sort cannot be undone ({index.State}).";
                    return;
                }
            }
            else return;
            if (index.ProcessId != current.ProcessId)
            {
                gearsetUndoLoadReason = "Undo is limited to the game process which performed the sort.";
                return;
            }
            var record = ReadRetainedGearsetUndo(index);
            using (var process = System.Diagnostics.Process.GetCurrentProcess())
            {
                if (record.Before.CapturedUtc < new DateTimeOffset(process.StartTime.ToUniversalTime()))
                {
                    gearsetUndoLoadReason = "Undo evidence belongs to an earlier game session.";
                    return;
                }
            }
            RememberGearsetUndo(index, record.Before, record.After, record.Plan, record.MacroEdits);
        }
        catch (Exception ex)
        {
            gearsetUndoLoadReason = "Retained undo evidence could not be validated: " + ex.GetBaseException().Message;
        }
    }

    private GearsetUndoRecord ReadRetainedGearsetUndo(GearsetUndoIndex index)
    {
        var folder = ValidateGearsetRunDirectory(index.RunDirectory);
        if (!Guid.TryParse(Path.GetFileName(folder), out var folderId) || folderId != index.SnapshotId)
            throw new InvalidDataException("Retained run identity does not match its directory.");
        var before = JsonSerializer.Deserialize<GearsetSnapshot>(ReadGearsetEvidence(Path.Combine(folder, "before.json")))
            ?? throw new InvalidDataException("Retained before snapshot is empty.");
        var after = JsonSerializer.Deserialize<GearsetSnapshot>(ReadGearsetEvidence(Path.Combine(folder, "after.json")))
            ?? throw new InvalidDataException("Retained after snapshot is empty.");
        using var planDocument = JsonDocument.Parse(ReadGearsetEvidence(Path.Combine(folder, "plan.json")));
        var plan = planDocument.RootElement.GetProperty("Plan").Deserialize<GearsetOrganizationPlan>()
            ?? throw new InvalidDataException("Retained sort plan is empty.");
        var edits = planDocument.RootElement.GetProperty("MacroEdits").Deserialize<GearsetMacroEdit[]>()
            ?? throw new InvalidDataException("Retained macro edit list is empty.");
        using var journal = JsonDocument.Parse(ReadGearsetEvidence(Path.Combine(folder, "journal.json")));
        if (journal.RootElement.GetProperty("State").GetString() != "Completed")
            throw new InvalidDataException("The retained sort did not complete successfully.");
        var swaps = journal.RootElement.GetProperty("Entries").EnumerateArray()
            .Where(entry => entry.GetProperty("Operation").GetString() is "Swap" or "Verified").ToArray();
        if (swaps.Length != plan.Moves.Count * 2)
            throw new InvalidDataException("The retained native swap journal is incomplete.");
        for (var i = 0; i < plan.Moves.Count; i++)
        {
            var move = plan.Moves[i];
            for (var phase = 0; phase < 2; phase++)
            {
                var entry = swaps[i * 2 + phase];
                if (entry.GetProperty("Operation").GetString() != (phase == 0 ? "Swap" : "Verified") ||
                    entry.GetProperty("SourceIndex").GetInt32() != move.SourceIndex ||
                    entry.GetProperty("TargetIndex").GetInt32() != move.TargetIndex)
                    throw new InvalidDataException("The retained native swap journal does not match the plan.");
            }
        }
        var record = new GearsetUndoRecord(index, before, after, plan, edits);
        ValidateRetainedGearsetUndo(record);
        return record;
    }

    private static void ValidateRetainedGearsetUndo(GearsetUndoRecord record)
    {
        var index = record.Index;
        if (index.SchemaVersion != 1 || index.SnapshotId == Guid.Empty || index.State != "Ready" ||
            index.ProcessId != record.Before.ProcessId || index.ProcessId != record.After.ProcessId ||
            index.ContentId == 0 || index.ContentId != record.Before.ContentId || index.ContentId != record.After.ContentId ||
            (record.Plan.Moves.Count == 0 && record.MacroEdits.Length == 0))
            throw new InvalidDataException("Retained undo identity or state is invalid.");
        var plan = GearsetOrganization.CreatePlan(record.Before.Entries.Select(e => new GearsetIdentity(e.Index, e.ClassJob, e.Name, e.Fingerprint)));
        if (JsonSerializer.Serialize(plan) != JsonSerializer.Serialize(record.Plan))
            throw new InvalidDataException("Retained sorting moves do not match the original role ordering.");
        var numberMap = plan.Assignments.ToDictionary(assignment => assignment.Gearset.OriginalIndex + 1, assignment => assignment.TargetIndex + 1);
        var expectedMacroEdits = new List<GearsetMacroEdit>();
        foreach (var macro in record.Before.Macros)
        for (var line = 0; line < macro.Lines.Length; line++)
        {
            var result = GearsetMacroReferences.AnalyzeLine(macro.Lines[line], numberMap);
            if (result.Blocker != null) throw new InvalidDataException("The retained macro reference plan has a blocker.");
            if (result.Changed) expectedMacroEdits.Add(new(macro.Set, macro.Index, line, macro.Lines[line], result.UpdatedLine));
        }
        if (JsonSerializer.Serialize(expectedMacroEdits) != JsonSerializer.Serialize(record.MacroEdits))
            throw new InvalidDataException("Retained macro changes do not match the validated gearset-number remapping.");
        var expected = new GearsetEntrySnapshot?[100];
        foreach (var entry in record.Before.Entries) expected[entry.Index] = entry;
        var positions = Enumerable.Range(0, 100).ToArray();
        foreach (var move in record.Plan.Moves)
        {
            (expected[move.SourceIndex], expected[move.TargetIndex]) = (expected[move.TargetIndex], expected[move.SourceIndex]);
            (positions[move.SourceIndex], positions[move.TargetIndex]) = (positions[move.TargetIndex], positions[move.SourceIndex]);
        }
        VerifyGearsetContents(record.After, expected, record.Before);
        VerifyGearsetHotbars(record.Before, record.After, positions);
        VerifyGearsetMacros(record.Before, record.After, record.MacroEdits);
        ValidateGearsetMacroEdits(record.Before, record.MacroEdits);
        ValidateGearsetMacroEdits(record.After, ReverseGearsetMacroEdits(record.MacroEdits));
    }

    private GearsetUndoStatus ReadGearsetUndoStatus(GearsetSnapshot snapshot, IReadOnlyCollection<string> blockers)
    {
        LoadGearsetUndo(snapshot);
        var record = gearsetUndoRecord;
        if (record == null) return new(false, gearsetUndoLoadReason, null);
        if (record.Index.State != "Ready") return new(false, $"The retained sort cannot be undone ({record.Index.State}).", null);
        if (record.Index.ProcessId != snapshot.ProcessId || record.Index.ContentId != snapshot.ContentId)
            return new(false, "Undo is limited to the character and game process which performed the sort.", null);
        try { RequireSameGearsetSnapshot(record.After, snapshot); }
        catch (Exception) { return new(false, "Gearsets, equipped gear, hotbars, Quick Panel or macros changed after the retained sort.", null); }
        if (blockers.Count != 0) return new(false, "Undo is blocked: " + string.Join("; ", blockers), null);
        return new(true, "The last sort can be restored from its verified before snapshot.", record.Index.SnapshotId);
    }

    private static GearsetMacroEdit[] ReverseGearsetMacroEdits(GearsetMacroEdit[] edits) =>
        edits.Select(edit => new GearsetMacroEdit(edit.Set, edit.Index, edit.Line, edit.After, edit.Before)).ToArray();

    private static void ValidateGearsetMacroEdits(GearsetSnapshot snapshot, GearsetMacroEdit[] edits)
    {
        if (edits.Select(edit => (edit.Set, edit.Index, edit.Line)).Distinct().Count() != edits.Length)
            throw new InvalidDataException("Duplicate macro changes are not permitted.");
        foreach (var group in edits.GroupBy(edit => (edit.Set, edit.Index)))
        {
            var macro = snapshot.Macros.Single(m => m.Set == group.Key.Set && m.Index == group.Key.Index);
            if (macro.Lines.Length != 15 || macro.LineBytesBase64.Length != 15 ||
                group.Any(edit => edit.Line < 0 || edit.Line >= 15 || edit.Before != macro.Lines[edit.Line]))
                throw new InvalidDataException("The retained macro edit does not match all original lines.");
            for (var line = 0; line < 15; line++)
            {
                var edit = group.SingleOrDefault(e => e.Line == line);
                var bytes = edit == null ? Convert.FromBase64String(macro.LineBytesBase64[line]) : Encoding.UTF8.GetBytes(edit.After);
                if (bytes.Length > 180 || bytes.Any(value => value < 32 && value != 9))
                    throw new InvalidDataException("The complete macro rewrite contains opaque, oversized or delimited content.");
                if (edit != null && Convert.ToBase64String(Encoding.UTF8.GetBytes(edit.Before)) != macro.LineBytesBase64[line])
                    throw new InvalidDataException("The edited macro line does not preserve its native encoding.");
            }
        }
    }

    private unsafe string UndoGearsets(GearsetRequest request, GearsetSnapshot snapshot,
        List<string> blockers, Func<GearsetSnapshot> capture)
    {
        var status = ReadGearsetUndoStatus(snapshot, blockers);
        if (!status.UndoAvailable) throw new InvalidOperationException(status.UndoReason);
        var record = gearsetUndoRecord!;
        var age = DateTimeOffset.UtcNow - request.CreatedUtc;
        if (!Guid.TryParse(request.Command, out var id) || id != record.Index.SnapshotId || request.ProcessId != snapshot.ProcessId ||
            age < TimeSpan.FromSeconds(-5) || age > TimeSpan.FromSeconds(30))
            throw new InvalidOperationException("The undo request is stale or does not match the current character's retained sort.");
        if (AgentGearSet.Instance() == null || AgentGearSet.MemberFunctionPointers.ReassignGearsetId == null)
            throw new InvalidOperationException("Native gearset reassignment is unavailable.");
        var macroEdits = ReverseGearsetMacroEdits(record.MacroEdits);
        ValidateGearsetMacroEdits(snapshot, macroEdits);
        if (macroEdits.Length != 0 && (RaptureMacroModule.MemberFunctionPointers.SetMacroLines == null ||
            RaptureMacroModule.MemberFunctionPointers.SetSavePendingFlag == null))
            throw new InvalidOperationException("Native macro editing is unavailable.");
        var moves = record.Plan.Moves.Reverse().Select(move =>
            new GearsetMove(move.OriginalIndex, move.TargetIndex, move.SourceIndex)).ToArray();
        var folder = Path.Combine(ValidateGearsetRunDirectory(record.Index.RunDirectory), "undo-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        var journal = new List<object>();
        void Save(string state, string? error = null) => File.WriteAllText(Path.Combine(folder, "journal.json"),
            JsonSerializer.Serialize(new { State = state, Error = error, SourceSnapshotId = id, Entries = journal }, GearsetJson));
        File.WriteAllText(Path.Combine(folder, "before.json"), JsonSerializer.Serialize(snapshot, GearsetJson));
        File.WriteAllText(Path.Combine(folder, "plan.json"), JsonSerializer.Serialize(new { Moves = moves, MacroEdits = macroEdits }, GearsetJson));
        Save("Started");
        // The persisted marker is consumed before the first native call. Uncertain attempts
        // cannot be replayed after either an IPC timeout or a backend reload.
        InvalidateGearsetUndo(snapshot.ContentId, "Started");
        preparedGearsets = null;
        var expected = new GearsetEntrySnapshot?[100];
        foreach (var entry in snapshot.Entries) expected[entry.Index] = entry;
        var positions = Enumerable.Range(0, 100).ToArray();
        var current = snapshot;
        var written = new List<GearsetMacroEdit>();
        try
        {
            foreach (var move in moves)
            {
                if (expected[move.SourceIndex] == null)
                    throw new InvalidOperationException("The inverse native move has no source gearset.");
                journal.Add(new { Operation = "Swap", move.SourceIndex, move.TargetIndex, StartedUtc = DateTimeOffset.UtcNow });
                Save("Applying");
                AgentGearSet.Instance()->ReassignGearsetId(move.SourceIndex, move.TargetIndex);
                (expected[move.SourceIndex], expected[move.TargetIndex]) = (expected[move.TargetIndex], expected[move.SourceIndex]);
                (positions[move.SourceIndex], positions[move.TargetIndex]) = (positions[move.TargetIndex], positions[move.SourceIndex]);
                current = capture();
                VerifyGearsetContents(current, expected, snapshot);
                VerifyGearsetHotbars(snapshot, current, positions);
                VerifyGearsetMacros(snapshot, current, []);
                journal.Add(new { Operation = "Verified", move.SourceIndex, move.TargetIndex, current.CurrentGearsetIndex });
                Save("Applying");
            }
            foreach (var group in macroEdits.GroupBy(edit => (edit.Set, edit.Index)))
            {
                journal.Add(new { Operation = "MacroWrite", group.Key.Set, group.Key.Index });
                Save("Applying");
                WriteGearsetMacro(snapshot.Macros.Single(m => m.Set == group.Key.Set && m.Index == group.Key.Index), group.ToArray());
                written.AddRange(group);
                current = capture();
                VerifyGearsetMacros(snapshot, current, written.ToArray());
                journal.Add(new { Operation = "MacroVerified", group.Key.Set, group.Key.Index });
                Save("Applying");
            }
            current = capture();
            RequireSameGearsetSnapshot(record.Before, current);
            VerifyGearsetContents(current, expected, snapshot);
            VerifyGearsetHotbars(snapshot, current, positions);
            VerifyGearsetMacros(snapshot, current, macroEdits);
            File.WriteAllText(Path.Combine(folder, "after.json"), JsonSerializer.Serialize(current, GearsetJson));
            Save("Completed");
            InvalidateGearsetUndo(snapshot.ContentId, "Undone");
            AgentGearSet.Instance()->Show();
            return JsonSerializer.Serialize(new { Undone = true, SnapshotId = id, snapshot.ProcessId, snapshot.CharacterName,
                snapshot.ContentId, SetCount = current.Entries.Length, Moves = moves.Length, MacroLinesUpdated = written.Count,
                Directory = folder, Snapshot = current, UndoAvailable = false, UndoReason = "The previous sort was restored.",
                UndoSnapshotId = (Guid?)null, AlreadySorted = false });
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(folder, "failure-snapshot.json"), JsonSerializer.Serialize(capture(), GearsetJson)); }
            catch (Exception captureError) { journal.Add(new { Operation = "SnapshotFailure", Error = captureError.GetBaseException().Message }); }
            try { Save("Stopped", ex.GetBaseException().Message); } catch (IOException) { }
            try { InvalidateGearsetUndo(snapshot.ContentId, "Failed"); } catch (IOException) { }
            throw new InvalidOperationException($"Gearset undo stopped. Inspect {folder} before recovery: {ex.GetBaseException().Message}", ex);
        }
    }
}
