namespace GearsetOrganizer.Core;

/// <summary>A saved set's identity in the original snapshot. All indices are zero based.</summary>
public sealed record GearsetIdentity(int OriginalIndex, byte ClassJob, string Name, string Fingerprint);

public sealed record GearsetAssignment(GearsetIdentity Gearset, int TargetIndex);

/// <summary>
/// Move the identified set from its current SourceIndex to TargetIndex. Native reassignment
/// swaps an occupied destination back to SourceIndex; it does not shift intervening sets.
/// </summary>
public sealed record GearsetMove(int OriginalIndex, int SourceIndex, int TargetIndex);

public sealed record GearsetOrganizationPlan(
    IReadOnlyList<GearsetAssignment> Assignments,
    IReadOnlyList<GearsetMove> Moves);

/// <summary>Pure planner; it never reads or changes game state.</summary>
public static class GearsetOrganization
{
    public const int Capacity = 100;

    // Job, then its base class. Arcanist belongs beside Summoner, retaining Scholar's
    // independent healer grouping. An unknown future job is kept at the end, never dropped.
    private static readonly byte[] ClassJobOrder =
    [
        19, 1, 21, 3, 32, 37,       // Paladin, Warrior, Dark Knight, Gunbreaker.
        24, 6, 28, 33, 40,          // White Mage, Scholar, Astrologian, Sage.
        20, 2, 22, 4, 30, 29, 34, 39, 41, // Monk, Dragoon, Ninja, Samurai, Reaper, Viper.
        23, 5, 31, 38,              // Bard, Machinist, Dancer.
        25, 7, 27, 26, 35, 42,      // Black Mage, Summoner, Red Mage, Pictomancer.
        36, 43,                    // Blue Mage, Beastmaster.
        8, 9, 10, 11, 12, 13, 14, 15, // Crafters.
        16, 17, 18,                // Gatherers.
    ];

    private static readonly IReadOnlyDictionary<byte, int> ClassJobRanks =
        ClassJobOrder.Select((job, rank) => (job, rank)).ToDictionary(pair => pair.job, pair => pair.rank);

    /// <summary>Group jobs by role and compact all existing sets into slots 0 through count - 1.</summary>
    public static GearsetOrganizationPlan CreatePlan(IEnumerable<GearsetIdentity> gearsets)
    {
        var original = ValidateGearsets(gearsets);
        var ordered = original.OrderBy(set => ClassJobRanks.GetValueOrDefault(set.ClassJob, int.MaxValue))
            .ThenBy(set => set.OriginalIndex)
            .Select(set => set.OriginalIndex)
            .ToArray();
        return CreateMovePlan(original, ordered);
    }

    /// <summary>
    /// Plan an exact permutation into consecutive slots. Each target entry identifies a set
    /// by its original index, independent of its name, fingerprint, or changing current index.
    /// </summary>
    public static GearsetOrganizationPlan CreateMovePlan(
        IEnumerable<GearsetIdentity> gearsets,
        IEnumerable<int> targetOriginalIndices)
    {
        var original = ValidateGearsets(gearsets);
        ArgumentNullException.ThrowIfNull(targetOriginalIndices);
        var targets = targetOriginalIndices.ToArray();
        if (targets.Length != original.Length || targets.Distinct().Count() != targets.Length)
            throw new ArgumentException("Target order must contain every original set exactly once.", nameof(targetOriginalIndices));

        var byOriginalIndex = original.ToDictionary(set => set.OriginalIndex);
        if (targets.Any(index => !byOriginalIndex.ContainsKey(index)))
            throw new ArgumentException("Target order contains an index absent from the original snapshot.", nameof(targetOriginalIndices));

        var assignments = targets.Select((index, target) => new GearsetAssignment(byOriginalIndex[index], target)).ToArray();
        var slots = new int?[Capacity];
        var positions = new Dictionary<int, int>();
        foreach (var set in original)
        {
            slots[set.OriginalIndex] = set.OriginalIndex;
            positions.Add(set.OriginalIndex, set.OriginalIndex);
        }

        var moves = new List<GearsetMove>();
        foreach (var assignment in assignments)
        {
            var identity = assignment.Gearset.OriginalIndex;
            var source = positions[identity];
            var target = assignment.TargetIndex;
            if (source == target) continue;

            moves.Add(new GearsetMove(identity, source, target));
            var displaced = slots[target];
            slots[source] = displaced;
            slots[target] = identity;
            positions[identity] = target;
            if (displaced.HasValue) positions[displaced.Value] = source;
        }

        if (assignments.Any(assignment => slots[assignment.TargetIndex] != assignment.Gearset.OriginalIndex)
            || slots.Skip(original.Length).Any(slot => slot.HasValue))
            throw new InvalidOperationException("Gearset permutation did not produce the requested final state.");

        return new GearsetOrganizationPlan(Array.AsReadOnly(assignments), moves.AsReadOnly());
    }

    private static GearsetIdentity[] ValidateGearsets(IEnumerable<GearsetIdentity> gearsets)
    {
        ArgumentNullException.ThrowIfNull(gearsets);
        var result = gearsets.ToArray();
        if (result.Length > Capacity)
            throw new ArgumentException($"At most {Capacity} gearsets are supported.", nameof(gearsets));
        var indices = new HashSet<int>();
        foreach (var gearset in result)
        {
            if (gearset is null || gearset.Name is null || string.IsNullOrEmpty(gearset.Fingerprint))
                throw new ArgumentException("Every gearset requires a name and nonempty fingerprint.", nameof(gearsets));
            if (gearset.OriginalIndex < 0 || gearset.OriginalIndex >= Capacity || !indices.Add(gearset.OriginalIndex))
                throw new ArgumentException("Original gearset indices must be unique and between 0 and 99.", nameof(gearsets));
        }

        return result;
    }
}
