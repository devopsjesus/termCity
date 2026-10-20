using TermCity.Core.Persistence;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class ResidentialPlotTests
{
    [Fact]
    public void FullRulesResidentialClickReservesOneCellIncludingAtTheMapEdge()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        var session = new GameSession(game, showGuide: false);
        session.PlaceCursor(new(game.Map.Width - 1, game.Map.Height - 1));
        Assert.True(session.Zone(ZoneType.Residential).Success);
        Assert.Equal(new CellRect(game.Map.Width - 1, game.Map.Height - 1, 1, 1),
            Assert.Single(game.VacantPlots(ZoneType.Residential)));
    }

    [Fact]
    public void NarrowResidentialStripsSupportCompactHomes()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        Assert.True(game.Designate(new(10, 19, 20, 1), ZoneType.Residential).Success);
        Assert.Equal(20, game.VacantPlots(ZoneType.Residential).Count);
        Assert.Equal(GrowthStatus.Ready, GrowthDiagnostics.ForZone(game, ZoneType.Residential).Status);
        TestCity.Advance(game, 8);
        Assert.NotEmpty(game.Map.BuildingFootprints);
        Assert.All(game.Map.BuildingFootprints.Values, plot =>
        {
            Assert.InRange(plot.Width, 1, 2);
            Assert.Equal(1, plot.Height);
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    public void FullRulesHomesAreMostlyOneCellWithCompactVarietyAndRoundTrip(int seed)
    {
        var game = TestCity.Flat(seed: seed, rules: CityRules.Full);
        Assert.True(game.Designate(new(10, 5, 40, 12), ZoneType.Residential).Success);
        var fuel = game.Map.Content.Buildings.Get("Woodlot");
        game.Map.SetBuildingFootprint(fuel, fuel.FootprintAt(52, 5));
        var water = game.Map.Content.Buildings.Get("Town Well");
        game.Map.SetBuildingFootprint(water, water.FootprintAt(52, 10));
        game.Designate(new(10, 17, 40, 2), ZoneType.Commercial);
        game.Designate(new(60, 17, 20, 2), ZoneType.Industrial);
        game.Touch();
        TestCity.Advance(game, 16);
        var plots = game.Map.BuildingFootprints.Values.Where(area =>
            game.Map.BuildingAt(area.X, area.Y)!.Zone == ZoneType.Residential).ToArray();
        Assert.NotEmpty(plots);
        Assert.All(plots, area =>
        {
            Assert.InRange(area.Width, 1, 2);
            Assert.InRange(area.Height, 1, 2);
        });
        Assert.True(plots.Count(area => area.Area == 1) > plots.Length / 2);
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
        var area = new CellRect(10, 18, 1, 1);
        Assert.True(game.Designate(area, ZoneType.Residential).Success);
        TestCity.Advance(game, 8);
        Assert.Equal(area, Assert.Single(game.Map.BuildingFootprints.Values));
    }

    [Fact]
    public void FourByFourHomesRoundTripWithoutChangingTheirCapacity()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        var area = new CellRect(10, 5, 4, 4);
        var cottage = game.Map.Content.Buildings.Get("Cottage");
        game.Designate(area, ZoneType.Residential);
        game.Map.SetBuildingFootprint(cottage, area);
        game.Touch();
        var restored = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Equal(area, Assert.Single(restored.Map.BuildingFootprints.Values));
        Assert.Equal(cottage.Capacity, restored.Map.BuildingAt(area.Right, area.Bottom)!.Capacity);
    }

    [Fact]
    public void ExistingNarrowHomesRemainLoadable()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        var area = new CellRect(10, 19, 2, 1);
        game.Designate(area, ZoneType.Residential);
        game.Map.SetBuildingFootprint(game.Map.Content.Buildings.Get("Cottage"), area);
        game.Touch();
        var restored = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Equal(area, Assert.Single(restored.Map.BuildingFootprints.Values));
    }
}
