using TermCity.Core.Session;

namespace TermCity.Tests;

public class BuildingCapacityTests
{
    [Theory]
    [InlineData("Woodlot", "300 fuel units")]
    [InlineData("Charcoal Burners", "1,200 fuel units")]
    [InlineData("Town Well", "260 water units")]
    [InlineData("Aqueduct", "900 water units")]
    [InlineData("Granary", "4,000 grain measures")]
    [InlineData("Market Stall", "14 jobs")]
    [InlineData("Workshop", "14 jobs")]
    public void CapacityDetailsUseMeaningfulBuildingUnits(string name, string expected)
    {
        var game = TestCity.Flat();
        var type = game.Map.Content.Buildings.Get(name);
        Assert.Equal(expected, ChoiceHelpContent.CapacityText(type));
        Assert.Contains($"Capacity: {expected}", ChoiceHelpContent.Building(game, type).Details);
    }
}
