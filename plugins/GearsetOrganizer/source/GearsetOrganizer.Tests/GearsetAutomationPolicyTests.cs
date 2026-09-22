using Xunit;

namespace GearsetOrganizer.Tests;

public sealed class GearsetAutomationPolicyTests
{
    [Fact]
    public void NoAutomationPluginsDoesNotRequireAnyExternalProvider()
    {
        var state = GearsetAutomationPolicy.Capture([], _ => throw new Exception("No provider should be called."));
        Assert.Empty(state.Blockers);
        Assert.Equal(7, state.Status.Count);
        Assert.All(state.Status.Values, status => Assert.Equal("NotLoaded", status));
    }

    [Fact]
    public void UnrelatedPluginsDoNotIntroduceDependenciesOrBlockSorting()
    {
        var state = GearsetAutomationPolicy.Capture(
            ["ExampleUtilityPlugin", "GearsetOrganizer", "ExampleCosmeticPlugin"],
            _ => throw new Exception("No provider should be called."));
        Assert.Empty(state.Blockers);
        Assert.DoesNotContain("ExampleUtilityPlugin", state.Status.Keys);
    }

    [Theory]
    [InlineData("GatherCraftControl")]
    [InlineData("GatherBuddyReborn")]
    [InlineData("Artisan")]
    [InlineData("HunterV2")]
    [InlineData("AutoDuty")]
    [InlineData("Lifestream")]
    [InlineData("AutoRetainer")]
    public void LoadedKnownPluginRequiresItsOwnIdleObservation(string pluginName)
    {
        var calls = new List<string>();
        var state = GearsetAutomationPolicy.Capture([pluginName], name =>
        {
            calls.Add(name);
            return [new GearsetAutomationSignal(name + ".Idle", "Idle", false)];
        });
        Assert.Equal(new[] { pluginName }, calls);
        Assert.Empty(state.Blockers);
        Assert.Equal("Loaded", state.Status[pluginName]);
        Assert.Equal("Idle", state.Status[pluginName + ".Idle"]);
    }

    [Fact]
    public void EveryBusyObservationBlocksEvenWhenAnotherObservationIsIdle()
    {
        var state = GearsetAutomationPolicy.Capture(["Artisan"], _ =>
        [
            new GearsetAutomationSignal("Crafting", "False", false),
            new GearsetAutomationSignal("Endurance", "True", true),
            new GearsetAutomationSignal("List", "True", true),
        ]);
        Assert.Equal(new[] { "Endurance is True.", "List is True." }, state.Blockers);
        Assert.Equal("False", state.Status["Crafting"]);
        Assert.Equal("True", state.Status["Endurance"]);
    }

    [Fact]
    public void ALoadedPluginWithoutObservationsIsUnknownAndBlocks()
    {
        var state = GearsetAutomationPolicy.Capture(["Lifestream"], _ => []);
        Assert.Single(state.Blockers);
        Assert.Contains("idle state is unknown", state.Blockers[0]);
        Assert.Equal("No idle-state observations are available.", state.Status["Lifestream.Error"]);
    }

    [Fact]
    public void MissingProviderOrChangedBindingBlocksAndRetainsUnderlyingReason()
    {
        var state = GearsetAutomationPolicy.Capture(["HunterV2"], _ =>
            throw new InvalidOperationException("Reflection wrapper", new MissingMemberException("CurrentState is unavailable.")));
        Assert.Equal(new[] { "HunterV2 idle state is unknown: CurrentState is unavailable." }, state.Blockers);
        Assert.Equal("CurrentState is unavailable.", state.Status["HunterV2.Error"]);
    }

    [Fact]
    public void ProviderFailureDoesNotPreventCapturingOtherActiveAutomation()
    {
        var calls = new List<string>();
        var state = GearsetAutomationPolicy.Capture(["Artisan", "AutoDuty"], name =>
        {
            calls.Add(name);
            if (name == "Artisan") throw new InvalidOperationException("Provider unavailable.");
            return [new GearsetAutomationSignal("AutoDuty.IsLooping", "True", true)];
        });
        Assert.Equal(new[] { "Artisan", "AutoDuty" }, calls);
        Assert.Equal(2, state.Blockers.Length);
        Assert.Contains("Artisan idle state is unknown: Provider unavailable.", state.Blockers);
        Assert.Contains("AutoDuty.IsLooping is True.", state.Blockers);
    }

    [Fact]
    public void RepeatedLoadedMetadataDoesNotDuplicateProviderCallsOrBlockers()
    {
        var calls = 0;
        var state = GearsetAutomationPolicy.Capture(["AutoRetainer", "AutoRetainer"], _ =>
        {
            calls++;
            return [new GearsetAutomationSignal("AutoRetainer.IsBusy", "True", true)];
        });
        Assert.Equal(1, calls);
        Assert.Single(state.Blockers);
    }
}
