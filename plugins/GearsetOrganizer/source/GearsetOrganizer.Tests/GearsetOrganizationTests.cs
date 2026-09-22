using GearsetOrganizer.Core;
using Xunit;

namespace GearsetOrganizer.Tests;

public sealed class GearsetOrganizationTests
{
    [Fact]
    public void EmptyListIsANoOp()
    {
        var plan = GearsetOrganization.CreatePlan([]);
        Assert.Empty(plan.Assignments);
        Assert.Empty(plan.Moves);
    }

    [Fact]
    public void AlreadyOrderedListDoesNotMoveOrRenameSets()
    {
        GearsetIdentity[] original = [Set(0, 19, "Tank A"), Set(1, 19, "Tank alternate"), Set(2, 24), Set(3, 11)];
        var plan = GearsetOrganization.CreatePlan(original.Reverse());
        Assert.Empty(plan.Moves);
        Assert.Equal(original, plan.Assignments.Select(assignment => assignment.Gearset));
        Assert.Equal(Enumerable.Range(0, original.Length), plan.Assignments.Select(assignment => assignment.TargetIndex));
    }

    [Fact]
    public void OccupiedDestinationsAreSwappedAndLaterSourcesFollowDisplacedSets()
    {
        GearsetIdentity[] original = [Set(0, 11), Set(1, 19), Set(2, 24)];
        var plan = GearsetOrganization.CreatePlan(original);
        Assert.Equal(new[] { new GearsetMove(1, 1, 0), new GearsetMove(2, 2, 1) }, plan.Moves);
        AssertPlanAndRollback(original, plan);
    }

    [Fact]
    public void HolesAreCompactedWithoutDroppingDisplacedSets()
    {
        GearsetIdentity[] original = [Set(2, 11), Set(7, 24), Set(99, 19), Set(0, 18)];
        var plan = GearsetOrganization.CreatePlan(original);
        Assert.Equal(new[] { 99, 7, 2, 0 }, plan.Assignments.Select(assignment => assignment.Gearset.OriginalIndex));
        AssertPlanAndRollback(original, plan);
    }

    [Fact]
    public void IdenticalFingerprintsAndNamesRemainSeparateStableIdentities()
    {
        GearsetIdentity[] original =
        [
            new(20, 11, "Identical", "same"),
            new(7, 11, "Identical", "same"),
            new(3, 19, "Other", "same"),
        ];
        var plan = GearsetOrganization.CreatePlan(original);
        Assert.Equal(new[] { 3, 7, 20 }, plan.Assignments.Select(assignment => assignment.Gearset.OriginalIndex));
        Assert.Equal(2, plan.Assignments.Count(assignment => assignment.Gearset.Name == "Identical"));
        AssertPlanAndRollback(original, plan);
    }

    [Fact]
    public void EveryKnownClassAndJobAppearsInApprovedRoleOrder()
    {
        // Independent, explicit expected inventory: tanks, healers, melee, ranged,
        // casters, limited, crafting, gathering. Base classes follow their jobs.
        byte[] expected =
        [
            19, 1, 21, 3, 32, 37, 24, 6, 28, 33, 40,
            20, 2, 22, 4, 30, 29, 34, 39, 41, 23, 5, 31, 38,
            25, 7, 27, 26, 35, 42, 36, 43, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18,
        ];
        var original = Enumerable.Range(1, 43).Select(job => Set(43 - job, (byte)job)).ToArray();
        var plan = GearsetOrganization.CreatePlan(original);
        Assert.Equal(expected, plan.Assignments.Select(assignment => assignment.Gearset.ClassJob));
        AssertPlanAndRollback(original, plan);
    }

    [Fact]
    public void BaseClassSetsFollowJobAlternatesAndUnknownJobsRemainTrailingStable()
    {
        GearsetIdentity[] original =
        [
            Set(0, 7), Set(1, 201), Set(2, 25, "Black Mage main"), Set(3, 0),
            Set(4, 25, "Black Mage alternate"), Set(5, 255), Set(6, 11),
        ];
        var plan = GearsetOrganization.CreatePlan(original.Reverse());
        Assert.Equal(new[] { 2, 4, 0, 6, 1, 3, 5 }, plan.Assignments.Select(assignment => assignment.Gearset.OriginalIndex));
        AssertPlanAndRollback(original, plan);
    }

    [Fact]
    public void FullReversedListCanBeReorderedAndRolledBack()
    {
        var original = Enumerable.Range(0, 100).Select(index => Set(index, 11)).ToArray();
        var plan = GearsetOrganization.CreateMovePlan(original, Enumerable.Range(0, 100).Reverse());
        Assert.Equal(50, plan.Moves.Count);
        AssertPlanAndRollback(original, plan);
    }

    [Fact]
    public void RandomPermutationsWithHolesConserveAllIdentitiesAndAreReversible()
    {
        var random = new Random(27519);
        for (var iteration = 0; iteration < 250; iteration++)
        {
            var count = random.Next(101);
            var selected = Enumerable.Range(0, 100).OrderBy(_ => random.Next()).Take(count).ToArray();
            var original = selected.Select(index => Set(index, (byte)random.Next(256))).ToArray();
            var target = selected.OrderBy(_ => random.Next()).ToArray();
            AssertPlanAndRollback(original, GearsetOrganization.CreateMovePlan(original, target));
            AssertPlanAndRollback(original, GearsetOrganization.CreatePlan(original));
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100)]
    [InlineData(int.MaxValue)]
    public void RejectsOutOfRangeOriginalIndices(int index)
        => Assert.Throws<ArgumentException>(() => GearsetOrganization.CreatePlan([Set(index, 11)]));

    [Fact]
    public void RejectsDuplicateOriginalIndices()
        => Assert.Throws<ArgumentException>(() => GearsetOrganization.CreatePlan([Set(5, 11), Set(5, 19)]));

    [Fact]
    public void RejectsMoreThanCapacity()
        => Assert.Throws<ArgumentException>(() => GearsetOrganization.CreatePlan(Enumerable.Range(0, 101).Select(index => Set(index, 11))));

    [Fact]
    public void RejectsMissingOrNullIdentityData()
    {
        Assert.Throws<ArgumentNullException>(() => GearsetOrganization.CreatePlan(null!));
        Assert.Throws<ArgumentException>(() => GearsetOrganization.CreatePlan([null!]));
        Assert.Throws<ArgumentException>(() => GearsetOrganization.CreatePlan([new(0, 11, null!, "hash")]));
        Assert.Throws<ArgumentException>(() => GearsetOrganization.CreatePlan([new(0, 11, "Name", "")]));
        Assert.Throws<ArgumentException>(() => GearsetOrganization.CreatePlan([new(0, 11, "Name", null!)]));
    }

    [Theory]
    [InlineData(new int[] { 3 })]
    [InlineData(new int[] { 3, 3 })]
    [InlineData(new int[] { 3, 4 })]
    [InlineData(new int[] { 3, -1 })]
    [InlineData(new int[] { 3, 100 })]
    [InlineData(new int[] { 3, 7, 9 })]
    public void RejectsIncompleteDuplicateAndUnknownTargets(int[] targets)
        => Assert.Throws<ArgumentException>(() => GearsetOrganization.CreateMovePlan([Set(3, 11), Set(7, 19)], targets));

    [Fact]
    public void RejectsNullTargets()
        => Assert.Throws<ArgumentNullException>(() => GearsetOrganization.CreateMovePlan([Set(3, 11)], null!));

    private static GearsetIdentity Set(int index, byte job, string? name = null)
        => new(index, job, name ?? $"Set {index}", $"fingerprint-{index}");

    private static void AssertPlanAndRollback(IReadOnlyList<GearsetIdentity> original, GearsetOrganizationPlan plan)
    {
        var initial = new int?[100];
        foreach (var set in original) initial[set.OriginalIndex] = set.OriginalIndex;
        var slots = (int?[])initial.Clone();
        var expectedIdentities = original.Select(set => set.OriginalIndex).Order().ToArray();

        foreach (var move in plan.Moves)
        {
            Assert.InRange(move.SourceIndex, 0, 99);
            Assert.InRange(move.TargetIndex, 0, 99);
            Assert.NotEqual(move.SourceIndex, move.TargetIndex);
            Assert.Equal(move.OriginalIndex, slots[move.SourceIndex]);
            (slots[move.SourceIndex], slots[move.TargetIndex]) = (slots[move.TargetIndex], slots[move.SourceIndex]);
            Assert.Equal(expectedIdentities, slots.Where(slot => slot.HasValue).Select(slot => slot!.Value).Order());
        }

        Assert.Equal(original.Count, plan.Assignments.Count);
        Assert.Equal(expectedIdentities, plan.Assignments.Select(assignment => assignment.Gearset.OriginalIndex).Order());
        Assert.Equal(Enumerable.Range(0, original.Count), plan.Assignments.Select(assignment => assignment.TargetIndex));
        foreach (var assignment in plan.Assignments)
        {
            Assert.Equal(assignment.Gearset.OriginalIndex, slots[assignment.TargetIndex]);
            Assert.Same(original.Single(set => set.OriginalIndex == assignment.Gearset.OriginalIndex), assignment.Gearset);
        }
        Assert.All(slots.Skip(original.Count), slot => Assert.Null(slot));

        // Reversing verified native swaps restores holes as well as occupied slots.
        foreach (var move in plan.Moves.Reverse())
            (slots[move.SourceIndex], slots[move.TargetIndex]) = (slots[move.TargetIndex], slots[move.SourceIndex]);
        Assert.Equal(initial, slots);
    }
}
