using TermCity.Core.Buildings;
using TermCity.Core.Persistence;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class CompactBuildingTests
{
    [Fact]
    public void ZonedTypesStartAtOneCellAndNeverRequireMoreThanTwoByTwo()
    {
        var buildings = BuildingRegistry.CreateDefault();
        foreach (var zone in Zones.Placeable)
        {
            Assert.Equal(new CellRect(4, 5, 1, 1), buildings.ForZone(zone)!.FootprintAt(4, 5));
            foreach (var type in buildings.Where(type => type.Zone == zone))
            {
                Assert.InRange(type.Width, 1, 2);
                Assert.InRange(type.Height, 1, 2);
            }
        }
        Assert.Equal(6, buildings.Get("Cottage").Capacity);
        Assert.Equal(14, buildings.Get("Market Stall").Capacity);
        Assert.Equal(14, buildings.Get("Workshop").Capacity);
    }

    [Theory]
    [InlineData(ZoneType.Residential)]
    [InlineData(ZoneType.Commercial)]
    [InlineData(ZoneType.Industrial)]
    public void SingleClickReservesExactlyOneMinimumPlot(ZoneType zone)
    {
        var session = new GameSession(TestCity.Flat(rules: CityRules.Full));
        session.PlaceCursor(new(10, 10));
        Assert.True(session.Zone(zone).Success);
        Assert.Equal(CellRect.Single(new(10, 10)), Assert.Single(session.Game.VacantPlots(zone)));
    }

    [Theory]
    [InlineData(ZoneType.Commercial)]
    [InlineData(ZoneType.Industrial)]
    public void BusinessesGrowMostlyInSingleCellsWithNoFootprintLargerThanTwoByTwo(ZoneType zone)
    {
        var game = TestCity.Flat(config: new GameConfig
        {
            ResidentialPerCommercial = 1, ResidentialPerIndustrial = 1,
            MaxNewCommercialPerWeek = 50, MaxNewIndustrialPerWeek = 50,
        });
        var home = game.Map.Content.Buildings.ForZone(ZoneType.Residential)!;
        for (int x = 0; x < 50; x++)
        {
            game.Map.SetZone(x, 5, ZoneType.Residential);
            game.Map.SetBuildingFootprint(home, new(x, 5, 1, 1));
            game.Map.SetHousehold(x, 5, new(2, 2, 1));
        }
        game.Touch();
        Assert.True(game.Designate(new(10, 21, 100, 10), zone).Success);
        game.AdvanceWeek();
        var plots = game.Map.BuildingFootprints.Values.Where(area =>
            game.Map.BuildingAt(area.X, area.Y)!.Zone == zone).ToArray();
        Assert.True(plots.Length >= 20);
        Assert.True(plots.Count(area => area.Area == 1) >= plots.Length * 0.65);
        Assert.All(plots, area =>
        {
            Assert.InRange(area.Width, 1, 2);
            Assert.InRange(area.Height, 1, 2);
        });
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Equal(game.Stats, loaded.Stats);
    }

    [Fact]
    public void NewCitiesStartAtCloseViewAndLoadedCitiesRetainTheirSavedZoom()
    {
        var session = new GameSession(TestCity.Flat());
        session.NewGame(new GameConfig { Seed = 42 });
        Assert.Equal(1, session.ZoomLevel);
        Assert.Equal("2x", session.ZoomLabel);
        session.SetZoom(-1);
        string path = Path.Combine(Path.GetTempPath(), "termcity-compact-view-" + Guid.NewGuid().ToString("N") + ".json");
        SaveGameStore.Save(session.Game, path, new SavedView(session.CameraX, session.CameraY, session.ZoomLevel));
        try
        {
            Assert.True(session.LoadFrom(path));
            Assert.Equal(-1, session.ZoomLevel);
        }
        finally { File.Delete(path); }
    }
}
