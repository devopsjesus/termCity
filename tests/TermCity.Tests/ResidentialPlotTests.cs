using TermCity.Core.Persistence;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class ResidentialPlotTests
{
    [Fact]
    public void FullRulesResidentialClickReservesTwoByTwoIncludingAtTheMapEdge()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        var session = new GameSession(game, showGuide: false);
        session.PlaceCursor(new(game.Map.Width - 1, game.Map.Height - 1));
        Assert.True(session.Zone(ZoneType.Residential).Success);
        Assert.Equal(new CellRect(game.Map.Width - 2, game.Map.Height - 2, 2, 2),
            Assert.Single(game.VacantPlots(ZoneType.Residential)));
    }

    [Fact]
    public void NarrowResidentialStripsExplainTheMinimumAndDoNotGrow()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        Assert.True(game.Designate(new(10, 19, 20, 1), ZoneType.Residential).Success);
        Assert.Empty(game.VacantPlots(ZoneType.Residential));
        Assert.Equal(GrowthStatus.NeedsSpace, GrowthDiagnostics.ForZone(game, ZoneType.Residential).Status);
        Assert.Contains("2x2", GrowthDiagnostics.ForCell(game, 10, 19).Message);
        TestCity.Advance(game, 8);
        Assert.Empty(game.Map.BuildingFootprints);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    public void FullRulesHomesUseVariedLargerPlotsAndRoundTrip(int seed)
    {
        var game = TestCity.Flat(seed: seed, rules: CityRules.Full);
        Assert.True(game.Designate(new(10, 5, 40, 12), ZoneType.Residential).Success);
        TestCity.Advance(game, 8);
        var plots = game.Map.BuildingFootprints.Values.ToArray();
        Assert.NotEmpty(plots);
        Assert.All(plots, area =>
        {
            Assert.InRange(area.Width, 2, 4);
            Assert.InRange(area.Height, 2, 4);
        });
        Assert.Contains(plots, area => area.Width > 2 || area.Height > 2);
        Assert.True(plots.Select(area => (area.Width, area.Height)).Distinct().Count() > 1);
        var restored = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Equal(plots.OrderBy(area => area.Y).ThenBy(area => area.X),
            restored.Map.BuildingFootprints.Values.OrderBy(area => area.Y).ThenBy(area => area.X));
    }

    [Fact]
    public void FullRulesHomesFitTheMinimumWhenNoLargerPlotFits()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        var area = new CellRect(10, 18, 2, 2);
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
