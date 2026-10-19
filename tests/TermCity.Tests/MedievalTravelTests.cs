using System.Text.Json.Nodes;
using TermCity.Core.Buildings;
using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;
using TermCity.GodotApp;

namespace TermCity.Tests;

public class MedievalTravelTests
{
    [Theory]
    [InlineData("Fire Watch", 18, 27)]
    [InlineData("Watch House", 10, 15)]
    [InlineData("Sheriff's Hall", 16, 24)]
    [InlineData("Gaol", 24, 36)]
    [InlineData("Apothecary", 12, 18)]
    [InlineData("Infirmary", 24, 36)]
    [InlineData("Hospice", 15, 23)]
    [InlineData("Chantry School", 12, 18)]
    [InlineData("Monastery", 28, 42)]
    [InlineData("Village Green", 8, 12)]
    [InlineData("Tavern", 11, 17)]
    [InlineData("Motte and Bailey", 20, 30)]
    [InlineData("Stone Keep", 28, 42)]
    [InlineData("Castle", 38, 57)]
    [InlineData("Chapel", 10, 15)]
    [InlineData("Parish Church", 18, 27)]
    [InlineData("Cathedral", 34, 51)]
    [InlineData("Market Cross", 18, 27)]
    [InlineData("Guildhall", 33, 50)]
    public void EveryAreaServiceHasFiftyPercentLargerTimelyRange(string name, int previous, int expected)
    {
        var type = new GameContent().Buildings.Get(name);
        Assert.Equal((int)Math.Ceiling(previous * 1.5), type.Radius);
        Assert.Equal(expected, type.Radius);
        Assert.Equal(expected / 4.0, type.ResponseTargetDays);
        Assert.Equal(expected * expected * 6, type.StaffingCapacity);
        Assert.Equal(expected, Assert.Single(AreaOfEffect.ForBuilding(type, new(0, 0)),
            area => area.Glyphs.Contains(type.Glyphs[0])).Radius);
    }

    private static CityGame Small(CityRules rules = CityRules.Classic) =>
        TestCity.Flat(config: new GameConfig { MapWidth = 80, MapHeight = 48, StartingMoney = 1_000_000 }, rules: rules);

    [Fact]
    public void SingleResidentialClickReservesACompleteHomePlotAndAttractsResidents()
    {
        var session = new GameSession(Small(), showGuide: false);
        session.PlaceCursor(new(10, 5));
        Assert.True(session.Zone(ZoneType.Residential).Success);
        Assert.Single(session.Game.VacantPlots(ZoneType.Residential));
        Assert.Equal(ZoneType.Residential, session.Game.Map.ZoneAt(10, 5));
        Assert.Equal(ZoneType.None, session.Game.Map.ZoneAt(11, 5));
        TestCity.Advance(session.Game, 12);
        Assert.True(session.Game.Stats.Population > 0);
    }

    [Fact]
    public void FreshGeneratedCitySettlesWithPaidStarterSuppliesAndRealSessionClock()
    {
        var game = CityGame.New(new GameConfig { Seed = 42 });
        var session = new GameSession(game, showGuide: false);
        game.Paused = true;
        var location = Enumerable.Range(0, game.Map.Width * game.Map.Height).Select(game.Map.PosOf)
            .First(p => new CellRect(p.X, p.Y, 8, 4).Cells().All(cell => game.CanBuildOn(cell.X, cell.Y)));
        var woodlot = game.Map.Content.Buildings.Get("Woodlot");
        var well = game.Map.Content.Buildings.Get("Town Well");
        Assert.True(game.PlaceBuilding(woodlot, new(location.X, location.Y + 2, 1, 1)).Success);
        Assert.True(game.PlaceBuilding(well, new(location.X + 4, location.Y + 2, 1, 1)).Success);
        session.PlaceCursor(location);
        Assert.True(session.Zone(ZoneType.Residential).Success);
        Assert.Single(game.VacantPlots(ZoneType.Residential));
        Assert.True(session.ClockSuspended);
        session.TogglePause();
        Assert.False(session.ClockSuspended);
        for (int frame = 0; frame < 2000 && game.Stats.Population == 0; frame++) session.Update(0.1);
        Assert.True(game.Stats.Population > 0, $"Week {game.Week}: {GrowthDiagnostics.ForZone(game, ZoneType.Residential).Message}");
        Assert.InRange(game.Money, 25_000, 38_000);
        Assert.Equal(0, game.Budget.Loan);
    }

    [Fact]
    public void IsolatedOneCellZoneIsACompleteCompactHomePlot()
    {
        var game = Small();
        game.Designate(new(10, 5, 1, 1), ZoneType.Residential);
        Assert.Equal(GrowthStatus.Ready, GrowthDiagnostics.ForCell(game, 10, 5).Status);
        Assert.Equal(new CellRect(10, 5, 1, 1), Assert.Single(game.VacantPlots(ZoneType.Residential)));
    }

    [Fact]
    public void MissingStarterDeliveriesExplainStalledGrowthAndSuppliesRestoreArrivals()
    {
        var game = Small(CityRules.Full);
        game.Designate(new(10, 5, 20, 2), ZoneType.Residential);
        TestCity.Advance(game, 8);
        Assert.True(game.Stats.Population > 0);
        Assert.Equal(GrowthStatus.LowAttraction, GrowthDiagnostics.ForZone(game, ZoneType.Residential).Status);
        Assert.Contains("Woodlot", GrowthDiagnostics.ForZone(game, ZoneType.Residential).Message);
        Assert.True(game.PlaceBuilding(game.Map.Content.Buildings.Get("Woodlot"), new(10, 8, 1, 1)).Success);
        Assert.True(game.PlaceBuilding(game.Map.Content.Buildings.Get("Town Well"), new(14, 8, 1, 1)).Success);
        int before = game.Stats.Population;
        TestCity.Advance(game, 12);
        Assert.True(game.Stats.Population > before);
    }

    [Fact]
    public void RoadsAreOptionalAndLocalRoadsBoostTravel()
    {
        var game = Small();
        for (int x = 0; x < 80; x++) game.Map.SetRoad(x, 20, false);
        game.Touch();
        game.Designate(new(10, 5, 30, 2), ZoneType.Residential);
        TestCity.Advance(game, 8);
        Assert.True(game.Stats.Households > 0);
        Assert.All(game.Map.BuildingFootprints.Values, area => Assert.InRange(area.Area, 1, 4));
        var a = game.Map.Index(10, 30);
        var b = game.Map.Index(30, 30);
        double walking = TravelRoutes.Compute(game.Map, [a]).Cost[b];
        for (int x = 10; x <= 30; x++) game.Map.SetRoad(x, 30, true);
        game.Touch();
        Assert.Equal(0, game.Network.ConnectedRoadCount);
        Assert.Equal(1, game.Network.AccessRank(a));
        Assert.Equal(6, walking / TravelRoutes.Compute(game.Map, [a]).Cost[b], 6);
    }

    [Fact]
    public void WaterRequiresABridgeAndDetoursCostTime()
    {
        var game = Small();
        for (int y = 0; y < 48; y++) game.Map.SetTerrain(40, y, game.Map.Content.Terrains.Get("Water"));
        game.Map.SetRoad(40, 20, false);
        int source = game.Map.Index(39, 10), target = game.Map.Index(41, 10);
        Assert.True(double.IsPositiveInfinity(TravelRoutes.Compute(game.Map, [source]).Cost[target]));
        game.Map.SetRoad(40, 30, true);
        Assert.True(double.IsFinite(TravelRoutes.Compute(game.Map, [source]).Cost[target]));
        Assert.True(TravelRoutes.Compute(game.Map, [source]).Cost[target] > 12);
    }

    [Fact]
    public void PathsAppearGraduallyAndPersistWithoutBecomingRoads()
    {
        var game = Small();
        var map = game.Map;
        var type = map.Content.Buildings.Get("Cottage");
        map.SetZone(10, 14, ZoneType.Residential);
        map.SetZone(11, 14, ZoneType.Residential);
        map.SetBuildingFootprint(type, new(10, 14, 2, 1));
        map.SetHousehold(10, 14, new(2, 2, 0));
        game.Touch();
        int roads = map.RoadCount;
        game.AdvanceDay();
        Assert.Single(map.PathLayer, w => w > 0);
        TestCity.Advance(game, 2);
        Assert.Contains(map.PathLayer, wear => wear >= 3);
        Assert.Equal(roads, map.RoadCount);
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Equal(map.PathLayer, loaded.Map.PathLayer);
        Assert.Equal(game.Stats.Population, loaded.Stats.Population);
        Assert.Equal(game.Stats.Households, loaded.Stats.Households);
        Assert.Equal(1, loaded.Stats.Residential.Filled);
        Assert.Equal(map.HouseholdAt(10, 14), map.HouseholdAt(11, 14));
        Assert.Equal(0, map.HouseholdLayer[map.Index(11, 14)].Total);
        var path = Enumerable.Range(0, map.PathLayer.Length).First(i => map.PathLayer[i] > 0);
        var preview = new PlacementPreview(PlacementKind.Demolish, CellRect.Single(map.PosOf(path)), new(1, 0, 0), "Path");
        Assert.True(preview.IsValid(game, path % map.Width, path / map.Width));
        Assert.True(game.Demolish(CellRect.Single(map.PosOf(path))).Success);
        Assert.Equal(0, map.PathLayer[path]);
    }

    [Fact]
    public void RoadlessBuildingsGraduallyWearAContinuousRouteBetweenTheirFootprints()
    {
        var game = Small();
        var map = game.Map;
        foreach (int cell in map.RoadCells.ToArray()) map.SetRoad(cell % map.Width, cell / map.Width, false);
        var well = map.Content.Buildings.Get("Town Well");
        map.SetBuildingFootprint(well, new(10, 10, 1, 1));
        map.SetBuildingFootprint(well, new(18, 10, 1, 1));
        Assert.True(TravelRoutes.WearPaths(map));
        Assert.InRange(map.PathLayer.Count(w => w > 0), 1, 2);
        for (int day = 0; day < 12; day++) TravelRoutes.WearPaths(map);
        for (int x = 11; x < 18; x++)
        {
            Assert.True(map.PathWearAt(x, 10) >= 3);
            Assert.Equal(10, FootpathLayout.Connections(map, x, 10));
            Assert.Equal("─", CellRenderer.Render(game, x, 10).Glyph);
        }
        Assert.Equal(0, map.PathWearAt(10, 10));
        Assert.Equal(0, map.PathWearAt(18, 10));
        Assert.Equal(0, map.RoadCount);
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Equal(map.PathLayer, loaded.Map.PathLayer);
    }

    [Fact]
    public void PathsWaitForLandRoadApproachesWhileGeneralTravelCanUseTheBridge()
    {
        var game = Small();
        var map = game.Map;
        foreach (int cell in map.RoadCells.ToArray()) map.SetRoad(cell % map.Width, cell / map.Width, false);
        var well = map.Content.Buildings.Get("Town Well");
        map.SetBuildingFootprint(well, new(10, 10, 1, 1));
        map.SetBuildingFootprint(well, new(18, 10, 1, 1));
        for (int y = 0; y < map.Height; y++) map.SetTerrain(14, y, map.Content.Terrains.Get("Water"));
        Assert.False(TravelRoutes.WearPaths(map));
        Assert.All(map.PathLayer, wear => Assert.Equal(0, wear));
        map.SetRoad(14, 10, true);
        Assert.True(double.IsFinite(TravelRoutes.Compute(map, [map.Index(10, 10)]).Cost[map.Index(18, 10)]));
        Assert.False(TravelRoutes.WearPaths(map));
        Assert.All(map.PathLayer, wear => Assert.Equal(0, wear));
        foreach (int x in new[] { 12, 13, 15, 16 }) map.SetRoad(x, 10, true);
        for (int y = 0; y < 10; y++)
        {
            map.SetRoad(12, y, true);
            map.SetRoad(16, y, true);
        }
        for (int day = 0; day < 12; day++) TravelRoutes.WearPaths(map);
        Assert.Equal(2, TravelRoutes.FootpathJourneys(map).Count());
        Assert.Contains(map.PathLayer, wear => wear >= 3);
        Assert.All(TravelRoutes.FootpathJourneys(map), journey =>
            Assert.True(TravelRoutes.IsPathRoadTarget(map, journey[^1]) &&
                !TravelRoutes.IsFootpathBridgeCell(map, journey[^1])));
        Assert.All(Enumerable.Range(0, map.PathLayer.Length).Where(cell => map.PathLayer[cell] > 0),
            cell => Assert.False(TravelRoutes.IsFootpathBridgeCell(map, cell)));
        for (int y = 0; y < map.Height; y++) Assert.Equal(0, map.PathWearAt(14, y));
    }

    [Fact]
    public void FootpathConnectionsRenderCornersAndJunctionsInsteadOfIsolatedDashes()
    {
        var game = Small();
        var map = game.Map;
        map.PathLayer[map.Index(10, 10)] = 3;
        map.PathLayer[map.Index(11, 10)] = 3;
        map.PathLayer[map.Index(10, 9)] = 3;
        Assert.Equal(3, FootpathLayout.Connections(map, 10, 10));
        Assert.Equal("└", CellRenderer.Render(game, 10, 10).Glyph);
        map.PathLayer[map.Index(10, 11)] = 3;
        Assert.Equal("├", CellRenderer.Render(game, 10, 10).Glyph);
        map.SetBuildingFootprint(map.Content.Buildings.Get("Town Well"), new(9, 10, 1, 1));
        Assert.Equal(15, FootpathLayout.Connections(map, 10, 10));
        Assert.Equal("┼", CellRenderer.Render(game, 10, 10).Glyph);
        Assert.Equal(0, FootpathLayout.Connections(map, 0, 0));
    }

    [Fact]
    public void DistantServicesHaveFirstArrivalAndNoRadiusCutoff()
    {
        var game = Small(CityRules.Full);
        game.HighestMilestone = 250;
        var fire = game.Map.Content.Buildings.Get("Fire Watch");
        Assert.True(game.PlaceBuilding(fire, new(5, 19, 1, 1)).Success);
        int target = game.Map.Index(65, 19);
        Assert.True(game.Services.ResponseDays(ServiceKind.Fire, target) > 1);
        Assert.False(game.Services.HasArrived(ServiceKind.Fire, target));
        Assert.Equal(0, game.Services.Coverage(ServiceKind.Fire, target));
        var saved = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Equal(game.Services.ResponseDays(ServiceKind.Fire, target), saved.Services.ResponseDays(ServiceKind.Fire, target));
        TestCity.Advance(game, 2);
        Assert.True(game.Services.HasArrived(ServiceKind.Fire, target));
        Assert.InRange(game.Services.Coverage(ServiceKind.Fire, target), 1, 100);
        int remote = game.Map.Index(79, 0);
        Assert.True(game.Services.ResponseDays(ServiceKind.Fire, remote) > fire.ResponseTargetDays);
        TestCity.Advance(game, 2);
        Assert.InRange(game.Services.Coverage(ServiceKind.Fire, remote), 1, 99);
    }

    [Fact]
    public void CapacityAddsResponseTimeAndExplicitWarnings()
    {
        var game = Small(CityRules.Full);
        var apothecary = game.Map.Content.Buildings.Get("Apothecary");
        game.Map.SetBuildingFootprint(apothecary, new(5, 19, 1, 1));
        var fuel = game.Map.Content.Buildings.Get("Charcoal Burners");
        game.Map.SetBuildingFootprint(fuel, new(5, 17, fuel.Width, fuel.Height));
        var water = game.Map.Content.Buildings.Get("Town Well");
        for (int x = 10; x < 14; x++)
            game.Map.SetBuildingFootprint(water, new(x, 17, 1, 1));
        game.Touch();
        int target = game.Map.Index(60, 19);
        double before = game.Services.ResponseDays(ServiceKind.Health, target);
        for (int x = 20; x < 78; x++)
        {
            game.Map.SetZone(x, 19, ZoneType.Residential);
            game.Map.SetBuilding(x, 19, game.Map.Content.Buildings.Get("Tenement"));
            game.Map.SetHousehold(x, 19, new(160, 0, 0));
        }
        game.Touch();
        Assert.True(game.Services.Workload(ServiceKind.Health) > 1);
        Assert.True(game.Services.ResponseDays(ServiceKind.Health, target) > before);
        Assert.Contains("capacity", CellInspector.Status(game, new(5, 19))!.Text);
        Assert.Contains(BuildingReport.Build(game, new(5, 19)).Groups[0].Rows,
            row => row[0].Text == "Workload" && row[1].Text.Contains('%'));
    }

    [Fact]
    public void DezoneRestoreAndDemolitionTreatAHomeAsOneBuilding()
    {
        var session = new GameSession(Small());
        var map = session.Game.Map;
        map.SetZone(10, 19, ZoneType.Residential);
        map.SetZone(11, 19, ZoneType.Residential);
        map.SetBuildingFootprint(map.Content.Buildings.Get("Cottage"), new(10, 19, 2, 1));
        map.SetHousehold(10, 19, new(2, 2, 1));
        session.Game.Touch();
        session.Game.Dezone(new(11, 19, 1, 1));
        Assert.Equal(1, session.Game.Stats.Residential.AwaitingRemoval);
        Assert.Equal(5, session.Game.Stats.Population);
        session.Game.Designate(new(11, 19, 1, 1), ZoneType.Residential);
        Assert.Empty(map.ZoneRemovals);
        Assert.Equal(ZoneType.Residential, map.ZoneAt(10, 19));
        session.PlaceCursor(new(11, 19));
        session.PreviewDemolish();
        Assert.True(session.ConfirmPreview().Success);
        Assert.Equal(0, session.Game.Stats.Population);
        Assert.Empty(map.BuildingFootprints);
        session.Game.Paused = true;
        session.RequestUndo();
        session.SelectPrompt(0);
        Assert.Equal(5, session.Game.Stats.Population);
        Assert.Single(session.Game.Map.BuildingFootprints);
    }

    [Fact]
    public void OlderSavesWithoutTravelMetadataRemainReadable()
    {
        var json = JsonNode.Parse(SaveGameStore.Serialize(Small()))!.AsObject();
        json.Remove("Paths");
        json.Remove("BuildingEstablishedDays");
        Assert.All(SaveGameStore.Deserialize(json.ToJsonString()).Map.PathLayer, w => Assert.Equal(0, w));
        json["BuildingEstablishedDays"] = JsonNode.Parse("""{"0":-1}""");
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(json.ToJsonString()));
    }

    [Fact]
    public void PixelBuildingsAreTransparentShadedAndExtendAbovePlots()
    {
        foreach (var type in new GameContent().Buildings)
        {
            var sprite = BuildingSprite.Render(type, type.Width, type.Height);
            Assert.Equal(sprite.Width * sprite.Height * 4, sprite.Pixels.Length);
            Assert.True(sprite.Height > type.Height * 22);
            Assert.Contains(sprite.Pixels.Where((_, i) => i % 4 == 3), alpha => alpha == 0);
            Assert.Contains(sprite.Pixels.Where((_, i) => i % 4 == 3), alpha => alpha == 255);
            Assert.True(Enumerable.Range(0, sprite.Width * sprite.Height)
                .Where(i => sprite.Pixels[i * 4 + 3] == 255)
                .Select(i => (sprite.Pixels[i * 4], sprite.Pixels[i * 4 + 1], sprite.Pixels[i * 4 + 2]))
                .Distinct().Count() >= 4, type.Name);
        }
    }
}
