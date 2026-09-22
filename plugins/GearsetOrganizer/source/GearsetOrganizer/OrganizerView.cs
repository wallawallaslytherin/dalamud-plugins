using System.Text.Json;

namespace GearsetOrganizer;

internal static class JsonValue
{
    public static JsonElement Property(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var found) ? found : default;
    public static string Text(JsonElement value, string name, string fallback = "") =>
        Property(value, name) is { ValueKind: JsonValueKind.String } found ? found.GetString() ?? fallback : fallback;
    public static int Number(JsonElement value, string name, int fallback = 0) =>
        Property(value, name) is { ValueKind: JsonValueKind.Number } found && found.TryGetInt32(out var number) ? number : fallback;
    public static ulong Identity(JsonElement value, string name) =>
        Property(value, name) is { ValueKind: JsonValueKind.Number } found && found.TryGetUInt64(out var number) ? number : 0;
    public static bool Flag(JsonElement value, string name) => Property(value, name).ValueKind == JsonValueKind.True;
    public static JsonElement[] Array(JsonElement value, string name) =>
        Property(value, name) is { ValueKind: JsonValueKind.Array } found ? found.EnumerateArray().ToArray() : [];
    public static string[] Strings(JsonElement value, string name) => Array(value, name)
        .Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString() ?? "").ToArray();
}

internal sealed record GearsetRow(int OldNumber, int NewNumber, string Name, string Job, string Role, int ItemLevel, bool Equipped);

internal sealed record OrganizerView
{
    public static readonly OrganizerView Empty = new();
    public string SnapshotId { get; init; } = "";
    public int ProcessId { get; init; }
    public ulong ContentId { get; init; }
    public string CharacterName { get; init; } = "";
    public int SetCount { get; init; }
    public int MoveCount { get; init; }
    public int MacroEditCount { get; init; }
    public bool CanApply { get; init; }
    public bool AlreadySorted { get; init; }
    public string[] Blockers { get; init; } = [];
    public bool UndoAvailable { get; init; }
    public string UndoSnapshotId { get; init; } = "";
    public string UndoReason { get; init; } = "No verified sort is available to undo.";
    public GearsetRow[] Rows { get; init; } = [];
    public DateTimeOffset PreviewedUtc { get; init; }
    public bool HasPreview => SnapshotId.Length > 0;
    public bool PreviewExpired => HasPreview && DateTimeOffset.UtcNow - PreviewedUtc > TimeSpan.FromMinutes(9);

    public static OrganizerView Preview(JsonElement result)
    {
        var snapshot = JsonValue.Property(result, "Snapshot");
        var entries = JsonValue.Array(snapshot, "Entries").ToDictionary(entry => JsonValue.Number(entry, "Index"));
        var assignments = JsonValue.Array(JsonValue.Property(result, "Plan"), "Assignments");
        var id = JsonValue.Text(result, "SnapshotId");
        var count = JsonValue.Number(result, "SetCount");
        if (!Guid.TryParse(id, out _) || count < 0 || count > 100 || entries.Count != count || assignments.Length != count ||
            entries.Keys.Any(index => index < 0 || index >= 100))
            throw new InvalidOperationException("The preview did not contain a complete, identified gearset list.");
        var currentIndex = JsonValue.Number(snapshot, "CurrentGearsetIndex", -1);
        var rows = assignments.Select(assignment =>
        {
            var identity = JsonValue.Property(assignment, "Gearset");
            var oldIndex = JsonValue.Number(identity, "OriginalIndex", -1);
            if (!entries.TryGetValue(oldIndex, out var entry))
                throw new InvalidOperationException("A preview assignment has no matching saved gearset.");
            return new GearsetRow(oldIndex + 1, JsonValue.Number(assignment, "TargetIndex", -1) + 1,
                JsonValue.Text(entry, "Name"), JsonValue.Text(entry, "ClassJobName"),
                Role(JsonValue.Number(entry, "ClassJob")), JsonValue.Number(entry, "ItemLevel"), oldIndex == currentIndex);
        }).OrderBy(row => row.NewNumber).ToArray();
        if (rows.Select(row => row.OldNumber).Distinct().Count() != count ||
            rows.Select(row => row.NewNumber).Distinct().Count() != count ||
            rows.Any(row => row.NewNumber < 1 || row.NewNumber > count))
            throw new InvalidOperationException("The preview is not a complete permutation of the saved gearsets.");
        var moves = JsonValue.Array(JsonValue.Property(result, "Plan"), "Moves").Length;
        return new OrganizerView
        {
            SnapshotId = id, ProcessId = JsonValue.Number(result, "ProcessId"), ContentId = JsonValue.Identity(result, "ContentId"),
            CharacterName = JsonValue.Text(result, "CharacterName"),
            SetCount = count, MoveCount = moves, MacroEditCount = JsonValue.Array(result, "MacroEdits").Length,
            CanApply = JsonValue.Flag(result, "CanApply"), AlreadySorted = moves == 0,
            Blockers = JsonValue.Strings(result, "Blockers"), UndoAvailable = JsonValue.Flag(result, "UndoAvailable"),
            UndoSnapshotId = JsonValue.Text(result, "UndoSnapshotId"), UndoReason = JsonValue.Text(result, "UndoReason"),
            Rows = rows, PreviewedUtc = DateTimeOffset.UtcNow,
        };
    }

    public OrganizerView WithStatus(JsonElement result)
    {
        var sameCharacter = ProcessId == JsonValue.Number(result, "ProcessId") && ContentId != 0 &&
            ContentId == JsonValue.Identity(result, "ContentId");
        var source = sameCharacter ? this : Empty;
        var blockers = JsonValue.Strings(result, "Blockers");
        return source with
        {
            ProcessId = JsonValue.Number(result, "ProcessId"), ContentId = JsonValue.Identity(result, "ContentId"),
            CharacterName = JsonValue.Text(result, "CharacterName"),
            SetCount = JsonValue.Number(result, "SetCount"), Blockers = blockers,
            CanApply = source.CanApply && blockers.Length == 0, AlreadySorted = JsonValue.Flag(result, "AlreadySorted"),
            UndoAvailable = JsonValue.Flag(result, "UndoAvailable"), UndoSnapshotId = JsonValue.Text(result, "UndoSnapshotId"),
            UndoReason = JsonValue.Text(result, "UndoReason"),
        };
    }

    private static string Role(int job) => job switch
    {
        1 or 3 or 19 or 21 or 32 or 37 => "Tanks",
        6 or 24 or 28 or 33 or 40 => "Healers",
        2 or 4 or 20 or 22 or 29 or 30 or 34 or 39 or 41 => "Melee DPS",
        5 or 23 or 31 or 38 => "Physical ranged DPS",
        7 or 25 or 26 or 27 or 35 or 42 => "Magical ranged DPS",
        36 or 43 => "Limited jobs",
        >= 8 and <= 15 => "Crafters",
        >= 16 and <= 18 => "Gatherers",
        _ => "Other sets",
    };
}
