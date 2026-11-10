using TermCity.Core.Persistence;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class ResidentialPlotTests
{
    [Fact]
    public void FullRulesResidentialClickReservesACompletePlotIncludingAtTheMapEdge()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        var session = new GameSession(game, showGuide: false);
        session.PlaceCursor(new(game.Map.Width - 5, game.Map.Height - 5));
        Assert.True(session.Zone(ZoneType.Residential).Success);
        Assert.Equal(new CellRect(game.Map.Width - 5, game.Map.Height - 5, 5, 5),
            Assert.Single(game.VacantPlots(ZoneType.Residential)));
    }

    [Fact]
    public void NarrowResidentialStripsExplainMissingSpaceInsteadOfSqueezingHomes()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        Assert.True(game.Designate(new(10, 19, 20, 1), ZoneType.Residential).Success);
        Assert.Empty(game.VacantPlots(ZoneType.Residential));
        Assert.Equal(GrowthStatus.NeedsSpace, GrowthDiagnostics.ForZone(game, ZoneType.Residential).Status);
        TestCity.Advance(game, 8);
        Assert.Empty(game.Map.BuildingFootprints);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    public void FullRulesHomesUseMostlyMinimumPlotsWithHorizontalVarietyAndRoundTrip(int seed)
    {
        var game = TestCity.Flat(seed: seed, rules: CityRules.Full);
        Assert.True(game.Designate(new(10, 5, 40, 15), ZoneType.Residential).Success);
        var fuel = game.Map.Content.Buildings.Get("Woodlot");
        game.Map.SetBuildingFootprint(fuel, fuel.FootprintAt(52, 5));
        var water = game.Map.Content.Buildings.Get("Town Well");
        game.Map.SetBuildingFootprint(water, water.FootprintAt(52, 15));
        game.Designate(new(10, 31, 40, 5), ZoneType.Commercial);
        game.Designate(new(60, 31, 20, 5), ZoneType.Industrial);
        game.Touch();
        TestCity.Advance(game, 16);
        var plots = game.Map.BuildingFootprints.Values.Where(area =>
            game.Map.BuildingAt(area.X, area.Y)!.Zone == ZoneType.Residential).ToArray();
        Assert.NotEmpty(plots);
        Assert.All(plots, area =>
        {
            Assert.InRange(area.Width, 5, 10);
            Assert.InRange(area.Height, 5, 10);
        });
        Assert.True(plots.Count(area => area.Area == 25) > plots.Length / 2);
        Assert.True(plots.Select(area => (area.Width, area.Height)).Distinct().Count() > 1);
        var restored = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Equal(plots.OrderBy(area => area.Y).ThenBy(area => area.X),
            restored.Map.BuildingFootprints.Values.Where(area =>
                restored.Map.BuildingAt(area.X, area.Y)!.Zone == ZoneType.Residential).OrderBy(area => area.Y).ThenBy(area => area.X));
    }

    [Fact]
    public void FullRulesHomesFitTheMinimumWhenNoLargerPlotFits()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        var area = new CellRect(10, 10, 5, 5);
        Assert.True(game.Designate(area, ZoneType.Residential).Success);
        TestCity.Advance(game, 8);
        Assert.Equal(area, Assert.Single(game.Map.BuildingFootprints.Values));
    }

    [Fact]
    public void ExpandedHomesRoundTripWithoutChangingTheirCapacity()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        var area = new CellRect(10, 25, 20, 20);
        var cottage = game.Map.Content.Buildings.Get("Cottage");
        game.Designate(area, ZoneType.Residential);
        game.Map.SetBuildingFootprint(cottage, area);
        game.Touch();
        var restored = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Equal(area, Assert.Single(restored.Map.BuildingFootprints.Values));
        Assert.Equal(cottage.Capacity, restored.Map.BuildingAt(area.Right, area.Bottom)!.Capacity);
    }

    [Fact]
    public void PreScaleNarrowHomeLayoutsAreRejectedRatherThanSilentlyExpanded()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        var area = new CellRect(10, 19, 2, 1);
        game.Designate(area, ZoneType.Residential);
        game.Map.SetBuildingFootprint(game.Map.Content.Buildings.Get("Cottage"), area);
        game.Touch();
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(SaveGameStore.Serialize(game)));
    }
}
