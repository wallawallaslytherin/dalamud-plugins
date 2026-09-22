using GearsetOrganizer.Core;
using Xunit;

namespace GearsetOrganizer.Tests;

public sealed class GearsetMacroReferencesTests
{
    private static readonly IReadOnlyDictionary<int, int> Mapping = new Dictionary<int, int> { [1] = 20, [2] = 1, [3] = 3, [20] = 2, [100] = 100 };

    [Theory]
    [InlineData("/gs 1", "/gs 20")]
    [InlineData("/gearset change 2", "/gearset change 1")]
    [InlineData("/gs equip 20", "/gs equip 2")]
    [InlineData("\t /GeArSeT\tCHANGE\t\"1\"  7  <wait.1>  ", "\t /GeArSeT\tCHANGE\t\"20\"  7  <wait.1>  ")]
    [InlineData("/gs change 1 \"2\" <wait.2.5>", "/gs change 20 \"2\" <wait.2.5>")]
    [InlineData("/gs \"2\"", "/gs \"1\"")]
    [InlineData("/micon 1 gearset", "/micon 20 gearset")]
    [InlineData("  /macroicon   \"20\"  \"GearSet\"  ", "  /macroicon   \"2\"  \"GearSet\"  ")]
    [InlineData("/micon 2 gearset <wait.1>", "/micon 1 gearset <wait.1>")]
    public void RemapsOnlyTheGearsetNumberAndPreservesAllSurroundingText(string line, string expected)
    {
        var result = GearsetMacroReferences.AnalyzeLine(line, Mapping);
        Assert.Equal(expected, result.UpdatedLine);
        Assert.True(result.Changed);
        Assert.Null(result.Blocker);
        Assert.Null(result.NamedReference);
    }

    [Theory]
    [InlineData("/gs change Paladin", "Paladin")]
    [InlineData("/gs change \"Black Mage\" 2 <wait.1>", "Black Mage")]
    [InlineData("/gs equip \"Warrior's gear\"", "Warrior's gear")]
    [InlineData("/gs \"Tank main\"", "Tank main")]
    [InlineData("/micon \"Goldsmith\" gearset", "Goldsmith")]
    [InlineData("/gs change 戦士 1", "戦士")]
    public void ExposesNamedReferencesForPrefixResolutionWithoutEditingThem(string line, string expectedName)
    {
        var result = GearsetMacroReferences.AnalyzeLine(line, Mapping);
        Assert.Equal(line, result.UpdatedLine);
        Assert.False(result.Changed);
        Assert.Null(result.Blocker);
        Assert.Equal(expectedName, result.NamedReference);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("/gearset")]
    [InlineData("/gs  ")]
    [InlineData("/echo /gs change 1")]
    [InlineData("/ac \"1\" <t>")]
    [InlineData("/micon 1")]
    [InlineData("/micon 1 action")]
    [InlineData("/micon \"gearset\" item")]
    [InlineData("/micon \"unclosed unrelated icon")]
    [InlineData("/hotbar copy WAR 1 PLD 1")]
    [InlineData("/crosshotbar set \"Sprint\" 1 LD1")]
    [InlineData("/gs change \"003\"")]
    [InlineData("/micon 100 gearset")]
    public void UnrelatedCommandsAndUnchangedNumbersAreByteForBytePreserved(string line)
    {
        var result = GearsetMacroReferences.AnalyzeLine(line, Mapping);
        Assert.Equal(new GearsetMacroLineResult(line, false, null), result);
    }

    [Theory]
    [InlineData("/gs change 4")]
    [InlineData("/micon 4 gearset")]
    [InlineData("/gs change 0")]
    [InlineData("/gs change 101")]
    [InlineData("/gs change 99999999999999999999999")]
    [InlineData("/gs change -1")]
    [InlineData("/gs change +1")]
    [InlineData("/gs change １")]
    [InlineData("/gs change \" 1\"")]
    [InlineData("/gs change ''")]
    [InlineData("/gs change '1'")]
    [InlineData("/gs change \"\"")]
    [InlineData("/gs change <gearset>")]
    [InlineData("/gs change 1; /gs delete 2")]
    [InlineData("/gs change 1 | something")]
    [InlineData("/gs change 1 arbitrary")]
    [InlineData("/gs change 1 2 3")]
    [InlineData("/gs change 1 <wait.abc>")]
    [InlineData("/gs change")]
    [InlineData("/gs equip")]
    [InlineData("/gs delete 1")]
    [InlineData("/gs \"delete\" 1")]
    [InlineData("/gearset reassign 1 2")]
    [InlineData("/gs save 1")]
    [InlineData("/gs view 1")]
    [InlineData("/gs unknown 1")]
    [InlineData("/gs change \"1")]
    [InlineData("/gs change \"1\"extra")]
    [InlineData("/gs change 1\"2\"")]
    [InlineData("/gs change \\\"1\\\"")]
    [InlineData("/gs change \"Tank\\\" 1\"")]
    [InlineData("/gs change “1”")]
    [InlineData("/micon 1 gearset extra")]
    [InlineData("/micon 1 action gearset")]
    [InlineData("/micon \"1 gearset")]
    [InlineData("/hotbar set gearset 1 2 3")]
    [InlineData("/crosshotbar gearset 1 2 3")]
    [InlineData("/chotbar set GearSet 1 2 3")]
    [InlineData("/pvphotbar gearset 1 2")]
    [InlineData("/gs change 1\n/gs change 2")]
    [InlineData("/gs change 1\r")]
    [InlineData("/gs change \u0002\u001f\u0003")]
    [InlineData("/gs change 1\0")]
    public void AmbiguousMalformedAndAbsentReferencesBlockWithoutChangingText(string line)
    {
        var result = GearsetMacroReferences.AnalyzeLine(line, Mapping);
        Assert.Equal(line, result.UpdatedLine);
        Assert.False(result.Changed);
        Assert.NotNull(result.Blocker);
        Assert.Null(result.NamedReference);
    }

    [Fact]
    public void MappingCycleDoesNotRepeatedlyRemapTheSameToken()
    {
        var mapping = new Dictionary<int, int> { [1] = 2, [2] = 3, [3] = 1 };
        Assert.Equal("/gs change 2 1", GearsetMacroReferences.AnalyzeLine("/gs change 1 1", mapping).UpdatedLine);
        Assert.Equal("/micon 3 gearset", GearsetMacroReferences.AnalyzeLine("/micon 2 gearset", mapping).UpdatedLine);
    }

    [Fact]
    public void InvalidNumberMappingsBlock()
    {
        IReadOnlyDictionary<int, int>[] invalid =
        [
            new Dictionary<int, int> { [0] = 1 },
            new Dictionary<int, int> { [1] = 101 },
            new Dictionary<int, int> { [1] = 2, [3] = 2 },
        ];
        foreach (var mapping in invalid)
            Assert.NotNull(GearsetMacroReferences.AnalyzeLine("/gs change 1", mapping).Blocker);
    }

    [Fact]
    public void NullInputsThrow()
    {
        Assert.Throws<ArgumentNullException>(() => GearsetMacroReferences.AnalyzeLine(null!, Mapping));
        Assert.Throws<ArgumentNullException>(() => GearsetMacroReferences.AnalyzeLine("/gs change 1", null!));
    }
}
