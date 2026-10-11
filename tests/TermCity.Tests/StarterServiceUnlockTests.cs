using TermCity.Core.Buildings;
using TermCity.Core.Session;
using TermCity.Core.Simulation;

namespace TermCity.Tests;

public class StarterServiceUnlockTests
{
    [Fact]
    public void FireWatchCoverageReachesEighteenCellsWithoutChangingOtherCosts()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        game.HighestMilestone = 100;
        var building = game.Map.Content.Buildings.Get("Fire Watch");
        Assert.Equal(18, building.Radius);
        Assert.Equal(9_000, building.Cost);
        Assert.Equal(160, building.WeeklyUpkeep);
        Assert.True(game.PlaceBuilding(building, new(80, 18, 1, 1)).Success);
        Assert.True(game.Services.Coverage(ServiceKind.Fire, game.Map.Index(97, 18)) > 0);
        Assert.True(game.Services.Coverage(ServiceKind.Fire, game.Map.Index(99, 18)) > 0);
        Assert.Equal(0, game.Services.Coverage(ServiceKind.Fire, game.Map.Index(103, 18)));
        Assert.Equal(18, Assert.Single(TermCity.Core.Rendering.AreaOfEffect.ForBuilding(building, new(80, 18))).Radius);
    }

    [Theory]
    [InlineData("Fire Watch")]
    [InlineData("Watch House")]
    [InlineData("Sheriff's Hall")]
    [InlineData("Apothecary")]
    [InlineData("Chantry School")]
    public void StarterCareUnlocksAtFirstPermanentMilestone(string name)
    {
        var game = TestCity.Flat();
        var building = game.Map.Content.Buildings.Get(name);
        Assert.Equal(100, CityProgression.RequiredPopulation(building));
        Assert.False(CityProgression.IsUnlocked(game, building));
        Assert.Contains("Unlock: Hamlet (100 souls to go)", ChoiceHelpContent.Building(game, building).Details);
        game.HighestMilestone = 100;
        Assert.True(CityProgression.IsUnlocked(game, building));
        Assert.Contains("Unlock: Hamlet (0 souls to go)", ChoiceHelpContent.Building(game, building).Details);
    }

    [Fact]
    public void LaterServiceThresholdsRemainUnchanged()
    {
        var game = TestCity.Flat();
        Assert.Equal(1_000, CityProgression.RequiredPopulation(game.Map.Content.Buildings.Get("Gaol")));
        Assert.Equal(1_000, CityProgression.RequiredPopulation(game.Map.Content.Buildings.Get("Infirmary")));
        Assert.Equal(1_000, CityProgression.RequiredPopulation(game.Map.Content.Buildings.Get("Monastery")));
    }

    [Theory]
    [InlineData("Village Green", 500, "Village")]
    [InlineData("Tavern", 1_000, "Market Town")]
    public void RecreationBuildingsUnlockAtTheirAssignedMilestone(string name, int threshold, string milestone)
    {
        var game = TestCity.Flat();
        var building = game.Map.Content.Buildings.Get(name);
        Assert.Equal(threshold, CityProgression.RequiredPopulation(building));
        game.HighestMilestone = threshold - 1;
        Assert.False(CityProgression.IsUnlocked(game, building));
        Assert.Contains($"Unlock: {milestone} ({threshold:N0} souls to go)",
            ChoiceHelpContent.Building(game, building).Details);
        game.HighestMilestone = threshold;
        Assert.True(CityProgression.IsUnlocked(game, building));
        Assert.Contains($"Unlock: {milestone} (0 souls to go)", ChoiceHelpContent.Building(game, building).Details);
    }
}
