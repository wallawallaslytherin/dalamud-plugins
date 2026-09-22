using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace GearsetOrganizer;

public sealed class OrganizerViewTests
{
    private const ulong CharacterId = 18446744073709551500UL;
    private const int GameProcessId = 4242;
    private const string PreviewId = "0146ab57-71ca-476f-a6b8-ce6b221681b7";
    private const string UndoId = "65cde6a3-07da-4388-a2b3-64d21a6f6ecb";

    [Fact]
    public void CompletePreviewRetainsExactIdentityAndOrdersMappedRows()
    {
        var before = DateTimeOffset.UtcNow;
        var view = OrganizerView.Preview(Preview());
        Assert.Equal(PreviewId, view.SnapshotId);
        Assert.Equal(CharacterId, view.ContentId);
        Assert.Equal(GameProcessId, view.ProcessId);
        Assert.True(view.HasPreview);
        Assert.True(view.CanApply);
        Assert.False(view.AlreadySorted);
        Assert.False(view.PreviewExpired);
        Assert.Equal(3, view.SetCount);
        Assert.Equal(2, view.MoveCount);
        Assert.Equal(1, view.MacroEditCount);
        Assert.InRange(view.PreviewedUtc, before, DateTimeOffset.UtcNow);
        Assert.Equal(new[] { 3, 10, 1 }, view.Rows.Select(row => row.OldNumber));
        Assert.Equal(new[] { 1, 2, 3 }, view.Rows.Select(row => row.NewNumber));
        Assert.Equal(new[] { "Paladin main", "Beastmaster", "Crafting alternate" }, view.Rows.Select(row => row.Name));
        Assert.Equal(new[] { "Tanks", "Limited jobs", "Crafters" }, view.Rows.Select(row => row.Role));
        Assert.Equal(new[] { 786, 3, 750 }, view.Rows.Select(row => row.ItemLevel));
        Assert.Equal("Crafting alternate", Assert.Single(view.Rows, row => row.Equipped).Name);
        Assert.True(view.UndoAvailable);
        Assert.Equal(UndoId, view.UndoSnapshotId);
    }

    [Fact]
    public void SameNameDifferentCharacterStatusInvalidatesPreviewWithoutRoundingIdentity()
    {
        // Both IDs exceed Int64 and the exact-integer range of IEEE754 doubles.
        var view = OrganizerView.Preview(Preview());
        var updated = view.WithStatus(Status(CharacterId + 1));
        Assert.Equal(view.CharacterName, updated.CharacterName);
        Assert.Equal(CharacterId + 1, updated.ContentId);
        Assert.Equal(GameProcessId, updated.ProcessId);
        Assert.False(updated.HasPreview);
        Assert.False(updated.CanApply);
        Assert.Empty(updated.Rows);
        Assert.Equal(3, updated.SetCount);
    }

    [Fact]
    public void ChangedProcessInvalidatesPreviewEvenForSameCharacter()
    {
        var view = OrganizerView.Preview(Preview());
        var updated = view.WithStatus(Status(CharacterId, GameProcessId + 1));
        Assert.False(updated.HasPreview);
        Assert.False(updated.CanApply);
        Assert.Empty(updated.Rows);
    }

    [Fact]
    public void StatusWithoutPreviewCannotAuthorizeSorting()
    {
        var view = OrganizerView.Empty.WithStatus(Status());
        Assert.Equal(CharacterId, view.ContentId);
        Assert.False(view.HasPreview);
        Assert.False(view.CanApply);
        Assert.Empty(view.Rows);
        Assert.False(view.PreviewExpired);
        Assert.True(view.UndoAvailable); // Undo has its own explicit, independently bound token.
        Assert.Equal(UndoId, view.UndoSnapshotId);
    }

    [Fact]
    public void MatchingStatusRetainsPreviewButNewBlockersPreventSorting()
    {
        var view = OrganizerView.Preview(Preview());
        var allowed = view.WithStatus(Status());
        Assert.Equal(PreviewId, allowed.SnapshotId);
        Assert.Same(view.Rows, allowed.Rows);
        Assert.Equal(view.PreviewedUtc, allowed.PreviewedUtc);
        Assert.True(allowed.CanApply);

        var blocked = allowed.WithStatus(Status(blockers: ["Gathering is active."]));
        Assert.Equal(PreviewId, blocked.SnapshotId);
        Assert.False(blocked.CanApply);
        Assert.Equal(new[] { "Gathering is active." }, blocked.Blockers);
        Assert.False(blocked.WithStatus(Status()).CanApply); // A status poll is not a new preview.
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("12345")]
    public void InvalidPreviewTokenIsRejected(string id)
        => Assert.Throws<InvalidOperationException>(() => OrganizerView.Preview(Mutate(node => node["SnapshotId"] = id)));

    [Fact]
    public void MissingPreviewTokenIsRejected()
        => Assert.Throws<InvalidOperationException>(() => OrganizerView.Preview(Mutate(node => node.Remove("SnapshotId"))));

    [Fact]
    public void MissingSavedEntryIsRejected()
        => Assert.Throws<InvalidOperationException>(() => OrganizerView.Preview(Mutate(node => node["Snapshot"]!["Entries"]!.AsArray().RemoveAt(0))));

    [Fact]
    public void MissingAssignmentIsRejected()
        => Assert.Throws<InvalidOperationException>(() => OrganizerView.Preview(Mutate(node => node["Plan"]!["Assignments"]!.AsArray().RemoveAt(0))));

    [Fact]
    public void AssignmentForAbsentOriginalSetIsRejected()
        => Assert.Throws<InvalidOperationException>(() => OrganizerView.Preview(Mutate(node => node["Plan"]!["Assignments"]![0]!["Gearset"]!["OriginalIndex"] = 99)));

    [Fact]
    public void DuplicateOriginalAssignmentIsRejected()
        => Assert.Throws<InvalidOperationException>(() => OrganizerView.Preview(Mutate(node => node["Plan"]!["Assignments"]![0]!["Gearset"]!["OriginalIndex"] = 9)));

    [Fact]
    public void DuplicateDestinationIsRejected()
        => Assert.Throws<InvalidOperationException>(() => OrganizerView.Preview(Mutate(node => node["Plan"]!["Assignments"]![0]!["TargetIndex"] = 1)));

    [Fact]
    public void MissingDestinationIsRejected()
        => Assert.Throws<InvalidOperationException>(() => OrganizerView.Preview(Mutate(node => node["Plan"]!["Assignments"]![0]!.AsObject().Remove("TargetIndex"))));

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(100)]
    public void DestinationOutsideConsecutiveTargetRangeIsRejected(int target)
        => Assert.Throws<InvalidOperationException>(() => OrganizerView.Preview(Mutate(node => node["Plan"]!["Assignments"]![0]!["TargetIndex"] = target)));

    [Fact]
    public void EmptyStateNeverExpiresAndOnlyRealPreviewAges()
    {
        Assert.False(OrganizerView.Empty.HasPreview);
        Assert.False(OrganizerView.Empty.PreviewExpired);
        var view = OrganizerView.Preview(Preview());
        Assert.False((view with { PreviewedUtc = DateTimeOffset.UtcNow.AddMinutes(-8) }).PreviewExpired);
        Assert.True((view with { PreviewedUtc = DateTimeOffset.UtcNow.AddMinutes(-10) }).PreviewExpired);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("18446744073709551616")]
    [InlineData("\"18446744073709551500\"")]
    [InlineData("1.25")]
    [InlineData("null")]
    public void InvalidIdentityRepresentationsDoNotReuseAnExistingPreview(string encodedIdentity)
    {
        using var document = JsonDocument.Parse($$"""{"ProcessId":4242,"ContentId":{{encodedIdentity}},"CharacterName":"Same Character","SetCount":3}""");
        var updated = OrganizerView.Preview(Preview()).WithStatus(document.RootElement);
        Assert.Equal(0UL, updated.ContentId);
        Assert.False(updated.HasPreview);
        Assert.False(updated.CanApply);
    }

    private static JsonElement Preview() => JsonSerializer.SerializeToElement(new
    {
        SnapshotId = PreviewId, ProcessId = GameProcessId, ContentId = CharacterId,
        CharacterName = "Same Character", SetCount = 3, CanApply = true,
        UndoAvailable = true, UndoSnapshotId = UndoId, UndoReason = "Ready", Blockers = Array.Empty<string>(),
        MacroEdits = new[] { new { Before = "/gs 1", After = "/gs 3" } },
        Snapshot = new
        {
            CurrentGearsetIndex = 0,
            Entries = new[]
            {
                new { Index = 0, Name = "Crafting alternate", ClassJob = 11, ClassJobName = "Goldsmith", ItemLevel = 750 },
                new { Index = 2, Name = "Paladin main", ClassJob = 19, ClassJobName = "Paladin", ItemLevel = 786 },
                new { Index = 9, Name = "Beastmaster", ClassJob = 43, ClassJobName = "Beastmaster", ItemLevel = 3 },
            },
        },
        Plan = new
        {
            Assignments = new[]
            {
                new { Gearset = new { OriginalIndex = 2 }, TargetIndex = 0 },
                new { Gearset = new { OriginalIndex = 9 }, TargetIndex = 1 },
                new { Gearset = new { OriginalIndex = 0 }, TargetIndex = 2 },
            },
            Moves = new[] { new { SourceIndex = 2, TargetIndex = 0 }, new { SourceIndex = 9, TargetIndex = 1 } },
        },
    });

    private static JsonElement Status(ulong contentId = CharacterId, int processId = GameProcessId, string[]? blockers = null)
        => JsonSerializer.SerializeToElement(new
        {
            ProcessId = processId, ContentId = contentId, CharacterName = "Same Character", SetCount = 3,
            AlreadySorted = false, Blockers = blockers ?? [], UndoAvailable = true, UndoSnapshotId = UndoId, UndoReason = "Ready",
        });

    private static JsonElement Mutate(Action<JsonObject> mutation)
    {
        var node = JsonNode.Parse(Preview().GetRawText())!.AsObject();
        mutation(node);
        return JsonSerializer.SerializeToElement(node);
    }
}
